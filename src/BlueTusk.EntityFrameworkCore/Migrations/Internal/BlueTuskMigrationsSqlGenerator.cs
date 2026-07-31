using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal sealed class BlueTuskMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies)
    : MigrationsSqlGenerator(dependencies)
{
    protected override void Generate(
        AlterColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        if (operation.ComputedColumnSql is not null)
        {
            throw new NotSupportedException("PostgreSQL generated-column alteration requires dropping and recreating the column.");
        }

        var helper = Dependencies.SqlGenerationHelper;
        var table = helper.DelimitIdentifier(operation.Table, operation.Schema);
        var column = helper.DelimitIdentifier(operation.Name);
        var columnType = GetColumnType(
            operation.Schema,
            operation.Table,
            operation.Name,
            operation,
            model);

        if (!string.Equals(columnType, operation.OldColumn.ColumnType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(operation.Collation, operation.OldColumn.Collation, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ").Append(table)
                .Append(" ALTER COLUMN ").Append(column)
                .Append(" TYPE ").Append(columnType);
            if (operation.Collation is not null)
            {
                builder.Append(" COLLATE ").Append(helper.DelimitIdentifier(operation.Collation));
            }

            EndStatement(builder);
        }

        if (operation.IsNullable != operation.OldColumn.IsNullable)
        {
            builder
                .Append("ALTER TABLE ").Append(table)
                .Append(" ALTER COLUMN ").Append(column)
                .Append(operation.IsNullable ? " DROP NOT NULL" : " SET NOT NULL");
            EndStatement(builder);
        }

        if (!Equals(operation.DefaultValue, operation.OldColumn.DefaultValue)
            || !string.Equals(operation.DefaultValueSql, operation.OldColumn.DefaultValueSql, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ").Append(table)
                .Append(" ALTER COLUMN ").Append(column);
            if (operation.DefaultValueSql is not null)
            {
                builder.Append(" SET DEFAULT (").Append(operation.DefaultValueSql).Append(")");
            }
            else if (operation.DefaultValue is not null)
            {
                builder.Append(" SET DEFAULT ");
                DefaultValue(operation.DefaultValue, columnType, operation.Name, builder);
            }
            else
            {
                builder.Append(" DROP DEFAULT");
            }

            EndStatement(builder);
        }

        if (!string.Equals(operation.Comment, operation.OldColumn.Comment, StringComparison.Ordinal))
        {
            builder
                .Append("COMMENT ON COLUMN ").Append(table).Append(".").Append(column)
                .Append(" IS ")
                .Append(operation.Comment is null ? "NULL" : $"'{EscapeLiteral(operation.Comment)}'");
            EndStatement(builder);
        }
    }

    protected override void Generate(
        RenameColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" RENAME COLUMN ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    protected override void Generate(
        RenameIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Append("ALTER INDEX ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
            .Append(" RENAME TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    protected override void Generate(
        DropIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Append("DROP INDEX ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        if (terminate)
        {
            EndStatement(builder);
        }
    }

    protected override void Generate(
        RenameTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        var helper = Dependencies.SqlGenerationHelper;
        var name = operation.Name;
        if (operation.NewName is not null && !string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Name, operation.Schema))
                .Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
            name = operation.NewName;
        }

        if (operation.NewSchema is not null
            && !string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ").Append(helper.DelimitIdentifier(name, operation.Schema))
                .Append(" SET SCHEMA ").Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
        }
    }

    protected override void Generate(
        RenameSequenceOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        var helper = Dependencies.SqlGenerationHelper;
        var name = operation.Name;
        if (operation.NewName is not null && !string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER SEQUENCE ").Append(helper.DelimitIdentifier(operation.Name, operation.Schema))
                .Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
            name = operation.NewName;
        }

        if (operation.NewSchema is not null
            && !string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER SEQUENCE ").Append(helper.DelimitIdentifier(name, operation.Schema))
                .Append(" SET SCHEMA ").Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
        }
    }

    protected override void ColumnDefinition(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        base.ColumnDefinition(schema, table, name, operation, model, builder);

        if (IsIdentityColumn(schema, table, name, operation, model))
        {
            builder.Append(" GENERATED BY DEFAULT AS IDENTITY");
        }
    }

    private static bool IsIdentityColumn(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model)
    {
        if (operation.DefaultValue is not null
            || operation.DefaultValueSql is not null
            || operation.ComputedColumnSql is not null)
        {
            return false;
        }

        var clrType = Nullable.GetUnderlyingType(operation.ClrType) ?? operation.ClrType;
        if (clrType != typeof(short) && clrType != typeof(int) && clrType != typeof(long))
        {
            return false;
        }

        return model?.GetRelationalModel()
            .FindTable(table, schema)
            ?.FindColumn(name)
            ?.PropertyMappings
            .Any(mapping =>
                mapping.Property.ValueGenerated == ValueGenerated.OnAdd
                && mapping.Property.IsPrimaryKey()) == true;
    }

    private void EndStatement(MigrationCommandListBuilder builder)
        => builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator).EndCommand();

    private static string EscapeLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
