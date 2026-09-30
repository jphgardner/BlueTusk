namespace BlueTusk.Sql;

/// <summary>Lexical single-statement admission; PostgreSQL still owns grammar and authorization.</summary>
public static class PostgreSqlStatementGuard
{
    public static string AdmitReadQuery(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if (sql.Length > 1024 * 1024 || sql.Contains('\0', StringComparison.Ordinal))
        {
            throw InvalidQuery();
        }

        var separator = -1;
        string? firstToken = null;
        for (var index = 0; index < sql.Length;)
        {
            var character = sql[index];
            if (char.IsWhiteSpace(character)) { index++; continue; }
            if (character == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\n' or '\r')) { index++; }
                continue;
            }
            if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var depth = 1;
                index += 2;
                while (index < sql.Length && depth > 0)
                {
                    if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
                    {
                        if (++depth > 32) { throw InvalidQuery(); }
                        index += 2;
                    }
                    else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else { index++; }
                }
                if (depth != 0) { throw InvalidQuery(); }
                continue;
            }
            if (separator >= 0) { throw InvalidQuery(); }
            if (character == ';') { separator = index++; continue; }

            if (character is '\'' or '"')
            {
                var quote = character;
                var escape = quote == '\'' && index > 0 && sql[index - 1] is 'e' or 'E' &&
                    (index < 2 || !IdentifierCharacter(sql[index - 2]));
                index++;
                var closed = false;
                while (index < sql.Length)
                {
                    if (escape && sql[index] == '\\')
                    {
                        index += 2;
                    }
                    else if (sql[index] == quote)
                    {
                        index++;
                        if (index < sql.Length && sql[index] == quote) { index++; }
                        else { closed = true; break; }
                    }
                    else { index++; }
                }
                if (!closed) { throw InvalidQuery(); }
                continue;
            }
            if (character == '$')
            {
                var delimiterEnd = index + 1;
                if (delimiterEnd < sql.Length && sql[delimiterEnd] != '$' &&
                    !(char.IsAsciiLetter(sql[delimiterEnd]) || sql[delimiterEnd] == '_'))
                {
                    index++;
                    continue;
                }
                while (delimiterEnd < sql.Length && IdentifierCharacter(sql[delimiterEnd]) && sql[delimiterEnd] != '$') { delimiterEnd++; }
                if (delimiterEnd < sql.Length && sql[delimiterEnd] == '$')
                {
                    var delimiter = sql[index..(delimiterEnd + 1)];
                    var close = sql.IndexOf(delimiter, delimiterEnd + 1, StringComparison.Ordinal);
                    if (close < 0) { throw InvalidQuery(); }
                    index = close + delimiter.Length;
                    continue;
                }
            }
            if (firstToken is null)
            {
                if (!char.IsAsciiLetter(character)) { throw InvalidQuery(); }
                var start = index;
                while (index < sql.Length && IdentifierCharacter(sql[index])) { index++; }
                firstToken = sql[start..index];
                continue;
            }
            index++;
        }

        if (firstToken is null || !(firstToken.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
            firstToken.Equals("WITH", StringComparison.OrdinalIgnoreCase) || firstToken.Equals("VALUES", StringComparison.OrdinalIgnoreCase) ||
            firstToken.Equals("TABLE", StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidQuery();
        }
        return separator < 0 ? sql : sql.Remove(separator, 1);
    }

    private static bool IdentifierCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '$';
    private static ArgumentException InvalidQuery() => new("One complete PostgreSQL read query is required.", "sql");
}
