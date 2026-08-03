namespace BlueTusk.Extensions.PostGIS;

/// <summary>A PostGIS geometry represented as EWKB or server-parseable WKT/EWKT.</summary>
public sealed class BlueTuskGeometry : IEquatable<BlueTuskGeometry>
{
    private readonly byte[]? _wellKnownBinary;
    private readonly string? _text;

    private BlueTuskGeometry(byte[]? wellKnownBinary, string? text)
    {
        _wellKnownBinary = wellKnownBinary;
        _text = text;
    }

    public bool HasWellKnownBinary => _wellKnownBinary is not null;

    public bool HasText => _text is not null;

    public static BlueTuskGeometry FromWellKnownBinary(ReadOnlySpan<byte> value)
    {
        BlueTuskSpatialValue.ValidateWellKnownBinary(value, nameof(value));
        return new BlueTuskGeometry(value.ToArray(), null);
    }

    public static BlueTuskGeometry FromText(string value) =>
        new(null, BlueTuskSpatialValue.ValidateText(value, nameof(value)));

    public ReadOnlySpan<byte> GetWellKnownBinary() =>
        _wellKnownBinary ?? throw new InvalidOperationException("This geometry contains text rather than EWKB.");

    public string GetText() =>
        _text ?? throw new InvalidOperationException("This geometry contains EWKB rather than text.");

    internal string GetTextOrHex() =>
        _text ?? Convert.ToHexString(_wellKnownBinary!);

    public bool Equals(BlueTuskGeometry? other) =>
        other is not null &&
        (ReferenceEquals(this, other) ||
         _text is not null && string.Equals(_text, other._text, StringComparison.Ordinal) ||
         _wellKnownBinary is not null &&
         other._wellKnownBinary is not null &&
         _wellKnownBinary.AsSpan().SequenceEqual(other._wellKnownBinary));

    public override bool Equals(object? obj) => obj is BlueTuskGeometry other && Equals(other);

    public override int GetHashCode() => BlueTuskSpatialValue.GetHashCode(_wellKnownBinary, _text);

    public override string ToString() => GetTextOrHex();
}

/// <summary>A PostGIS geography represented as EWKB or server-parseable WKT/EWKT.</summary>
public sealed class BlueTuskGeography : IEquatable<BlueTuskGeography>
{
    private readonly byte[]? _wellKnownBinary;
    private readonly string? _text;

    private BlueTuskGeography(byte[]? wellKnownBinary, string? text)
    {
        _wellKnownBinary = wellKnownBinary;
        _text = text;
    }

    public bool HasWellKnownBinary => _wellKnownBinary is not null;

    public bool HasText => _text is not null;

    public static BlueTuskGeography FromWellKnownBinary(ReadOnlySpan<byte> value)
    {
        BlueTuskSpatialValue.ValidateWellKnownBinary(value, nameof(value));
        return new BlueTuskGeography(value.ToArray(), null);
    }

    public static BlueTuskGeography FromText(string value) =>
        new(null, BlueTuskSpatialValue.ValidateText(value, nameof(value)));

    public ReadOnlySpan<byte> GetWellKnownBinary() =>
        _wellKnownBinary ?? throw new InvalidOperationException("This geography contains text rather than EWKB.");

    public string GetText() =>
        _text ?? throw new InvalidOperationException("This geography contains EWKB rather than text.");

    internal string GetTextOrHex() =>
        _text ?? Convert.ToHexString(_wellKnownBinary!);

    public bool Equals(BlueTuskGeography? other) =>
        other is not null &&
        (ReferenceEquals(this, other) ||
         _text is not null && string.Equals(_text, other._text, StringComparison.Ordinal) ||
         _wellKnownBinary is not null &&
         other._wellKnownBinary is not null &&
         _wellKnownBinary.AsSpan().SequenceEqual(other._wellKnownBinary));

    public override bool Equals(object? obj) => obj is BlueTuskGeography other && Equals(other);

    public override int GetHashCode() => BlueTuskSpatialValue.GetHashCode(_wellKnownBinary, _text);

    public override string ToString() => GetTextOrHex();
}

internal static class BlueTuskSpatialValue
{
    public static void ValidateWellKnownBinary(ReadOnlySpan<byte> value, string parameterName)
    {
        if (value.Length < 5)
        {
            throw new ArgumentException("A PostGIS EWKB value must contain at least a byte-order and type header.", parameterName);
        }

        if (value[0] is not (0 or 1))
        {
            throw new ArgumentException("A PostGIS EWKB byte-order marker must be zero or one.", parameterName);
        }
    }

    public static string ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A PostGIS text value cannot contain a null character.", parameterName);
        }

        return value;
    }

    public static int GetHashCode(byte[]? wellKnownBinary, string? text)
    {
        var hash = new HashCode();
        if (wellKnownBinary is not null)
        {
            foreach (var value in wellKnownBinary)
            {
                hash.Add(value);
            }
        }
        else
        {
            hash.Add(text, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
