namespace BlueTusk.TypeSystem;

internal static class BlueTuskMultirangeTextParser
{
    public static string[] Parse(string text)
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

        public string[] Parse()
        {
            SkipWhitespace();
            Require('{');
            SkipWhitespace();
            if (TryConsume('}'))
            {
                SkipWhitespace();
                RequireEnd();
                return [];
            }

            var ranges = new List<string>();
            while (true)
            {
                ranges.Add(ParseRange());
                SkipWhitespace();
                if (TryConsume(','))
                {
                    SkipWhitespace();
                    continue;
                }

                Require('}');
                SkipWhitespace();
                RequireEnd();
                return ranges.ToArray();
            }
        }

        private string ParseRange()
        {
            var start = _offset;
            if (_text.AsSpan(_offset).StartsWith("empty", StringComparison.OrdinalIgnoreCase))
            {
                _offset += "empty".Length;
                if (Peek() is not ',' and not '}' && !char.IsWhiteSpace(Peek()))
                {
                    throw Error("Unexpected characters follow an empty range.");
                }

                return _text[start.._offset];
            }

            if (Peek() is not '(' and not '[')
            {
                throw Error("Expected a range value.");
            }

            _offset++;
            var quoted = false;
            while (_offset < _text.Length)
            {
                var character = _text[_offset++];
                if (character == '\\')
                {
                    if (_offset == _text.Length)
                    {
                        throw Error("A range ends with an incomplete escape.");
                    }

                    _offset++;
                    continue;
                }

                if (character == '"')
                {
                    if (quoted && Peek() == '"')
                    {
                        _offset++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }

                    continue;
                }

                if (!quoted && character is ')' or ']')
                {
                    return _text[start.._offset];
                }
            }

            throw Error(quoted
                ? "A quoted range boundary has no closing quote."
                : "A range has no closing parenthesis or bracket.");
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
                throw Error("Unexpected characters follow the multirange value.");
            }
        }

        private InvalidOperationException Error(string message) =>
            new($"Malformed PostgreSQL multirange text at offset {_offset}: {message}");
    }
}
