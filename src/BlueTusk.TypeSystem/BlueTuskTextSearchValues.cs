using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace BlueTusk.TypeSystem;

public enum BlueTuskTextSearchWeight : byte
{
    D = 0,
    C = 1,
    B = 2,
    A = 3,
}

[Flags]
public enum BlueTuskTextSearchWeights : byte
{
    None = 0,
    D = 1,
    C = 2,
    B = 4,
    A = 8,
}

public readonly record struct BlueTuskTextSearchPosition
{
    public BlueTuskTextSearchPosition(int position, BlueTuskTextSearchWeight weight = BlueTuskTextSearchWeight.D)
    {
        if (position is < 1 or > 16_383)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                "A PostgreSQL text-search position must be between 1 and 16383.");
        }

        if (!Enum.IsDefined(weight))
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }

        Position = position;
        Weight = weight;
    }

    public int Position { get; }

    public BlueTuskTextSearchWeight Weight { get; }

    public override string ToString() => Weight == BlueTuskTextSearchWeight.D
        ? Position.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Position}{Weight}");
}

public sealed class BlueTuskTextSearchVectorEntry : IEquatable<BlueTuskTextSearchVectorEntry>
{
    private const int MaximumLexemeByteCount = 2046;
    private readonly BlueTuskTextSearchPosition[] _positions;
    private readonly ReadOnlyCollection<BlueTuskTextSearchPosition> _positionView;

    public BlueTuskTextSearchVectorEntry(
        string lexeme,
        IEnumerable<BlueTuskTextSearchPosition>? positions = null)
    {
        BlueTuskTextSearchText.ValidateLexeme(lexeme, MaximumLexemeByteCount);
        Lexeme = lexeme;
        _positions = NormalizePositions(positions);
        _positionView = Array.AsReadOnly(_positions);
    }

    public string Lexeme { get; }

    public IReadOnlyList<BlueTuskTextSearchPosition> Positions => _positionView;

    public bool Equals(BlueTuskTextSearchVectorEntry? other) =>
        other is not null &&
        string.Equals(Lexeme, other.Lexeme, StringComparison.Ordinal) &&
        _positions.AsSpan().SequenceEqual(other._positions);

    public override bool Equals(object? obj) => obj is BlueTuskTextSearchVectorEntry other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Lexeme, StringComparer.Ordinal);
        foreach (var position in _positions)
        {
            hash.Add(position);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var text = new StringBuilder().Append(BlueTuskTextSearchText.QuoteLexeme(Lexeme));
        if (_positions.Length > 0)
        {
            text.Append(':');
            for (var index = 0; index < _positions.Length; index++)
            {
                if (index > 0)
                {
                    text.Append(',');
                }

                text.Append(_positions[index]);
            }
        }

        return text.ToString();
    }

    private static BlueTuskTextSearchPosition[] NormalizePositions(
        IEnumerable<BlueTuskTextSearchPosition>? positions)
    {
        if (positions is null)
        {
            return [];
        }

        var normalized = positions
            .GroupBy(position => position.Position)
            .Select(group => new BlueTuskTextSearchPosition(
                group.Key,
                group.Max(position => position.Weight)))
            .OrderBy(position => position.Position)
            .ToArray();
        if (normalized.Length > 256)
        {
            throw new ArgumentException(
                "A PostgreSQL tsvector lexeme cannot contain more than 256 positions.",
                nameof(positions));
        }

        return normalized;
    }
}

