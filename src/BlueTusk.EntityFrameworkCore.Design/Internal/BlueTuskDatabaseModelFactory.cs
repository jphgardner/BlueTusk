using System.Data;
using System.Data.Common;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Data.Schema;
using BlueTusk.EntityFrameworkCore.Collations;
using BlueTusk.EntityFrameworkCore.Collations.Internal;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Extensions;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using BlueTusk.EntityFrameworkCore.Partitioning;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using BlueTusk.EntityFrameworkCore.Publications;
using BlueTusk.EntityFrameworkCore.Publications.Internal;
using BlueTusk.EntityFrameworkCore.Routines;
using BlueTusk.EntityFrameworkCore.Routines.Internal;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using BlueTusk.EntityFrameworkCore.Rules;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using BlueTusk.EntityFrameworkCore.Subscriptions;
using BlueTusk.EntityFrameworkCore.Subscriptions.Internal;
using BlueTusk.EntityFrameworkCore.TableInheritance;
using BlueTusk.EntityFrameworkCore.TableInheritance.Internal;
using BlueTusk.EntityFrameworkCore.Triggers;
using BlueTusk.EntityFrameworkCore.Triggers.Internal;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
using BlueTusk.EntityFrameworkCore.Views;
using BlueTusk.EntityFrameworkCore.Views.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;

#pragma warning disable EF1001 // Provider design-time code consumes provider infrastructure metadata.

namespace BlueTusk.EntityFrameworkCore.Design.Internal;

public sealed class BlueTuskDatabaseModelFactory : DatabaseModelFactory
{
    public override DatabaseModel Create(
        string connectionString,
        DatabaseModelFactoryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        using var connection = new BlueTuskConnection(connectionString);
        return Create(connection, options);
    }

    public override DatabaseModel Create(
        DbConnection connection,
        DatabaseModelFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        var closeConnection = connection.State == ConnectionState.Closed;
        if (closeConnection)
        {
            connection.Open();
        }

        try
        {
            return CreateModel(connection, options);
        }
        finally
        {
            if (closeConnection)
            {
                connection.Close();
            }
        }
    }

    private static DatabaseModel CreateModel(
        DbConnection connection,
        DatabaseModelFactoryOptions options)
    {
        var model = ReadDatabase(connection);
        var selection = new Selection(options);
        var tables = ReadTables(connection, model, selection);
        ReadColumns(connection, tables);
        ReadConstraints(connection, tables);
        ReadExclusionConstraints(connection, tables);
        ReadTriggers(connection, tables);
        ReadRules(connection, tables);
        ReadPublications(connection, model);
        ReadSubscriptions(connection, model);
        ReadIndexes(connection, tables);
        ReadForeignKeys(connection, tables);
        ReadTableInheritance(connection, tables);
        ReadRowLevelSecurity(connection, tables);
        ReadPartitioning(connection, tables);
        ReadSequences(connection, model, selection);
        ReadExtensions(connection, model, selection);
        ReadCollations(connection, model, selection);
        ReadUserDefinedTypes(connection, model, selection);
        ReadRoutines(connection, model, selection);
        ReadViews(connection, model, tables, selection);
        ReadPropertyGraphs(connection, model, selection);
        return model;
    }

    private static DatabaseModel ReadDatabase(DbConnection connection)
    {
        const string sql = """
            SELECT current_database(), current_schema(), d.datcollate
            FROM pg_catalog.pg_database AS d
            WHERE d.datname = current_database()
            """;
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("PostgreSQL did not return current database metadata.");
        }

