namespace BlueTusk.Data;

internal static class BlueTuskSqlIdentifier
{
    public static string Quote(string identifier, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier, parameterName);
        if (identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "PostgreSQL identifiers cannot contain a null character.",
                parameterName);
        }

        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
