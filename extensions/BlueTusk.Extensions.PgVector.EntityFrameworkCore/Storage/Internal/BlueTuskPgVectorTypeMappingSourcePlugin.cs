using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.Extensions.PgVector.EntityFrameworkCore.Storage.Internal;

internal sealed record BlueTuskPgVectorTypeMappingOptions(string Schema);

internal sealed class BlueTuskPgVectorTypeMappingSourcePlugin : IRelationalTypeMappingSourcePlugin
{
    private static readonly VectorTypeDefinition[] Definitions =
    [
        new(typeof(BlueTuskVector), "vector", BlueTuskVector.MaxDimensions),
        new(typeof(BlueTuskHalfVector), "halfvec", BlueTuskHalfVector.MaxDimensions),
        new(typeof(BlueTuskSparseVector), "sparsevec", BlueTuskSparseVector.MaxDimensions),
    ];

    private readonly Dictionary<Type, RelationalTypeMapping> _defaultMappings = [];
    private readonly string _schema;

    public BlueTuskPgVectorTypeMappingSourcePlugin(BlueTuskPgVectorTypeMappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _schema = options.Schema;
        foreach (var definition in Definitions)
        {
            var scalar = CreateScalarMapping(definition, null);
            _defaultMappings.Add(definition.ClrType, scalar);
            _defaultMappings.Add(
                definition.ClrType.MakeArrayType(),
                CreateArrayMapping(definition, scalar, null));
        }
    }

    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType is { } requestedClrType
            ? Nullable.GetUnderlyingType(requestedClrType) ?? requestedClrType
            : null;
        var storeType = mappingInfo.StoreTypeName;
        if (storeType is null)
        {
            return clrType is not null && _defaultMappings.TryGetValue(clrType, out var defaultMapping)
                ? defaultMapping
                : null;
        }

        if (!TryParseStoreType(storeType, out var definition, out var isArray))
        {
            return null;
        }

        var expectedClrType = isArray ? definition.ClrType.MakeArrayType() : definition.ClrType;
        if (clrType is not null && clrType != expectedClrType)
        {
            return null;
        }

        if (!isArray)
        {
            return CreateScalarMapping(definition, storeType);
        }

        var elementStoreType = storeType.Trim()[..^2].TrimEnd();
        var element = CreateScalarMapping(definition, elementStoreType);
        return CreateArrayMapping(definition, element, storeType);
    }

    private RelationalTypeMapping CreateScalarMapping(
        VectorTypeDefinition definition,
        string? storeType) =>
        definition.Name switch
        {
            "vector" => new BlueTuskPgVectorTypeMapping<BlueTuskVector>(_schema, definition.Name, storeType),
            "halfvec" => new BlueTuskPgVectorTypeMapping<BlueTuskHalfVector>(_schema, definition.Name, storeType),
            "sparsevec" => new BlueTuskPgVectorTypeMapping<BlueTuskSparseVector>(_schema, definition.Name, storeType),
            _ => throw new InvalidOperationException($"Unknown pgvector type {definition.Name}."),
        };

    private RelationalTypeMapping CreateArrayMapping(
        VectorTypeDefinition definition,
        RelationalTypeMapping elementMapping,
        string? storeType) =>
        definition.Name switch
        {
            "vector" => new BlueTuskPgVectorArrayTypeMapping<BlueTuskVector>(
                _schema,
                definition.Name,
                elementMapping,
                storeType),
            "halfvec" => new BlueTuskPgVectorArrayTypeMapping<BlueTuskHalfVector>(
                _schema,
                definition.Name,
                elementMapping,
                storeType),
            "sparsevec" => new BlueTuskPgVectorArrayTypeMapping<BlueTuskSparseVector>(
                _schema,
                definition.Name,
                elementMapping,
                storeType),
            _ => throw new InvalidOperationException($"Unknown pgvector type {definition.Name}."),
        };

    private bool TryParseStoreType(
        string storeType,
        out VectorTypeDefinition definition,
        out bool isArray)
    {
        var candidate = storeType.Trim();
        isArray = candidate.EndsWith("[]", StringComparison.Ordinal);
        if (isArray)
        {
            candidate = candidate[..^2].TrimEnd();
        }

        var modifierStart = candidate.LastIndexOf('(');
        var dimensions = -1;
        if (modifierStart >= 0)
        {
            if (candidate[^1] != ')' ||
                !int.TryParse(
                    candidate.AsSpan(modifierStart + 1, candidate.Length - modifierStart - 2),
                    out dimensions))
            {
                definition = default;
                return false;
            }

            candidate = candidate[..modifierStart].TrimEnd();
        }

        foreach (var available in Definitions)
        {
            if (!MatchesTypeName(candidate, available.Name) ||
                dimensions != -1 && dimensions is < 1 ||
                dimensions > available.MaxDimensions)
            {
                continue;
            }

            definition = available;
            return true;
        }

        definition = default;
        return false;
    }

    private bool MatchesTypeName(string candidate, string typeName)
    {
        if (!candidate.Contains('.', StringComparison.Ordinal))
        {
            return string.Equals(candidate.Trim('"'), typeName, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var parsed = BlueTuskTypeName.Parse(candidate);
            return string.Equals(parsed.Schema, _schema, StringComparison.Ordinal) &&
                string.Equals(parsed.Name, typeName, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private readonly record struct VectorTypeDefinition(Type ClrType, string Name, int MaxDimensions);
}
