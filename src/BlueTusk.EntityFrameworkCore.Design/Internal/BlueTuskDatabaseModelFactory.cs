using System.Data;
using System.Data.Common;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;

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
        ReadIndexes(connection, tables);
        ReadForeignKeys(connection, tables);
        ReadSequences(connection, model, selection);
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
                   attribute.attname
            FROM pg_catalog.pg_index AS idx
            JOIN pg_catalog.pg_class AS table_class ON table_class.oid = idx.indrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = table_class.relnamespace
            JOIN pg_catalog.pg_class AS index_class ON index_class.oid = idx.indexrelid
            JOIN pg_catalog.pg_am AS access_method ON access_method.oid = index_class.relam
            CROSS JOIN LATERAL unnest(idx.indkey) WITH ORDINALITY AS key(attnum, position)
            JOIN pg_catalog.pg_attribute AS attribute
                ON attribute.attrelid = idx.indrelid AND attribute.attnum = key.attnum
            WHERE NOT idx.indisprimary
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_constraint AS con
                  WHERE con.conindid = idx.indexrelid)
            ORDER BY n.nspname, table_class.relname, index_class.relname, key.position
            """;

        DatabaseIndex? index = null;
        string? currentIndex = null;
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
            if (!string.Equals(currentIndex, indexIdentity, StringComparison.Ordinal))
            {
                currentIndex = indexIdentity;
                index = new DatabaseIndex
                {
                    Table = table,
                    Name = indexName,
                    IsUnique = reader.GetBoolean(3),
                    Filter = GetNullableString(reader, 5),
                };
                index["BlueTusk:IndexMethod"] = reader.GetString(4);
                table.Indexes.Add(index);
            }

            index!.Columns.Add(FindColumn(table, reader.GetString(6)));
        }
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
            SELECT schemaname,
                   sequencename,
                   data_type::text,
                   start_value,
                   increment_by,
                   min_value,
                   max_value,
                   cycle
            FROM pg_catalog.pg_sequences
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_class AS sequence_class
                  JOIN pg_catalog.pg_namespace AS sequence_namespace
                    ON sequence_namespace.oid = sequence_class.relnamespace
                  JOIN pg_catalog.pg_depend AS dependency
                    ON dependency.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                   AND dependency.objid = sequence_class.oid
                  WHERE sequence_class.relkind = 'S'
                    AND sequence_namespace.nspname = schemaname
                    AND sequence_class.relname = sequencename
                    AND dependency.deptype IN ('a', 'i'))
            ORDER BY schemaname, sequencename
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
