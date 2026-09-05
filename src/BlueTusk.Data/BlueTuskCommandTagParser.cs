using System.Globalization;

namespace BlueTusk.Data;

internal static class BlueTuskCommandTagParser
{
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
        var tag = commandTag.AsSpan().Trim(' ');
        var firstSpace = tag.IndexOf(' ');
        return firstSpace > 0 &&
               tag[..firstSpace] is ("INSERT" or "UPDATE" or "DELETE" or "MERGE" or "MOVE" or "FETCH" or "COPY") &&
               long.TryParse(tag[(tag.LastIndexOf(' ') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }
}
