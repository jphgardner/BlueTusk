using System.Globalization;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace BlueTusk.Extensions.PostGIS.EntityFrameworkCore;

/// <summary>Converts between BlueTusk's lossless PostGIS transport values and NetTopologySuite geometries.</summary>
public static class BlueTuskPostGisGeometryConversions
{
    /// <summary>Encodes a NetTopologySuite geometry as an immutable EWKB transport value.</summary>
    public static BlueTuskGeometry ToBlueTuskGeometry(this Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return BlueTuskGeometry.FromWellKnownBinary(Write(geometry));
    }

    /// <summary>Encodes a NetTopologySuite geometry as an immutable EWKB geography transport value.</summary>
    public static BlueTuskGeography ToBlueTuskGeography(this Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return BlueTuskGeography.FromWellKnownBinary(Write(geometry));
    }

    /// <summary>Decodes a BlueTusk geometry transport value into the rich NetTopologySuite model.</summary>
    public static Geometry ToNetTopologySuite(this BlueTuskGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return geometry.HasWellKnownBinary
            ? Read(geometry.GetWellKnownBinary().ToArray())
            : ReadText(geometry.GetText());
    }

    /// <summary>Decodes a BlueTusk geography transport value into the rich NetTopologySuite model.</summary>
    public static Geometry ToNetTopologySuite(this BlueTuskGeography geography)
    {
        ArgumentNullException.ThrowIfNull(geography);
        return geography.HasWellKnownBinary
            ? Read(geography.GetWellKnownBinary().ToArray())
            : ReadText(geography.GetText());
    }

    internal static TGeometry FromGeometry<TGeometry>(BlueTuskGeometry geometry)
        where TGeometry : Geometry =>
        Cast<TGeometry>(geometry.ToNetTopologySuite());

    internal static TGeometry FromGeography<TGeometry>(BlueTuskGeography geography)
        where TGeometry : Geometry =>
        Cast<TGeometry>(geography.ToNetTopologySuite());

    internal static BlueTuskGeometry[] ToGeometryArray<TGeometry>(TGeometry[] values)
        where TGeometry : Geometry =>
        values.Select(value => value is null ? null! : value.ToBlueTuskGeometry()).ToArray();

    internal static BlueTuskGeography[] ToGeographyArray<TGeometry>(TGeometry[] values)
        where TGeometry : Geometry =>
        values.Select(value => value is null ? null! : value.ToBlueTuskGeography()).ToArray();

    internal static TGeometry[] FromGeometryArray<TGeometry>(BlueTuskGeometry[] values)
        where TGeometry : Geometry =>
        values.Select(value => value is null ? null! : FromGeometry<TGeometry>(value)).ToArray();

    internal static TGeometry[] FromGeographyArray<TGeometry>(BlueTuskGeography[] values)
        where TGeometry : Geometry =>
        values.Select(value => value is null ? null! : FromGeography<TGeometry>(value)).ToArray();

    private static byte[] Write(Geometry geometry) =>
        new PostGisWriter
        {
            HandleOrdinates = Ordinates.XYZM,
        }.Write(geometry);

    private static Geometry Read(byte[] value) =>
        new PostGisReader
        {
            HandleOrdinates = Ordinates.XYZM,
        }.Read(value);

    private static Geometry ReadText(string value)
    {
        var text = value.AsSpan().Trim();
        var hexadecimal = text.StartsWith("\\x", StringComparison.OrdinalIgnoreCase)
            ? text[2..]
            : text;
        if (hexadecimal.Length >= 10 &&
            hexadecimal.Length % 2 == 0 &&
            IsHexadecimal(hexadecimal))
        {
            return Read(Convert.FromHexString(hexadecimal));
        }

        var srid = 0;
        if (text.StartsWith("SRID=", StringComparison.OrdinalIgnoreCase))
        {
            var separator = text.IndexOf(';');
            if (separator < 6 ||
                !int.TryParse(text[5..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out srid))
            {
                throw new FormatException("The PostGIS EWKT value contains an invalid SRID prefix.");
            }

            text = text[(separator + 1)..];
        }

        var geometry = new WKTReader().Read(text.ToString());
        geometry.SRID = srid;
        return geometry;
    }

    private static bool IsHexadecimal(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static TGeometry Cast<TGeometry>(Geometry geometry)
        where TGeometry : Geometry =>
        geometry as TGeometry
        ?? throw new InvalidOperationException(
            $"PostGIS returned '{geometry.GeometryType}', which cannot materialize as '{typeof(TGeometry).Name}'.");
}