        return new DatabaseModel
        {
            DatabaseName = reader.GetString(0),
            DefaultSchema = GetNullableString(reader, 1),
            Collation = GetNullableString(reader, 2),
        };
    }

    private static Dictionary<(string Schema, string Name), DatabaseTable> ReadTables(
        DbConnection connection,
        DatabaseModel model,
        Selection selection)
    {
        const string sql = """
            SELECT n.nspname,
                   c.relname,
                   c.relkind::text,
                   pg_catalog.obj_description(c.oid, 'pg_class')
            FROM pg_catalog.pg_class AS c
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm')
              AND NOT c.relispartition
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname !~ '^pg_toast'
            ORDER BY n.nspname, c.relname
            """;

        var tables = new Dictionary<(string Schema, string Name), DatabaseTable>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            if (!selection.IncludesTable(schema, name))
            {
                continue;
            }

            var relationKind = reader.GetString(2);
            DatabaseTable table = relationKind is "v" or "m"
                ? new DatabaseView()
                : new DatabaseTable();
            table.Database = model;
            table.Schema = schema;
            table.Name = name;
            table.Comment = GetNullableString(reader, 3);
            if (relationKind == "m")
            {
                table["BlueTusk:MaterializedView"] = true;
            }
            else if (relationKind == "p")
            {
                table["BlueTusk:PartitionedTable"] = true;
            }

            model.Tables.Add(table);
            tables.Add((schema, name), table);
        }

        return tables;
    }

    private static void ReadTableInheritance(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT child_namespace.nspname,
                   child.relname,
                   parent_namespace.nspname,
                   parent.relname
            FROM pg_catalog.pg_inherits AS inheritance
            JOIN pg_catalog.pg_class AS child ON child.oid = inheritance.inhrelid
            JOIN pg_catalog.pg_namespace AS child_namespace ON child_namespace.oid = child.relnamespace
            JOIN pg_catalog.pg_class AS parent ON parent.oid = inheritance.inhparent
            JOIN pg_catalog.pg_namespace AS parent_namespace ON parent_namespace.oid = parent.relnamespace
            WHERE NOT child.relispartition
              AND child.relkind IN ('r', 'p')
              AND parent.relkind IN ('r', 'p')
              AND child_namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND child_namespace.nspname !~ '^pg_toast'
            ORDER BY child_namespace.nspname, child.relname, inheritance.inhseqno
            """;

        var definitions = new Dictionary<
            (string Schema, string Name),
            List<BlueTuskInheritedTableDefinition>>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var child = (reader.GetString(0), reader.GetString(1));
            if (!tables.ContainsKey(child))
            {
                continue;
            }

            if (!definitions.TryGetValue(child, out var parents))
            {
                parents = [];
                definitions.Add(child, parents);
            }

            parents.Add(new BlueTuskInheritedTableDefinition(reader.GetString(3), reader.GetString(2)));
        }

        foreach (var (child, parents) in definitions)
        {
            var definition = new BlueTuskTableInheritanceDefinition(parents);
            BlueTuskTableInheritanceMetadata.Validate(definition, child.Name, child.Schema);
            tables[child][BlueTuskTableInheritanceMetadata.AnnotationName] =
                BlueTuskTableInheritanceMetadata.Serialize(definition);
        }
    }

    private static void ReadPartitioning(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT parent_namespace.nspname,
                   parent.relname,
                   child_namespace.nspname,
                   child.relname,
                   pg_catalog.pg_get_partkeydef(parent.oid),
                   pg_catalog.pg_get_expr(child.relpartbound, child.oid, true)
            FROM pg_catalog.pg_class AS parent
            JOIN pg_catalog.pg_namespace AS parent_namespace ON parent_namespace.oid = parent.relnamespace
            LEFT JOIN pg_catalog.pg_inherits AS inheritance ON inheritance.inhparent = parent.oid
            LEFT JOIN pg_catalog.pg_class AS child
                ON child.oid = inheritance.inhrelid AND child.relispartition
            LEFT JOIN pg_catalog.pg_namespace AS child_namespace ON child_namespace.oid = child.relnamespace
            WHERE parent.relkind = 'p'
              AND parent_namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND parent_namespace.nspname !~ '^pg_toast'
            ORDER BY parent_namespace.nspname, parent.relname, child_namespace.nspname, child.relname
            """;

        var relationships = new List<PartitionRelationship>();
        var keys = new Dictionary<(string Schema, string Name), PartitionKey>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var parent = (Schema: reader.GetString(0), Name: reader.GetString(1));
            var key = ParsePartitionKey(reader.GetString(4));
            if (keys.TryGetValue(parent, out var existing) && existing != key)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL returned inconsistent partition keys for '{parent.Schema}.{parent.Name}'.");
            }

            keys[parent] = key;
            if (!reader.IsDBNull(2))
            {
                relationships.Add(new PartitionRelationship(
                    parent,
                    (reader.GetString(2), reader.GetString(3)),
                    reader.GetString(5)));
            }
        }

        var childrenByParent = relationships
            .GroupBy(relationship => relationship.Parent)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var (root, table) in tables)
        {
            if (!keys.TryGetValue(root, out var key))
            {
                continue;
            }

            table[BlueTuskPartitionMetadata.AnnotationName] = BlueTuskPartitionMetadata.Serialize(
                BuildPartitioning(root, key, keys, childrenByParent));
        }
    }

    private static void ReadRowLevelSecurity(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT namespace.nspname,
                   relation.relname,
                   relation.relrowsecurity,
                   relation.relforcerowsecurity,
                   policy.policyname,
                   policy.permissive,
                   policy.roles,
                   policy.cmd,
                   policy.qual,
                   policy.with_check
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            LEFT JOIN pg_catalog.pg_policies AS policy
              ON policy.schemaname = namespace.nspname
             AND policy.tablename = relation.relname
            WHERE relation.relkind IN ('r', 'p')
              AND NOT relation.relispartition
              AND namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND namespace.nspname !~ '^pg_toast'
            ORDER BY namespace.nspname, relation.relname, policy.policyname
            """;

        var definitions = new Dictionary<
            (string Schema, string Name),
            (bool Enabled, bool Forced, List<BlueTuskRowSecurityPolicyDefinition> Policies)>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!tables.ContainsKey(key))
            {
                continue;
            }

            if (!definitions.TryGetValue(key, out var definition))
            {
                definition = (reader.GetBoolean(2), reader.GetBoolean(3), []);
                definitions.Add(key, definition);
            }

            if (reader.IsDBNull(4))
            {
                continue;
            }

            definition.Policies.Add(new BlueTuskRowSecurityPolicyDefinition(
                reader.GetString(4),
                reader.GetString(5).Equals("PERMISSIVE", StringComparison.OrdinalIgnoreCase)
                    ? BlueTuskRowSecurityPolicyBehavior.Permissive
                    : BlueTuskRowSecurityPolicyBehavior.Restrictive,
                ParsePolicyCommand(reader.GetString(7)),
                ParsePostgreSqlArray(reader.GetValue(6))
                    .Select(role => role.Equals("public", StringComparison.OrdinalIgnoreCase)
                        ? BlueTuskRowSecurityRoleDefinition.Public
                        : BlueTuskRowSecurityRoleDefinition.Named(role))
                    .ToArray(),
                GetNullableString(reader, 8),
                GetNullableString(reader, 9)));
        }

        foreach (var (key, definition) in definitions)
        {
            if (!definition.Enabled && !definition.Forced && definition.Policies.Count == 0)
            {
                continue;
            }

            var rowLevelSecurity = new BlueTuskRowLevelSecurityDefinition(
                definition.Enabled,
                definition.Forced,
                definition.Policies);
            BlueTuskRowLevelSecurityBuilder.ValidateDefinition(rowLevelSecurity);
            tables[key][BlueTuskRowLevelSecurityMetadata.AnnotationName] =
                BlueTuskRowLevelSecurityMetadata.Serialize(rowLevelSecurity);
        }
    }

    private static BlueTuskRowSecurityPolicyCommand ParsePolicyCommand(string command) =>
        command.ToUpperInvariant() switch
        {
            "ALL" => BlueTuskRowSecurityPolicyCommand.All,
            "SELECT" => BlueTuskRowSecurityPolicyCommand.Select,
            "INSERT" => BlueTuskRowSecurityPolicyCommand.Insert,
            "UPDATE" => BlueTuskRowSecurityPolicyCommand.Update,
            "DELETE" => BlueTuskRowSecurityPolicyCommand.Delete,
            var value => throw new InvalidOperationException(
                $"PostgreSQL returned an unknown row-security policy command '{value}'."),
        };

    private static BlueTuskPartitioningDefinition BuildPartitioning(
        (string Schema, string Name) table,
        PartitionKey key,
        IReadOnlyDictionary<(string Schema, string Name), PartitionKey> keys,
        IReadOnlyDictionary<(string Schema, string Name), PartitionRelationship[]> childrenByParent)
    {
        var partitions = childrenByParent.TryGetValue(table, out var children)
            ? children.Select(child => new BlueTuskPartitionDefinition(
                    child.Child.Name,
                    child.Child.Schema,
                    BlueTuskPartitionBound.FromSql(child.BoundSql),
                    keys.TryGetValue(child.Child, out var childKey)
                        ? BuildPartitioning(child.Child, childKey, keys, childrenByParent)
                        : null))
                .OrderBy(partition => partition.Schema, StringComparer.Ordinal)
                .ThenBy(partition => partition.Name, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<BlueTuskPartitionDefinition>();
        return new BlueTuskPartitioningDefinition(key.Strategy, [], partitions, key.Sql);
    }

    private static PartitionKey ParsePartitionKey(string definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
        var openingParenthesis = definition.IndexOf('(');
        var closingParenthesis = definition.LastIndexOf(')');
        if (openingParenthesis <= 0 || closingParenthesis <= openingParenthesis)
        {
            throw new InvalidOperationException($"PostgreSQL returned an invalid partition key: '{definition}'.");
        }

        var strategy = definition[..openingParenthesis].Trim().ToUpperInvariant() switch
        {
            "RANGE" => BlueTuskPartitionStrategy.Range,
            "LIST" => BlueTuskPartitionStrategy.List,
            "HASH" => BlueTuskPartitionStrategy.Hash,
            var value => throw new InvalidOperationException(
                $"PostgreSQL returned an unknown partition strategy '{value}'."),
        };
        return new PartitionKey(
            strategy,
            definition[(openingParenthesis + 1)..closingParenthesis]);
    }

    private static void ReadColumns(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT n.nspname,
                   c.relname,
                   a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull,
                   pg_catalog.pg_get_expr(ad.adbin, ad.adrelid),
                   a.attidentity::text,
                   a.attgenerated::text,
                   pg_catalog.col_description(c.oid, a.attnum),
                   coll.collname
            FROM pg_catalog.pg_attribute AS a
            JOIN pg_catalog.pg_class AS c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_attrdef AS ad
                ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
            LEFT JOIN pg_catalog.pg_collation AS coll ON coll.oid = a.attcollation
            WHERE c.relkind IN ('r', 'p', 'v', 'm')
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY n.nspname, c.relname, a.attnum
            """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!tables.TryGetValue((reader.GetString(0), reader.GetString(1)), out var table))
            {
                continue;
            }

            var defaultExpression = GetNullableString(reader, 5);
            var identity = GetNullableString(reader, 6);
            var generated = GetNullableString(reader, 7);
            var column = new DatabaseColumn
            {
                Table = table,
                Name = reader.GetString(2),
                StoreType = reader.GetString(3),
                IsNullable = reader.GetBoolean(4),
                Comment = GetNullableString(reader, 8),
                Collation = GetNullableString(reader, 9),
            };

            if (!string.IsNullOrEmpty(generated))
            {
                column.ComputedColumnSql = defaultExpression;
                column.IsStored = true;
                column.ValueGenerated = ValueGenerated.OnAddOrUpdate;
            }
            else
            {
                column.DefaultValueSql = defaultExpression;
                if (!string.IsNullOrEmpty(identity)
                    || defaultExpression?.StartsWith("nextval(", StringComparison.OrdinalIgnoreCase) == true)
                {
                    column.ValueGenerated = ValueGenerated.OnAdd;
                }
            }

            table.Columns.Add(column);
        }
    }

    private static void ReadConstraints(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT n.nspname,
                   c.relname,
                   con.conname,
                   con.contype::text,
                   a.attname
            FROM pg_catalog.pg_constraint AS con
            JOIN pg_catalog.pg_class AS c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS key(attnum, position)
            JOIN pg_catalog.pg_attribute AS a
                ON a.attrelid = con.conrelid AND a.attnum = key.attnum
            WHERE con.contype IN ('p', 'u')
            ORDER BY n.nspname, c.relname, con.conname, key.position
            """;

        DatabasePrimaryKey? primaryKey = null;
        DatabaseUniqueConstraint? uniqueConstraint = null;
        string? currentConstraint = null;
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!tables.TryGetValue((reader.GetString(0), reader.GetString(1)), out var table))
            {
                continue;
            }

            var constraintName = reader.GetString(2);
            var constraintType = reader.GetString(3);
            if (!string.Equals(currentConstraint, $"{table.Schema}.{table.Name}.{constraintName}", StringComparison.Ordinal))
            {
                currentConstraint = $"{table.Schema}.{table.Name}.{constraintName}";
                primaryKey = null;
                uniqueConstraint = null;
                if (constraintType == "p")
                {
                    primaryKey = new DatabasePrimaryKey { Table = table, Name = constraintName };
                    table.PrimaryKey = primaryKey;
                }
                else
                {
                    uniqueConstraint = new DatabaseUniqueConstraint { Table = table, Name = constraintName };
                    table.UniqueConstraints.Add(uniqueConstraint);
                }
            }

            var column = FindColumn(table, reader.GetString(4));
            primaryKey?.Columns.Add(column);
            uniqueConstraint?.Columns.Add(column);
        }
    }

    private static void ReadExclusionConstraints(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT namespace.nspname,
                   table_class.relname,
                   constraint_entry.conname,
                   access_method.amname,
                   constraint_entry.condeferrable,
                   constraint_entry.condeferred,
                   pg_catalog.pg_get_expr(index_entry.indpred, index_entry.indrelid, false),
                   CASE WHEN index_class.reltablespace = 0 THEN NULL ELSE tablespace.spcname END,
                   index_class.reloptions,
                   operator_key.position,
                   pg_catalog.pg_get_indexdef(constraint_entry.conindid, operator_key.position::integer, false),
                   operator_namespace.nspname,
                   operator_entry.oprname,
                   ARRAY(
                       SELECT included_attribute.attname
                       FROM unnest(index_entry.indkey) WITH ORDINALITY AS included_key(attnum, position)
                       JOIN pg_catalog.pg_attribute AS included_attribute
                         ON included_attribute.attrelid = index_entry.indrelid
                        AND included_attribute.attnum = included_key.attnum
                       WHERE included_key.position > index_entry.indnkeyatts
                       ORDER BY included_key.position)
            FROM pg_catalog.pg_constraint AS constraint_entry
            JOIN pg_catalog.pg_class AS table_class ON table_class.oid = constraint_entry.conrelid
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = table_class.relnamespace
            JOIN pg_catalog.pg_index AS index_entry ON index_entry.indexrelid = constraint_entry.conindid
            JOIN pg_catalog.pg_class AS index_class ON index_class.oid = constraint_entry.conindid
            JOIN pg_catalog.pg_am AS access_method ON access_method.oid = index_class.relam
            LEFT JOIN pg_catalog.pg_tablespace AS tablespace ON tablespace.oid = index_class.reltablespace
            CROSS JOIN LATERAL unnest(constraint_entry.conexclop) WITH ORDINALITY
              AS operator_key(operator_oid, position)
            JOIN pg_catalog.pg_operator AS operator_entry ON operator_entry.oid = operator_key.operator_oid
            JOIN pg_catalog.pg_namespace AS operator_namespace
              ON operator_namespace.oid = operator_entry.oprnamespace
            WHERE constraint_entry.contype = 'x'
              AND constraint_entry.conparentid = 0
            ORDER BY namespace.nspname,
                     table_class.relname,
                     constraint_entry.conname,
                     operator_key.position
            """;
        var seeds = new Dictionary<
            (string Schema, string Table, string Name),
            ExclusionConstraintSeed>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tableKey = (reader.GetString(0), reader.GetString(1));
            if (!tables.ContainsKey(tableKey))
            {
                continue;
            }

            var key = (tableKey.Item1, tableKey.Item2, reader.GetString(2));
            if (!seeds.TryGetValue(key, out var seed))
            {
                var storageParameters = reader.IsDBNull(8)
                    ? []
                    : ParsePostgreSqlArray(reader.GetValue(8))
                        .Select(value => value.Split('=', 2))
                        .Where(parts => parts.Length == 2)
                        .Select(parts => new BlueTuskExclusionParameterDefinition(parts[0], parts[1]))
                        .ToArray();
                seed = new ExclusionConstraintSeed(
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5),
                    GetNullableString(reader, 6),
                    GetNullableString(reader, 7),
                    ParsePostgreSqlArray(reader.GetValue(13)),
                    storageParameters,
                    []);
                seeds.Add(key, seed);
            }

            seed.Elements.Add(new BlueTuskExclusionElementDefinition(
                reader.GetString(10),
                IsColumn: false,
                IsPreformatted: true,
                reader.GetString(12),
                reader.GetString(11),
                Collation: null,
                CollationSchema: null,
                OperatorClass: null,
                OperatorClassSchema: null,
                OperatorClassParameters: [],
                Descending: false,
                BlueTuskExclusionNullSortOrder.Default));
        }

        foreach (var tableGroup in seeds.GroupBy(item => (item.Key.Schema, item.Key.Table)))
        {
            var definitions = tableGroup.Select(item => new BlueTuskExclusionConstraintDefinition(
                    item.Key.Name,
                    item.Value.IndexMethod,
                    item.Value.Elements,
                    item.Value.IncludedColumns,
                    item.Value.StorageParameters,
                    item.Value.Tablespace,
                    item.Value.PredicateSql,
                    item.Value.IsDeferrable,
                    item.Value.IsInitiallyDeferred))
                .ToArray();
            tables[tableGroup.Key][BlueTuskExclusionConstraintMetadata.AnnotationName] =
                BlueTuskExclusionConstraintMetadata.Serialize(definitions);
        }
    }

    private static void ReadTriggers(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT namespace.nspname,
                   relation.relname,
                   trigger_entry.tgname,
                   pg_catalog.pg_get_triggerdef(trigger_entry.oid, false),
                   trigger_entry.tgenabled::text,
                   (
                       SELECT extension_entry.extname
                       FROM pg_catalog.pg_depend AS extension_dependency
                       JOIN pg_catalog.pg_extension AS extension_entry
                         ON extension_entry.oid = extension_dependency.refobjid
                       WHERE extension_dependency.classid = 'pg_catalog.pg_trigger'::pg_catalog.regclass
                         AND extension_dependency.objid = trigger_entry.oid
                         AND extension_dependency.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass
                         AND extension_dependency.deptype = 'x'
                       LIMIT 1)
            FROM pg_catalog.pg_trigger AS trigger_entry
            JOIN pg_catalog.pg_class AS relation ON relation.oid = trigger_entry.tgrelid
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            WHERE NOT trigger_entry.tgisinternal
              AND trigger_entry.tgparentid = 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_depend AS dependency
                  WHERE dependency.classid = 'pg_catalog.pg_trigger'::pg_catalog.regclass
                    AND dependency.objid = trigger_entry.oid
                    AND dependency.deptype = 'e')
            ORDER BY namespace.nspname, relation.relname, trigger_entry.tgname
            """;
        var definitions = new Dictionary<(string Schema, string Table), List<BlueTuskTriggerDefinition>>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tableKey = (reader.GetString(0), reader.GetString(1));
            if (!tables.ContainsKey(tableKey))
            {
                continue;
            }

            if (!definitions.TryGetValue(tableKey, out var triggers))
            {
                triggers = [];
                definitions.Add(tableKey, triggers);
            }

            triggers.Add(new BlueTuskTriggerDefinition(
                reader.GetString(2),
                BlueTuskTriggerTiming.Before,
                [],
                BlueTuskTriggerOrientation.Statement,
                FunctionName: null,
                FunctionSchema: null,
                Arguments: [],
                WhenSql: null,
                OldTransitionTable: null,
                NewTransitionTable: null,
                IsConstraint: false,
                ReferencedTable: null,
                ReferencedTableSchema: null,
                IsDeferrable: false,
                IsInitiallyDeferred: false,
                reader.GetString(4) switch
                {
                    "O" => BlueTuskTriggerEnabledMode.Origin,
                    "D" => BlueTuskTriggerEnabledMode.Disabled,
                    "R" => BlueTuskTriggerEnabledMode.Replica,
                    "A" => BlueTuskTriggerEnabledMode.Always,
                    var value => throw new InvalidOperationException($"Unknown PostgreSQL trigger mode '{value}'."),
                },
                reader.GetString(3),
                GetNullableString(reader, 5)));
        }

        foreach (var (tableKey, triggers) in definitions)
        {
            tables[tableKey][BlueTuskTriggerMetadata.AnnotationName] = BlueTuskTriggerMetadata.Serialize(triggers);
        }
    }

    private static void ReadRules(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT namespace.nspname,
                   relation.relname,
                   rewrite.rulename,
                   pg_catalog.pg_get_ruledef(rewrite.oid, false),
                   rewrite.ev_enabled::text
            FROM pg_catalog.pg_rewrite AS rewrite
            JOIN pg_catalog.pg_class AS relation ON relation.oid = rewrite.ev_class
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            WHERE rewrite.rulename <> '_RETURN'
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_depend AS dependency
                  WHERE dependency.classid = 'pg_catalog.pg_rewrite'::pg_catalog.regclass
                    AND dependency.objid = rewrite.oid
                    AND dependency.deptype = 'e')
            ORDER BY namespace.nspname, relation.relname, rewrite.rulename
            """;
        var definitions = new Dictionary<(string Schema, string Table), List<BlueTuskRuleDefinition>>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tableKey = (reader.GetString(0), reader.GetString(1));
            if (!tables.ContainsKey(tableKey))
            {
                continue;
            }

            if (!definitions.TryGetValue(tableKey, out var rules))
            {
                rules = [];
                definitions.Add(tableKey, rules);
            }

            rules.Add(new BlueTuskRuleDefinition(
                reader.GetString(2),
                BlueTuskRuleEvent.Insert,
                IsInstead: false,
                ConditionSql: null,
                ActionSql: null,
                reader.GetString(4) switch
                {
                    "O" => BlueTuskRuleEnabledMode.Origin,
                    "D" => BlueTuskRuleEnabledMode.Disabled,
                    "R" => BlueTuskRuleEnabledMode.Replica,
                    "A" => BlueTuskRuleEnabledMode.Always,
                    var value => throw new InvalidOperationException($"Unknown PostgreSQL rule mode '{value}'."),
                },
                reader.GetString(3)));
        }

        foreach (var (tableKey, rules) in definitions)
        {
            tables[tableKey][BlueTuskRuleMetadata.AnnotationName] = BlueTuskRuleMetadata.Serialize(rules);
        }
    }

    private static void ReadPublications(DbConnection connection, DatabaseModel model)
    {
        const string publicationSql = """
            SELECT publication_entry.pubname,
                   publication_entry.puballtables,
                   COALESCE((pg_catalog.to_jsonb(publication_entry)->>'puballsequences')::boolean, false),
                   publication_entry.pubinsert,
                   publication_entry.pubupdate,
                   publication_entry.pubdelete,
                   publication_entry.pubtruncate,
                   publication_entry.pubviaroot,
                   COALESCE(pg_catalog.to_jsonb(publication_entry)->>'pubgencols', 'n')
            FROM pg_catalog.pg_publication AS publication_entry
            WHERE NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_depend AS dependency
                WHERE dependency.classid = 'pg_catalog.pg_publication'::pg_catalog.regclass
                  AND dependency.objid = publication_entry.oid
                  AND dependency.deptype = 'e')
            ORDER BY publication_entry.pubname
            """;
        var publications = new Dictionary<string, BlueTuskPublicationDefinition>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = publicationSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var operations = BlueTuskPublicationOperations.None;
                if (reader.GetBoolean(3))
                {
                    operations |= BlueTuskPublicationOperations.Insert;
                }

                if (reader.GetBoolean(4))
                {
                    operations |= BlueTuskPublicationOperations.Update;
                }

                if (reader.GetBoolean(5))
                {
                    operations |= BlueTuskPublicationOperations.Delete;
                }

                if (reader.GetBoolean(6))
                {
                    operations |= BlueTuskPublicationOperations.Truncate;
                }

                var name = reader.GetString(0);
                publications.Add(name, new BlueTuskPublicationDefinition(
                    name,
                    Tables: [],
                    Schemas: [],
                    reader.GetBoolean(1),
                    reader.GetBoolean(2),
                    operations,
                    reader.GetBoolean(7),
                    reader.GetString(8) switch
                    {
                        "n" => BlueTuskPublicationGeneratedColumns.None,
                        "s" => BlueTuskPublicationGeneratedColumns.Stored,
                        var value => throw new InvalidOperationException(
                            $"Unknown PostgreSQL publication generated-column mode '{value}'."),
                    }));
            }
        }

        if (publications.Count == 0)
        {
            return;
        }

        const string tableSql = """
            SELECT publication_entry.pubname,
                   namespace.nspname,
                   relation.relname,
                   COALESCE((pg_catalog.to_jsonb(publication_relation)->>'prexcept')::boolean, false),
                   CASE WHEN publication_relation.prattrs IS NULL THEN NULL ELSE (
                       SELECT pg_catalog.array_to_json(pg_catalog.array_agg(attribute.attname ORDER BY attribute.attnum))::text
                       FROM pg_catalog.pg_attribute AS attribute
                       WHERE attribute.attrelid = publication_relation.prrelid
                         AND attribute.attnum = ANY (publication_relation.prattrs::smallint[])
                         AND NOT attribute.attisdropped)
                   END,
                   pg_catalog.pg_get_expr(publication_relation.prqual, publication_relation.prrelid, false)
            FROM pg_catalog.pg_publication_rel AS publication_relation
            JOIN pg_catalog.pg_publication AS publication_entry
              ON publication_entry.oid = publication_relation.prpubid
            JOIN pg_catalog.pg_class AS relation ON relation.oid = publication_relation.prrelid
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            ORDER BY publication_entry.pubname, namespace.nspname, relation.relname
            """;
        var tables = publications.Keys.ToDictionary(
            name => name,
            _ => new List<BlueTuskPublicationTableDefinition>(),
            StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = tableSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var publicationName = reader.GetString(0);
                if (!tables.TryGetValue(publicationName, out var publicationTables))
                {
                    continue;
                }

                var columnsJson = GetNullableString(reader, 4);
                publicationTables.Add(new BlueTuskPublicationTableDefinition(
                    reader.GetString(2),
                    reader.GetString(1),
                    IncludeDescendants: false,
                    columnsJson is null
                        ? null
                        : JsonSerializer.Deserialize<string[]>(columnsJson)
                          ?? throw new InvalidOperationException("Publication columns were not a JSON array."),
                    GetNullableString(reader, 5),
                    reader.GetBoolean(3)));
            }
        }

        const string schemaSql = """
            SELECT publication_entry.pubname, namespace.nspname
            FROM pg_catalog.pg_publication_namespace AS publication_namespace
            JOIN pg_catalog.pg_publication AS publication_entry
              ON publication_entry.oid = publication_namespace.pnpubid
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = publication_namespace.pnnspid
            ORDER BY publication_entry.pubname, namespace.nspname
            """;
        var schemas = publications.Keys.ToDictionary(
            name => name,
            _ => new List<string>(),
            StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = schemaSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (schemas.TryGetValue(reader.GetString(0), out var publicationSchemas))
                {
                    publicationSchemas.Add(reader.GetString(1));
                }
            }
        }

        var definitions = new BlueTuskPublicationDefinitionSet(publications.Values.Select(definition =>
            definition with
            {
                Tables = tables[definition.Name],
                Schemas = schemas[definition.Name],
            }).ToArray());
        model[BlueTuskPublicationMetadata.AnnotationName] = BlueTuskPublicationMetadata.Serialize(definitions);
    }

    private static void ReadSubscriptions(DbConnection connection, DatabaseModel model)
    {
        var version = ReadServerVersionNumber(connection);
        var sql = $"""
            SELECT subscription.subname,
                   subscription.subenabled,
                   subscription.subbinary,
                   subscription.substream::text,
                   subscription.subtwophasestate::text,
                   subscription.subdisableonerr,
                   {(version >= 160000 ? "subscription.subpasswordrequired" : "true")},
                   {(version >= 160000 ? "subscription.subrunasowner" : "false")},
                   {(version >= 160000 ? "subscription.suborigin" : "'any'::text")},
                   {(version >= 170000 ? "subscription.subfailover" : "false")},
                   {(version >= 190000 ? "subscription.subretaindeadtuples" : "false")},
                   {(version >= 190000 ? "subscription.submaxretention" : "0")},
                   {(version >= 190000 ? "subscription.subwalrcvtimeout::text" : "'-1'::text")},
                   subscription.subslotname,
                   subscription.subsynccommit,
                   pg_catalog.array_to_json(subscription.subpublications)::text,
                   {(version >= 190000 ? "foreign_server.srvname" : "NULL::text")}
            FROM pg_catalog.pg_subscription AS subscription
            {(version >= 190000 ? "LEFT JOIN pg_catalog.pg_foreign_server AS foreign_server ON foreign_server.oid = subscription.subserver" : string.Empty)}
            WHERE subscription.subdbid = (
                    SELECT database_entry.oid
                    FROM pg_catalog.pg_database AS database_entry
                    WHERE database_entry.datname = current_database())
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_depend AS dependency
                  WHERE dependency.classid = 'pg_catalog.pg_subscription'::pg_catalog.regclass
                    AND dependency.objid = subscription.oid
                    AND dependency.deptype = 'e')
            ORDER BY subscription.subname
            """;
        var definitions = new List<BlueTuskSubscriptionDefinition>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var publications = JsonSerializer.Deserialize<string[]>(reader.GetString(15))
                ?? throw new InvalidOperationException("Subscription publications were not a JSON array.");
            definitions.Add(new BlueTuskSubscriptionDefinition(
                reader.GetString(0),
                reader.IsDBNull(16)
                    ? BlueTuskSubscriptionConnection.Redacted
                    : BlueTuskSubscriptionConnection.FromForeignServer(reader.GetString(16)),
                publications,
                GetNullableString(reader, 13),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetString(3) switch
                {
                    "false" or "f" => BlueTuskSubscriptionStreamingMode.Off,
                    "true" or "t" => BlueTuskSubscriptionStreamingMode.On,
                    "p" => BlueTuskSubscriptionStreamingMode.Parallel,
                    var value => throw new InvalidOperationException(
                        $"Unknown PostgreSQL subscription streaming mode '{value}'."),
                },
                reader.GetString(14) switch
                {
                    "off" => BlueTuskSubscriptionSynchronousCommit.Off,
                    "local" => BlueTuskSubscriptionSynchronousCommit.Local,
                    "remote_write" => BlueTuskSubscriptionSynchronousCommit.RemoteWrite,
                    "on" => BlueTuskSubscriptionSynchronousCommit.On,
                    "remote_apply" => BlueTuskSubscriptionSynchronousCommit.RemoteApply,
                    var value => throw new InvalidOperationException(
                        $"Unknown PostgreSQL subscription synchronous-commit mode '{value}'."),
                },
                reader.GetString(4) != "d",
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetString(8) switch
                {
                    "any" => BlueTuskSubscriptionOrigin.Any,
                    "none" => BlueTuskSubscriptionOrigin.None,
                    var value => throw new InvalidOperationException(
                        $"Unknown PostgreSQL subscription origin '{value}'."),
                },
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.GetInt32(11),
                reader.GetString(12),
                ConnectOnCreate: false,
                CreateSlot: false,
                CopyData: false));
        }

        if (definitions.Count > 0)
        {
            model[BlueTuskSubscriptionMetadata.AnnotationName] = BlueTuskSubscriptionMetadata.Serialize(
                new BlueTuskSubscriptionDefinitionSet(definitions));
        }
    }

    private static int ReadServerVersionNumber(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.current_setting('server_version_num')::integer";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ReadIndexes(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT n.nspname,
                   table_class.relname,
                   index_class.relname,
                   idx.indisunique,
                   access_method.amname,
                   pg_catalog.pg_get_expr(idx.indpred, idx.indrelid),
                   attribute.attname,
                   key.position,
                   idx.indnkeyatts,
                   operator_namespace.nspname,
                   operator_class.opcname,
                   collation_namespace.nspname,
                   index_collation.collname,
                   ((idx.indoption)[key.position - 1] & 1) <> 0,
                   ((idx.indoption)[key.position - 1] & 2) <> 0,
                   idx.indnullsnotdistinct,
                   index_class.reloptions
            FROM pg_catalog.pg_index AS idx
            JOIN pg_catalog.pg_class AS table_class ON table_class.oid = idx.indrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = table_class.relnamespace
            JOIN pg_catalog.pg_class AS index_class ON index_class.oid = idx.indexrelid
            JOIN pg_catalog.pg_am AS access_method ON access_method.oid = index_class.relam
            CROSS JOIN LATERAL unnest(idx.indkey) WITH ORDINALITY AS key(attnum, position)
            LEFT JOIN pg_catalog.pg_attribute AS attribute
                ON attribute.attrelid = idx.indrelid AND attribute.attnum = key.attnum
            LEFT JOIN pg_catalog.pg_opclass AS operator_class
                ON operator_class.oid = (idx.indclass)[key.position - 1]
            LEFT JOIN pg_catalog.pg_namespace AS operator_namespace
                ON operator_namespace.oid = operator_class.opcnamespace
            LEFT JOIN pg_catalog.pg_collation AS index_collation
                ON index_collation.oid = (idx.indcollation)[key.position - 1]
            LEFT JOIN pg_catalog.pg_namespace AS collation_namespace
                ON collation_namespace.oid = index_collation.collnamespace
            WHERE NOT idx.indisprimary
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_constraint AS con
                  WHERE con.conindid = idx.indexrelid)
            ORDER BY n.nspname, table_class.relname, index_class.relname, key.position
            """;

        DatabaseIndex? index = null;
        string? currentIndex = null;
        HashSet<string> skippedExpressionIndexes = new(StringComparer.Ordinal);
        List<string?> operatorClasses = [];
        List<string?> collations = [];
        List<int> nullSortOrders = [];
        List<string> includeColumns = [];
        List<bool> descending = [];
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!tables.TryGetValue((reader.GetString(0), reader.GetString(1)), out var table))
            {
                continue;
            }

            var indexName = reader.GetString(2);
            var indexIdentity = $"{table.Schema}.{table.Name}.{indexName}";
            if (skippedExpressionIndexes.Contains(indexIdentity))
            {
                continue;
            }

            if (!string.Equals(currentIndex, indexIdentity, StringComparison.Ordinal))
            {
                ApplyAdvancedIndexAnnotations(
                    index,
                    operatorClasses,
                    collations,
                    nullSortOrders,
                    includeColumns,
                    descending);
                currentIndex = indexIdentity;
                operatorClasses = [];
                collations = [];
                nullSortOrders = [];
                includeColumns = [];
                descending = [];
                index = new DatabaseIndex
                {
                    Table = table,
                    Name = indexName,
                    IsUnique = reader.GetBoolean(3),
                    Filter = GetNullableString(reader, 5),
                };
                index[BlueTuskIndexAnnotations.Method] = reader.GetString(4);
                if (reader.GetBoolean(3) && reader.GetBoolean(15))
                {
                    index[BlueTuskIndexAnnotations.NullsDistinct] = false;
                }

                if (!reader.IsDBNull(16))
                {
                    var storageParameters = ParsePostgreSqlArray(reader.GetValue(16));
                    index[BlueTuskIndexAnnotations.StorageParameters] =
                        BlueTuskIndexAnnotations.SerializeStorageParameters(
                            storageParameters.Select(value => value.Split('=', 2))
                                .Where(parts => parts.Length == 2)
                                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal));
                }

                table.Indexes.Add(index);
            }

            var position = reader.GetInt64(7);
            var keyCount = reader.GetInt16(8);
            if (position <= keyCount)
            {
                if (reader.IsDBNull(6))
                {
                    table.Indexes.Remove(index!);
                    skippedExpressionIndexes.Add(indexIdentity);
                    index = null;
                    currentIndex = null;
                    continue;
                }

                index!.Columns.Add(FindColumn(table, reader.GetString(6)));
                var operatorClass = reader.IsDBNull(10)
                    ? null
                    : QualifyCatalogueIdentifier(reader.GetString(9), reader.GetString(10), "pg_catalog");
                operatorClasses.Add(operatorClass);
                var collation = reader.IsDBNull(12)
                    ? null
                    : QualifyCatalogueIdentifier(reader.GetString(11), reader.GetString(12), "pg_catalog");
                collations.Add(collation);
                descending.Add(reader.GetBoolean(13));
                nullSortOrders.Add(reader.GetBoolean(14)
                    ? (int)BlueTuskIndexNullSortOrder.NullsFirst
                    : (int)BlueTuskIndexNullSortOrder.NullsLast);
            }
            else if (!reader.IsDBNull(6))
            {
                includeColumns.Add(reader.GetString(6));
            }
        }

        ApplyAdvancedIndexAnnotations(
            index,
            operatorClasses,
            collations,
            nullSortOrders,
            includeColumns,
            descending);
    }

    private static void ApplyAdvancedIndexAnnotations(
        DatabaseIndex? index,
        IReadOnlyList<string?> operatorClasses,
        IReadOnlyList<string?> collations,
        IReadOnlyList<int> nullSortOrders,
        IReadOnlyList<string> includeColumns,
        IReadOnlyList<bool> descending)
    {
        if (index is null)
        {
            return;
        }

        index[BlueTuskIndexAnnotations.OperatorClasses] = operatorClasses.ToArray();
        index[BlueTuskIndexAnnotations.Collations] = collations.ToArray();
        index[BlueTuskIndexAnnotations.NullSortOrders] = nullSortOrders.ToArray();
        index[BlueTuskIndexAnnotations.IncludeProperties] = includeColumns.ToArray();
        index.IsDescending = descending.ToArray();
    }

    private static string QualifyCatalogueIdentifier(string schema, string name, string defaultSchema) =>
        string.Equals(schema, defaultSchema, StringComparison.Ordinal) ? name : $"{schema}.{name}";

    private static string[] ParsePostgreSqlArray(object value)
    {
        if (value is string[] strings)
        {
            return strings;
        }

        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(text) || text == "{}"
            ? Array.Empty<string>()
            : text.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void ReadForeignKeys(
        DbConnection connection,
        Dictionary<(string Schema, string Name), DatabaseTable> tables)
    {
        const string sql = """
            SELECT source_namespace.nspname,
                   source_table.relname,
                   con.conname,
                   target_namespace.nspname,
                   target_table.relname,
                   source_column.attname,
                   target_column.attname,
                   con.confdeltype::text
            FROM pg_catalog.pg_constraint AS con
            JOIN pg_catalog.pg_class AS source_table ON source_table.oid = con.conrelid
            JOIN pg_catalog.pg_namespace AS source_namespace ON source_namespace.oid = source_table.relnamespace
            JOIN pg_catalog.pg_class AS target_table ON target_table.oid = con.confrelid
            JOIN pg_catalog.pg_namespace AS target_namespace ON target_namespace.oid = target_table.relnamespace
            CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS source_key(attnum, position)
            JOIN LATERAL unnest(con.confkey) WITH ORDINALITY AS target_key(attnum, position)
                ON target_key.position = source_key.position
            JOIN pg_catalog.pg_attribute AS source_column
                ON source_column.attrelid = con.conrelid AND source_column.attnum = source_key.attnum
            JOIN pg_catalog.pg_attribute AS target_column
                ON target_column.attrelid = con.confrelid AND target_column.attnum = target_key.attnum
            WHERE con.contype = 'f'
            ORDER BY source_namespace.nspname, source_table.relname, con.conname, source_key.position
            """;

        DatabaseForeignKey? foreignKey = null;
        string? currentForeignKey = null;
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!tables.TryGetValue((reader.GetString(0), reader.GetString(1)), out var table)
                || !tables.TryGetValue((reader.GetString(3), reader.GetString(4)), out var principalTable))
            {
                continue;
            }

            var foreignKeyName = reader.GetString(2);
            var identity = $"{table.Schema}.{table.Name}.{foreignKeyName}";
            if (!string.Equals(currentForeignKey, identity, StringComparison.Ordinal))
            {
                currentForeignKey = identity;
                foreignKey = new DatabaseForeignKey
                {
                    Table = table,
                    PrincipalTable = principalTable,
                    Name = foreignKeyName,
                    OnDelete = MapReferentialAction(reader.GetString(7)),
                };
                table.ForeignKeys.Add(foreignKey);
            }

            foreignKey!.Columns.Add(FindColumn(table, reader.GetString(5)));
            foreignKey.PrincipalColumns.Add(FindColumn(principalTable, reader.GetString(6)));
        }
    }

    private static void ReadSequences(
        DbConnection connection,
        DatabaseModel model,
        Selection selection)
    {
        const string sql = """
            SELECT sequence_namespace.nspname,
                   sequence_class.relname,
                   pg_catalog.format_type(sequence.seqtypid, NULL),
                   sequence.seqstart,
                   sequence.seqincrement,
                   sequence.seqmin,
                   sequence.seqmax,
                   sequence.seqcycle
            FROM pg_catalog.pg_class AS sequence_class
            JOIN pg_catalog.pg_namespace AS sequence_namespace
              ON sequence_namespace.oid = sequence_class.relnamespace
            JOIN pg_catalog.pg_sequence AS sequence
              ON sequence.seqrelid = sequence_class.oid
            WHERE sequence_class.relkind = 'S'
              AND sequence_namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_depend AS dependency
                  WHERE dependency.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    AND dependency.objid = sequence_class.oid
                    AND dependency.deptype IN ('a', 'i'))
            ORDER BY sequence_namespace.nspname, sequence_class.relname
            """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var schema = reader.GetString(0);
            if (!selection.IncludesSchemaObject(schema))
            {
                continue;
            }

            model.Sequences.Add(new DatabaseSequence
            {
                Database = model,
                Schema = schema,
                Name = reader.GetString(1),
                StoreType = reader.GetString(2),
                StartValue = GetNullableInt64(reader, 3),
                IncrementBy = checked((int?)GetNullableInt64(reader, 4)),
                MinValue = GetNullableInt64(reader, 5),
                MaxValue = GetNullableInt64(reader, 6),
                IsCyclic = reader.GetBoolean(7),
            });
        }
    }

    private static void ReadPropertyGraphs(
        DbConnection connection,
        DatabaseModel model,
        Selection selection)
    {
        if (connection is not BlueTuskConnection blueTuskConnection)
        {
            return;
        }

        var inspector = new BlueTuskPropertyGraphSchemaInspector(blueTuskConnection);
        var definitions = inspector.Inspect()
            .Where(graph => selection.IncludesSchemaObject(graph.Name.Schema))
            .Select(ToDefinition)
            .ToArray();
        if (definitions.Length > 0)
        {
            model[BlueTuskPropertyGraphMetadata.AnnotationName] =
                BlueTuskPropertyGraphMetadata.Serialize(definitions);
        }
    }

    private static void ReadUserDefinedTypes(
        DbConnection connection,
        DatabaseModel model,
        Selection selection)
    {
        const string typeSql = """
            SELECT namespace.nspname,
                   type_entry.typname,
                   type_entry.typtype::text,
                   pg_catalog.format_type(type_entry.typbasetype, type_entry.typtypmod),
                   CASE
                     WHEN type_entry.typtype = 'd'
                      AND type_entry.typcollation <> 0
                      AND type_entry.typcollation <> base_type.typcollation
                     THEN collation_namespace.nspname
                   END,
                   CASE
                     WHEN type_entry.typtype = 'd'
                      AND type_entry.typcollation <> 0
                      AND type_entry.typcollation <> base_type.typcollation
                     THEN collation_entry.collname
                   END,
                   pg_catalog.pg_get_expr(type_entry.typdefaultbin, 0, true),
                   type_entry.typnotnull
            FROM pg_catalog.pg_type AS type_entry
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = type_entry.typnamespace
            LEFT JOIN pg_catalog.pg_type AS base_type ON base_type.oid = type_entry.typbasetype
            LEFT JOIN pg_catalog.pg_collation AS collation_entry
              ON collation_entry.oid = type_entry.typcollation
            LEFT JOIN pg_catalog.pg_namespace AS collation_namespace
              ON collation_namespace.oid = collation_entry.collnamespace
            LEFT JOIN pg_catalog.pg_class AS composite_class ON composite_class.oid = type_entry.typrelid
            WHERE (type_entry.typtype IN ('e', 'd')
                   OR (type_entry.typtype = 'c' AND composite_class.relkind = 'c'))
              AND namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND namespace.nspname !~ '^pg_toast'
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_depend AS dependency
                  WHERE dependency.classid = 'pg_catalog.pg_type'::pg_catalog.regclass
                    AND dependency.objid = type_entry.oid
                    AND dependency.deptype = 'e')
            ORDER BY namespace.nspname, type_entry.typname
            """;
        var enums = new Dictionary<(string Schema, string Name), List<string>>();
        var domains = new Dictionary<(string Schema, string Name), DomainSeed>();
        var composites = new Dictionary<
            (string Schema, string Name),
            List<BlueTuskCompositeAttributeDefinition>>();
        var ranges = new List<BlueTuskRangeTypeDefinition>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = typeSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!selection.IncludesSchemaObject(key.Item1))
                {
                    continue;
                }

                switch (reader.GetString(2))
                {
                    case "e":
                        enums.Add(key, []);
                        break;
                    case "d":
                        domains.Add(key, new DomainSeed(
                            reader.GetString(3),
                            reader.IsDBNull(4)
                                ? null
                                : $"{reader.GetString(4)}.{reader.GetString(5)}",
                            GetNullableString(reader, 6),
                            reader.GetBoolean(7),
                            []));
                        break;
                    case "c":
                        composites.Add(key, []);
                        break;
                }
            }
        }

        const string rangeSql = """
            SELECT range_namespace.nspname,
                   range_type.typname,
                   subtype_namespace.nspname,
                   subtype_type.typname,
                   operator_namespace.nspname,
                   operator_class.opcname,
                   collation_namespace.nspname,
                   collation_entry.collname,
                   canonical_namespace.nspname,
                   canonical_function.proname,
                   difference_namespace.nspname,
                   difference_function.proname,
                   multirange_namespace.nspname,
                   multirange_type.typname
            FROM pg_catalog.pg_range AS range_entry
            JOIN pg_catalog.pg_type AS range_type ON range_type.oid = range_entry.rngtypid
            JOIN pg_catalog.pg_namespace AS range_namespace ON range_namespace.oid = range_type.typnamespace
            JOIN pg_catalog.pg_type AS subtype_type ON subtype_type.oid = range_entry.rngsubtype
            JOIN pg_catalog.pg_namespace AS subtype_namespace ON subtype_namespace.oid = subtype_type.typnamespace
            JOIN pg_catalog.pg_opclass AS operator_class ON operator_class.oid = range_entry.rngsubopc
            JOIN pg_catalog.pg_namespace AS operator_namespace ON operator_namespace.oid = operator_class.opcnamespace
            LEFT JOIN pg_catalog.pg_collation AS collation_entry ON collation_entry.oid = range_entry.rngcollation
            LEFT JOIN pg_catalog.pg_namespace AS collation_namespace
              ON collation_namespace.oid = collation_entry.collnamespace
            LEFT JOIN pg_catalog.pg_proc AS canonical_function
              ON canonical_function.oid = range_entry.rngcanonical
            LEFT JOIN pg_catalog.pg_namespace AS canonical_namespace
              ON canonical_namespace.oid = canonical_function.pronamespace
            LEFT JOIN pg_catalog.pg_proc AS difference_function
              ON difference_function.oid = range_entry.rngsubdiff
            LEFT JOIN pg_catalog.pg_namespace AS difference_namespace
              ON difference_namespace.oid = difference_function.pronamespace
            JOIN pg_catalog.pg_type AS multirange_type ON multirange_type.oid = range_entry.rngmultitypid
            JOIN pg_catalog.pg_namespace AS multirange_namespace
              ON multirange_namespace.oid = multirange_type.typnamespace
            WHERE range_namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND range_namespace.nspname !~ '^pg_toast'
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_depend AS dependency
                  WHERE dependency.classid = 'pg_catalog.pg_type'::pg_catalog.regclass
                    AND dependency.objid = range_type.oid
                    AND dependency.deptype = 'e')
            ORDER BY range_namespace.nspname, range_type.typname
            """;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = rangeSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                if (!selection.IncludesSchemaObject(schema))
                {
                    continue;
                }

                ranges.Add(new BlueTuskRangeTypeDefinition(
                    reader.GetString(1),
                    schema,
                    new BlueTuskQualifiedName(reader.GetString(3), reader.GetString(2)),
                    new BlueTuskQualifiedName(reader.GetString(5), reader.GetString(4)),
                    reader.IsDBNull(6)
                        ? null
                        : new BlueTuskQualifiedName(reader.GetString(7), reader.GetString(6)),
                    reader.IsDBNull(8)
                        ? null
                        : new BlueTuskQualifiedName(reader.GetString(9), reader.GetString(8)),
                    reader.IsDBNull(10)
                        ? null
                        : new BlueTuskQualifiedName(reader.GetString(11), reader.GetString(10)),
                    new BlueTuskQualifiedName(reader.GetString(13), reader.GetString(12))));
            }
        }

        const string enumSql = """
            SELECT namespace.nspname, type_entry.typname, enum_entry.enumlabel
            FROM pg_catalog.pg_enum AS enum_entry
            JOIN pg_catalog.pg_type AS type_entry ON type_entry.oid = enum_entry.enumtypid
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = type_entry.typnamespace
            ORDER BY namespace.nspname, type_entry.typname, enum_entry.enumsortorder
            """;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = enumSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (enums.TryGetValue((reader.GetString(0), reader.GetString(1)), out var labels))
                {
                    labels.Add(reader.GetString(2));
                }
            }
        }

        const string domainConstraintSql = """
            SELECT namespace.nspname,
                   type_entry.typname,
                   constraint_entry.conname,
                   pg_catalog.pg_get_expr(constraint_entry.conbin, 0, true),
                   constraint_entry.convalidated
            FROM pg_catalog.pg_constraint AS constraint_entry
            JOIN pg_catalog.pg_type AS type_entry ON type_entry.oid = constraint_entry.contypid
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = type_entry.typnamespace
            WHERE constraint_entry.contype = 'c'
            ORDER BY namespace.nspname, type_entry.typname, constraint_entry.conname
            """;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = domainConstraintSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (domains.TryGetValue((reader.GetString(0), reader.GetString(1)), out var domain))
                {
                    domain.Constraints.Add(new BlueTuskDomainConstraintDefinition(
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetBoolean(4)));
                }
            }
        }

        const string compositeAttributeSql = """
            SELECT namespace.nspname,
                   type_entry.typname,
                   attribute_entry.attname,
                   pg_catalog.format_type(attribute_entry.atttypid, attribute_entry.atttypmod),
                   CASE
                     WHEN attribute_entry.attcollation <> 0
                      AND attribute_entry.attcollation <> attribute_type.typcollation
                     THEN collation_namespace.nspname
                   END,
                   CASE
                     WHEN attribute_entry.attcollation <> 0
                      AND attribute_entry.attcollation <> attribute_type.typcollation
                     THEN collation_entry.collname
                   END
            FROM pg_catalog.pg_type AS type_entry
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = type_entry.typnamespace
            JOIN pg_catalog.pg_class AS composite_class
              ON composite_class.oid = type_entry.typrelid AND composite_class.relkind = 'c'
            JOIN pg_catalog.pg_attribute AS attribute_entry
              ON attribute_entry.attrelid = composite_class.oid
             AND attribute_entry.attnum > 0
             AND NOT attribute_entry.attisdropped
            JOIN pg_catalog.pg_type AS attribute_type ON attribute_type.oid = attribute_entry.atttypid
            LEFT JOIN pg_catalog.pg_collation AS collation_entry
              ON collation_entry.oid = attribute_entry.attcollation
            LEFT JOIN pg_catalog.pg_namespace AS collation_namespace
              ON collation_namespace.oid = collation_entry.collnamespace
            ORDER BY namespace.nspname, type_entry.typname, attribute_entry.attnum
            """;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = compositeAttributeSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (composites.TryGetValue((reader.GetString(0), reader.GetString(1)), out var attributes))
                {
                    attributes.Add(new BlueTuskCompositeAttributeDefinition(
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4)
                            ? null
                            : $"{reader.GetString(4)}.{reader.GetString(5)}"));
                }
            }
        }

        var definitions = new BlueTuskUserDefinedTypeDefinitionSet(
            enums.Select(item => new BlueTuskEnumTypeDefinition(item.Key.Name, item.Key.Schema, item.Value)).ToArray(),
            domains.Select(item => new BlueTuskDomainTypeDefinition(
                item.Key.Name,
                item.Key.Schema,
                item.Value.BaseStoreType,
                item.Value.Collation,
                item.Value.DefaultSql,
                item.Value.IsNotNull,
                item.Value.Constraints)).ToArray(),
            composites.Select(item => new BlueTuskCompositeTypeDefinition(
                item.Key.Name,
                item.Key.Schema,
                item.Value)).ToArray())
        {
            Ranges = ranges,
        };
        if (definitions.Enums.Count > 0 ||
            definitions.Domains.Count > 0 ||
            definitions.Composites.Count > 0 ||
            definitions.Ranges.Count > 0)
        {
            model[BlueTuskUserDefinedTypeMetadata.AnnotationName] =
                BlueTuskUserDefinedTypeMetadata.Serialize(definitions);
        }
    }

    private static void ReadExtensions(
        DbConnection connection,
        DatabaseModel model,
        Selection selection)
    {
        const string sql = """
            SELECT extension_entry.extname,
                   namespace.nspname,
                   extension_entry.extversion,
                   dependency_extension.extname
            FROM pg_catalog.pg_extension AS extension_entry
            JOIN pg_catalog.pg_namespace AS namespace
              ON namespace.oid = extension_entry.extnamespace
            LEFT JOIN pg_catalog.pg_depend AS dependency
              ON dependency.classid = 'pg_catalog.pg_extension'::pg_catalog.regclass
             AND dependency.objid = extension_entry.oid
             AND dependency.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass
             AND dependency.deptype = 'n'
            LEFT JOIN pg_catalog.pg_extension AS dependency_extension
              ON dependency_extension.oid = dependency.refobjid
            WHERE namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND namespace.nspname !~ '^pg_toast'
            ORDER BY extension_entry.extname, dependency_extension.extname
            """;
        var extensions = new Dictionary<string, ExtensionSeed>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var schema = reader.GetString(1);
            if (!selection.IncludesSchemaObject(schema))
            {
                continue;
            }

            if (!extensions.TryGetValue(name, out var extension))
            {
                extension = new ExtensionSeed(schema, reader.GetString(2), []);
                extensions.Add(name, extension);
            }

            if (!reader.IsDBNull(3))
            {
                extension.Dependencies.Add(reader.GetString(3));
            }
        }

        if (extensions.Count > 0)
        {
            model[BlueTuskExtensionMetadata.AnnotationName] = BlueTuskExtensionMetadata.Serialize(
                new BlueTuskExtensionDefinitionSet(extensions.Select(item =>
                        new BlueTuskExtensionDefinition(
                            item.Key,
                            item.Value.Schema,
                            item.Value.Version,
                            item.Value.Dependencies))
                    .ToArray()));
        }
    }

    private static void ReadCollations(
        DbConnection connection,
        DatabaseModel model,
        Selection selection)
    {
        const string sql = """
            SELECT collation_entry.collname,
                   namespace.nspname,
                   collation_entry.collprovider::text,
                   collation_entry.collisdeterministic,
                   collation_entry.collcollate,
                   collation_entry.collctype,
                   COALESCE(
                       to_jsonb(collation_entry) ->> 'colllocale',
                       to_jsonb(collation_entry) ->> 'colliculocale'),
                   to_jsonb(collation_entry) ->> 'collicurules',
                   collation_entry.collversion
            FROM pg_catalog.pg_collation AS collation_entry
            JOIN pg_catalog.pg_namespace AS namespace
              ON namespace.oid = collation_entry.collnamespace
            WHERE namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND namespace.nspname !~ '^pg_toast'
              AND collation_entry.collencoding IN (
                  -1,
                  pg_catalog.pg_char_to_encoding(current_setting('server_encoding')))
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_depend AS dependency
                  WHERE dependency.classid = 'pg_catalog.pg_collation'::pg_catalog.regclass
                    AND dependency.objid = collation_entry.oid
                    AND dependency.deptype = 'e')
            ORDER BY namespace.nspname, collation_entry.collname
            """;
        var collations = new List<BlueTuskCollationDefinition>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var schema = reader.GetString(1);
            if (!selection.IncludesSchemaObject(schema))
            {
                continue;
            }

            var provider = reader.GetString(2) switch
            {
                "b" => BlueTuskCollationProvider.Builtin,
                "c" => BlueTuskCollationProvider.Libc,
                "i" => BlueTuskCollationProvider.Icu,
                "d" => (BlueTuskCollationProvider?)null,
                var value => throw new NotSupportedException(
                    $"PostgreSQL collation '{schema}.{name}' uses unknown provider code '{value}'."),
            };
            var lcCollate = reader.IsDBNull(4) ? null : reader.GetString(4);
            var lcCtype = reader.IsDBNull(5) ? null : reader.GetString(5);
            var locale = reader.IsDBNull(6) ? null : reader.GetString(6);
            if ((provider is null or BlueTuskCollationProvider.Libc) &&
                string.Equals(lcCollate, lcCtype, StringComparison.Ordinal))
            {
                locale = lcCollate;
                lcCollate = null;
                lcCtype = null;
            }

            collations.Add(new BlueTuskCollationDefinition(
                name,
                schema,
                provider,
                locale,
                lcCollate,
                lcCtype,
                reader.GetBoolean(3),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        if (collations.Count > 0)
        {
            model[BlueTuskCollationMetadata.AnnotationName] = BlueTuskCollationMetadata.Serialize(
                new BlueTuskCollationDefinitionSet(collations));
        }
    }

    private static void ReadRoutines(
        DbConnection connection,
        DatabaseModel model,
        Selection selection)
    {
        const string sql = """
            SELECT namespace.nspname,
                   routine_entry.proname,
                   routine_entry.prokind::text,
                   pg_catalog.oidvectortypes(routine_entry.proargtypes),
                   pg_catalog.pg_get_function_identity_arguments(routine_entry.oid),
                   pg_catalog.pg_get_function_arguments(routine_entry.oid),
                   pg_catalog.pg_get_function_result(routine_entry.oid),
                   pg_catalog.pg_get_functiondef(routine_entry.oid),
                   routine_entry.prokind = 'w',
                   routine_entry.prosqlbody IS NOT NULL
            FROM pg_catalog.pg_proc AS routine_entry
            JOIN pg_catalog.pg_namespace AS namespace
              ON namespace.oid = routine_entry.pronamespace
            WHERE routine_entry.prokind IN ('f', 'p', 'w')
              AND namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND namespace.nspname !~ '^pg_toast'
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_depend AS dependency
                  WHERE dependency.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                    AND dependency.objid = routine_entry.oid
                    AND dependency.deptype = 'e')
            ORDER BY namespace.nspname,
                     routine_entry.proname,
                     pg_catalog.oidvectortypes(routine_entry.proargtypes),
                     routine_entry.prokind
            """;
        var definitions = new List<BlueTuskRoutineDefinition>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var schema = reader.GetString(0);
            if (!selection.IncludesSchemaObject(schema))
            {
                continue;
            }

            var kind = reader.GetString(2) == "p"
                ? BlueTuskRoutineKind.Procedure
                : BlueTuskRoutineKind.Function;
            definitions.Add(new BlueTuskRoutineDefinition(
                kind,
                reader.GetString(1),
                schema,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                GetNullableString(reader, 6),
                reader.GetString(7),
                IsWindow: reader.GetBoolean(8),
                HasTrackedBodyDependencies: reader.GetBoolean(9)));
        }

        if (definitions.Count > 0)
        {
            model[BlueTuskRoutineMetadata.AnnotationName] =
                BlueTuskRoutineMetadata.Serialize(new BlueTuskRoutineDefinitionSet(definitions));
        }
    }

    private static void ReadViews(
        DbConnection connection,
        DatabaseModel model,
        Dictionary<(string Schema, string Name), DatabaseTable> tables,
        Selection selection)
    {
        const string dependencySql = """
            SELECT child_namespace.nspname,
                   child.relname,
                   parent_namespace.nspname,
                   parent.relname
            FROM pg_catalog.pg_rewrite AS rewrite_entry
            JOIN pg_catalog.pg_class AS child ON child.oid = rewrite_entry.ev_class
            JOIN pg_catalog.pg_namespace AS child_namespace ON child_namespace.oid = child.relnamespace
            JOIN pg_catalog.pg_depend AS dependency
              ON dependency.classid = 'pg_catalog.pg_rewrite'::pg_catalog.regclass
             AND dependency.objid = rewrite_entry.oid
             AND dependency.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
             AND dependency.deptype = 'n'
            JOIN pg_catalog.pg_class AS parent ON parent.oid = dependency.refobjid
            JOIN pg_catalog.pg_namespace AS parent_namespace ON parent_namespace.oid = parent.relnamespace
            WHERE child.relkind IN ('v', 'm')
              AND parent.relkind IN ('v', 'm')
              AND child.oid <> parent.oid
              AND child_namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND child_namespace.nspname !~ '^pg_toast'
            ORDER BY child_namespace.nspname, child.relname, parent_namespace.nspname, parent.relname
            """;
        var dependencies = new Dictionary<
            (string Schema, string Name),
            List<BlueTuskViewDependencyDefinition>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = dependencySql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!tables.ContainsKey(key) || !selection.IncludesTable(key.Item1, key.Item2))
                {
                    continue;
                }

                if (!dependencies.TryGetValue(key, out var viewDependencies))
                {
                    viewDependencies = [];
                    dependencies.Add(key, viewDependencies);
                }

                var dependency = new BlueTuskViewDependencyDefinition(reader.GetString(3), reader.GetString(2));
                if (!viewDependencies.Contains(dependency))
                {
                    viewDependencies.Add(dependency);
                }
            }
        }

        const string sql = """
            SELECT namespace.nspname,
                   relation.relname,
                   relation.relkind::text,
                   pg_catalog.pg_get_viewdef(relation.oid, false),
                   relation.reloptions,
                   relation.relispopulated,
                   access_method.amname,
                   tablespace.spcname
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            LEFT JOIN pg_catalog.pg_am AS access_method ON access_method.oid = relation.relam
            LEFT JOIN pg_catalog.pg_tablespace AS tablespace ON tablespace.oid = relation.reltablespace
            WHERE relation.relkind IN ('v', 'm')
              AND namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND namespace.nspname !~ '^pg_toast'
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_depend AS dependency
                  WHERE dependency.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    AND dependency.objid = relation.oid
                    AND dependency.deptype = 'e')
            ORDER BY namespace.nspname, relation.relname
            """;
        var views = new List<BlueTuskViewDefinition>();
        var materializedViews = new List<BlueTuskMaterializedViewDefinition>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = (Schema: reader.GetString(0), Name: reader.GetString(1));
                if (!tables.TryGetValue(key, out var table) || !selection.IncludesTable(key.Schema, key.Name))
                {
                    continue;
                }

                var columns = table.Columns.Select(column => column.Name).ToArray();
                var viewDependencies = dependencies.TryGetValue(key, out var discoveredDependencies)
                    ? discoveredDependencies.ToArray()
                    : Array.Empty<BlueTuskViewDependencyDefinition>();
                var options = reader.IsDBNull(4)
                    ? Array.Empty<string>()
                    : ParsePostgreSqlArray(reader.GetValue(4));
                if (reader.GetString(2) == "v")
                {
                    views.Add(new BlueTuskViewDefinition(
                        key.Name,
                        key.Schema,
                        reader.GetString(3),
                        columns,
                        viewDependencies,
                        SecurityBarrier: HasOption(options, "security_barrier", "true"),
                        SecurityInvoker: HasOption(options, "security_invoker", "true"),
                        CheckOption: GetOption(options, "check_option")?.ToLowerInvariant() switch
                        {
                            "local" => BlueTuskViewCheckOption.Local,
                            "cascaded" => BlueTuskViewCheckOption.Cascaded,
                            _ => null,
                        }));
                }
                else
                {
                    materializedViews.Add(new BlueTuskMaterializedViewDefinition(
                        key.Name,
                        key.Schema,
                        reader.GetString(3),
                        columns,
                        viewDependencies,
                        reader.IsDBNull(6) ? "heap" : reader.GetString(6),
                        options.Select(ParseStorageParameter).ToArray(),
                        GetNullableString(reader, 7),
                        reader.GetBoolean(5)));
                }
            }
        }

        if (views.Count > 0 || materializedViews.Count > 0)
        {
            model[BlueTuskViewMetadata.AnnotationName] = BlueTuskViewMetadata.Serialize(
                new BlueTuskViewDefinitionSet(views, materializedViews));
        }
    }

    private static bool HasOption(IReadOnlyList<string> options, string name, string value) =>
        string.Equals(GetOption(options, name), value, StringComparison.OrdinalIgnoreCase);

    private static string? GetOption(IReadOnlyList<string> options, string name)
    {
        var prefix = $"{name}=";
        return options.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];
    }

    private static BlueTuskMaterializedViewStorageParameterDefinition ParseStorageParameter(string option)
    {
        var separator = option.IndexOf('=');
        if (separator <= 0 || separator == option.Length - 1)
        {
            throw new InvalidOperationException(
                $"PostgreSQL returned an invalid materialized-view storage parameter '{option}'.");
        }

        return new BlueTuskMaterializedViewStorageParameterDefinition(
            option[..separator],
            option[(separator + 1)..]);
    }

    private static BlueTuskPropertyGraphDefinition ToDefinition(
        BlueTuskPropertyGraphSchema graph)
    {
        var labels = graph.Labels.ToDictionary(label => label.Name, StringComparer.Ordinal);
        return new BlueTuskPropertyGraphDefinition(
            graph.Name.Name,
            graph.Name.Schema,
            graph.ElementTables.Select(element =>
            {
                var elementProperties = element.Properties.ToDictionary(
                    property => property.Name,
                    StringComparer.Ordinal);
                var elementLabels = element.Labels.Select(labelName =>
                {
                    labels.TryGetValue(labelName, out var label);
                    var propertyNames = label?.Properties ?? elementProperties.Keys.ToArray();
                    return new BlueTuskGraphLabelDefinition(
                        labelName,
                        propertyNames
                            .Where(elementProperties.ContainsKey)
                            .Select(propertyName =>
                            {
                                var property = elementProperties[propertyName];
                                return new BlueTuskGraphPropertyDefinition(
                                    property.Expression,
                                    property.Name,
                                    IsColumn: false);
                            })
                            .ToArray());
                }).ToArray();
                return new BlueTuskGraphElementTableDefinition(
                    element.Alias,
                    element.Kind == BlueTuskPropertyGraphElementKind.Vertex
                        ? BlueTuskGraphElementKind.Vertex
                        : BlueTuskGraphElementKind.Edge,
                    element.Table.Name,
                    element.Table.Schema,
                    element.KeyColumns.Select(column => column.Name).ToArray(),
                    elementLabels,
                    ToEndpoint(element, BlueTuskPropertyGraphEdgeEnd.Source),
                    ToEndpoint(element, BlueTuskPropertyGraphEdgeEnd.Destination));
            }).ToArray());
    }

    private static BlueTuskGraphEndpointDefinition? ToEndpoint(
        BlueTuskPropertyGraphElementTable element,
        BlueTuskPropertyGraphEdgeEnd end)
    {
        var endpoint = element.Endpoints.SingleOrDefault(candidate => candidate.End == end);
        return endpoint is null
            ? null
            : new BlueTuskGraphEndpointDefinition(
                endpoint.VertexTableAlias,
                endpoint.Columns.Select(column => column.EdgeTableColumn).ToArray(),
                endpoint.Columns.Select(column => column.VertexTableColumn).ToArray());
    }

    private static DatabaseColumn FindColumn(DatabaseTable table, string name)
        => table.Columns.Single(column => string.Equals(column.Name, name, StringComparison.Ordinal));

    private static string? GetNullableString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static ReferentialAction MapReferentialAction(string action)
        => action switch
        {
            "c" => ReferentialAction.Cascade,
            "n" => ReferentialAction.SetNull,
            "d" => ReferentialAction.SetDefault,
            "r" => ReferentialAction.Restrict,
            _ => ReferentialAction.NoAction,
        };

    private readonly record struct PartitionRelationship(
        (string Schema, string Name) Parent,
        (string Schema, string Name) Child,
        string BoundSql);

    private readonly record struct PartitionKey(BlueTuskPartitionStrategy Strategy, string Sql);

    private sealed record DomainSeed(
        string BaseStoreType,
        string? Collation,
        string? DefaultSql,
        bool IsNotNull,
        List<BlueTuskDomainConstraintDefinition> Constraints);

    private sealed record ExtensionSeed(
        string Schema,
        string Version,
        List<string> Dependencies);

    private sealed record ExclusionConstraintSeed(
        string IndexMethod,
        bool IsDeferrable,
        bool IsInitiallyDeferred,
        string? PredicateSql,
        string? Tablespace,
        IReadOnlyList<string> IncludedColumns,
        IReadOnlyList<BlueTuskExclusionParameterDefinition> StorageParameters,
        List<BlueTuskExclusionElementDefinition> Elements);

    private sealed class Selection
    {
        private readonly HashSet<string> _schemas;
        private readonly HashSet<string> _tables;

        public Selection(DatabaseModelFactoryOptions options)
        {
            _schemas = options.Schemas.ToHashSet(StringComparer.Ordinal);
            _tables = options.Tables.ToHashSet(StringComparer.Ordinal);
        }

        public bool IncludesTable(string schema, string table)
            => _schemas.Count == 0 && _tables.Count == 0
                || _schemas.Contains(schema)
                || _tables.Contains(table)
                || _tables.Contains($"{schema}.{table}");

        public bool IncludesSchemaObject(string schema)
            => _tables.Count == 0 && (_schemas.Count == 0 || _schemas.Contains(schema));
    }
}

#pragma warning restore EF1001
