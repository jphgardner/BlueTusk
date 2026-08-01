using System.Text;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal sealed class BlueTuskMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies)
    : MigrationsSqlGenerator(dependencies)
{
    protected override void Generate(
        MigrationOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        switch (operation)
        {
            case CreateBlueTuskPropertyGraphOperation create:
                Generate(create, builder);
                break;
            case DropBlueTuskPropertyGraphOperation drop:
                Generate(drop, builder);
                break;
            case AlterBlueTuskPropertyGraphOperation alter:
                Generate(alter, builder);
                break;
            default:
                base.Generate(operation, model, builder);
                break;
        }
    }

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

    protected override void Generate(
        EnsureSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .Append("CREATE SCHEMA IF NOT EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder);
    }

    protected override void Generate(
        DropSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .Append("DROP SCHEMA ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder);
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

    private void Generate(
        CreateBlueTuskPropertyGraphOperation operation,
        MigrationCommandListBuilder builder)
    {
        if (operation.Definition is null)
        {
            throw new InvalidOperationException("A property-graph create operation requires a definition.");
        }

        GenerateCapabilityGuarded([BuildCreateSql(operation.Definition)], builder);
    }

    private void Generate(
        DropBlueTuskPropertyGraphOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        GenerateCapabilityGuarded(
            [$"DROP PROPERTY GRAPH {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema)}"],
            builder);
    }

    private void Generate(
        AlterBlueTuskPropertyGraphOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        var statements = new List<string>();
        var currentName = operation.Name;
        var currentSchema = operation.Schema;
        if (!string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            if (operation.NewSchema is null)
            {
                throw new InvalidOperationException(
                    "A property graph cannot be moved to an unspecified schema.");
            }

            statements.Add(
                $"ALTER PROPERTY GRAPH {helper.DelimitIdentifier(currentName, currentSchema)} " +
                $"SET SCHEMA {helper.DelimitIdentifier(operation.NewSchema)}");
            currentSchema = operation.NewSchema;
        }

        if (!string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            statements.Add(
                $"ALTER PROPERTY GRAPH {helper.DelimitIdentifier(currentName, currentSchema)} " +
                $"RENAME TO {helper.DelimitIdentifier(operation.NewName)}");
        }

        if (statements.Count == 0)
        {
            throw new InvalidOperationException("A property-graph alter operation must change its name or schema.");
        }

        GenerateCapabilityGuarded(statements, builder);
    }

    private string BuildCreateSql(BlueTuskPropertyGraphDefinition graph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graph.Name);
        if (graph.ElementTables.Count == 0)
        {
            throw new InvalidOperationException(
                $"Property graph '{graph.Name}' must contain at least one element table.");
        }

        var helper = Dependencies.SqlGenerationHelper;
        var sql = new StringBuilder()
            .Append("CREATE PROPERTY GRAPH ")
            .Append(helper.DelimitIdentifier(graph.Name, graph.Schema));
        AppendElementGroup(
            sql,
            "VERTEX",
            graph.ElementTables.Where(element => element.Kind == BlueTuskGraphElementKind.Vertex));
        AppendElementGroup(
            sql,
            "EDGE",
            graph.ElementTables.Where(element => element.Kind == BlueTuskGraphElementKind.Edge));
        return sql.ToString();
    }

    private void AppendElementGroup(
        StringBuilder sql,
        string keyword,
        IEnumerable<BlueTuskGraphElementTableDefinition> elements)
    {
        var materialized = elements.ToArray();
        if (materialized.Length == 0)
        {
            return;
        }

        sql.Append(' ').Append(keyword).Append(" TABLES (");
        for (var index = 0; index < materialized.Length; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            AppendElement(sql, materialized[index]);
        }

        sql.Append(')');
    }

    private void AppendElement(StringBuilder sql, BlueTuskGraphElementTableDefinition element)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(element.Alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(element.Table);
        var helper = Dependencies.SqlGenerationHelper;
        sql.Append(helper.DelimitIdentifier(element.Table, element.Schema))
            .Append(" AS ")
            .Append(helper.DelimitIdentifier(element.Alias));
        AppendColumns(sql, " KEY", element.KeyColumns);

        if (element.Kind == BlueTuskGraphElementKind.Edge)
        {
            AppendEndpoint(sql, " SOURCE", element.Source, element.Alias);
            AppendEndpoint(sql, " DESTINATION", element.Destination, element.Alias);
        }

        foreach (var label in element.Labels)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(label.Name);
            sql.Append(" LABEL ").Append(helper.DelimitIdentifier(label.Name));
            if (label.Properties.Count > 0)
            {
                sql.Append(" PROPERTIES (");
                for (var index = 0; index < label.Properties.Count; index++)
                {
                    if (index > 0)
                    {
                        sql.Append(", ");
                    }

                    var property = label.Properties[index];
                    ArgumentException.ThrowIfNullOrWhiteSpace(property.Expression);
                    ArgumentException.ThrowIfNullOrWhiteSpace(property.Name);
                    sql.Append(property.IsColumn
                            ? helper.DelimitIdentifier(property.Expression)
                            : property.Expression)
                        .Append(" AS ")
                        .Append(helper.DelimitIdentifier(property.Name));
                }

                sql.Append(')');
            }
        }
    }

    private void AppendEndpoint(
        StringBuilder sql,
        string keyword,
        BlueTuskGraphEndpointDefinition? endpoint,
        string edgeAlias)
    {
        if (endpoint is null)
        {
            throw new InvalidOperationException(
                $"Edge table '{edgeAlias}' requires both source and destination endpoints.");
        }

        if (endpoint.EdgeKeyColumns.Count == 0 ||
            endpoint.EdgeKeyColumns.Count != endpoint.VertexKeyColumns.Count)
        {
            throw new InvalidOperationException(
                $"Edge table '{edgeAlias}' endpoint key columns must be non-empty and have matching counts.");
        }

        sql.Append(keyword);
        AppendColumns(sql, " KEY", endpoint.EdgeKeyColumns);
        sql.Append(" REFERENCES ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(endpoint.VertexTableAlias));
        AppendColumns(sql, string.Empty, endpoint.VertexKeyColumns);
    }

    private void AppendColumns(StringBuilder sql, string prefix, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            return;
        }

        sql.Append(prefix).Append(" (");
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columns[index]));
        }

        sql.Append(')');
    }

    private void GenerateCapabilityGuarded(
        IReadOnlyList<string> statements,
        MigrationCommandListBuilder builder)
    {
        var delimiter = "$BlueTuskGraph$";
        while (statements.Any(statement => statement.Contains(delimiter, StringComparison.Ordinal)))
        {
            delimiter = delimiter.Insert(delimiter.Length - 1, "_");
        }

        builder
            .Append("DO ").AppendLine(delimiter)
            .AppendLine("BEGIN")
            .AppendLine("    IF current_setting('server_version_num')::integer < 190000")
            .AppendLine("       OR pg_catalog.to_regclass('information_schema.property_graphs') IS NULL THEN")
            .AppendLine("        RAISE EXCEPTION USING")
            .AppendLine("            ERRCODE = '0A000',")
            .AppendLine("            MESSAGE = 'BlueTusk property-graph migrations require PostgreSQL 19 with SQL/PGQ support.';")
            .AppendLine("    END IF;");
        foreach (var statement in statements)
        {
            builder.Append("    EXECUTE '")
                .Append(EscapeLiteral(statement))
                .AppendLine("';");
        }

        builder.AppendLine("END;")
            .Append(delimiter);
        EndStatement(builder);
    }

    private static string EscapeLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
