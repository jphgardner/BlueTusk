using System.Net;
using System.Net.Sockets;

namespace BlueTusk.TypeSystem;

public abstract class BlueTuskNetworkAddressCodec : BlueTuskCodec<BlueTuskNetworkAddress>
{
    private const byte PostgreSqlAddressFamilyIpv4 = 2;
    private const byte PostgreSqlAddressFamilyIpv6 = 3;
    private readonly bool _isCidr;

    protected BlueTuskNetworkAddressCodec(bool isCidr) => _isCidr = isCidr;

    public override BlueTuskNetworkAddress ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskNetworkAddress.Parse(reader.ReadRemainingUtf8(), _isCidr);
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining < 8)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values require a four-byte header and an IP address.");
        }

        var family = reader.ReadByte();
        var prefixLength = reader.ReadByte();
        var cidrMarker = reader.ReadByte();
        var addressLength = reader.ReadByte();
        var expectedLength = family switch
        {
            PostgreSqlAddressFamilyIpv4 => 4,
            PostgreSqlAddressFamilyIpv6 => 16,
            _ => throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary value contains unknown address family {family}."),
        };
        if (addressLength != expectedLength || reader.Remaining != expectedLength || cidrMarker > 1)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary header does not match its address payload.");
        }

        return new BlueTuskNetworkAddress(
            new IPAddress(reader.ReadBytes(expectedLength)),
            prefixLength,
            _isCidr);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskNetworkAddress value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (value.IsCidr != _isCidr)
        {
            throw new InvalidOperationException(
                $"A {(value.IsCidr ? "cidr" : "inet")} value cannot be encoded as PostgreSQL {type.Name}.");
        }

        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
            return;
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        var address = value.Address.GetAddressBytes();
        writer.WriteByte(value.Address.AddressFamily switch
        {
            AddressFamily.InterNetwork => PostgreSqlAddressFamilyIpv4,
            AddressFamily.InterNetworkV6 => PostgreSqlAddressFamilyIpv6,
            _ => throw new InvalidOperationException("PostgreSQL supports only IPv4 and IPv6 addresses."),
        });
        writer.WriteByte(checked((byte)value.PrefixLength));
        writer.WriteByte(_isCidr ? (byte)1 : (byte)0);
        writer.WriteByte(checked((byte)address.Length));
        writer.WriteBytes(address);
    }
}

public sealed class BlueTuskInetCodec : BlueTuskNetworkAddressCodec
{
    public BlueTuskInetCodec()
        : base(isCidr: false)
    {
    }
}

public sealed class BlueTuskCidrCodec : BlueTuskNetworkAddressCodec
{
    public BlueTuskCidrCodec()
        : base(isCidr: true)
    {
    }
}

public sealed class BlueTuskMacAddressCodec : BlueTuskCodec<BlueTuskMacAddress>
{
    public override BlueTuskMacAddress ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Text => BlueTuskMacAddress.Parse(reader.ReadRemainingUtf8()),
            BlueTuskDataFormat.Binary when reader.Remaining == 6 =>
                new BlueTuskMacAddress(ReadUnsigned(ref reader, 6)),
            BlueTuskDataFormat.Binary => throw InvalidBinary(type, 6),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskMacAddress value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            WriteUnsigned(ref writer, value.Value, 6);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    internal static ulong ReadUnsigned(ref BlueTuskReader reader, int byteCount)
    {
        ulong value = 0;
        for (var index = 0; index < byteCount; index++)
        {
            value = (value << 8) | reader.ReadByte();
        }

        return value;
    }

    internal static void WriteUnsigned(ref BlueTuskWriter writer, ulong value, int byteCount)
    {
        for (var index = byteCount - 1; index >= 0; index--)
        {
            writer.WriteByte((byte)(value >> (index * 8)));
        }
    }

    private static InvalidOperationException InvalidBinary(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskMacAddress8Codec : BlueTuskCodec<BlueTuskMacAddress8>
{
    public override BlueTuskMacAddress8 ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskMacAddress8.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining is not (6 or 8))
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values must contain six or eight bytes.");
        }

        if (reader.Remaining == 8)
        {
            return new BlueTuskMacAddress8(reader.ReadUInt64BigEndian());
        }

        var eui48 = BlueTuskMacAddressCodec.ReadUnsigned(ref reader, 6);
        return new BlueTuskMacAddress8(
            ((eui48 & 0xFF_FFFF_000000) << 16) |
            0x0000_00FF_FE00_0000 |
            (eui48 & 0x00_0000_FFFFFF));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskMacAddress8 value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteUInt64BigEndian(value.Value);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}
