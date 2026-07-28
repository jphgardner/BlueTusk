namespace BlueTusk.Protocol;

public sealed record BlueTuskParameterStatus(string Name, string Value);

public readonly record struct BlueTuskBackendKeyData(int ProcessId, int SecretKey);

public enum BlueTuskTransactionStatus : byte
{
    Idle = (byte)'I',
    InTransaction = (byte)'T',
    FailedTransaction = (byte)'E',
}

public sealed record BlueTuskFieldDescription(
    string Name,
    uint TableOid,
    short ColumnAttributeNumber,
    uint TypeOid,
    short TypeSize,
    int TypeModifier,
    short FormatCode);

public sealed record BlueTuskDataRow(IReadOnlyList<ReadOnlyMemory<byte>?> Values);

public enum BlueTuskCopyFormat : byte
{
    Text = 0,
    Binary = 1,
}

public sealed record BlueTuskCopyResponse(
    BlueTuskCopyFormat Format,
    IReadOnlyList<BlueTuskCopyFormat> ColumnFormats);

public sealed record BlueTuskError(IReadOnlyDictionary<char, string> Fields)
{
    public string Severity => Get('V') ?? Get('S') ?? "ERROR";

    public string? SqlState => Get('C');

    public string Message => Get('M') ?? "PostgreSQL reported an unspecified error.";

    public string? Detail => Get('D');

    public string? Hint => Get('H');

    private string? Get(char code) => Fields.TryGetValue(code, out var value) ? value : null;
}
