using NetTopologySuite.Geometries;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Provides translation-only PostGIS operations that are not represented by NetTopologySuite methods.</summary>
public static class BlueTuskPostGisDbFunctionsExtensions
{
    /// <summary>Translates to <c>ST_DWithin</c> using the mapped geometry or geography distance units.</summary>
    public static bool IsWithinDistance(
        this DbFunctions _,
        Geometry left,
        Geometry right,
        double distance) =>
        throw TranslationOnly();

    /// <summary>Translates to the index-aware PostGIS bounding-box overlap operator.</summary>
    public static bool BoundingBoxIntersects(
        this DbFunctions _,
        Geometry left,
        Geometry right) =>
        throw TranslationOnly();

    /// <summary>Translates to <c>ST_Transform</c> and preserves the CLR geometry subtype.</summary>
    public static TGeometry Transform<TGeometry>(
        this DbFunctions _,
        TGeometry geometry,
        int srid)
        where TGeometry : Geometry =>
        throw TranslationOnly();

    /// <summary>Translates to <c>ST_MakeValid</c>.</summary>
    public static Geometry MakeValid(
        this DbFunctions _,
        Geometry geometry) =>
        throw TranslationOnly();

    /// <summary>Translates to <c>ST_Force2D</c> and preserves the CLR geometry subtype.</summary>
    public static TGeometry Force2D<TGeometry>(
        this DbFunctions _,
        TGeometry geometry)
        where TGeometry : Geometry =>
        throw TranslationOnly();

    /// <summary>Translates to <c>ST_AsGeoJSON</c>.</summary>
    public static string AsGeoJson(
        this DbFunctions _,
        Geometry geometry) =>
        throw TranslationOnly();

    private static InvalidOperationException TranslationOnly() =>
        new("PostGIS functions can only be used in translated database queries.");
}
