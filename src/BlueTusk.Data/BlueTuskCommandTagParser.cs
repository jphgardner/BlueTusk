using System.Globalization;

namespace BlueTusk.Data;

internal static class BlueTuskCommandTagParser
{
    private static readonly HashSet<string> CountedCommands = new(StringComparer.Ordinal)
    {
        "INSERT",
        "UPDATE",
        "DELETE",
        "MERGE",
        "MOVE",
        "FETCH",
        "COPY",
    };

    public static bool TryGetRecordsAffected(string commandTag, out int count)
    {
        count = 0;
        return TryGetRowsAffected(commandTag, out var rows) &&
            rows <= int.MaxValue &&
            (count = (int)rows) >= 0;
    }

    public static bool TryGetRowsAffected(string commandTag, out long count)
    {
        count = 0;
        var tokens = commandTag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 1 &&
               CountedCommands.Contains(tokens[0]) &&
               long.TryParse(tokens[^1], NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }
}
