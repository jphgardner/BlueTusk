using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

internal static class BlueTuskSchemaCollections
{
    private static readonly CollectionDefinition[] Definitions =
    [
        new("MetaDataCollections", 0, 0),
        new("DataSourceInformation", 0, 0),
        new("DataTypes", 0, 0),
        new("Restrictions", 0, 0),
        new("ReservedWords", 0, 0),
        new("Databases", 1, 1),
        new("Schemas", 2, 2),
        new("Tables", 4, 3),
        new("Columns", 4, 4),
    ];

    public static DataTable Get(
        BlueTuskConnection connection,
        string collectionName,
        string?[]? restrictions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        var definition = GetDefinition(collectionName);
        ValidateRestrictions(definition, restrictions);
        return definition.Name switch
        {
            "MetaDataCollections" => CreateMetadataCollections(),
            "DataSourceInformation" => CreateDataSourceInformation(connection),
            "DataTypes" => CreateDataTypes(connection),
            "Restrictions" => CreateRestrictions(),
            "ReservedWords" => CreateReservedWords(),
            "Databases" => Execute(
                connection,
                """
                SELECT datname::text AS database_name,
                       pg_catalog.pg_get_userbyid(datdba)::text AS owner,
                       pg_catalog.pg_encoding_to_char(encoding)::text AS encoding
                FROM pg_catalog.pg_database
                WHERE (@r0::text IS NULL OR datname = @r0)
                ORDER BY datname
                """,
                restrictions),
            "Schemas" => Execute(
                connection,
                """
                SELECT current_database()::text AS catalog_name,
                       nspname::text AS schema_name,
                       pg_catalog.pg_get_userbyid(nspowner)::text AS schema_owner
                FROM pg_catalog.pg_namespace
                WHERE (@r0::text IS NULL OR current_database() = @r0)
                  AND (@r1::text IS NULL OR nspname = @r1)
                ORDER BY nspname
                """,
                restrictions),
            "Tables" => Execute(
                connection,
                """
                SELECT table_catalog::text,
                       table_schema::text,
                       table_name::text,
                       table_type::text
                FROM information_schema.tables
                WHERE (@r0::text IS NULL OR table_catalog = @r0)
                  AND (@r1::text IS NULL OR table_schema = @r1)
                  AND (@r2::text IS NULL OR table_name = @r2)
                  AND (@r3::text IS NULL OR table_type = @r3)
                ORDER BY table_catalog, table_schema, table_name
                """,
                restrictions),
            "Columns" => Execute(
                connection,
                """
                SELECT table_catalog::text,
                       table_schema::text,
                       table_name::text,
                       column_name::text,
                       ordinal_position,
                       column_default::text,
                       is_nullable::text,
                       data_type::text,
                       udt_schema::text,
                       udt_name::text,
                       character_maximum_length,
                       numeric_precision,
                       numeric_scale,
                       datetime_precision
                FROM information_schema.columns
                WHERE (@r0::text IS NULL OR table_catalog = @r0)
                  AND (@r1::text IS NULL OR table_schema = @r1)
                  AND (@r2::text IS NULL OR table_name = @r2)
                  AND (@r3::text IS NULL OR column_name = @r3)
                ORDER BY table_catalog, table_schema, table_name, ordinal_position
                """,
                restrictions),
            _ => throw new UnreachableException(),
        };
    }

