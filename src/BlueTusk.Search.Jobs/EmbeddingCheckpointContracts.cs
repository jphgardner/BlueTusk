namespace BlueTusk.Search.Jobs;

public sealed record SearchEmbeddingCheckpointOptions
{
    public string Schema { get; init; } = "bluetusk_search_embeddings";
    public int Dimensions { get; init; } = 3;
    public int MaxModels { get; init; } = 128;
    public int MaxRecords { get; init; } = 100_000;
    public long MaxReservedVectorBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxBatchTexts { get; init; } = 256;
    public int MaxTextBytes { get; init; } = 64 * 1024;
    public int MaxBatchInputBytes { get; init; } = 1024 * 1024;
    public int MaxConcurrentOperations { get; init; } = 8;
    public int MaxActiveProviderBatches { get; init; } = 8;
    public int MaxActiveProviderBatchesPerTenant { get; init; } = 2;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(1);
    public int CommandTimeoutSeconds { get; init; } = 30;
}

public sealed class SearchEmbeddingCheckpointOwnershipException() : Exception("The embedding checkpoint owner expired or was replaced; its results were not committed.");
