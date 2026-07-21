using System.Buffers.Binary;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskGeometricCodecTests
{
    private static readonly BlueTuskPoint[] SamplePoints =
    [
        new BlueTuskPoint(1.5, -2.25),
        new BlueTuskPoint(3, 4),
        new BlueTuskPoint(-5.5, 6.75),
    ];

    [Fact]
    public void Fixed_width_geometric_values_round_trip_in_text_and_binary()
    {
        AssertRoundTrip(new BlueTuskPointCodec(), BlueTuskBuiltInTypes.Point, SamplePoints[0], 16);
        AssertRoundTrip(new BlueTuskLineCodec(), BlueTuskBuiltInTypes.Line, new BlueTuskLine(1, 2, 3), 24);
        AssertRoundTrip(
            new BlueTuskLineSegmentCodec(),
            BlueTuskBuiltInTypes.LineSegment,
            new BlueTuskLineSegment(SamplePoints[0], SamplePoints[1]),
            32);
        AssertRoundTrip(
            new BlueTuskBoxCodec(),
            BlueTuskBuiltInTypes.Box,
            new BlueTuskBox(SamplePoints[0], SamplePoints[1]),
            32);
        AssertRoundTrip(
            new BlueTuskCircleCodec(),
            BlueTuskBuiltInTypes.Circle,
            new BlueTuskCircle(SamplePoints[0], 3.5),
            24);
    }

    [Fact]
    public void Variable_width_geometric_values_round_trip_in_text_and_binary()
    {
        var openPath = new BlueTuskPath(SamplePoints, isClosed: false);
        var closedPath = new BlueTuskPath(SamplePoints, isClosed: true);
        var polygon = new BlueTuskPolygon(SamplePoints);

        AssertRoundTrip(
            new BlueTuskPathCodec(),
            BlueTuskBuiltInTypes.Path,
            openPath,
            5 + (SamplePoints.Length * 16));
        AssertRoundTrip(
            new BlueTuskPathCodec(),
            BlueTuskBuiltInTypes.Path,
            closedPath,
            5 + (SamplePoints.Length * 16));
        AssertRoundTrip(
            new BlueTuskPolygonCodec(),
            BlueTuskBuiltInTypes.Polygon,
            polygon,
            4 + (SamplePoints.Length * 16));
    }

    [Fact]
    public void Binary_layouts_match_postgresql_send_functions()
    {
        var pointBytes = Write(
            new BlueTuskPointCodec(),
            BlueTuskBuiltInTypes.Point,
            new BlueTuskPoint(1.5, -2.25),
            BlueTuskDataFormat.Binary);
        var boxBytes = Write(
            new BlueTuskBoxCodec(),
            BlueTuskBuiltInTypes.Box,
            new BlueTuskBox(new BlueTuskPoint(1, 2), new BlueTuskPoint(3, 4)),
            BlueTuskDataFormat.Binary);
        var pathBytes = Write(
            new BlueTuskPathCodec(),
            BlueTuskBuiltInTypes.Path,
            new BlueTuskPath(SamplePoints[..2], isClosed: true),
            BlueTuskDataFormat.Binary);

        Assert.Equal(Convert.FromHexString("3FF8000000000000C002000000000000"), pointBytes);
        Assert.Equal(3, ReadDouble(boxBytes, 0));
        Assert.Equal(4, ReadDouble(boxBytes, 8));
        Assert.Equal(1, ReadDouble(boxBytes, 16));
        Assert.Equal(2, ReadDouble(boxBytes, 24));
        Assert.Equal(1, pathBytes[0]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(pathBytes.AsSpan(1)));
    }

    [Fact]
    public void Box_normalizes_corners_using_postgresql_nan_ordering()
    {
        var value = new BlueTuskBox(new BlueTuskPoint(double.NaN, 1), new BlueTuskPoint(2, 3));

        Assert.True(double.IsNaN(value.High.X));
        Assert.Equal(3, value.High.Y);
        Assert.Equal(2, value.Low.X);
        Assert.Equal(1, value.Low.Y);
        Assert.Equal("(NaN,3),(2,1)", value.ToString());
    }

    [Fact]
    public void Paths_and_polygons_have_deep_value_semantics_and_defensive_storage()
    {
        var source = SamplePoints.ToArray();
        var path = new BlueTuskPath(source, isClosed: false);
        var polygon = new BlueTuskPolygon(source);
        source[0] = default;

        Assert.Equal(SamplePoints[0], path[0]);
        Assert.Equal(SamplePoints[0], polygon[0]);
        Assert.Equal(new BlueTuskPath(SamplePoints, isClosed: false), path);
        Assert.NotEqual(new BlueTuskPath(SamplePoints, isClosed: true), path);
        Assert.Equal(new BlueTuskPolygon(SamplePoints), polygon);
    }

    [Fact]
    public void Invalid_geometric_values_and_binary_layouts_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new BlueTuskLine(0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BlueTuskCircle(new BlueTuskPoint(1, 2), -1));
        Assert.Throws<ArgumentException>(() => new BlueTuskPath([], isClosed: false));
        Assert.Throws<ArgumentException>(() => new BlueTuskPolygon([]));

        Assert.Throws<InvalidOperationException>(() =>
            ReadPoint(new byte[15]));

        var invalidPath = Convert.FromHexString("0200000001").Concat(new byte[16]).ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            ReadPath(invalidPath));

        var invalidPolygon = Convert.FromHexString("00000002").Concat(new byte[16]).ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            ReadPolygon(invalidPolygon));
    }

    private static void AssertRoundTrip<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        int binaryLength)
    {
        foreach (var format in new[] { BlueTuskDataFormat.Text, BlueTuskDataFormat.Binary })
        {
            var bytes = Write(codec, type, value, format);
            var reader = new BlueTuskReader(bytes);
            Assert.Equal(value, codec.ReadTyped(ref reader, format, type));
            Assert.Equal(0, reader.Remaining);
            if (format == BlueTuskDataFormat.Binary)
            {
                Assert.Equal(binaryLength, bytes.Length);
            }
        }
    }

    private static byte[] Write<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        Span<byte> destination = stackalloc byte[256];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, type);
        return destination[..writer.WrittenCount].ToArray();
    }

    private static double ReadDouble(byte[] bytes, int offset) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset)));

    private static BlueTuskPoint ReadPoint(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskPointCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Point);
    }

    private static BlueTuskPath ReadPath(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskPathCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Path);
    }

    private static BlueTuskPolygon ReadPolygon(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskPolygonCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Polygon);
    }
}
