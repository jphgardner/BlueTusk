namespace BlueTusk.TypeSystem;

internal interface IBlueTuskRangeCodecFactory
{
    IBlueTuskCodec? CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec);
}

internal interface IBlueTuskArrayRangeCodecFactory
{
    IBlueTuskCodec CreateArrayRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec);
}

internal interface IBlueTuskMultirangeCodecFactory
{
    IBlueTuskCodec CreateMultirangeCodec(BlueTuskTypeDescriptor rangeType);
}

/// <summary>Provides the non-generic dispatch required by a strongly typed BlueTusk codec.</summary>
public abstract class BlueTuskCodec<T> :
    IBlueTuskCodec<T>,
    IBlueTuskRangeCodecFactory,
    IBlueTuskArrayRangeCodecFactory
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

    IBlueTuskCodec IBlueTuskRangeCodecFactory.CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        new BlueTuskRangeCodec<T>(subtype, subtypeCodec);

    IBlueTuskCodec IBlueTuskArrayRangeCodecFactory.CreateArrayRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        new BlueTuskRangeCodec<T[]>(subtype, subtypeCodec);
}
