using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace BlueTusk.Compatibility;

internal static class BlueTuskRuntime
{
#if !NET6_0_OR_GREATER
    private static readonly Random LegacyRandom = new();
#endif

    public static void Shuffle<T>(T[] values)
    {
#if NET8_0_OR_GREATER
        Random.Shared.Shuffle(values);
#elif NET6_0_OR_GREATER
        ShuffleCore(Random.Shared, values);
#else
        lock (LegacyRandom)
        {
            ShuffleCore(LegacyRandom, values);
        }
#endif
    }

#if !NET8_0_OR_GREATER
    private static void ShuffleCore<T>(Random random, T[] values)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (values[index], values[other]) = (values[other], values[index]);
        }
    }
#endif

    public static void ValidateBufferArguments(byte[] buffer, int offset, int count)
    {
        BlueTuskGuard.NotNull(buffer);
        BlueTuskGuard.NotNegative(offset);
        BlueTuskGuard.NotNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("The buffer is smaller than the requested offset and count.");
        }
    }

    public static string ToHexStringLower(ReadOnlySpan<byte> bytes)
    {
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(bytes);
#else
        return Convert.ToHexString(bytes).ToLowerInvariant();
#endif
    }

    public static byte[] HmacSha256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
#if NET6_0_OR_GREATER
        return HMACSHA256.HashData(key, data);
#else
        var keyBytes = key.ToArray();
        try
        {
            using var hmac = new HMACSHA256(keyBytes);
            var output = new byte[32];
            if (!hmac.TryComputeHash(data, output, out _))
            {
                throw new CryptographicException("The HMAC output buffer is too small.");
            }

            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
#endif
    }

    public const long TicksPerMicrosecond = 10;

#if NET6_0_OR_GREATER
    public static int MaxArrayLength => Array.MaxLength;

    public static string Format(IFormatProvider? provider, [InterpolatedStringHandlerArgument(nameof(provider))] ref DefaultInterpolatedStringHandler handler) =>
        string.Create(provider, ref handler);
#else
    public const int MaxArrayLength = 0X7FFFFFC7;

    public static string Format(IFormatProvider? provider, FormattableString value) => value.ToString(provider);
#endif

    public static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    public static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    public static bool IsAsciiLetterOrDigit(char value) => IsAsciiDigit(value) || IsAsciiLetter(value);

    public static bool IsAsciiHexDigit(char value) => IsAsciiDigit(value) || value is >= 'A' and <= 'F' or >= 'a' and <= 'f';

    public static Guid ReadGuidBigEndian(ReadOnlySpan<byte> source)
    {
#if NET8_0_OR_GREATER
        return new Guid(source, bigEndian: true);
#else
        Span<byte> bytes = stackalloc byte[16];
        source.CopyTo(bytes);
        ReverseGuidFields(bytes);
        return new Guid(bytes);
#endif
    }

    public static bool TryWriteGuidBigEndian(Guid value, Span<byte> destination, out int bytesWritten)
    {
#if NET8_0_OR_GREATER
        return value.TryWriteBytes(destination, bigEndian: true, out bytesWritten);
#else
        if (!value.TryWriteBytes(destination))
        {
            bytesWritten = 0;
            return false;
        }

        ReverseGuidFields(destination);
        bytesWritten = 16;
        return true;
#endif
    }

#if !NET8_0_OR_GREATER
    private static void ReverseGuidFields(Span<byte> bytes)
    {
        bytes[..4].Reverse();
        bytes.Slice(4, 2).Reverse();
        bytes.Slice(6, 2).Reverse();
    }
#endif

    public static TimeSpan GetElapsedTime(long startingTimestamp)
    {
#if NET7_0_OR_GREATER
        return Stopwatch.GetElapsedTime(startingTimestamp);
#else
        return TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - startingTimestamp) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));
#endif
    }
}
