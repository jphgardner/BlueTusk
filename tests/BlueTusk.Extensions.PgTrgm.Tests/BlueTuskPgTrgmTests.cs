using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Extensions.PgTrgm.Tests;

public sealed class BlueTuskPgTrgmTests
{
    [Fact]
    public void Build_carries_feature_only_registration_in_an_immutable_snapshot()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.UsePgTrgm("Application Extensions"));

        using var dataSource = builder.Build();
        builder.Features.Register("test.late-feature", 42);

        var feature = dataSource.Features.GetRequired<BlueTuskPgTrgmFeature>(
            BlueTuskPgTrgmFeature.RegistryName);
        Assert.Equal("Application Extensions", feature.Schema);
        Assert.False(dataSource.Features.Contains("test.late-feature"));
        Assert.DoesNotContain(
            dataSource.TypeRegistry.Types,
            type => type.Schema == "Application Extensions");
    }

    [Fact]
    public async Task Comparison_requires_explicit_pg_trgm_registration()
    {
        await using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=localhost;Username=test;Password=test")
            .Build();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await dataSource.ComparePgTrgmAsync("BlueTusk", "PostgreSQL"));
    }

    [Fact]
    public async Task Pg_trgm_plugin_executes_functions_operators_and_safe_parameters_live()
    {
        var connectionString = GetConnectionString();
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var create = administration.CreateCommand("CREATE EXTENSION IF NOT EXISTS pg_trgm"))
        {
            _ = await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        const string adversarial = "BlueTusk'); DROP TABLE extension_data; --";
        await using (var dataSource = new BlueTuskDataSourceBuilder(connectionString)
                         .UsePgTrgm()
                         .Build())
        {
            AssertPerfect(await dataSource.ComparePgTrgmAsync(adversarial, adversarial));
        }

        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var move = administration.CreateCommand(
                         "CREATE SCHEMA IF NOT EXISTS \"Application Extensions\"; " +
                         "ALTER EXTENSION pg_trgm SET SCHEMA \"Application Extensions\""))
        {
            _ = await move.ExecuteNonQueryAsync(CancellationToken.None);
        }

        try
        {
            await using var customDataSource = new BlueTuskDataSourceBuilder(connectionString)
                .UsePgTrgm("Application Extensions")
                .Build();
            AssertPerfect(await customDataSource.ComparePgTrgmAsync(adversarial, adversarial));
        }
        finally
        {
            await using var administration = BlueTuskDataSource.Create(connectionString);
            await using var restore = administration.CreateCommand(
                "ALTER EXTENSION pg_trgm SET SCHEMA public; " +
                "DROP SCHEMA IF EXISTS \"Application Extensions\"");
            _ = await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static void AssertPerfect(BlueTuskPgTrgmComparison result)
    {
        Assert.Equal(1f, result.Similarity);
        Assert.Equal(1f, result.WordSimilarity);
        Assert.Equal(1f, result.StrictWordSimilarity);
        Assert.True(result.IsSimilar);
        Assert.True(result.IsWordSimilar);
        Assert.True(result.IsStrictWordSimilar);
        Assert.NotEmpty(result.Trigrams);
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
}
