using System.Buffers.Text;
using BlueTusk.Streams;

internal static class StreamsPrimaryChange
{
    internal static bool IsExpectedInsert(Change change, long key) =>
        change is InsertChange { NewRow: var row } &&
        row.Table is { Schema: "public", Name: "streams_primary_source" } &&
        row["id"] is { State: ChangeColumnState.Value, Encoding: ChangeValueEncoding.Text } column &&
        Utf8Parser.TryParse(column.Data.Span, out long deliveredKey, out var consumed) &&
        consumed == column.Data.Length && deliveredKey == key;
}
