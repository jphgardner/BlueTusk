namespace BlueTusk.TypeSystem;

public sealed class BlueTuskSplitCodec<T> : BlueTuskCodec<T>
{
    private readonly IBlueTuskCodec<T> _binaryCodec;
    private readonly IBlueTuskCodec<T> _textCodec;

    public BlueTuskSplitCodec(
        IBlueTuskCodec<T> binaryCodec,
        IBlueTuskCodec<T> textCodec)
    {
        _binaryCodec = binaryCodec ?? throw new ArgumentNullException(nameof(binaryCodec));
        _textCodec = textCodec ?? throw new ArgumentNullException(nameof(textCodec));
    }

    public override T ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        Select(format).ReadTyped(ref reader, format, type);

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        T value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        Select(format).WriteTyped(ref writer, value, format, type);

    private IBlueTuskCodec<T> Select(BlueTuskDataFormat format) => format switch
    {
        BlueTuskDataFormat.Binary => _binaryCodec,
        BlueTuskDataFormat.Text => _textCodec,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
