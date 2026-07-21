using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cache can be shared by a data source and active connections; disposing its semaphore would race those readers.")]
internal sealed class BlueTuskTypeMetadataCache
{
    private const string CatalogueQuery =
        "SELECT t.oid::text, n.nspname, t.typname, t.typtype::text, t.typcategory::text, " +
        "NULLIF(t.typelem, 0)::text, NULLIF(t.typbasetype, 0)::text, NULLIF(t.typarray, 0)::text, " +
        "NULLIF(r.rngsubtype, 0)::text, t.typdelim::text " +
        "FROM pg_catalog.pg_type AS t " +
        "JOIN pg_catalog.pg_namespace AS n ON n.oid = t.typnamespace " +
        "LEFT JOIN pg_catalog.pg_range AS r ON r.rngtypid = t.oid OR r.rngmultitypid = t.oid " +
        "ORDER BY t.oid; " +
        "SELECT enumtypid::text, enumlabel " +
        "FROM pg_catalog.pg_enum ORDER BY enumtypid, enumsortorder; " +
        "SELECT t.oid::text, a.attnum::text, a.attname, a.atttypid::text " +
        "FROM pg_catalog.pg_type AS t " +
        "JOIN pg_catalog.pg_attribute AS a ON a.attrelid = t.typrelid " +
        "WHERE t.typtype = 'c' AND a.attnum > 0 AND NOT a.attisdropped " +
        "ORDER BY t.oid, a.attnum; " +
        "SELECT current_setting('lc_monetary'), " +
        "COALESCE(max(fractional_digits) FILTER (WHERE " +
        "((power(10::numeric, -fractional_digits))::money)::numeric <> 0), 0)::text " +
        "FROM generate_series(0, 10) AS digits(fractional_digits)";

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly BlueTuskTypeRegistry? _configuredTypes;
    private BlueTuskTypeRegistry _registry;
    private int _loaded;

    public BlueTuskTypeMetadataCache(BlueTuskTypeRegistry? configuredTypes = null)
    {
        _configuredTypes = configuredTypes;
        _registry = BlueTuskTypeCatalogue.BuildRegistry([], configuredTypes);
    }

    public BlueTuskTypeRegistry Registry => Volatile.Read(ref _registry);

    public async ValueTask EnsureLoadedAsync(
        IBlueTuskPhysicalSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Volatile.Read(ref _loaded) != 0)
        {
            return;
        }

        await LoadAsync(session, force: false, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ReloadAsync(
        IBlueTuskPhysicalSession session,
        CancellationToken cancellationToken) =>
        LoadAsync(session, force: true, cancellationToken);

    private async ValueTask LoadAsync(
        IBlueTuskPhysicalSession session,
        bool force,
        CancellationToken cancellationToken)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && Volatile.Read(ref _loaded) != 0)
            {
                return;
            }

            var result = await session.ExecuteSimpleQueryAsync(CatalogueQuery, cancellationToken).ConfigureAwait(false);
            if (result.ResultSets.Count != 4)
            {
                throw new InvalidOperationException("PostgreSQL type catalogue query returned an unexpected result shape.");
            }

            var resultSet = result.ResultSets[0];
            var enumResultSet = result.ResultSets[1];
            var compositeResultSet = result.ResultSets[2];
            var moneyResultSet = result.ResultSets[3];
            if (enumResultSet.Fields.Count != 2)
            {
                throw new InvalidOperationException("PostgreSQL enum catalogue query returned an unexpected result shape.");
            }

            var enumLabels = new Dictionary<BlueTuskTypeId, List<string>>();
            foreach (var row in enumResultSet.Rows)
            {
                var typeId = new BlueTuskTypeId(ParseUInt32(row.Values[0], "enum type OID"));
                if (!enumLabels.TryGetValue(typeId, out var labels))
                {
                    labels = [];
                    enumLabels.Add(typeId, labels);
                }

                labels.Add(GetRequiredText(row.Values[1], "enum label"));
            }

            if (compositeResultSet.Fields.Count != 4)
            {
                throw new InvalidOperationException(
                    "PostgreSQL composite catalogue query returned an unexpected result shape.");
            }

