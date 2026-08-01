using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Storage;
using NetTopologySuite.Geometries;

namespace BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Storage.Internal;

internal sealed record BlueTuskPostGisTypeMappingOptions(string Schema);

internal sealed class BlueTuskPostGisTypeMappingSourcePlugin(
    BlueTuskPostGisTypeMappingOptions options)
    : IRelationalTypeMappingSourcePlugin
{
    private static readonly HashSet<Type> SupportedTypes =
    [
        typeof(Geometry),
        typeof(Point),
        typeof(LineString),
        typeof(Polygon),
        typeof(MultiPoint),
        typeof(MultiLineString),
        typeof(MultiPolygon),
        typeof(GeometryCollection),
    ];

    private readonly string _schema = options?.Schema
        ?? throw new ArgumentNullException(nameof(options));

    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType is { } requestedClrType
            ? Nullable.GetUnderlyingType(requestedClrType) ?? requestedClrType
            : null;
        var storeType = mappingInfo.StoreTypeName;

        var isArray = clrType?.IsArray == true;
        var geometryType = isArray ? clrType!.GetElementType() : clrType;
        if (geometryType is not null && !SupportedTypes.Contains(geometryType))
        {
            return null;
        }

        SpatialStoreType spatialType;
        if (storeType is null)
        {
            if (geometryType is null)
            {
                return null;
            }

            spatialType = new SpatialStoreType("geometry", isArray, null);
        }
        else if (!TryParseStoreType(storeType, out spatialType))
        {
            return null;
        }

        if (clrType is not null && isArray != spatialType.IsArray)
        {
            return null;
        }

        geometryType ??= typeof(Geometry);
        if (!spatialType.IsArray)
        {
            return CreateScalarMapping(geometryType, spatialType.Name, spatialType.StoreType);
        }

        var elementStoreType = spatialType.StoreType is null
            ? null
            : spatialType.StoreType.Trim()[..^2].TrimEnd();
        var element = CreateScalarMapping(geometryType, spatialType.Name, elementStoreType);
        return CreateArrayMapping(
            geometryType,
            spatialType.Name,
            element,
            spatialType.StoreType);
    }

    private RelationalTypeMapping CreateScalarMapping(
        Type geometryType,
        string typeName,
        string? storeType)
    {
        var providerType = string.Equals(typeName, "geography", StringComparison.Ordinal)
            ? typeof(BlueTuskGeography)
            : typeof(BlueTuskGeometry);
        var mappingType = typeof(BlueTuskPostGisTypeMapping<,>)
            .MakeGenericType(geometryType, providerType);
        return (RelationalTypeMapping)Activator.CreateInstance(
            mappingType,
            _schema,
            typeName,
            storeType)!;
    }

    private RelationalTypeMapping CreateArrayMapping(
        Type geometryType,
        string typeName,
        RelationalTypeMapping elementMapping,
        string? storeType)
    {
        var providerType = string.Equals(typeName, "geography", StringComparison.Ordinal)
            ? typeof(BlueTuskGeography)
            : typeof(BlueTuskGeometry);
        var mappingType = typeof(BlueTuskPostGisArrayTypeMapping<,>)
            .MakeGenericType(geometryType, providerType);
        return (RelationalTypeMapping)Activator.CreateInstance(
            mappingType,
            _schema,
            typeName,
            elementMapping,
            storeType)!;
    }

    private bool TryParseStoreType(string storeType, out SpatialStoreType spatialType)
    {
        var candidate = storeType.Trim();
        var isArray = candidate.EndsWith("[]", StringComparison.Ordinal);
        if (isArray)
        {
            candidate = candidate[..^2].TrimEnd();
        }

        var baseType = candidate;
        var modifierStart = candidate.IndexOf('(');
        if (modifierStart >= 0)
        {
            if (candidate[^1] != ')')
            {
                spatialType = default;
                return false;
            }

            baseType = candidate[..modifierStart].TrimEnd();
        }

        string typeName;
        if (!baseType.Contains('.', StringComparison.Ordinal))
        {
            typeName = baseType.Trim('"').ToLowerInvariant();
        }
        else
        {
            try
            {
                var parsed = BlueTuskTypeName.Parse(baseType);
                if (!string.Equals(parsed.Schema, _schema, StringComparison.Ordinal))
                {
                    spatialType = default;
                    return false;
                }

                typeName = parsed.Name;
            }
            catch (FormatException)
            {
                spatialType = default;
                return false;
            }
        }

        if (typeName is not ("geometry" or "geography"))
        {
            spatialType = default;
            return false;
        }

        spatialType = new SpatialStoreType(typeName, isArray, storeType);
        return true;
    }

    private readonly record struct SpatialStoreType(
        string Name,
        bool IsArray,
        string? StoreType);
}
