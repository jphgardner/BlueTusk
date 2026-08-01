using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.Extensions.Citext.EntityFrameworkCore.Storage.Internal;

internal sealed record BlueTuskCitextTypeMappingOptions(string Schema);

internal sealed class BlueTuskCitextTypeMappingSourcePlugin : IRelationalTypeMappingSourcePlugin
{
    private readonly BlueTuskCitextTypeMapping _scalar;
    private readonly BlueTuskCitextArrayTypeMapping _array;
    private readonly string _schema;

    public BlueTuskCitextTypeMappingSourcePlugin(BlueTuskCitextTypeMappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _schema = options.Schema;
        _scalar = new BlueTuskCitextTypeMapping(_schema);
        _array = new BlueTuskCitextArrayTypeMapping(_schema, _scalar);
    }

    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType is { } requestedClrType
            ? Nullable.GetUnderlyingType(requestedClrType) ?? requestedClrType
            : null;
        var storeType = mappingInfo.StoreTypeName;
        var isArray = false;
        var isCitextStoreType = storeType is not null && IsCitextStoreType(storeType, out isArray);

        if (clrType == typeof(BlueTuskCitext) && (storeType is null || isCitextStoreType && !isArray))
        {
            return _scalar;
        }

        if (clrType == typeof(BlueTuskCitext[]) && (storeType is null || isCitextStoreType && isArray))
        {
            return _array;
        }

        if (clrType is null && isCitextStoreType)
        {
            return isArray ? _array : _scalar;
        }

        return null;
    }

    private bool IsCitextStoreType(string storeType, out bool isArray)
    {
        var candidate = storeType.Trim();
        isArray = candidate.EndsWith("[]", StringComparison.Ordinal);
        if (isArray)
        {
            candidate = candidate[..^2].TrimEnd();
        }

        if (!candidate.Contains('.', StringComparison.Ordinal))
        {
            return string.Equals(candidate.Trim('"'), "citext", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var parsed = BlueTuskTypeName.Parse(candidate);
            return string.Equals(parsed.Schema, _schema, StringComparison.Ordinal) &&
                string.Equals(parsed.Name, "citext", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
