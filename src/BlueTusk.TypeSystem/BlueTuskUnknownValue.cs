namespace BlueTusk.TypeSystem;

/// <summary>Preserves an unrecognised PostgreSQL value without losing its type or format.</summary>
public sealed record BlueTuskUnknownValue(
    BlueTuskTypeDescriptor Type,
    BlueTuskDataFormat Format,
    ReadOnlyMemory<byte> Data)
{
    public string GetText() => Format == BlueTuskDataFormat.Text
        ? System.Text.Encoding.UTF8.GetString(Data.Span)
        : throw new InvalidOperationException("A binary value has no provider-independent text representation.");
}

