namespace BlueTusk.Protocol;

public readonly record struct BlueTuskBindParameter(short FormatCode, ReadOnlyMemory<byte>? Value);

