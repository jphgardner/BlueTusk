namespace BlueTusk.EntityFrameworkCore.Publications;

[Flags]
public enum BlueTuskPublicationOperations
{
    None = 0,
    Insert = 1,
    Update = 2,
    Delete = 4,
    Truncate = 8,
    All = Insert | Update | Delete | Truncate,
}

public enum BlueTuskPublicationGeneratedColumns
{
    None,
    Stored,
}

public sealed record BlueTuskPublicationTableDefinition(
    string Name,
    string? Schema,
    bool IncludeDescendants,
    IReadOnlyList<string>? Columns,
    string? RowFilterSql,
    bool IsExcluded = false);

public sealed record BlueTuskPublicationDefinition(
    string Name,
    IReadOnlyList<BlueTuskPublicationTableDefinition> Tables,
    IReadOnlyList<string> Schemas,
    bool AllTables,
    bool AllSequences,
    BlueTuskPublicationOperations Operations,
    bool PublishViaPartitionRoot,
    BlueTuskPublicationGeneratedColumns GeneratedColumns);

public sealed record BlueTuskPublicationDefinitionSet(IReadOnlyList<BlueTuskPublicationDefinition> Publications)
{
    public static BlueTuskPublicationDefinitionSet Empty { get; } = new([]);
}
