using System.Globalization;
using System.Security.Cryptography;

namespace BlueTusk.Edge;

/// <summary>An opt-in UUIDv8 identity: 60 random stream bits followed by a 60-bit, strictly increasing sequence.</summary>
public static class EdgeOrderedMutationId
{
    public const long MaxSequence = (1L << 60) - 1;

    public static string NewStreamId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8))[1..].ToLowerInvariant();

    public static Guid Create(string streamId, long sequence)
    {
        if (streamId is null || streamId.Length != 15 || !streamId.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        { throw new ArgumentException("An ordered stream ID must contain 15 lowercase hex digits.", nameof(streamId)); }
        if (sequence is < 1 or > MaxSequence) { throw new ArgumentOutOfRangeException(nameof(sequence)); }
        var number = sequence.ToString("x15", CultureInfo.InvariantCulture);
        return Guid.ParseExact($"{streamId[..8]}-{streamId[8..12]}-8{streamId[12..]}-a{number[..3]}-{number[3..]}", "D");
    }

    public static bool TryParse(Guid id, out string streamId, out long sequence)
    {
        var hex = id.ToString("N");
        if (hex[12] != '8' || hex[16] != 'a') { streamId = string.Empty; sequence = 0; return false; }
        streamId = string.Concat(hex.AsSpan(0, 12), hex.AsSpan(13, 3));
        var high = long.Parse(hex.AsSpan(17, 3), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var low = long.Parse(hex.AsSpan(20), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        sequence = (high << 48) | low;
        return true;
    }
}
