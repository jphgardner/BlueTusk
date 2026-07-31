namespace BlueTusk.Client;

/// <summary>
/// One PostgreSQL pipeline synchronization group. All queries are sent before the group's explicit Sync boundary.
/// </summary>
public sealed record BlueTuskPipelineGroup(IReadOnlyList<BlueTuskBatchQuery> Queries);

/// <summary>One ordered pipeline-group outcome, including any server error raised before its Sync boundary.</summary>
public sealed record BlueTuskPipelineGroupResult(
    BlueTuskQueryResult Result,
    BlueTuskServerException? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>The ordered outcomes for all synchronization groups in one PostgreSQL pipeline flush.</summary>
public sealed record BlueTuskPipelineResult(IReadOnlyList<BlueTuskPipelineGroupResult> Groups)
{
    public bool Succeeded => Groups.All(static group => group.Succeeded);
}
