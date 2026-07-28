using System.Text;

namespace BlueTusk.TypeSystem;

internal readonly record struct BlueTuskParsedRange(
    bool IsEmpty,
    bool LowerInclusive,
    string? LowerBound,
    bool UpperInclusive,
    string? UpperBound);

internal static class BlueTuskRangeTextParser
{
    public static BlueTuskParsedRange Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        return parser.Parse();
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _offset;

        public Parser(string text)
        {
            _text = text;
        }

        public BlueTuskParsedRange Parse()
        {
            SkipWhitespace();
            if (TryConsumeWord("empty"))
            {
                SkipWhitespace();
                RequireEnd();
                return new BlueTuskParsedRange(
                    IsEmpty: true,
                    LowerInclusive: false,
                    LowerBound: null,
                    UpperInclusive: false,
                    UpperBound: null);
            }

            var lowerInclusive = Peek() switch
            {
                '[' => true,
                '(' => false,
                _ => throw Error("Expected a left parenthesis or bracket."),
            };
            _offset++;
            var lower = ParseBound(',');
            Require(',');
            var upper = ParseBound(')', ']');
            var closing = Peek();
            if (closing is not ')' and not ']')
            {
                throw Error("Expected a right parenthesis or bracket.");
            }

            _offset++;
            SkipWhitespace();
            RequireEnd();
            return new BlueTuskParsedRange(
                IsEmpty: false,
                lowerInclusive && lower is not null,
                lower,
                closing == ']' && upper is not null,
                upper);
        }

        private string? ParseBound(params char[] terminators)
        {
            if (terminators.Contains(Peek()))
            {
                return null;
            }

            if (TryConsume('"'))
            {
                var quoted = new StringBuilder();
                while (_offset < _text.Length)
                {
                    var character = _text[_offset++];
                    if (character == '"')
                    {
                        if (Peek() == '"')
                        {
                            _offset++;
                            quoted.Append('"');
                            continue;
                        }

                        if (!terminators.Contains(Peek()))
                        {
                            throw Error("Unexpected characters follow a quoted range boundary.");
                        }

                        return quoted.ToString();
                    }

                    if (character == '\\')
                    {
                        if (_offset == _text.Length)
                        {
                            throw Error("A quoted range boundary ends with an incomplete escape.");
                        }

                        character = _text[_offset++];
                    }

                    quoted.Append(character);
                }

                throw Error("A quoted range boundary has no closing quote.");
            }

            var unquoted = new StringBuilder();
            while (_offset < _text.Length && !terminators.Contains(Peek()))
            {
                var character = _text[_offset++];
                if (character == '"')
                {
                    throw Error("A quote inside an unquoted range boundary must be escaped.");
                }

                if (character is '(' or ')' or '[' or ']' or ',')
                {
                    throw Error("A structural character inside an unquoted range boundary must be escaped.");
                }

                if (character == '\\')
                {
                    if (_offset == _text.Length)
                    {
                        throw Error("A range boundary ends with an incomplete escape.");
                    }

                    character = _text[_offset++];
                }

                unquoted.Append(character);
            }

            return unquoted.Length == 0 ? null : unquoted.ToString();
        }

        private bool TryConsumeWord(string value)
        {
            if (!_text.AsSpan(_offset).StartsWith(value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _offset += value.Length;
            return true;
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

        private void RequireEnd()
        {
            if (_offset != _text.Length)
            {
                throw Error("Unexpected characters follow the range value.");
            }
        }

        private InvalidOperationException Error(string message) =>
            new($"Malformed PostgreSQL range text at offset {_offset}: {message}");
    }
}
