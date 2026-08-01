namespace BlueTusk.Diagnostics;

internal static class BlueTuskSqlDiagnosticParser
{
    private const int MaximumQueryTags = 8;
    private const int MaximumQueryTagLength = 256;

    internal static BlueTuskSqlDiagnosticInfo Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tags = new List<string>();
        var index = 0;
        while (index < sql.Length)
        {
            SkipSeparators(sql, ref index);
            if (StartsWith(sql, index, "--"))
            {
                index += 2;
                var start = index;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                {
                    index++;
                }

                AddQueryTag(tags, sql.AsSpan(start, index - start));
                continue;
            }

            if (StartsWith(sql, index, "/*"))
            {
                SkipBlockComment(sql, ref index);
                continue;
            }

            break;
        }

        var tokenStart = index;
        while (index < sql.Length && IsIdentifierCharacter(sql[index]))
        {
            index++;
        }

        var operation = index == tokenStart
            ? "OTHER"
            : sql[tokenStart..index].ToUpperInvariant();
        return new BlueTuskSqlDiagnosticInfo(operation, [.. tags]);
    }

    private static void SkipSeparators(string sql, ref int index)
    {
        while (index < sql.Length && (char.IsWhiteSpace(sql[index]) || sql[index] == ';'))
        {
            index++;
        }
    }

    private static void SkipBlockComment(string sql, ref int index)
    {
        var depth = 0;
        while (index < sql.Length)
        {
            if (StartsWith(sql, index, "/*"))
            {
                depth++;
                index += 2;
                continue;
            }

            if (StartsWith(sql, index, "*/"))
            {
                depth--;
                index += 2;
                if (depth == 0)
                {
                    return;
                }

                continue;
            }

            index++;
        }
    }

    private static void AddQueryTag(List<string> tags, ReadOnlySpan<char> value)
    {
        if (tags.Count == MaximumQueryTags)
        {
            return;
        }

        value = value.Trim();
        if (value.IsEmpty)
        {
            return;
        }

        if (value.Length > MaximumQueryTagLength)
        {
            value = value[..MaximumQueryTagLength];
        }

        tags.Add(value.ToString());
    }

    private static bool StartsWith(string value, int index, string expected) =>
        index <= value.Length - expected.Length &&
        value.AsSpan(index, expected.Length).SequenceEqual(expected);

    private static bool IsIdentifierCharacter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
}

internal readonly record struct BlueTuskSqlDiagnosticInfo(
    string Operation,
    string[] QueryTags);
