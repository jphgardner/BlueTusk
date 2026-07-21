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

