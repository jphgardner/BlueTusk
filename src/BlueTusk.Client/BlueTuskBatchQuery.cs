namespace BlueTusk.Client;

/// <summary>One unnamed statement in an extended-query batch.</summary>
public sealed record BlueTuskBatchQuery(
    string Sql,
    IReadOnlyList<BlueTuskExtendedQueryParameter> Parameters,
    bool UseBinaryResults = true);

/// <summary>One named prepared statement execution in an extended-query batch.</summary>
public sealed record BlueTuskPreparedBatchQuery(
    string StatementName,
    IReadOnlyList<BlueTuskExtendedQueryParameter> Parameters,
    bool UseBinaryResults = true);
