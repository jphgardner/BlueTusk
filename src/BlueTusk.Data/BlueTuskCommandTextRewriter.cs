using System.Text;

namespace BlueTusk.Data;

internal sealed record BlueTuskCommandPlan(
    string Sql,
    IReadOnlyList<BlueTuskParameter> Parameters,
    bool UsesNamedParameters);

internal static class BlueTuskCommandTextRewriter
{
    public static BlueTuskCommandPlan Rewrite(
        string sql,
        BlueTuskParameterCollection parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        Dictionary<string, BlueTuskParameter>? namedParameters = null;
        Dictionary<string, int>? ordinals = null;
        List<BlueTuskParameter>? ordered = null;
        StringBuilder? rewritten = null;
        var segmentStart = 0;
        var hasPositionalParameters = false;

        for (var index = 0; index < sql.Length;)
        {
            var current = sql[index];
            if (current == '\'')
            {
                index = SkipSingleQuotedString(sql, index);
                continue;
            }

            if (current == '"')
            {
                index = SkipDoubleQuotedIdentifier(sql, index);
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index = SkipLineComment(sql, index + 2);
                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index = SkipBlockComment(sql, index);
                continue;
            }

            if (current == '$')
            {
                if (TryReadDollarQuoteDelimiter(sql, index, out var delimiter))
                {
                    index = SkipDollarQuotedString(sql, index, delimiter);
                    continue;
                }

                if (index + 1 < sql.Length && char.IsAsciiDigit(sql[index + 1]))
                {
                    if (rewritten is not null)
                    {
                        throw new InvalidOperationException(
                            "A command cannot mix positional and named parameters.");
                    }

                    hasPositionalParameters = true;
                }

                index++;
                continue;
            }

            if (current is not ('@' or ':') ||
                index + 1 >= sql.Length ||
                !IsParameterNameStart(sql[index + 1]) ||
                current == ':' && index > 0 && sql[index - 1] == ':')
            {
                index++;
                continue;
            }

            if (hasPositionalParameters)
            {
                throw new InvalidOperationException(
                    "A command cannot mix positional and named parameters.");
            }

            var nameStart = index + 1;
            var end = nameStart + 1;
            while (end < sql.Length && IsParameterNamePart(sql[end]))
            {
                end++;
            }

            var name = sql[nameStart..end];
            namedParameters ??= BuildNamedParameterMap(parameters);
            if (!namedParameters.TryGetValue(name, out var parameter))
            {
                throw new InvalidOperationException(
                    $"Command text references named parameter '{name}', but the parameter collection does not contain it.");
            }

            ordinals ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ordered ??= [];
            if (!ordinals.TryGetValue(name, out var ordinal))
            {
                ordered.Add(parameter);
                ordinal = ordered.Count;
                ordinals.Add(name, ordinal);
            }

            rewritten ??= new StringBuilder(sql.Length + 8);
            rewritten.Append(sql, segmentStart, index - segmentStart);
            rewritten.Append('$').Append(ordinal);
            index = end;
            segmentStart = end;
        }

        if (rewritten is null)
        {
            return new BlueTuskCommandPlan(sql, parameters.Items, UsesNamedParameters: false);
        }

        rewritten.Append(sql, segmentStart, sql.Length - segmentStart);
        return new BlueTuskCommandPlan(
            rewritten.ToString(),
            ordered!,
            UsesNamedParameters: true);
    }

    private static Dictionary<string, BlueTuskParameter> BuildNamedParameterMap(
        BlueTuskParameterCollection parameters)
    {
        var result = new Dictionary<string, BlueTuskParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters.Items)
        {
            var name = NormalizeParameterName(parameter.ParameterName);
            if (name.Length == 0)
            {
                continue;
            }

            if (!result.TryAdd(name, parameter))
            {
                throw new InvalidOperationException(
                    $"The parameter collection contains duplicate named parameter '{name}'.");
            }
        }

        return result;
    }

    private static string NormalizeParameterName(string name)
    {
        var start = name.Length > 0 && name[0] is '@' or ':' ? 1 : 0;
        return name[start..];
    }

    private static int SkipSingleQuotedString(string sql, int start)
    {
        var escapeBackslashes = start > 0 &&
            sql[start - 1] is 'e' or 'E' &&
            (start == 1 || !IsParameterNamePart(sql[start - 2]));
        for (var index = start + 1; index < sql.Length; index++)
        {
            if (escapeBackslashes && sql[index] == '\\' && index + 1 < sql.Length)
            {
                index++;
                continue;
            }

            if (sql[index] != '\'')
            {
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == '\'')
            {
                index++;
                continue;
            }

            return index + 1;
        }

        return sql.Length;
    }

    private static int SkipDoubleQuotedIdentifier(string sql, int start)
    {
        for (var index = start + 1; index < sql.Length; index++)
        {
            if (sql[index] != '"')
            {
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == '"')
            {
                index++;
                continue;
            }

            return index + 1;
        }

        return sql.Length;
    }

    private static int SkipLineComment(string sql, int start)
    {
        var newline = sql.IndexOf('\n', start);
        return newline < 0 ? sql.Length : newline + 1;
    }

    private static int SkipBlockComment(string sql, int start)
    {
        var depth = 1;
        for (var index = start + 2; index < sql.Length - 1; index++)
        {
            if (sql[index] == '/' && sql[index + 1] == '*')
            {
                depth++;
                index++;
            }
            else if (sql[index] == '*' && sql[index + 1] == '/')
            {
                depth--;
                index++;
                if (depth == 0)
                {
                    return index + 1;
                }
            }
        }

        return sql.Length;
    }

    private static bool TryReadDollarQuoteDelimiter(
        string sql,
        int start,
        out string delimiter)
    {
        var index = start + 1;
        if (index < sql.Length && sql[index] == '$')
        {
            delimiter = "$$";
            return true;
        }

        if (index >= sql.Length || !IsParameterNameStart(sql[index]))
        {
            delimiter = string.Empty;
            return false;
        }

        index++;
        while (index < sql.Length && IsParameterNamePart(sql[index]))
        {
            index++;
        }

        if (index >= sql.Length || sql[index] != '$')
        {
            delimiter = string.Empty;
            return false;
        }

        delimiter = sql[start..(index + 1)];
        return true;
    }

    private static int SkipDollarQuotedString(string sql, int start, string delimiter)
    {
        var contentStart = start + delimiter.Length;
        var end = sql.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        return end < 0 ? sql.Length : end + delimiter.Length;
    }

    private static bool IsParameterNameStart(char value) =>
        value == '_' || char.IsLetter(value);

    private static bool IsParameterNamePart(char value) =>
        value == '_' || char.IsLetterOrDigit(value);
}
