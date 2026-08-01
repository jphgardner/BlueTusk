using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.Extensions.PgVector.EntityFrameworkCore.Storage.Internal;

internal sealed record BlueTuskPgVectorTypeMappingOptions(string Schema);

internal sealed class BlueTuskPgVectorTypeMappingSourcePlugin : IRelationalTypeMappingSourcePlugin
{
    private readonly string _schema;
    private readonly BlueTuskPgVectorTypeMapping _scalar;
    private readonly BlueTuskPgVectorArrayTypeMapping _array;

    public BlueTuskPgVectorTypeMappingSourcePlugin(BlueTuskPgVectorTypeMappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _schema = options.Schema;
        _scalar = new BlueTuskPgVectorTypeMapping(_schema);
        _array = new BlueTuskPgVectorArrayTypeMapping(_schema, _scalar);
    }

    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType is { } requestedClrType
            ? Nullable.GetUnderlyingType(requestedClrType) ?? requestedClrType
            : null;
        var storeType = mappingInfo.StoreTypeName;
        var isArray = false;
        var isVectorStoreType = storeType is not null && IsVectorStoreType(storeType, out isArray);

        if (clrType == typeof(BlueTuskVector) && (storeType is null || isVectorStoreType && !isArray))
        {
            return storeType is null ? _scalar : new BlueTuskPgVectorTypeMapping(_schema, storeType);
        }

        if (clrType == typeof(BlueTuskVector[]) && (storeType is null || isVectorStoreType && isArray))
        {
            if (storeType is null)
            {
                return _array;
            }

            var elementStoreType = storeType.Trim()[..^2].TrimEnd();
            var element = new BlueTuskPgVectorTypeMapping(_schema, elementStoreType);
            return new BlueTuskPgVectorArrayTypeMapping(_schema, element, storeType);
        }

        if (clrType is null && isVectorStoreType)
        {
            if (!isArray)
            {
                return new BlueTuskPgVectorTypeMapping(_schema, storeType);
            }

            var elementStoreType = storeType!.Trim()[..^2].TrimEnd();
            var element = new BlueTuskPgVectorTypeMapping(_schema, elementStoreType);
            return new BlueTuskPgVectorArrayTypeMapping(_schema, element, storeType);
        }

        return null;
    }

    private bool IsVectorStoreType(string storeType, out bool isArray)
    {
        var candidate = storeType.Trim();
        isArray = candidate.EndsWith("[]", StringComparison.Ordinal);
        if (isArray)
        {
            candidate = candidate[..^2].TrimEnd();
        }

        var modifierStart = candidate.LastIndexOf('(');
        if (modifierStart >= 0)
        {
            if (candidate[^1] != ')' ||
                !int.TryParse(candidate.AsSpan(modifierStart + 1, candidate.Length - modifierStart - 2), out var dimensions) ||
                dimensions is < 1 or > BlueTuskVector.MaxDimensions)
            {
                return false;
            }

            candidate = candidate[..modifierStart].TrimEnd();
        }

        if (!candidate.Contains('.', StringComparison.Ordinal))
        {
            return string.Equals(candidate.Trim('"'), "vector", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var parsed = BlueTuskTypeName.Parse(candidate);
            return string.Equals(parsed.Schema, _schema, StringComparison.Ordinal) &&
                string.Equals(parsed.Name, "vector", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
