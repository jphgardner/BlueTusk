namespace BlueTusk.Client;

/// <summary>A typed value supplied to PostgreSQL through the extended query protocol.</summary>
public readonly record struct BlueTuskExtendedQueryParameter(
    uint TypeOid,
    short FormatCode,
    ReadOnlyMemory<byte>? Value);
