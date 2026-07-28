namespace BlueTusk.TypeSystem;

public interface IBlueTuskCodec
{
    Type ClrType { get; }

    object? Read(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type);

    void Write(
        ref BlueTuskWriter writer,
        object? value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type);
}

public interface IBlueTuskCodec<T> : IBlueTuskCodec
{
    T ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type);

    void WriteTyped(
        ref BlueTuskWriter writer,
        T value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type);
}

/// <summary>
/// Selects the parameter wire format when a codec cannot represent every CLR value in binary.
/// </summary>
public interface IBlueTuskWriteFormatSelector
{
    BlueTuskDataFormat DefaultWriteFormat => BlueTuskDataFormat.Binary;

    BlueTuskDataFormat GetPreferredWriteFormat(
        object value,
        BlueTuskTypeDescriptor type);
}
