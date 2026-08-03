using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Collations;
using BlueTusk.EntityFrameworkCore.Collations.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration-builder extensions for PostgreSQL collations.</summary>
public static class BlueTuskCollationMigrationBuilderExtensions
{
    public static OperationBuilder<CreateBlueTuskCollationOperation> CreateBlueTuskCollation(
        this MigrationBuilder migrationBuilder,
        BlueTuskCollationDefinition definition,
        bool ifNotExists = false)
    {
        BlueTuskCollationMetadata.Validate(definition);
        return Add(migrationBuilder, new CreateBlueTuskCollationOperation
        {
            Definition = definition,
            IfNotExists = ifNotExists,
        });
    }

    public static OperationBuilder<CreateBlueTuskCollationFromOperation> CreateBlueTuskCollationFrom(
        this MigrationBuilder migrationBuilder,
        string name,
        string sourceName,
        string? schema = null,
        string? sourceSchema = null,
        bool ifNotExists = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ValidateOptional(schema, nameof(schema));
        ValidateOptional(sourceSchema, nameof(sourceSchema));
        return Add(migrationBuilder, new CreateBlueTuskCollationFromOperation
        {
            Name = name,
            Schema = schema,
            SourceName = sourceName,
            SourceSchema = sourceSchema,
            IfNotExists = ifNotExists,
        });
    }

    public static OperationBuilder<RenameBlueTuskCollationOperation> RenameBlueTuskCollation(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName,
        string? schema = null,
        string? newSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ValidateOptional(schema, nameof(schema));
        ValidateOptional(newSchema, nameof(newSchema));
        return Add(migrationBuilder, new RenameBlueTuskCollationOperation
        {
            Name = name,
            Schema = schema,
            NewName = newName,
            NewSchema = newSchema,
        });
    }

    public static OperationBuilder<RefreshBlueTuskCollationVersionOperation> RefreshBlueTuskCollationVersion(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateOptional(schema, nameof(schema));
        return Add(migrationBuilder, new RefreshBlueTuskCollationVersionOperation
        {
            Name = name,
            Schema = schema,
        });
    }

    public static OperationBuilder<DropBlueTuskCollationOperation> DropBlueTuskCollation(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null,
        bool ifExists = false,
        bool cascade = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateOptional(schema, nameof(schema));
        return Add(migrationBuilder, new DropBlueTuskCollationOperation
        {
            Name = name,
            Schema = schema,
            IfExists = ifExists,
            Cascade = cascade,
            IsDestructiveChange = true,
        });
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OperationBuilder<CreateBlueTuskCollationOperation> CreateBlueTuskCollation(
        this MigrationBuilder migrationBuilder,
        string serializedDefinition,
        bool ifNotExists = false) =>
        CreateBlueTuskCollation(
            migrationBuilder,
            BlueTuskCollationMetadata.DeserializeDefinition(serializedDefinition),
            ifNotExists);

    private static OperationBuilder<TOperation> Add<TOperation>(
        MigrationBuilder migrationBuilder,
        TOperation operation)
        where TOperation : MigrationOperation
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<TOperation>(operation);
    }

    private static void ValidateOptional(string? value, string parameterName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        }
    }
}
