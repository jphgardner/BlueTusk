using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace BlueTusk.Extensions.HStore;

/// <summary>An immutable PostgreSQL hstore value with nullable text values.</summary>
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "The public value name mirrors PostgreSQL's hstore type.")]
public sealed class BlueTuskHStore :
    IReadOnlyDictionary<string, string?>,
    IEquatable<BlueTuskHStore>
{
    private readonly Dictionary<string, string?> _values;

    public BlueTuskHStore(params KeyValuePair<string, string?>[] pairs)
        : this((IEnumerable<KeyValuePair<string, string?>>)pairs)
    {
    }

    public BlueTuskHStore(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        _values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            ArgumentNullException.ThrowIfNull(pair.Key);
            if (!_values.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    $"The hstore key '{pair.Key}' is duplicated.",
                    nameof(pairs));
            }
        }
    }

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<string?> Values => _values.Values;

    public string? this[string key] => _values[key];

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out string? value) => _values.TryGetValue(key, out value);

    public static BlueTuskHStore Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Parser.Parse(value);
    }

    public bool Equals(BlueTuskHStore? other)
    {
        if (other is null || Count != other.Count)
        {
            return false;
        }

        foreach (var pair in _values)
        {
            if (!other._values.TryGetValue(pair.Key, out var otherValue) ||
                !string.Equals(pair.Value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is BlueTuskHStore other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pair in _values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var pair in _values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            AppendQuoted(builder, pair.Key);
            builder.Append("=>");
            if (pair.Value is null)
            {
                builder.Append("NULL");
            }
            else
            {
                AppendQuoted(builder, pair.Value);
            }
        }

        return builder.ToString();
    }

    public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static void AppendQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            if (character is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        builder.Append('"');
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _input;
        private int _offset;

        private Parser(ReadOnlySpan<char> input)
        {
            _input = input;
        }

        public static BlueTuskHStore Parse(ReadOnlySpan<char> input)
        {
            var parser = new Parser(input);
            return parser.ParseValue();
        }

        private BlueTuskHStore ParseValue()
        {
            var pairs = new List<KeyValuePair<string, string?>>();
            SkipWhiteSpace();
            if (AtEnd)
            {
                return new BlueTuskHStore(pairs);
            }

            while (true)
            {
                var key = ReadToken(isKey: true, out var keyWasQuoted);
                if (key.Length == 0 && !keyWasQuoted)
                {
                    throw Invalid();
                }

                SkipWhiteSpace();
                if (!TryRead('=') || !TryRead('>'))
                {
                    throw Invalid();
                }

                var value = ReadToken(isKey: false, out var quoted);
                pairs.Add(new KeyValuePair<string, string?>(
                    key,
                    !quoted && value.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : value));

                SkipWhiteSpace();
                if (AtEnd)
                {
                    break;
                }

                if (!TryRead(','))
                {
                    throw Invalid();
                }

                SkipWhiteSpace();
                if (AtEnd)
                {
                    throw Invalid();
                }
            }

            try
            {
                return new BlueTuskHStore(pairs);
            }
            catch (ArgumentException exception)
            {
                throw new FormatException("The hstore value contains a duplicate key.", exception);
            }
        }

        private string ReadToken(bool isKey, out bool quoted)
        {
            SkipWhiteSpace();
            if (AtEnd)
            {
                throw Invalid();
            }

            quoted = _input[_offset] == '"';
            if (quoted)
            {
                _offset++;
                var builder = new StringBuilder();
                while (!AtEnd)
                {
                    var character = _input[_offset++];
                    if (character == '"')
                    {
                        SkipWhiteSpace();
                        return builder.ToString();
                    }

                    if (character == '\\')
                    {
                        if (AtEnd)
                        {
                            throw Invalid();
                        }

                        character = _input[_offset++];
                    }

                    builder.Append(character);
                }

                throw Invalid();
            }

            var start = _offset;
            var escaped = false;
            StringBuilder? unescaped = null;
            while (!AtEnd)
            {
                var character = _input[_offset];
                if (!escaped && (isKey ? character == '=' : character == ','))
                {
                    break;
                }

                _offset++;
                if (!escaped && character == '\\')
                {
                    escaped = true;
                    unescaped ??= new StringBuilder(_input[start..(_offset - 1)].ToString());
                    continue;
                }

                unescaped?.Append(character);
                escaped = false;
            }

            if (escaped)
            {
                throw Invalid();
            }

            return (unescaped?.ToString() ?? _input[start.._offset].ToString()).Trim();
        }

        private bool AtEnd => _offset == _input.Length;

        private bool TryRead(char expected)
        {
            if (AtEnd || _input[_offset] != expected)
            {
                return false;
            }

            _offset++;
            return true;
        }

        private void SkipWhiteSpace()
        {
            while (!AtEnd && char.IsWhiteSpace(_input[_offset]))
            {
                _offset++;
            }
        }

        private static FormatException Invalid() =>
            new("The value is not a valid PostgreSQL hstore representation.");
    }
}
