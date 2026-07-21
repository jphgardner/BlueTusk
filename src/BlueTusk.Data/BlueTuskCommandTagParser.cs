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
        var tokens = commandTag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 1 &&
               CountedCommands.Contains(tokens[0]) &&
               int.TryParse(tokens[^1], NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }
}
