using System.Text;

namespace BlueTusk.TypeSystem;

internal static class BlueTuskRecordTextParser
{
    public static string?[] Parse(string text, int? expectedFieldCount = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        return parser.Parse(expectedFieldCount);
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _offset;

        public Parser(string text) => _text = text;

        public string?[] Parse(int? expectedFieldCount)
        {
            SkipWhitespace();
            Require('(');
            var fields = new List<string?>();
            if (Peek() == ')' && expectedFieldCount == 0)
            {
                _offset++;
            }
            else
            {
                while (true)
                {
                    fields.Add(ParseField());
                    if (TryConsume(','))
                    {
                        continue;
                    }

                    Require(')');
                    break;
                }
            }

            SkipWhitespace();
            if (_offset != _text.Length)
            {
                throw Error("Unexpected characters follow the record value.");
            }

            if (expectedFieldCount is { } count && fields.Count != count)
            {
                throw Error($"The record contains {fields.Count} fields; {count} were expected.");
            }

            return fields.ToArray();
        }

        private string? ParseField()
        {
            if (Peek() is ',' or ')')
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

                        if (Peek() is not ',' and not ')')
                        {
                            throw Error("Unexpected characters follow a quoted record field.");
                        }

                        return quoted.ToString();
                    }

                    if (character == '\\')
                    {
                        if (_offset == _text.Length)
                        {
                            throw Error("A quoted record field ends with an incomplete escape.");
                        }

                        character = _text[_offset++];
                    }

                    quoted.Append(character);
                }

                throw Error("A quoted record field has no closing quote.");
            }

            var unquoted = new StringBuilder();
            while (_offset < _text.Length && Peek() is not ',' and not ')')
            {
                var character = _text[_offset++];
                if (character == '"' || character == '(')
                {
                    throw Error("An unquoted record field contains an unexpected structural character.");
                }

                if (character == '\\')
                {
                    if (_offset == _text.Length)
                    {
                        throw Error("A record field ends with an incomplete escape.");
                    }

                    character = _text[_offset++];
                }

                unquoted.Append(character);
            }

            return unquoted.Length == 0 ? null : unquoted.ToString();
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
            new($"Malformed PostgreSQL record text at offset {_offset}: {message}");
    }
}
