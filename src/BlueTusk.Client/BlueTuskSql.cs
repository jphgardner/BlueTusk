namespace BlueTusk.Client;

/// <summary>Quotes PostgreSQL SQL identifiers and string literals.</summary>
public static class BlueTuskSql
{
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        RejectNullCharacter(identifier, nameof(identifier));
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public static string QuoteLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RejectNullCharacter(value, nameof(value));
        return $"E'{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static void RejectNullCharacter(string value, string parameterName)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "PostgreSQL SQL tokens cannot contain a null character.",
                parameterName);
        }
    }
}