    public static Task<DataTable> GetAsync(
        BlueTuskConnection connection,
        string collectionName,
        string?[]? restrictions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        cancellationToken.ThrowIfCancellationRequested();
        var definition = GetDefinition(collectionName);
        ValidateRestrictions(definition, restrictions);
        return definition.Name switch
        {
            "Databases" => ExecuteAsync(
                connection,
                """
                SELECT datname::text AS database_name,
                       pg_catalog.pg_get_userbyid(datdba)::text AS owner,
                       pg_catalog.pg_encoding_to_char(encoding)::text AS encoding
                FROM pg_catalog.pg_database
                WHERE (@r0::text IS NULL OR datname = @r0)
                ORDER BY datname
                """,
                restrictions,
                cancellationToken),
            "Schemas" => ExecuteAsync(
                connection,
                """
                SELECT current_database()::text AS catalog_name,
                       nspname::text AS schema_name,
                       pg_catalog.pg_get_userbyid(nspowner)::text AS schema_owner
                FROM pg_catalog.pg_namespace
                WHERE (@r0::text IS NULL OR current_database() = @r0)
                  AND (@r1::text IS NULL OR nspname = @r1)
                ORDER BY nspname
                """,
                restrictions,
                cancellationToken),
            "Tables" => ExecuteAsync(
                connection,
                """
                SELECT table_catalog::text,
                       table_schema::text,
                       table_name::text,
                       table_type::text
                FROM information_schema.tables
                WHERE (@r0::text IS NULL OR table_catalog = @r0)
                  AND (@r1::text IS NULL OR table_schema = @r1)
                  AND (@r2::text IS NULL OR table_name = @r2)
                  AND (@r3::text IS NULL OR table_type = @r3)
                ORDER BY table_catalog, table_schema, table_name
                """,
                restrictions,
                cancellationToken),
            "Columns" => ExecuteAsync(
                connection,
                """
                SELECT table_catalog::text,
                       table_schema::text,
                       table_name::text,
                       column_name::text,
                       ordinal_position,
                       column_default::text,
                       is_nullable::text,
                       data_type::text,
                       udt_schema::text,
                       udt_name::text,
                       character_maximum_length,
                       numeric_precision,
                       numeric_scale,
                       datetime_precision
                FROM information_schema.columns
                WHERE (@r0::text IS NULL OR table_catalog = @r0)
                  AND (@r1::text IS NULL OR table_schema = @r1)
                  AND (@r2::text IS NULL OR table_name = @r2)
                  AND (@r3::text IS NULL OR column_name = @r3)
                ORDER BY table_catalog, table_schema, table_name, ordinal_position
                """,
                restrictions,
                cancellationToken),
            _ => Task.FromResult(Get(connection, definition.Name, restrictions)),
        };
    }

