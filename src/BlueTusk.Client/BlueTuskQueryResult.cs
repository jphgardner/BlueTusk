using BlueTusk.Protocol;

namespace BlueTusk.Client;

public sealed record BlueTuskResultSet(
    IReadOnlyList<BlueTuskFieldDescription> Fields,
    IReadOnlyList<BlueTuskDataRow> Rows,
    string CommandTag);

public sealed record BlueTuskQueryResult(IReadOnlyList<BlueTuskResultSet> ResultSets)
{
    public BlueTuskResultSet? FirstOrDefault => ResultSets.Count == 0 ? null : ResultSets[0];
}

internal readonly record struct BlueTuskScalarQueryResult(
    BlueTuskFieldDescription? Field,
    ReadOnlyMemory<byte>? Value,
    bool HasValue)
{
    internal static BlueTuskScalarQueryResult FromQueryResult(BlueTuskQueryResult result)
    {
        var resultSet = result.FirstOrDefault;
        return resultSet is { Fields.Count: > 0, Rows.Count: > 0 }
            ? new BlueTuskScalarQueryResult(resultSet.Fields[0], resultSet.Rows[0].Values[0], true)
            : default;
    }
}
