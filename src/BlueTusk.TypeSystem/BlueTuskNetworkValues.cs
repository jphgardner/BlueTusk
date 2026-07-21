using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace BlueTusk.TypeSystem;

public readonly record struct BlueTuskNetworkAddress
{
    public BlueTuskNetworkAddress(IPAddress address, int prefixLength, bool isCidr = false)
    {
        ArgumentNullException.ThrowIfNull(address);
        var maximumPrefix = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => throw new ArgumentException("PostgreSQL inet and cidr support only IPv4 and IPv6.", nameof(address)),
        };
        if (prefixLength < 0 || prefixLength > maximumPrefix)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }

        if (isCidr && !HasZeroHostBits(address, prefixLength))
        {
            throw new ArgumentException("A PostgreSQL cidr value cannot have bits set to the right of its prefix.", nameof(address));
        }

        Address = address;
        PrefixLength = prefixLength;
        IsCidr = isCidr;
    }

    public IPAddress Address { get; }

    public int PrefixLength { get; }

    public bool IsCidr { get; }

    public static BlueTuskNetworkAddress Parse(string value, bool isCidr = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        var separator = text.LastIndexOf('/');
        var addressText = separator < 0 ? text : text[..separator];
        var address = IPAddress.Parse(addressText);
        var prefixLength = separator < 0
            ? address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128
            : int.Parse(text[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);
        return new BlueTuskNetworkAddress(address, prefixLength, isCidr);
    }

    public override string ToString()
    {
        var maximumPrefix = Address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return !IsCidr && PrefixLength == maximumPrefix
            ? Address.ToString()
            : string.Create(CultureInfo.InvariantCulture, $"{Address}/{PrefixLength}");
    }

    internal static bool HasZeroHostBits(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits != 0 && (bytes[wholeBytes] & (0xFF >> remainingBits)) != 0)
        {
            return false;
        }

        for (var index = wholeBytes + (remainingBits == 0 ? 0 : 1); index < bytes.Length; index++)
        {
            if (bytes[index] != 0)
            {
                return false;
            }
        }

        return true;
    }
}

public readonly record struct BlueTuskMacAddress
{
    private const ulong MaximumValue = 0x0000_FFFF_FFFF_FFFF;

    public BlueTuskMacAddress(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaximumValue);
        Value = value;
    }

    public ulong Value { get; }

    public static BlueTuskMacAddress Parse(string value) => new(ParseBytes(value, 6));

    public override string ToString() => Format(Value, 6);

    internal static ulong ParseBytes(string value, int expectedBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<char> hexadecimal = stackalloc char[16];
        var count = 0;
        foreach (var character in value.AsSpan().Trim())
        {
            if (character is ':' or '-' or '.')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(character) || count == hexadecimal.Length)
            {
                throw new FormatException("The PostgreSQL MAC address contains invalid hexadecimal data.");
            }

            hexadecimal[count++] = character;
        }

        if (count != expectedBytes * 2)
        {
            throw new FormatException($"The PostgreSQL MAC address must contain exactly {expectedBytes} bytes.");
        }

        return ulong.Parse(hexadecimal[..count], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    internal static string Format(ulong value, int byteCount) => string.Create(
        (byteCount * 3) - 1,
        (Value: value, ByteCount: byteCount),
        static (characters, state) =>
        {
            for (var index = 0; index < state.ByteCount; index++)
            {
                if (index > 0)
                {
                    characters[(index * 3) - 1] = ':';
                }

                var item = (byte)(state.Value >> ((state.ByteCount - index - 1) * 8));
                characters[index * 3] = Hex(item >> 4);
                characters[(index * 3) + 1] = Hex(item & 0x0F);
            }
        });

    private static char Hex(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);
}

public readonly record struct BlueTuskMacAddress8(ulong Value)
{
    public static BlueTuskMacAddress8 Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var digits = 0;
        foreach (var character in value.AsSpan().Trim())
        {
            if (character is not (':' or '-' or '.'))
            {
                digits++;
            }
        }

        if (digits == 12)
        {
            var eui48 = BlueTuskMacAddress.Parse(value).Value;
            return new BlueTuskMacAddress8(
                ((eui48 & 0xFF_FFFF_000000) << 16) |
                0x0000_00FF_FE00_0000 |
                (eui48 & 0x00_0000_FFFFFF));
        }

        return new BlueTuskMacAddress8(BlueTuskMacAddress.ParseBytes(value, 8));
    }

    public override string ToString() => BlueTuskMacAddress.Format(Value, 8);
}