public sealed class BlueTuskTextSearchVector : IReadOnlyList<BlueTuskTextSearchVectorEntry>,
    IEquatable<BlueTuskTextSearchVector>
{
    private readonly BlueTuskTextSearchVectorEntry[] _entries;

    public BlueTuskTextSearchVector(IEnumerable<BlueTuskTextSearchVectorEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries
            .GroupBy(entry => entry?.Lexeme ?? throw new ArgumentException("A tsvector entry cannot be null.", nameof(entries)))
            .Select(group => new BlueTuskTextSearchVectorEntry(
                group.Key,
                group.SelectMany(entry => entry.Positions)))
            .OrderBy(entry => entry.Lexeme, BlueTuskTextSearchLexemeComparer.Instance)
            .ToArray();
    }

    public int Count => _entries.Length;

    public BlueTuskTextSearchVectorEntry this[int index] => _entries[index];

    public static BlueTuskTextSearchVector Parse(string value) => BlueTuskTextSearchText.ParseVector(value);

    public bool Equals(BlueTuskTextSearchVector? other) =>
        other is not null && _entries.AsSpan().SequenceEqual(other._entries);

    public override bool Equals(object? obj) => obj is BlueTuskTextSearchVector other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in _entries)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<BlueTuskTextSearchVectorEntry> GetEnumerator() =>
        ((IEnumerable<BlueTuskTextSearchVectorEntry>)_entries).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(' ', (IEnumerable<BlueTuskTextSearchVectorEntry>)_entries);
}

public abstract record BlueTuskTextSearchQueryNode;

public sealed record BlueTuskTextSearchQueryLexeme : BlueTuskTextSearchQueryNode
{
    private const int MaximumLexemeByteCount = 2047;

    public BlueTuskTextSearchQueryLexeme(
        string lexeme,
        BlueTuskTextSearchWeights weights = BlueTuskTextSearchWeights.None,
        bool isPrefix = false)
    {
        BlueTuskTextSearchText.ValidateLexeme(lexeme, MaximumLexemeByteCount);
        if ((weights & ~BlueTuskTextSearchWeights.A & ~BlueTuskTextSearchWeights.B &
             ~BlueTuskTextSearchWeights.C & ~BlueTuskTextSearchWeights.D) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weights));
        }

        Lexeme = lexeme;
        Weights = weights;
        IsPrefix = isPrefix;
    }

    public string Lexeme { get; }

    public BlueTuskTextSearchWeights Weights { get; }

    public bool IsPrefix { get; }
}

public sealed record BlueTuskTextSearchQueryNot : BlueTuskTextSearchQueryNode
{
    public BlueTuskTextSearchQueryNot(BlueTuskTextSearchQueryNode operand)
    {
        ArgumentNullException.ThrowIfNull(operand);
        Operand = operand;
    }

    public BlueTuskTextSearchQueryNode Operand { get; }
}

public enum BlueTuskTextSearchQueryOperator : byte
{
    And = 2,
    Or = 3,
    Phrase = 4,
}

public sealed record BlueTuskTextSearchQueryBinary : BlueTuskTextSearchQueryNode
{
    public BlueTuskTextSearchQueryBinary(
        BlueTuskTextSearchQueryOperator @operator,
        BlueTuskTextSearchQueryNode left,
        BlueTuskTextSearchQueryNode right,
        int phraseDistance = 1)
    {
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator));
        }

        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (@operator == BlueTuskTextSearchQueryOperator.Phrase && phraseDistance is < 0 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(
                nameof(phraseDistance),
                "A PostgreSQL phrase distance must be between 0 and 16384.");
        }

        Operator = @operator;
        Left = left;
        Right = right;
        PhraseDistance = @operator == BlueTuskTextSearchQueryOperator.Phrase ? phraseDistance : 0;
    }

    public BlueTuskTextSearchQueryOperator Operator { get; }

    public BlueTuskTextSearchQueryNode Left { get; }

    public BlueTuskTextSearchQueryNode Right { get; }

    public int PhraseDistance { get; }
}

public sealed class BlueTuskTextSearchQuery : IEquatable<BlueTuskTextSearchQuery>
{
    public BlueTuskTextSearchQuery(BlueTuskTextSearchQueryNode? root) => Root = root;

    public BlueTuskTextSearchQueryNode? Root { get; }

    public static BlueTuskTextSearchQuery Empty { get; } = new(root: null);

    public static BlueTuskTextSearchQuery Parse(string value) => BlueTuskTextSearchText.ParseQuery(value);

    public bool Equals(BlueTuskTextSearchQuery? other) => other is not null && Equals(Root, other.Root);

    public override bool Equals(object? obj) => obj is BlueTuskTextSearchQuery other && Equals(other);

    public override int GetHashCode() => Root?.GetHashCode() ?? 0;

