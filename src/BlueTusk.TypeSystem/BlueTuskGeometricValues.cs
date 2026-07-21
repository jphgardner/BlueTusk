using System.Collections;
using System.Globalization;
using System.Text;

namespace BlueTusk.TypeSystem;

public readonly record struct BlueTuskPoint(double X, double Y)
{
    public static BlueTuskPoint Parse(string value) => BlueTuskGeometricText.ParsePoint(value);

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"({X:R},{Y:R})");
}

public readonly record struct BlueTuskLine
{
    public BlueTuskLine(double a, double b, double c)
    {
        if (a == 0 && b == 0)
        {
            throw new ArgumentException("PostgreSQL line coefficients A and B cannot both be zero.");
        }

        A = a;
        B = b;
        C = c;
    }

    public double A { get; }

    public double B { get; }

    public double C { get; }

    public static BlueTuskLine Parse(string value)
    {
        var components = BlueTuskGeometricText.ParseScalars(value, '{', '}', 3);
        return new BlueTuskLine(components[0], components[1], components[2]);
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{{{A:R},{B:R},{C:R}}}");
}

public readonly record struct BlueTuskLineSegment(BlueTuskPoint Start, BlueTuskPoint End)
{
    public static BlueTuskLineSegment Parse(string value)
    {
        var points = BlueTuskGeometricText.ParsePointList(value, '[', ']');
        if (points.Length != 2)
        {
            throw new FormatException("A PostgreSQL line segment must contain exactly two points.");
        }

        return new BlueTuskLineSegment(points[0], points[1]);
    }

    public override string ToString() => $"[{Start},{End}]";
}

public readonly record struct BlueTuskBox
{
    public BlueTuskBox(BlueTuskPoint first, BlueTuskPoint second)
    {
        High = new BlueTuskPoint(
            BlueTuskGeometricText.PostgreSqlMaximum(first.X, second.X),
            BlueTuskGeometricText.PostgreSqlMaximum(first.Y, second.Y));
        Low = new BlueTuskPoint(
            BlueTuskGeometricText.PostgreSqlMinimum(first.X, second.X),
            BlueTuskGeometricText.PostgreSqlMinimum(first.Y, second.Y));
    }

    public BlueTuskPoint High { get; }

    public BlueTuskPoint Low { get; }

    public static BlueTuskBox Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var points = BlueTuskGeometricText.ParsePointSequence(value.AsSpan().Trim());
        if (points.Length != 2)
        {
            throw new FormatException("A PostgreSQL box must contain exactly two points.");
        }

        return new BlueTuskBox(points[0], points[1]);
    }

    public override string ToString() => $"{High},{Low}";
}

public sealed class BlueTuskPath : IReadOnlyList<BlueTuskPoint>, IEquatable<BlueTuskPath>
{
    private readonly BlueTuskPoint[] _points;

    public BlueTuskPath(IEnumerable<BlueTuskPoint> points, bool isClosed)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = points.ToArray();
        if (_points.Length == 0)
        {
            throw new ArgumentException("A PostgreSQL path must contain at least one point.", nameof(points));
        }

        IsClosed = isClosed;
    }

    public bool IsClosed { get; }

    public int Count => _points.Length;

    public BlueTuskPoint this[int index] => _points[index];

    public static BlueTuskPath Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        if (text.Length < 2)
        {
            throw new FormatException("A PostgreSQL path is missing its delimiters.");
        }

        var isClosed = text[0] switch
        {
            '(' when text[^1] == ')' => true,
            '[' when text[^1] == ']' => false,
            _ => throw new FormatException("A PostgreSQL path must be enclosed by parentheses or brackets."),
        };
        return new BlueTuskPath(BlueTuskGeometricText.ParsePointSequence(text[1..^1]), isClosed);
    }

    public bool Equals(BlueTuskPath? other) =>
        other is not null && IsClosed == other.IsClosed && _points.AsSpan().SequenceEqual(other._points);

    public override bool Equals(object? obj) => obj is BlueTuskPath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsClosed);
        foreach (var point in _points)
        {
            hash.Add(point);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<BlueTuskPoint> GetEnumerator() =>
        ((IEnumerable<BlueTuskPoint>)_points).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => BlueTuskGeometricText.FormatPointList(
        IsClosed ? '(' : '[',
        IsClosed ? ')' : ']',
        _points);
}

public sealed class BlueTuskPolygon : IReadOnlyList<BlueTuskPoint>, IEquatable<BlueTuskPolygon>
{
    private readonly BlueTuskPoint[] _points;

    public BlueTuskPolygon(IEnumerable<BlueTuskPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = points.ToArray();
        if (_points.Length == 0)
        {
            throw new ArgumentException("A PostgreSQL polygon must contain at least one point.", nameof(points));
        }
    }

    public int Count => _points.Length;

    public BlueTuskPoint this[int index] => _points[index];

    public static BlueTuskPolygon Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        if (text.Length < 2 || text[0] != '(' || text[^1] != ')')
        {
            throw new FormatException("A PostgreSQL polygon must be enclosed by parentheses.");
        }

        return new BlueTuskPolygon(BlueTuskGeometricText.ParsePointSequence(text[1..^1]));
    }

    public bool Equals(BlueTuskPolygon? other) =>
        other is not null && _points.AsSpan().SequenceEqual(other._points);

    public override bool Equals(object? obj) => obj is BlueTuskPolygon other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var point in _points)
        {
            hash.Add(point);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<BlueTuskPoint> GetEnumerator() =>
        ((IEnumerable<BlueTuskPoint>)_points).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => BlueTuskGeometricText.FormatPointList('(', ')', _points);
}

