using System.Globalization;
using System.Text;

namespace BlueTusk.TypeSystem;

internal static class BlueTuskArrayTextParser
{
    public static BlueTuskParsedArray Parse(string text, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text, delimiter);
        return parser.Parse();
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly char _delimiter;
        private int _offset;

        public Parser(string text, char delimiter)
        {
            _text = text;
            _delimiter = delimiter;
        }

        public BlueTuskParsedArray Parse()
        {
            SkipWhitespace();
            var specifiedLowerBounds = new List<int>();
            var specifiedLengths = new List<int>();
            while (TryConsume('['))
            {
                var lowerBound = ParseInteger();
                Require(':');
                var upperBound = ParseInteger();
                Require(']');
                var length = checked(upperBound - lowerBound + 1);
                if (length < 0)
                {
                    throw Error("An array upper bound cannot be less than one below its lower bound.");
                }

                specifiedLowerBounds.Add(lowerBound);
                specifiedLengths.Add(length);
                SkipWhitespace();
            }

            if (specifiedLengths.Count != 0)
            {
                Require('=');
                SkipWhitespace();
            }

            var root = ParseLevel();
            SkipWhitespace();
            if (_offset != _text.Length)
            {
                throw Error("Unexpected characters follow the array value.");
            }

            var lengths = GetLengths(root);
            if (specifiedLengths.Count != 0)
            {
                if (!specifiedLengths.SequenceEqual(lengths))
                {
                    throw Error("The specified array dimensions do not match the array contents.");
                }

                return new BlueTuskParsedArray(
                    specifiedLengths.ToArray(),
                    specifiedLowerBounds.ToArray(),
                    Flatten(root));
            }

            return new BlueTuskParsedArray(
                lengths,
                Enumerable.Repeat(1, lengths.Length).ToArray(),
                Flatten(root));
        }

        private Node ParseLevel()
        {
            Require('{');
            SkipWhitespace();
            if (TryConsume('}'))
            {
                return new Node([], IsNested: false);
            }

            var nested = Peek() == '{';
            var items = new List<object?>();
            while (true)
            {
                SkipWhitespace();
                if ((Peek() == '{') != nested)
                {
                    throw Error("Array elements and sub-arrays cannot be mixed at the same level.");
                }

                items.Add(nested ? ParseLevel() : ParseElement());
                SkipWhitespace();
                if (TryConsume(_delimiter))
                {
                    continue;
                }

                Require('}');
                return new Node(items, nested);
            }
        }

        private string? ParseElement()
        {
            if (TryConsume('"'))
            {
                var quoted = new StringBuilder();
                while (_offset < _text.Length)
                {
                    var character = _text[_offset++];
                    if (character == '"')
                    {
                        return quoted.ToString();
                    }

                    if (character == '\\')
                    {
                        if (_offset == _text.Length)
                        {
                            throw Error("A quoted array element ends with an incomplete escape.");
                        }

                        character = _text[_offset++];
                    }

                    quoted.Append(character);
                }

                throw Error("A quoted array element has no closing quote.");
            }

            var unquoted = new StringBuilder();
            var significantLength = 0;
            var hasEscapes = false;
            while (_offset < _text.Length)
            {
                var character = Peek();
                if (character == _delimiter || character == '}')
                {
                    break;
                }

                _offset++;
                if (character == '{' || character == '"')
                {
                    throw Error("An unquoted array element contains an unexpected structural character.");
                }

                if (character == '\\')
                {
                    if (_offset == _text.Length)
                    {
                        throw Error("An array element ends with an incomplete escape.");
                    }

                    character = _text[_offset++];
                    hasEscapes = true;
                    unquoted.Append(character);
                    significantLength = unquoted.Length;
                    continue;
                }

                if (unquoted.Length == 0 && char.IsWhiteSpace(character))
                {
                    continue;
                }

                unquoted.Append(character);
                if (!char.IsWhiteSpace(character))
                {
                    significantLength = unquoted.Length;
                }
            }

            unquoted.Length = significantLength;
            if (unquoted.Length == 0)
            {
                throw Error("An array element cannot be empty unless it is quoted.");
            }

            var value = unquoted.ToString();
            return !hasEscapes && string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
        }

        private int ParseInteger()
        {
            SkipWhitespace();
            var start = _offset;
            if (Peek() is '+' or '-')
            {
                _offset++;
            }

            while (_offset < _text.Length && char.IsAsciiDigit(_text[_offset]))
            {
                _offset++;
            }

            if (!int.TryParse(
                    _text.AsSpan(start, _offset - start),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                throw Error("An array dimension bound is not a valid Int32.");
            }

            SkipWhitespace();
            return value;
        }

        private void Require(char expected)
        {
            if (!TryConsume(expected))
            {
                throw Error($"Expected '{expected}'.");
            }
        }

        private bool TryConsume(char expected)
        {
            if (Peek() != expected)
            {
                return false;
            }

            _offset++;
            return true;
        }

        private char Peek() => _offset < _text.Length ? _text[_offset] : '\0';

        private void SkipWhitespace()
        {
            while (_offset < _text.Length && char.IsWhiteSpace(_text[_offset]))
            {
                _offset++;
            }
        }

        private InvalidOperationException Error(string message) =>
            new($"Malformed PostgreSQL array text at offset {_offset}: {message}");

        private static int[] GetLengths(Node node)
        {
            if (!node.IsNested)
            {
                return [node.Items.Count];
            }

            var children = node.Items.Cast<Node>().ToArray();
            var childLengths = GetLengths(children[0]);
            foreach (var child in children.Skip(1))
            {
                if (!GetLengths(child).SequenceEqual(childLengths))
                {
                    throw new InvalidOperationException(
                        "Malformed PostgreSQL array text: multidimensional arrays must be rectangular.");
                }
            }

            return [children.Length, .. childLengths];
        }

        private static string?[] Flatten(Node node)
        {
            if (!node.IsNested)
            {
                return node.Items.Cast<string?>().ToArray();
            }

            return node.Items.Cast<Node>().SelectMany(Flatten).ToArray();
        }
    }

    private sealed record Node(IReadOnlyList<object?> Items, bool IsNested);
}

internal sealed record BlueTuskParsedArray(
    int[] Lengths,
    int[] LowerBounds,
    string?[] Elements);