            var compositeFields = new Dictionary<BlueTuskTypeId, List<BlueTuskCompositeField>>();
            foreach (var row in compositeResultSet.Rows)
            {
                var typeId = new BlueTuskTypeId(ParseUInt32(row.Values[0], "composite type OID"));
                if (!compositeFields.TryGetValue(typeId, out var fields))
                {
                    fields = [];
                    compositeFields.Add(typeId, fields);
                }

                fields.Add(new BlueTuskCompositeField
                {
                    Position = checked((int)ParseUInt32(row.Values[1], "composite field position")),
                    Name = GetRequiredText(row.Values[2], "composite field name"),
                    Type = new BlueTuskTypeId(ParseUInt32(row.Values[3], "composite field type OID")),
                });
            }

            if (moneyResultSet.Fields.Count != 2 || moneyResultSet.Rows.Count != 1)
            {
                throw new InvalidOperationException("PostgreSQL money metadata probe returned an unexpected result shape.");
            }

            var moneyRow = moneyResultSet.Rows[0].Values;
            var moneyFormat = new BlueTuskMoneyFormat(
                GetRequiredText(moneyRow[0], "lc_monetary"),
                checked((int)ParseUInt32(moneyRow[1], "money fractional digits")));
            if (resultSet.Fields.Count != 10)
            {
                throw new InvalidOperationException("PostgreSQL type catalogue query returned an unexpected column count.");
            }

            var types = new BlueTuskCatalogueType[resultSet.Rows.Count];
            for (var index = 0; index < resultSet.Rows.Count; index++)
            {
                var values = resultSet.Rows[index].Values;
                types[index] = new BlueTuskCatalogueType
                {
                    Id = new BlueTuskTypeId(ParseUInt32(values[0], "oid")),
                    Schema = GetRequiredText(values[1], "schema"),
                    Name = GetRequiredText(values[2], "name"),
                    PostgreSqlKind = GetRequiredCharacter(values[3], "kind"),
                    PostgreSqlCategory = GetRequiredCharacter(values[4], "category"),
                    ElementType = ParseOptionalTypeId(values[5]),
                    BaseType = ParseOptionalTypeId(values[6]),
                    ArrayType = ParseOptionalTypeId(values[7]),
                    RangeSubtype = ParseOptionalTypeId(values[8]),
                    Delimiter = GetRequiredCharacter(values[9], "delimiter"),
                    EnumLabels = enumLabels.TryGetValue(
                        new BlueTuskTypeId(ParseUInt32(values[0], "oid")),
                        out var labels)
                        ? labels.ToArray()
                        : Array.Empty<string>(),
                    CompositeFields = compositeFields.TryGetValue(
                        new BlueTuskTypeId(ParseUInt32(values[0], "oid")),
                        out var fields)
                        ? fields.ToArray()
                        : Array.Empty<BlueTuskCompositeField>(),
                };
            }

            Volatile.Write(
                ref _registry,
                BlueTuskTypeCatalogue.BuildRegistry(types, _configuredTypes, moneyFormat));
            Volatile.Write(ref _loaded, 1);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static BlueTuskTypeId? ParseOptionalTypeId(ReadOnlyMemory<byte>? value) =>
        value is null ? null : new BlueTuskTypeId(ParseUInt32(value, "type OID"));

    private static uint ParseUInt32(ReadOnlyMemory<byte>? value, string field)
    {
        var text = GetRequiredText(value, field);
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidOperationException($"PostgreSQL type catalogue returned invalid {field} '{text}'.");
    }

    private static char GetRequiredCharacter(ReadOnlyMemory<byte>? value, string field)
    {
        var text = GetRequiredText(value, field);
        return text.Length == 1
            ? text[0]
            : throw new InvalidOperationException($"PostgreSQL type catalogue returned invalid {field} '{text}'.");
    }

    private static string GetRequiredText(ReadOnlyMemory<byte>? value, string field) =>
        value is { } bytes
            ? Encoding.UTF8.GetString(bytes.Span)
            : throw new InvalidOperationException($"PostgreSQL type catalogue returned null {field}.");
}
