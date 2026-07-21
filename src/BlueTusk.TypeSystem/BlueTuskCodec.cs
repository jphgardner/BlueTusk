namespace BlueTusk.TypeSystem;

/// <summary>Provides the non-generic dispatch required by a strongly typed BlueTusk codec.</summary>
public abstract class BlueTuskCodec<T> : IBlueTuskCodec<T>
{
    public Type ClrType => typeof(T);

    public abstract T ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type);

    public abstract void WriteTyped(
        ref BlueTuskWriter writer,
        T value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type);

    object? IBlueTuskCodec.Read(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => ReadTyped(ref reader, format, type);

    void IBlueTuskCodec.Write(
        ref BlueTuskWriter writer,
        object? value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (value is not T typedValue)
        {
            throw new InvalidCastException(
                $"The {type.QualifiedName} codec requires a {typeof(T).FullName} value.");
        }

        WriteTyped(ref writer, typedValue, format, type);
    }
}
