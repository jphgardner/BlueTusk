using System.Net;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskNetworkCodecTests
{
    [Fact]
    public void Inet_binary_preserves_ipv4_host_bits_and_prefix()
    {
        var value = BlueTuskNetworkAddress.Parse("192.168.1.5/24");
        var codec = new BlueTuskInetCodec();
        var bytes = Write(codec, BlueTuskBuiltInTypes.Inet, value, BlueTuskDataFormat.Binary);

        Assert.Equal(Convert.FromHexString("02180004C0A80105"), bytes);
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.Inet, value);
    }

    [Fact]
    public void Cidr_binary_preserves_ipv6_network_and_kind()
    {
        var value = BlueTuskNetworkAddress.Parse("2001:db8::/32", isCidr: true);
        var codec = new BlueTuskCidrCodec();
        var bytes = Write(codec, BlueTuskBuiltInTypes.Cidr, value, BlueTuskDataFormat.Binary);

        Assert.Equal(3, bytes[0]);
        Assert.Equal(32, bytes[1]);
        Assert.Equal(1, bytes[2]);
        Assert.Equal(16, bytes[3]);
        Assert.Equal(IPAddress.Parse("2001:db8::"), new IPAddress(bytes.AsSpan(4)));
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.Cidr, value);
    }

    [Fact]
    public void Cidr_rejects_nonzero_host_bits()
    {
        Assert.Throws<ArgumentException>(() =>
            BlueTuskNetworkAddress.Parse("192.168.1.5/24", isCidr: true));
    }

    [Fact]
    public void Macaddr_round_trips_exactly_six_bytes()
    {
        var value = BlueTuskMacAddress.Parse("08:00:2b:01:02:03");
        var codec = new BlueTuskMacAddressCodec();

        Assert.Equal("08:00:2b:01:02:03", value.ToString());
        Assert.Equal(
            Convert.FromHexString("08002B010203"),
            Write(codec, BlueTuskBuiltInTypes.Macaddr, value, BlueTuskDataFormat.Binary));
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.Macaddr, value);
    }

    [Fact]
    public void Macaddr8_expands_eui48_with_fffe_and_accepts_six_byte_binary()
    {
        var expected = BlueTuskMacAddress8.Parse("08:00:2b:01:02:03");
        var codec = new BlueTuskMacAddress8Codec();
        var reader = new BlueTuskReader(Convert.FromHexString("08002B010203"));

        Assert.Equal("08:00:2b:ff:fe:01:02:03", expected.ToString());
        Assert.Equal(
            expected,
            codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, BlueTuskBuiltInTypes.Macaddr8));
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.Macaddr8, expected);
    }

    private static void AssertRoundTrip<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value)
    {
        foreach (var format in new[] { BlueTuskDataFormat.Text, BlueTuskDataFormat.Binary })
        {
            var bytes = Write(codec, type, value, format);
            var reader = new BlueTuskReader(bytes);
            Assert.Equal(value, codec.ReadTyped(ref reader, format, type));
            Assert.Equal(0, reader.Remaining);
        }
    }

    private static byte[] Write<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        Span<byte> destination = stackalloc byte[128];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, type);
        return destination[..writer.WrittenCount].ToArray();
    }
}
