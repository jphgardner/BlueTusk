using System.Data.Common;
using System.Text;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NetTopologySuite.Geometries;

namespace BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskPostGisTypeMapping<TGeometry, TProvider> : RelationalTypeMapping
    where TGeometry : Geometry
    where TProvider : class
{
    private readonly string _postgreSqlTypeName;

    public BlueTuskPostGisTypeMapping(
        string schema,
        string typeName,
        string? storeType = null)
        : this(
            new RelationalTypeMappingParameters(
                CreateCoreParameters(),
                storeType ?? BlueTuskSqlIdentifier.Delimit(typeName, schema),
                StoreTypePostfix.None,
                System.Data.DbType.Object),
            BlueTuskSqlIdentifier.Delimit(typeName, schema))
    {
    }

    private BlueTuskPostGisTypeMapping(
        RelationalTypeMappingParameters parameters,
        string postgreSqlTypeName)
        : base(parameters)
    {
        _postgreSqlTypeName = postgreSqlTypeName;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskPostGisTypeMapping<TGeometry, TProvider>(parameters, _postgreSqlTypeName);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        if (parameter is BlueTuskParameter blueTuskParameter)
        {
            blueTuskParameter.PostgreSqlTypeName = _postgreSqlTypeName;
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value)
    {
        var geometry = (TGeometry)value;
        var transport = geometry.ToBlueTuskGeometry();
        return $"'{Convert.ToHexString(transport.GetWellKnownBinary())}'::{StoreType}";
    }

    private static CoreTypeMappingParameters CreateCoreParameters()
    {
        var comparer = new ValueComparer<TGeometry>(
            (left, right) => SpatialEquals(left, right),
            value => SpatialHashCode(value),
            value => SpatialSnapshot(value));
        return new CoreTypeMappingParameters(
            typeof(TGeometry),
            converter: CreateConverter(),
            comparer: comparer,
            keyComparer: comparer);
    }

    private static ValueConverter CreateConverter()
    {
        if (typeof(TProvider) == typeof(BlueTuskGeometry))
        {
            return new ValueConverter<TGeometry, BlueTuskGeometry>(
                value => value.ToBlueTuskGeometry(),
                value => BlueTuskPostGisGeometryConversions.FromGeometry<TGeometry>(value));
        }

        if (typeof(TProvider) == typeof(BlueTuskGeography))
        {
            return new ValueConverter<TGeometry, BlueTuskGeography>(
                value => value.ToBlueTuskGeography(),
                value => BlueTuskPostGisGeometryConversions.FromGeography<TGeometry>(value));
        }

        throw new InvalidOperationException($"Unsupported PostGIS provider type '{typeof(TProvider).Name}'.");
    }

    private static bool SpatialEquals(TGeometry? left, TGeometry? right) =>
        ReferenceEquals(left, right) ||
        left is not null && right is not null &&
        left.SRID == right.SRID &&
        left.EqualsExact(right);

    private static int SpatialHashCode(TGeometry? value) =>
        value is null ? 0 : HashCode.Combine(value.SRID, value.GetHashCode());

    private static TGeometry SpatialSnapshot(TGeometry value) =>
        (TGeometry)value.Copy();
}

internal sealed class BlueTuskPostGisArrayTypeMapping<TGeometry, TProvider> : RelationalTypeMapping
    where TGeometry : Geometry
    where TProvider : class
{
    private readonly string _postgreSqlTypeName;

    public BlueTuskPostGisArrayTypeMapping(
        string schema,
        string typeName,
        RelationalTypeMapping elementMapping,
        string? storeType = null)
        : this(
            CreateParameters(schema, typeName, elementMapping, storeType),
            $"{BlueTuskSqlIdentifier.Delimit(typeName, schema)}[]")
    {
    }

    private BlueTuskPostGisArrayTypeMapping(
        RelationalTypeMappingParameters parameters,
        string postgreSqlTypeName)
        : base(parameters)
    {
        _postgreSqlTypeName = postgreSqlTypeName;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskPostGisArrayTypeMapping<TGeometry, TProvider>(parameters, _postgreSqlTypeName);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        if (parameter is BlueTuskParameter blueTuskParameter)
        {
            blueTuskParameter.PostgreSqlTypeName = _postgreSqlTypeName;
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value)
    {
        var values = (TGeometry[])value;
        var builder = new StringBuilder("ARRAY[");
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(((RelationalTypeMapping)ElementTypeMapping!).GenerateSqlLiteral(values[index]));
        }

        return builder.Append("]::").Append(StoreType).ToString();
    }

    private static RelationalTypeMappingParameters CreateParameters(
        string schema,
        string typeName,
        RelationalTypeMapping elementMapping,
        string? storeType)
    {
        var comparer = new ValueComparer<TGeometry[]>(
            (left, right) => ArrayEquals(left, right),
            value => ArrayHashCode(value),
            value => ArraySnapshot(value));
        return new RelationalTypeMappingParameters(
            new CoreTypeMappingParameters(
                typeof(TGeometry[]),
                converter: CreateConverter(),
                comparer: comparer,
                keyComparer: comparer,
                elementMapping: elementMapping),
            storeType ?? $"{BlueTuskSqlIdentifier.Delimit(typeName, schema)}[]",
            StoreTypePostfix.None,
            System.Data.DbType.Object);
    }

    private static ValueConverter CreateConverter()
    {
        if (typeof(TProvider) == typeof(BlueTuskGeometry))
        {
            return new ValueConverter<TGeometry[], BlueTuskGeometry[]>(
                values => BlueTuskPostGisGeometryConversions.ToGeometryArray(values),
                values => BlueTuskPostGisGeometryConversions.FromGeometryArray<TGeometry>(values));
        }

        if (typeof(TProvider) == typeof(BlueTuskGeography))
        {
            return new ValueConverter<TGeometry[], BlueTuskGeography[]>(
                values => BlueTuskPostGisGeometryConversions.ToGeographyArray(values),
                values => BlueTuskPostGisGeometryConversions.FromGeographyArray<TGeometry>(values));
        }

        throw new InvalidOperationException($"Unsupported PostGIS provider type '{typeof(TProvider).Name}'.");
    }

    private static bool ArrayEquals(TGeometry[]? left, TGeometry[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (ReferenceEquals(left[index], right[index]))
            {
                continue;
            }

            if (left[index] is null || right[index] is null ||
                left[index].SRID != right[index].SRID ||
                !left[index].EqualsExact(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int ArrayHashCode(TGeometry[]? values)
    {
        if (values is null)
        {
            return 0;
        }

        var hash = new HashCode();
        foreach (var value in values)
        {
            hash.Add(value?.SRID ?? 0);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    private static TGeometry[] ArraySnapshot(TGeometry[] values) =>
        values.Select(value => value is null ? null! : (TGeometry)value.Copy()).ToArray();
}
