using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Data.Schema;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PropertyGraphMigrationIntegrationTests
{
    private const string Schema = "ef_graph_migrations";
    private const string ArchiveSchema = "ef_graph_migrations_archive";

    [Fact]
    public async Task Guarded_graph_migrations_create_alter_and_drop_with_quoted_identifiers()
    {
        var connectionString = GetConnectionString();
        var definition = CreateDefinition();
        await using var context = CreateContext(connectionString);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var createSql = Generate(
            generator,
            context.Model,
            new CreatePropertyGraphOperation { Definition = definition });

        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        await using var capabilityConnection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var supportsSqlPgq = capabilityConnection.ServerCapabilities is { SupportsSqlPgq: true };
        await capabilityConnection.CloseAsync();

        await ExecuteNonQueryAsync(
            connectionString,
            $"DROP SCHEMA IF EXISTS {Schema} CASCADE; DROP SCHEMA IF EXISTS {ArchiveSchema} CASCADE");
        if (!supportsSqlPgq)
        {
            var exception = await Assert.ThrowsAsync<BlueTuskException>(
                () => ExecuteNonQueryAsync(connectionString, createSql));
            Assert.Equal("0A000", exception.SqlState);
            Assert.Contains("require PostgreSQL 19", exception.Message, StringComparison.Ordinal);
            return;
        }

        try
        {
            await ExecuteNonQueryAsync(
                connectionString,
                """
                CREATE SCHEMA ef_graph_migrations;
                CREATE SCHEMA ef_graph_migrations_archive;
                CREATE TABLE ef_graph_migrations."People ""Table" (
                    "Person Id" int4 PRIMARY KEY,
                    "Display ""Name" text NOT NULL);
                CREATE TABLE ef_graph_migrations."Friend ""Edges" (
                    "Edge Id" int4 PRIMARY KEY,
                    "From Id" int4 NOT NULL REFERENCES ef_graph_migrations."People ""Table" ("Person Id"),
                    "To Id" int4 NOT NULL REFERENCES ef_graph_migrations."People ""Table" ("Person Id"));
                """);
            await ExecuteNonQueryAsync(connectionString, createSql);

            var inspector = new BlueTuskPropertyGraphSchemaInspector(dataSource);
            var created = Assert.Single(
                await inspector.InspectAsync(
                    new BlueTuskPropertyGraphInspectionOptions
                    {
                        Schema = Schema,
                        Name = "Social \"Graph",
                    },
                    CancellationToken.None));
            Assert.Equal(2, created.ElementTables.Count);
            Assert.Contains(created.Labels, label => label.Name == "Person \"Label");

            var alterSql = Generate(
                generator,
                context.Model,
                new AlterPropertyGraphOperation
                {
                    Name = "Social \"Graph",
                    Schema = Schema,
                    NewName = "Renamed \"Graph",
                    NewSchema = ArchiveSchema,
                });
            Assert.Contains("ALTER PROPERTY GRAPH", alterSql, StringComparison.Ordinal);
            Assert.Contains("SET SCHEMA", alterSql, StringComparison.Ordinal);
            Assert.Contains("RENAME TO", alterSql, StringComparison.Ordinal);
            await ExecuteNonQueryAsync(connectionString, alterSql);

            var renamed = Assert.Single(
                await inspector.InspectAsync(
                    new BlueTuskPropertyGraphInspectionOptions
                    {
                        Schema = ArchiveSchema,
                        Name = "Renamed \"Graph",
                    },
                    CancellationToken.None));
            Assert.Equal("Renamed \"Graph", renamed.Name.Name);

            var dropSql = Generate(
                generator,
                context.Model,
                new DropPropertyGraphOperation
                {
                    Name = renamed.Name.Name,
                    Schema = renamed.Name.Schema,
                });
            await ExecuteNonQueryAsync(connectionString, dropSql);
            Assert.Empty(
                await inspector.InspectAsync(
                    new BlueTuskPropertyGraphInspectionOptions
                    {
                        Schema = ArchiveSchema,
                        Name = "Renamed \"Graph",
                    },
                    CancellationToken.None));
        }
        finally
        {
            await ExecuteNonQueryAsync(
                connectionString,
                $"DROP SCHEMA IF EXISTS {Schema} CASCADE; DROP SCHEMA IF EXISTS {ArchiveSchema} CASCADE");
        }
    }

    private static BlueTuskPropertyGraphDefinition CreateDefinition() =>
        new(
            "Social \"Graph",
            Schema,
            [
                new BlueTuskGraphElementTableDefinition(
                    "People Vertex",
                    BlueTuskGraphElementKind.Vertex,
                    "People \"Table",
                    Schema,
                    ["Person Id"],
                    [
                        new BlueTuskGraphLabelDefinition(
                            "Person \"Label",
                            [
                                new BlueTuskGraphPropertyDefinition("Person Id", "Id", IsColumn: true),
                                new BlueTuskGraphPropertyDefinition("Display \"Name", "Name", IsColumn: true),
                            ]),
                    ],
                    Source: null,
                    Destination: null),
                new BlueTuskGraphElementTableDefinition(
                    "Friend Edge",
                    BlueTuskGraphElementKind.Edge,
                    "Friend \"Edges",
                    Schema,
                    ["Edge Id"],
                    [new BlueTuskGraphLabelDefinition("Knows \"Label", [])],
                    new BlueTuskGraphEndpointDefinition("People Vertex", ["From Id"], ["Person Id"]),
                    new BlueTuskGraphEndpointDefinition("People Vertex", ["To Id"], ["Person Id"])),
            ]);

    private static string Generate(
        IMigrationsSqlGenerator generator,
        Microsoft.EntityFrameworkCore.Metadata.IModel model,
        MigrationOperation operation) =>
        string.Join(
            Environment.NewLine,
            generator.Generate([operation], model).Select(command => command.CommandText));

    private static TestContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new TestContext(options);
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }

    private sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options);
}