    public override string ToString() => Root is null ? string.Empty : BlueTuskTextSearchText.FormatQueryNode(Root);
}

internal sealed class BlueTuskTextSearchLexemeComparer : IComparer<string>
{
    public static BlueTuskTextSearchLexemeComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        return Encoding.UTF8.GetBytes(x).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y));
    }
}

internal static class BlueTuskTextSearchText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void ValidateLexeme(string lexeme, int maximumByteCount)
    {
        ArgumentNullException.ThrowIfNull(lexeme);
        if (lexeme.Length == 0 || lexeme.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A PostgreSQL text-search lexeme cannot be empty or contain a null character.", nameof(lexeme));
        }

        if (StrictUtf8.GetByteCount(lexeme) > maximumByteCount)
        {
            throw new ArgumentException(
                $"The PostgreSQL text-search lexeme exceeds {maximumByteCount} UTF-8 bytes.",
                nameof(lexeme));
        }
    }

    public static string QuoteLexeme(string lexeme) => $"'{lexeme.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal)}'";

    public static BlueTuskTextSearchVector ParseVector(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parser = new VectorParser(value);
        return parser.Parse();
    }

    public static BlueTuskTextSearchQuery ParseQuery(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parser = new QueryParser(value);
        return parser.Parse();
    }

    public static string FormatQueryNode(BlueTuskTextSearchQueryNode node) => node switch
    {
        BlueTuskTextSearchQueryLexeme lexeme => FormatQueryLexeme(lexeme),
        BlueTuskTextSearchQueryNot not => $"!{FormatUnaryOperand(not.Operand)}",
        BlueTuskTextSearchQueryBinary binary =>
            $"({FormatQueryNode(binary.Left)} {FormatOperator(binary)} {FormatQueryNode(binary.Right)})",
        _ => throw new InvalidOperationException($"Unknown text-search query node {node.GetType().FullName}."),
    };

    private static string FormatQueryLexeme(BlueTuskTextSearchQueryLexeme value)
    {
        var text = new StringBuilder(QuoteLexeme(value.Lexeme));
        if (value.IsPrefix || value.Weights != BlueTuskTextSearchWeights.None)
        {
            text.Append(':');
            if (value.IsPrefix)
            {
                text.Append('*');
            }

            if (value.Weights.HasFlag(BlueTuskTextSearchWeights.A))
            {
                text.Append('A');
            }

            if (value.Weights.HasFlag(BlueTuskTextSearchWeights.B))
            {
                text.Append('B');
            }

            if (value.Weights.HasFlag(BlueTuskTextSearchWeights.C))
            {
                text.Append('C');
            }

            if (value.Weights.HasFlag(BlueTuskTextSearchWeights.D))
            {
                text.Append('D');
            }
        }

        return text.ToString();
    }

    private static string FormatUnaryOperand(BlueTuskTextSearchQueryNode node) =>
        node is BlueTuskTextSearchQueryLexeme or BlueTuskTextSearchQueryNot
            ? FormatQueryNode(node)
            : $"({FormatQueryNode(node)})";

    private static string FormatOperator(BlueTuskTextSearchQueryBinary value) => value.Operator switch
    {
        BlueTuskTextSearchQueryOperator.And => "&",
        BlueTuskTextSearchQueryOperator.Or => "|",
        BlueTuskTextSearchQueryOperator.Phrase when value.PhraseDistance == 1 => "<->",
        BlueTuskTextSearchQueryOperator.Phrase => $"<{value.PhraseDistance.ToString(CultureInfo.InvariantCulture)}>",
        _ => throw new InvalidOperationException("Unknown PostgreSQL text-search query operator."),
    };

    private ref struct VectorParser
    {
        private readonly ReadOnlySpan<char> _value;
        private int _offset;

        public VectorParser(string value) => _value = value.AsSpan();

        public BlueTuskTextSearchVector Parse()
        {
            var entries = new List<BlueTuskTextSearchVectorEntry>();
            SkipWhiteSpace();
            while (_offset < _value.Length)
            {
                var lexeme = ReadLexeme();
                var positions = new List<BlueTuskTextSearchPosition>();
                if (TryRead(':'))
                {
                    do
                    {
                        var position = ReadInteger();
                        var weight = BlueTuskTextSearchWeight.D;
                        if (_offset < _value.Length && _value[_offset] is 'A' or 'B' or 'C' or 'D' or 'a' or 'b' or 'c' or 'd')
                        {
                            weight = Enum.Parse<BlueTuskTextSearchWeight>(
                                _value[_offset++].ToString(),
                                ignoreCase: true);
                        }

                        positions.Add(new BlueTuskTextSearchPosition(position, weight));
                    }
                    while (TryRead(','));
                }

                entries.Add(new BlueTuskTextSearchVectorEntry(lexeme, positions));
                if (_offset < _value.Length && !char.IsWhiteSpace(_value[_offset]))
                {
                    throw new FormatException("PostgreSQL tsvector entries must be separated by whitespace.");
                }

                SkipWhiteSpace();
            }

            return new BlueTuskTextSearchVector(entries);
        }

        private string ReadLexeme()
        {
            if (_offset >= _value.Length)
            {
                throw new FormatException("The PostgreSQL tsvector contains a missing lexeme.");
            }

            return _value[_offset] == '\''
                ? ReadQuotedLexeme(_value, ref _offset)
                : ReadUnquotedLexeme(_value, ref _offset, static character =>
                    char.IsWhiteSpace(character) || character == ':');
        }

        private int ReadInteger()
        {
            var start = _offset;
            while (_offset < _value.Length && char.IsAsciiDigit(_value[_offset]))
            {
                _offset++;
            }

            if (start == _offset || !int.TryParse(
                    _value[start.._offset],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var result))
            {
                throw new FormatException("A PostgreSQL tsvector position must be an integer.");
            }

            return result;
        }

        private bool TryRead(char character)
        {
            if (_offset >= _value.Length || _value[_offset] != character)
            {
                return false;
            }

            _offset++;
            return true;
        }

        private void SkipWhiteSpace()
        {
            while (_offset < _value.Length && char.IsWhiteSpace(_value[_offset]))
            {
                _offset++;
            }
        }
    }

    private sealed class QueryParser
    {
        private const int MaximumNestingDepth = 256;
        private readonly string _value;
        private int _depth;
        private int _offset;

        public QueryParser(string value) => _value = value;

        public BlueTuskTextSearchQuery Parse()
        {
            SkipWhiteSpace();
            if (_offset == _value.Length)
            {
                return BlueTuskTextSearchQuery.Empty;
            }

            var root = ParseOr();
            SkipWhiteSpace();
            if (_offset != _value.Length)
            {
                throw new FormatException("The PostgreSQL tsquery contains trailing input.");
            }

            return new BlueTuskTextSearchQuery(root);
        }

        private BlueTuskTextSearchQueryNode ParseOr()
        {
            var left = ParseAnd();
            while (TryRead('|'))
            {
                left = new BlueTuskTextSearchQueryBinary(
                    BlueTuskTextSearchQueryOperator.Or,
                    left,
                    ParseAnd());
            }

            return left;
        }

        private BlueTuskTextSearchQueryNode ParseAnd()
        {
            var left = ParsePhrase();
            while (TryRead('&'))
            {
                left = new BlueTuskTextSearchQueryBinary(
                    BlueTuskTextSearchQueryOperator.And,
                    left,
                    ParsePhrase());
            }

            return left;
        }

        private BlueTuskTextSearchQueryNode ParsePhrase()
        {
            var left = ParseNot();
            while (TryReadPhrase(out var distance))
            {
                left = new BlueTuskTextSearchQueryBinary(
                    BlueTuskTextSearchQueryOperator.Phrase,
                    left,
                    ParseNot(),
                    distance);
            }

            return left;
        }

        private BlueTuskTextSearchQueryNode ParseNot() => TryRead('!')
            ? new BlueTuskTextSearchQueryNot(ParseNot())
            : ParsePrimary();

        private BlueTuskTextSearchQueryNode ParsePrimary()
        {
            SkipWhiteSpace();
            if (TryRead('('))
            {
                if (++_depth > MaximumNestingDepth)
                {
                    throw new FormatException("The PostgreSQL tsquery nesting depth exceeds 256.");
                }

                var node = ParseOr();
                if (!TryRead(')'))
                {
                    throw new FormatException("The PostgreSQL tsquery contains an unterminated group.");
                }

                _depth--;
                return node;
            }

            var lexeme = ReadQueryLexeme();
            var weights = BlueTuskTextSearchWeights.None;
            var isPrefix = false;
            if (TryRead(':'))
            {
                var modifierCount = 0;
                while (_offset < _value.Length)
                {
                    var modifier = char.ToUpperInvariant(_value[_offset]);
                    if (modifier == '*')
                    {
                        isPrefix = true;
                    }
                    else if (modifier is >= 'A' and <= 'D')
                    {
                        weights |= modifier switch
                        {
                            'A' => BlueTuskTextSearchWeights.A,
                            'B' => BlueTuskTextSearchWeights.B,
                            'C' => BlueTuskTextSearchWeights.C,
                            _ => BlueTuskTextSearchWeights.D,
                        };
                    }
                    else
                    {
                        break;
                    }

                    modifierCount++;
                    _offset++;
                }

                if (modifierCount == 0)
                {
                    throw new FormatException("The PostgreSQL tsquery contains an empty lexeme modifier.");
                }
            }

            return new BlueTuskTextSearchQueryLexeme(lexeme, weights, isPrefix);
        }

        private string ReadQueryLexeme()
        {
            SkipWhiteSpace();
            if (_offset >= _value.Length)
            {
                throw new FormatException("The PostgreSQL tsquery contains a missing lexeme.");
            }

            var span = _value.AsSpan();
            return span[_offset] == '\''
                ? ReadQuotedLexeme(span, ref _offset)
                : ReadUnquotedLexeme(span, ref _offset, static character =>
                    char.IsWhiteSpace(character) || character is ':' or '&' or '|' or '!' or '(' or ')' or '<');
        }

        private bool TryReadPhrase(out int distance)
        {
            SkipWhiteSpace();
            distance = 0;
            if (_offset >= _value.Length || _value[_offset] != '<')
            {
                return false;
            }

            if (_value.AsSpan(_offset).StartsWith("<->", StringComparison.Ordinal))
            {
                _offset += 3;
                distance = 1;
                return true;
            }

            var end = _value.IndexOf('>', _offset + 1);
            if (end < 0 || !int.TryParse(
                    _value.AsSpan(_offset + 1, end - _offset - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out distance))
            {
                throw new FormatException("The PostgreSQL tsquery contains an invalid phrase operator.");
            }

            _offset = end + 1;
            return true;
        }

        private bool TryRead(char character)
        {
            SkipWhiteSpace();
            if (_offset >= _value.Length || _value[_offset] != character)
            {
                return false;
            }

            _offset++;
            return true;
        }

        private void SkipWhiteSpace()
        {
            while (_offset < _value.Length && char.IsWhiteSpace(_value[_offset]))
            {
                _offset++;
            }
        }
    }

    private static string ReadQuotedLexeme(ReadOnlySpan<char> value, ref int offset)
    {
        var result = new StringBuilder();
        offset++;
        while (offset < value.Length)
        {
            var character = value[offset++];
            if (character == '\'')
            {
                if (offset < value.Length && value[offset] == '\'')
                {
                    result.Append('\'');
                    offset++;
                    continue;
                }

                return result.ToString();
            }

            if (character == '\\' && offset < value.Length)
            {
                result.Append(value[offset++]);
            }
            else
            {
                result.Append(character);
            }
        }

        throw new FormatException("The PostgreSQL text-search value contains an unterminated quoted lexeme.");
    }

    private static string ReadUnquotedLexeme(
        ReadOnlySpan<char> value,
        ref int offset,
        Func<char, bool> isDelimiter)
    {
        var start = offset;
        while (offset < value.Length && !isDelimiter(value[offset]))
        {
            offset++;
        }

        if (start == offset)
        {
            throw new FormatException("The PostgreSQL text-search value contains a missing lexeme.");
        }

        return value[start..offset].ToString();
    }
}
