using BlueTusk.EntityFrameworkCore.CheckConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Migration operations for PostgreSQL CHECK-constraint options.</summary>
public static class BlueTuskCheckConstraintMigrationBuilderExtensions
{
    /// <summary>Adds a PostgreSQL CHECK constraint with optional NOT VALID and NO INHERIT clauses.</summary>
    public static OperationBuilder<AddCheckConstraintOperation> AddBlueTuskCheckConstraint(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string sql,
        string? schema = null,
        bool notValid = false,
        bool noInherit = false,
        bool notEnforced = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var operation = new AddCheckConstraintOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
            Sql = sql,
        };
        if (notValid)
        {
            operation[BlueTuskCheckConstraintMetadata.NotValidAnnotationName] = true;
        }

        if (noInherit)
        {
            operation[BlueTuskCheckConstraintMetadata.NoInheritAnnotationName] = true;
        }

        if (notEnforced)
        {
            operation[BlueTuskCheckConstraintMetadata.NotEnforcedAnnotationName] = true;
        }

        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<AddCheckConstraintOperation>(operation);
    }

    /// <summary>Validates an existing PostgreSQL CHECK constraint against all table rows.</summary>
    public static OperationBuilder<ValidateBlueTuskCheckConstraintOperation> ValidateBlueTuskCheckConstraint(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var operation = new ValidateBlueTuskCheckConstraintOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
        };
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<ValidateBlueTuskCheckConstraintOperation>(operation);
    }
}
