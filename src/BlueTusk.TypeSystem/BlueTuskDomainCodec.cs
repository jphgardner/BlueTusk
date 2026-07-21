namespace BlueTusk.TypeSystem;

/// <summary>Delegates a PostgreSQL domain's wire representation to its catalogue-discovered base type.</summary>
public sealed class BlueTuskDomainCodec : IBlueTuskCodec
{
    private readonly BlueTuskTypeDescriptor _baseType;
    private readonly IBlueTuskCodec _baseCodec;

    public BlueTuskDomainCodec(BlueTuskTypeDescriptor baseType, IBlueTuskCodec baseCodec)
    {
        _baseType = baseType ?? throw new ArgumentNullException(nameof(baseType));
        _baseCodec = baseCodec ?? throw new ArgumentNullException(nameof(baseCodec));
    }

    public Type ClrType => _baseCodec.ClrType;

    public object? Read(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        _baseCodec.Read(ref reader, format, _baseType);

    public void Write(
        ref BlueTuskWriter writer,
        object? value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        _baseCodec.Write(ref writer, value, format, _baseType);
}