public readonly record struct BlueTuskCircle
{
    public BlueTuskCircle(BlueTuskPoint center, double radius)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "A PostgreSQL circle radius cannot be negative.");
        }

        Center = center;
        Radius = radius;
    }

    public BlueTuskPoint Center { get; }

    public double Radius { get; }

    public static BlueTuskCircle Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        if (text.Length < 6 || text[0] != '<' || text[^1] != '>')
        {
            throw new FormatException("A PostgreSQL circle must be enclosed by angle brackets.");
        }

        var pointEnd = text.IndexOf(')');
        if (pointEnd < 0 || pointEnd + 1 >= text.Length || text[pointEnd + 1] != ',')
        {
            throw new FormatException("A PostgreSQL circle must contain a center point and radius.");
        }

        var center = BlueTuskGeometricText.ParsePoint(text[1..(pointEnd + 1)]);
        var radius = BlueTuskGeometricText.ParseScalar(text[(pointEnd + 2)..^1]);
        return new BlueTuskCircle(center, radius);
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"<{Center},{Radius:R}>");
}

internal static class BlueTuskGeometricText
{
    public static BlueTuskPoint ParsePoint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ParsePoint(value.AsSpan());
    }

    public static BlueTuskPoint ParsePoint(ReadOnlySpan<char> value)
    {
        var text = value.Trim();
        if (text.Length < 5 || text[0] != '(' || text[^1] != ')')
        {
            throw new FormatException("A PostgreSQL point must be enclosed by parentheses.");
        }

        var components = text[1..^1];
        var separator = components.IndexOf(',');
        if (separator < 0 || components[(separator + 1)..].Contains(','))
        {
            throw new FormatException("A PostgreSQL point must contain exactly two coordinates.");
        }

        return new BlueTuskPoint(
            ParseScalar(components[..separator]),
            ParseScalar(components[(separator + 1)..]));
    }

    public static double[] ParseScalars(string value, char open, char close, int count)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        if (text.Length < 2 || text[0] != open || text[^1] != close)
        {
            throw new FormatException($"The PostgreSQL value must be enclosed by {open} and {close}.");
        }

        var result = new double[count];
        var remaining = text[1..^1];
        for (var index = 0; index < count; index++)
        {
            var separator = remaining.IndexOf(',');
            if (index == count - 1)
            {
                if (separator >= 0)
                {
                    throw new FormatException($"The PostgreSQL value must contain exactly {count} components.");
                }

                result[index] = ParseScalar(remaining);
            }
            else
            {
                if (separator < 0)
                {
                    throw new FormatException($"The PostgreSQL value must contain exactly {count} components.");
                }

                result[index] = ParseScalar(remaining[..separator]);
                remaining = remaining[(separator + 1)..];
            }
        }

        return result;
    }

    public static BlueTuskPoint[] ParsePointList(string value, char open, char close)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        if (text.Length < 2 || text[0] != open || text[^1] != close)
        {
            throw new FormatException($"The PostgreSQL value must be enclosed by {open} and {close}.");
        }

        return ParsePointSequence(text[1..^1]);
    }

    public static BlueTuskPoint[] ParsePointSequence(ReadOnlySpan<char> value)
    {
        var text = value.Trim();
        var points = new List<BlueTuskPoint>();
        var offset = 0;
        while (offset < text.Length)
        {
            while (offset < text.Length && char.IsWhiteSpace(text[offset]))
            {
                offset++;
            }

            if (offset >= text.Length || text[offset] != '(')
            {
                throw new FormatException("A PostgreSQL geometric point list contains invalid syntax.");
            }

            var pointEnd = text[offset..].IndexOf(')');
            if (pointEnd < 0)
            {
                throw new FormatException("A PostgreSQL geometric point list contains an unterminated point.");
            }

            pointEnd += offset;
            points.Add(ParsePoint(text[offset..(pointEnd + 1)]));
            offset = pointEnd + 1;
            while (offset < text.Length && char.IsWhiteSpace(text[offset]))
            {
                offset++;
            }

            if (offset == text.Length)
            {
                break;
            }

            if (text[offset] != ',')
            {
                throw new FormatException("PostgreSQL geometric points must be separated by commas.");
            }

            offset++;
        }

        if (points.Count == 0)
        {
            throw new FormatException("A PostgreSQL geometric point list must contain at least one point.");
        }

        return points.ToArray();
    }

    public static double ParseScalar(ReadOnlySpan<char> value) =>
        double.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static string FormatPointList(char open, char close, IReadOnlyList<BlueTuskPoint> points)
    {
        var text = new StringBuilder().Append(open);
        for (var index = 0; index < points.Count; index++)
        {
            if (index > 0)
            {
                text.Append(',');
            }

            text.Append(points[index]);
        }

        return text.Append(close).ToString();
    }

    public static double PostgreSqlMaximum(double first, double second) =>
        double.IsNaN(first) || (!double.IsNaN(second) && first >= second) ? first : second;

    public static double PostgreSqlMinimum(double first, double second) =>
        double.IsNaN(first) ? second : double.IsNaN(second) || first <= second ? first : second;
}