    private static CollectionDefinition GetDefinition(string collectionName) =>
        Definitions.FirstOrDefault(
            definition => string.Equals(
                definition.Name,
                collectionName,
                StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"Schema collection '{collectionName}' is not supported.",
            nameof(collectionName));

    private static void ValidateRestrictions(
        CollectionDefinition definition,
        string?[]? restrictions)
    {
        if (restrictions is { Length: > 0 } &&
            restrictions.Length > definition.Restrictions)
        {
            throw new ArgumentException(
                $"Schema collection '{definition.Name}' accepts at most " +
                $"{definition.Restrictions} restriction values.",
                nameof(restrictions));
        }
    }

    private static DataTable CreateMetadataCollections()
    {
        var table = NewTable("MetaDataCollections");
        table.Columns.Add("CollectionName", typeof(string));
        table.Columns.Add("NumberOfRestrictions", typeof(int));
        table.Columns.Add("NumberOfIdentifierParts", typeof(int));
        foreach (var definition in Definitions)
        {
            table.Rows.Add(
                definition.Name,
                definition.Restrictions,
                definition.IdentifierParts);
        }

        return table;
    }

    private static DataTable CreateRestrictions()
    {
        var table = NewTable("Restrictions");
        table.Columns.Add("CollectionName", typeof(string));
        table.Columns.Add("RestrictionName", typeof(string));
        table.Columns.Add("RestrictionDefault", typeof(string));
        table.Columns.Add("RestrictionNumber", typeof(int));
        AddRestrictions(table, "Databases", "Database");
        AddRestrictions(table, "Schemas", "Catalog", "Schema");
        AddRestrictions(table, "Tables", "Catalog", "Schema", "Table", "TableType");
        AddRestrictions(table, "Columns", "Catalog", "Schema", "Table", "Column");
        return table;
    }

    private static void AddRestrictions(
        DataTable table,
        string collection,
        params string[] restrictions)
    {
        for (var index = 0; index < restrictions.Length; index++)
        {
            table.Rows.Add(collection, restrictions[index], DBNull.Value, index + 1);
        }
    }

    private static DataTable CreateDataSourceInformation(BlueTuskConnection connection)
    {
        var table = NewTable("DataSourceInformation");
        table.Columns.Add("CompositeIdentifierSeparatorPattern", typeof(string));
        table.Columns.Add("DataSourceProductName", typeof(string));
        table.Columns.Add("DataSourceProductVersion", typeof(string));
        table.Columns.Add("DataSourceProductVersionNormalized", typeof(string));
        table.Columns.Add("GroupByBehavior", typeof(int));
        table.Columns.Add("IdentifierPattern", typeof(string));
        table.Columns.Add("IdentifierCase", typeof(int));
        table.Columns.Add("OrderByColumnsInSelect", typeof(bool));
        table.Columns.Add("ParameterMarkerFormat", typeof(string));
        table.Columns.Add("ParameterMarkerPattern", typeof(string));
        table.Columns.Add("ParameterNameMaxLength", typeof(int));
        table.Columns.Add("ParameterNamePattern", typeof(string));
        table.Columns.Add("QuotedIdentifierPattern", typeof(string));
        table.Columns.Add("QuotedIdentifierCase", typeof(int));
        table.Columns.Add("StatementSeparatorPattern", typeof(string));
        table.Columns.Add("StringLiteralPattern", typeof(string));
        table.Columns.Add("SupportedJoinOperators", typeof(int));
        table.Rows.Add(
            "\\.",
            "PostgreSQL",
            connection.ServerVersion,
            connection.ServerVersion,
            3,
            @"(^\p{Ll}[\p{Ll}\p{Nd}_$]*$)",
            2,
            false,
            "{0}",
            @"@[\p{L}_][\p{L}\p{Nd}_]*",
            63,
            @"^[\p{L}_][\p{L}\p{Nd}_]*$",
            "\"(([^\"]|\"\")*)\"",
            1,
            ";",
            "'(([^']|'')*)'",
            15);
        return table;
    }

    private static DataTable CreateDataTypes(BlueTuskConnection connection)
    {
        var table = NewTable("DataTypes");
        table.Columns.Add("TypeName", typeof(string));
        table.Columns.Add("ProviderDbType", typeof(int));
        table.Columns.Add("ColumnSize", typeof(long));
        table.Columns.Add("CreateFormat", typeof(string));
        table.Columns.Add("CreateParameters", typeof(string));
        table.Columns.Add("DataType", typeof(string));
        table.Columns.Add("IsAutoIncrementable", typeof(bool));
        table.Columns.Add("IsBestMatch", typeof(bool));
        table.Columns.Add("IsCaseSensitive", typeof(bool));
        table.Columns.Add("IsFixedLength", typeof(bool));
        table.Columns.Add("IsFixedPrecisionScale", typeof(bool));
        table.Columns.Add("IsLong", typeof(bool));
        table.Columns.Add("IsNullable", typeof(bool));
        table.Columns.Add("IsSearchable", typeof(bool));
        table.Columns.Add("IsSearchableWithLike", typeof(bool));
        table.Columns.Add("IsUnsigned", typeof(bool));
        table.Columns.Add("MaximumScale", typeof(short));
        table.Columns.Add("MinimumScale", typeof(short));
        table.Columns.Add("IsConcurrencyType", typeof(bool));
        table.Columns.Add("IsLiteralSupported", typeof(bool));
        table.Columns.Add("LiteralPrefix", typeof(string));
        table.Columns.Add("LiteralSuffix", typeof(string));

        foreach (var type in connection.TypeRegistry.Types.OrderBy(type => type.Id.Oid))
        {
            var field = new BlueTuskFieldDescription(
                type.Name,
                0,
                0,
                type.Id.Oid,
                -1,
                -1,
                0);
            var dataType = BlueTuskValueDecoder.GetFieldType(
                connection.TypeRegistry,
                field);
            table.Rows.Add(
                type.QualifiedName,
                unchecked((int)type.Id.Oid),
                DBNull.Value,
                type.QualifiedName,
                DBNull.Value,
                dataType.FullName ?? dataType.Name,
                false,
                true,
                dataType == typeof(string),
                false,
                false,
                dataType == typeof(string) || dataType == typeof(byte[]),
                true,
                true,
                dataType == typeof(string),
                false,
                DBNull.Value,
                DBNull.Value,
                false,
                true,
                dataType == typeof(string) ? "'" : DBNull.Value,
                dataType == typeof(string) ? "'" : DBNull.Value);
        }

        return table;
    }

    private static DataTable CreateReservedWords()
    {
        var table = NewTable("ReservedWords");
        table.Columns.Add("ReservedWord", typeof(string));
        string[] words =
        [
            "ALL", "ANALYSE", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC",
            "ASYMMETRIC", "AUTHORIZATION", "BINARY", "BOTH", "CASE", "CAST",
            "CHECK", "COLLATE", "COLLATION", "COLUMN", "CONCURRENTLY",
            "CONSTRAINT", "CREATE", "CROSS", "CURRENT_CATALOG", "CURRENT_DATE",
            "CURRENT_ROLE", "CURRENT_SCHEMA", "CURRENT_TIME", "CURRENT_TIMESTAMP",
            "CURRENT_USER", "DEFAULT", "DEFERRABLE", "DESC", "DISTINCT", "DO",
            "ELSE", "END", "EXCEPT", "FALSE", "FETCH", "FOR", "FOREIGN", "FREEZE",
            "FROM", "FULL", "GRANT", "GROUP", "HAVING", "ILIKE", "IN", "INITIALLY",
            "INNER", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "LATERAL",
            "LEADING", "LEFT", "LIKE", "LIMIT", "LOCALTIME", "LOCALTIMESTAMP",
            "NATURAL", "NOT", "NOTNULL", "NULL", "OFFSET", "ON", "ONLY", "OR",
            "ORDER", "OUTER", "OVERLAPS", "PLACING", "PRIMARY", "REFERENCES",
            "RETURNING", "RIGHT", "SELECT", "SESSION_USER", "SIMILAR", "SOME",
            "SYMMETRIC", "TABLE", "TABLESAMPLE", "THEN", "TO", "TRAILING", "TRUE",
            "UNION", "UNIQUE", "USER", "USING", "VARIADIC", "VERBOSE", "WHEN",
            "WHERE", "WINDOW", "WITH",
        ];
        foreach (var word in words)
        {
            table.Rows.Add(word);
        }

        return table;
    }

    private static DataTable Execute(
        BlueTuskConnection connection,
        string sql,
        string?[]? restrictions)
    {
        EnsureOpen(connection);
        using var command = CreateCommand(connection, sql, restrictions);
        using var reader = command.ExecuteReader();
        return Materialize(reader);
    }

    private static async Task<DataTable> ExecuteAsync(
        BlueTuskConnection connection,
        string sql,
        string?[]? restrictions,
        CancellationToken cancellationToken)
    {
        EnsureOpen(connection);
        await using var command = CreateCommand(connection, sql, restrictions);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await MaterializeAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    private static BlueTuskCommand CreateCommand(
        BlueTuskConnection connection,
        string sql,
        string?[]? restrictions)
    {
        var command = new BlueTuskCommand(sql, connection);
        var count = restrictions?.Length ?? 0;
        for (var index = 0; index < count; index++)
        {
            command.Parameters.Add(
                new BlueTuskParameter((object?)restrictions![index] ?? DBNull.Value)
                {
                    ParameterName = $"r{index}",
                    PostgreSqlTypeOid = BlueTuskBuiltInTypes.Text.Id.Oid,
                });
        }

        var required = sql.Contains("@r3", StringComparison.Ordinal) ? 4 :
            sql.Contains("@r2", StringComparison.Ordinal) ? 3 :
            sql.Contains("@r1", StringComparison.Ordinal) ? 2 : 1;
        for (var index = count; index < required; index++)
        {
            command.Parameters.Add(
                new BlueTuskParameter(DBNull.Value)
                {
                    ParameterName = $"r{index}",
                    PostgreSqlTypeOid = BlueTuskBuiltInTypes.Text.Id.Oid,
                });
        }

        return command;
    }

    private static DataTable Materialize(DbDataReader reader)
    {
        var table = CreateResultTable(reader);
        while (reader.Read())
        {
            AddRow(table, reader);
        }

        return table;
    }

    private static async Task<DataTable> MaterializeAsync(
        DbDataReader reader,
        CancellationToken cancellationToken)
    {
        var table = CreateResultTable(reader);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            AddRow(table, reader);
        }

        return table;
    }

    private static DataTable CreateResultTable(DbDataReader reader)
    {
        var table = NewTable("SchemaCollection");
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            var column = table.Columns.Add(
                reader.GetName(ordinal),
                Nullable.GetUnderlyingType(reader.GetFieldType(ordinal)) ??
                reader.GetFieldType(ordinal));
            column.AllowDBNull = true;
        }

        return table;
    }

    private static void AddRow(DataTable table, DbDataReader reader)
    {
        var values = new object[reader.FieldCount];
        _ = reader.GetValues(values);
        table.Rows.Add(values);
    }

    private static void EnsureOpen(BlueTuskConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The connection must be open to query live schema collections.");
        }
    }

    private static DataTable NewTable(string name) =>
        new(name)
        {
            Locale = CultureInfo.InvariantCulture,
        };

    private sealed record CollectionDefinition(
        string Name,
        int Restrictions,
        int IdentifierParts);
}
