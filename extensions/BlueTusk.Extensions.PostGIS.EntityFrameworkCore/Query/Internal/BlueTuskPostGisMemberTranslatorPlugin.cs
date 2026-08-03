using System.Reflection;
using BlueTusk.EntityFrameworkCore.Storage;
using BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using NetTopologySuite.Geometries;

namespace BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskPostGisMemberTranslatorPlugin : IMemberTranslatorPlugin
{
    public BlueTuskPostGisMemberTranslatorPlugin(
        ISqlExpressionFactory sqlExpressionFactory,
        IRelationalTypeMappingSource typeMappingSource,
        BlueTuskPostGisTypeMappingOptions options)
    {
        Translators =
        [
            new BlueTuskPostGisMemberTranslator(
                sqlExpressionFactory,
                typeMappingSource,
                options.Schema),
        ];
    }

    public IEnumerable<IMemberTranslator> Translators { get; }
}

internal sealed class BlueTuskPostGisMemberTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource,
    string schema)
    : IMemberTranslator
{
    private static readonly Dictionary<string, string> Members =
        new(StringComparer.Ordinal)
        {
            [nameof(Geometry.Area)] = "st_area",
            [nameof(Geometry.Length)] = "st_length",
            [nameof(Geometry.IsEmpty)] = "st_isempty",
            [nameof(Geometry.IsSimple)] = "st_issimple",
            [nameof(Geometry.IsValid)] = "st_isvalid",
            [nameof(Geometry.NumGeometries)] = "st_numgeometries",
            [nameof(Geometry.NumPoints)] = "st_npoints",
            [nameof(Geometry.SRID)] = "st_srid",
            [nameof(Geometry.Boundary)] = "st_boundary",
            [nameof(Geometry.Envelope)] = "st_envelope",
            [nameof(Geometry.Centroid)] = "st_centroid",
            [nameof(Geometry.InteriorPoint)] = "st_pointonsurface",
            [nameof(Geometry.PointOnSurface)] = "st_pointonsurface",
            [nameof(Point.X)] = "st_x",
            [nameof(Point.Y)] = "st_y",
            [nameof(Point.Z)] = "st_z",
            [nameof(Point.M)] = "st_m",
        };

    private static readonly HashSet<string> GeographyMembers =
        new(StringComparer.Ordinal)
        {
            nameof(Geometry.Area),
            nameof(Geometry.Length),
            nameof(Geometry.Centroid),
        };

    public SqlExpression? Translate(
        SqlExpression? instance,
        MemberInfo member,
        Type returnType,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (instance is null ||
            member.DeclaringType is null ||
            !typeof(Geometry).IsAssignableFrom(member.DeclaringType) ||
            !Members.TryGetValue(member.Name, out var functionName))
        {
            return null;
        }

        var spatialMapping = instance.TypeMapping
            ?? throw new InvalidOperationException(
                $"PostGIS member '{member.Name}' requires a mapped geometry or geography operand.");
        var isGeography = spatialMapping.StoreType.Contains(
            "geography",
            StringComparison.OrdinalIgnoreCase);
        if (isGeography && !GeographyMembers.Contains(member.Name))
        {
            throw new InvalidOperationException(
                $"PostGIS member '{member.Name}' is only supported for geometry operands; " +
                "the query operand is mapped as geography.");
        }

        var resultMapping = typeof(Geometry).IsAssignableFrom(returnType)
            ? typeMappingSource.FindMapping(
                returnType,
                BlueTuskSqlIdentifier.Delimit(isGeography ? "geography" : "geometry", schema),
                keyOrIndex: false)
            : typeMappingSource.FindMapping(returnType);
        return sqlExpressionFactory.Function(
            schema,
            functionName,
            [instance],
            nullable: true,
            argumentsPropagateNullability: [true],
            returnType,
            resultMapping);
    }
}
