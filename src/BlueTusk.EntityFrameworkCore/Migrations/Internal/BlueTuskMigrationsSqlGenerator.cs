using System.Text;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
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
            case CreateBlueTuskPartitionOperation createPartition:
                Generate(createPartition, builder);
                break;
            case DropBlueTuskPartitionOperation dropPartition:
                Generate(dropPartition, builder);
                break;
            case AlterBlueTuskPartitionOperation alterPartition:
                Generate(alterPartition, builder);
                break;
            case AttachBlueTuskPartitionOperation attachPartition:
                Generate(attachPartition, builder);
                break;
            case DetachBlueTuskPartitionOperation detachPartition:
                Generate(detachPartition, builder);
                break;
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
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .Append("CREATE TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
            .AppendLine(" (");
        using (builder.Indent())
        {
            CreateTableColumns(operation, model, builder);
            CreateTableConstraints(operation, model, builder);
            builder.AppendLine();
        }

        builder.Append(")");
        if (operation[BlueTuskPartitionMetadata.AnnotationName] is string serializedDefinition)
        {
            AppendPartitioningClause(builder, BlueTuskPartitionMetadata.Deserialize(serializedDefinition));
        }

        if (terminate)
        {
            EndStatement(builder);
        }
    }

    protected override void Generate(
        AlterTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        var current = operation[BlueTuskPartitionMetadata.AnnotationName] as string;
        var previous = operation.OldTable[BlueTuskPartitionMetadata.AnnotationName] as string;
        if (!string.Equals(current, previous, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"PostgreSQL cannot change partition strategy or keys for table '{operation.Schema}.{operation.Name}' in place. " +
                "Create an explicit data-preserving replacement migration.");
        }

        base.Generate(operation, model, builder);
    }

    protected override void Generate(
        CreateIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        var helper = Dependencies.SqlGenerationHelper;
        var concurrently = operation[BlueTuskIndexAnnotations.IsConcurrent] as bool? == true;
        builder.Append("CREATE ");
        if (operation.IsUnique)
        {
            builder.Append("UNIQUE ");
        }

        builder.Append("INDEX ");
        if (concurrently)
        {
            builder.Append("CONCURRENTLY ");
        }

        builder
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema));

        if (operation[BlueTuskIndexAnnotations.Method] is string method)
        {
            builder.Append(" USING ");
            AppendQualifiedIdentifier(builder, method);
        }

        var operatorClasses = operation[BlueTuskIndexAnnotations.OperatorClasses] as string[];
        var collations = operation[BlueTuskIndexAnnotations.Collations] as string[];
        var nullSortOrders = operation[BlueTuskIndexAnnotations.NullSortOrders] as int[];
        var expressions = operation[BlueTuskIndexAnnotations.Expressions] as string[];
        builder.Append(" (");
        for (var index = 0; index < operation.Columns.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            if (expressions is { Length: > 0 } &&
                index < expressions.Length &&
                !string.IsNullOrEmpty(expressions[index]))
            {
                builder.Append("(").Append(expressions[index]).Append(")");
            }
            else
            {
                builder.Append(helper.DelimitIdentifier(operation.Columns[index]));
            }

            if (collations is { Length: > 0 } &&
                index < collations.Length &&
                !string.IsNullOrWhiteSpace(collations[index]))
            {
                builder.Append(" COLLATE ");
                AppendQualifiedIdentifier(builder, collations[index]);
            }

            if (operatorClasses is { Length: > 0 } &&
                index < operatorClasses.Length &&
                !string.IsNullOrWhiteSpace(operatorClasses[index]))
            {
                builder.Append(" ");
                AppendQualifiedIdentifier(builder, operatorClasses[index]);
            }

            if (IsDescending(operation, index))
            {
                builder.Append(" DESC");
            }

            if (nullSortOrders is { Length: > 0 } && index < nullSortOrders.Length)
            {
                builder.Append(nullSortOrders[index] switch
                {
                    (int)BlueTuskIndexNullSortOrder.Default => string.Empty,
                    (int)BlueTuskIndexNullSortOrder.NullsFirst => " NULLS FIRST",
                    (int)BlueTuskIndexNullSortOrder.NullsLast => " NULLS LAST",
                    _ => throw new InvalidOperationException(
                        $"Index '{operation.Name}' has an invalid null sort order at key {index}."),
                });
            }
        }

        builder.Append(")");

        if (operation[BlueTuskIndexAnnotations.IncludeProperties] is string[] includeProperties &&
            includeProperties.Length > 0)
        {
            builder.Append(" INCLUDE (");
            for (var index = 0; index < includeProperties.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(helper.DelimitIdentifier(includeProperties[index]));
            }

            builder.Append(")");
        }

        if (operation[BlueTuskIndexAnnotations.NullsDistinct] is bool nullsDistinct)
        {
            if (!operation.IsUnique)
            {
                throw new InvalidOperationException(
                    $"Index '{operation.Name}' configures null distinctness but is not unique.");
            }

            builder.Append(nullsDistinct ? " NULLS DISTINCT" : " NULLS NOT DISTINCT");
        }

        if (operation[BlueTuskIndexAnnotations.StorageParameters] is string serializedParameters)
        {
            var parameters = BlueTuskIndexAnnotations.DeserializeStorageParameters(serializedParameters);
            if (parameters.Count > 0)
            {
                builder.Append(" WITH (");
                var index = 0;
                foreach (var (name, value) in parameters)
                {
                    if (index++ > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(name).Append(" = ").Append(value);
                }

                builder.Append(")");
            }
        }

        if (!string.IsNullOrWhiteSpace(operation.Filter))
        {
            builder.Append(" WHERE ").Append(operation.Filter);
        }

        if (terminate)
        {
            EndIndexStatement(builder, concurrently);
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
            .Append(operation[BlueTuskIndexAnnotations.IsConcurrent] as bool? == true ? "CONCURRENTLY " : string.Empty)
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        if (terminate)
        {
            EndIndexStatement(
                builder,
                operation[BlueTuskIndexAnnotations.IsConcurrent] as bool? == true);
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

    private void EndIndexStatement(MigrationCommandListBuilder builder, bool suppressTransaction)
        => builder
            .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator)
            .EndCommand(suppressTransaction: suppressTransaction);

    private static bool IsDescending(CreateIndexOperation operation, int index) =>
        operation.IsDescending is { Length: 0 } ||
        operation.IsDescending is { Length: > 0 } values && index < values.Length && values[index];

    private void AppendQualifiedIdentifier(MigrationCommandListBuilder builder, string identifier)
    {
        var parts = identifier.Split('.');
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(".");
            }

            builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(parts[index]));
        }
    }

    private void Generate(
        CreateBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.ParentName);
        ArgumentNullException.ThrowIfNull(operation.Definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Definition.Name);
        builder
            .Append("CREATE TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.Definition.Name,
                operation.Definition.Schema))
            .Append(" PARTITION OF ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.ParentName,
                operation.ParentSchema))
            .Append(" ");
        AppendPartitionBound(builder, operation.Definition.Bound);
        if (operation.Definition.Partitioning is { } subpartitioning)
        {
            AppendPartitioningClause(builder, subpartitioning);
        }

        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder
            .Append("DROP TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        var currentName = operation.Name;
        var currentSchema = operation.Schema;
        if (!string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ")
                .Append(helper.DelimitIdentifier(currentName, currentSchema))
                .Append(" RENAME TO ")
                .Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
            currentName = operation.NewName;
        }

        if (operation.NewSchema is not null &&
            !string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ")
                .Append(helper.DelimitIdentifier(currentName, currentSchema))
                .Append(" SET SCHEMA ")
                .Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
        }
    }

    private void Generate(
        AttachBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.ParentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.PartitionName);
        ArgumentNullException.ThrowIfNull(operation.Bound);
        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.ParentName,
                operation.ParentSchema))
            .Append(" ATTACH PARTITION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.PartitionName,
                operation.PartitionSchema))
            .Append(" ");
        AppendPartitionBound(builder, operation.Bound);
        EndStatement(builder);
    }

    private void Generate(
        DetachBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.ParentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.PartitionName);
        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.ParentName,
                operation.ParentSchema))
            .Append(" DETACH PARTITION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.PartitionName,
                operation.PartitionSchema));
        builder.Append(operation.Mode switch
        {
            BlueTuskPartitionDetachMode.Normal => string.Empty,
            BlueTuskPartitionDetachMode.Concurrently => " CONCURRENTLY",
            BlueTuskPartitionDetachMode.Finalize => " FINALIZE",
            _ => throw new InvalidOperationException($"Unknown partition detach mode '{operation.Mode}'."),
        });
        if (operation.Mode == BlueTuskPartitionDetachMode.Concurrently)
        {
            EndIndexStatement(builder, suppressTransaction: true);
        }
        else
        {
            EndStatement(builder);
        }
    }

    private void AppendPartitioningClause(
        MigrationCommandListBuilder builder,
        BlueTuskPartitioningDefinition definition)
    {
        BlueTuskPartitioningBuilder.ValidateDefinition(definition);
        builder.Append(" PARTITION BY ").Append(definition.Strategy switch
        {
            BlueTuskPartitionStrategy.Range => "RANGE",
            BlueTuskPartitionStrategy.List => "LIST",
            BlueTuskPartitionStrategy.Hash => "HASH",
            _ => throw new InvalidOperationException($"Unknown partition strategy '{definition.Strategy}'."),
        }).Append(" (");
        if (!string.IsNullOrWhiteSpace(definition.KeySql))
        {
            builder.Append(definition.KeySql);
        }
        else
        {
            for (var index = 0; index < definition.Keys.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                var key = definition.Keys[index];
                if (key.IsColumn)
                {
                    builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(key.Expression));
                }
                else
                {
                    builder.Append("(").Append(key.Expression).Append(")");
                }

                if (key.Collation is not null)
                {
                    builder.Append(" COLLATE ");
                    AppendQualifiedIdentifier(builder, key.Collation);
                }

                if (key.OperatorClass is not null)
                {
                    builder.Append(" ");
                    AppendQualifiedIdentifier(builder, key.OperatorClass);
                }
            }
        }

        builder.Append(")");
    }

    private static void AppendPartitionBound(
        MigrationCommandListBuilder builder,
        BlueTuskPartitionBound bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        switch (bound.Kind)
        {
            case BlueTuskPartitionBoundKind.Range:
                builder.Append("FOR VALUES FROM (")
                    .Append(string.Join(", ", bound.From))
                    .Append(") TO (")
                    .Append(string.Join(", ", bound.To))
                    .Append(")");
                break;
            case BlueTuskPartitionBoundKind.List:
                builder.Append("FOR VALUES IN (");
                for (var index = 0; index < bound.Values.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(", ");
                    }

                    var tuple = bound.Values[index];
                    builder.Append(tuple.Length == 1 ? tuple[0] : $"({string.Join(", ", tuple)})");
                }

                builder.Append(")");
                break;
            case BlueTuskPartitionBoundKind.Hash:
                builder.Append("FOR VALUES WITH (MODULUS ")
                    .Append(bound.Modulus.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(", REMAINDER ")
                    .Append(bound.Remainder.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(")");
                break;
            case BlueTuskPartitionBoundKind.Default:
                builder.Append("DEFAULT");
                break;
            case BlueTuskPartitionBoundKind.Sql:
                builder.Append(bound.Sql!);
                break;
            default:
                throw new InvalidOperationException($"Unknown partition bound kind '{bound.Kind}'.");
        }
    }

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
