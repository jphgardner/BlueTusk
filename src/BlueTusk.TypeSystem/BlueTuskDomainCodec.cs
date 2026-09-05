namespace BlueTusk.TypeSystem;

/// <summary>Delegates a PostgreSQL domain's wire representation to its catalogue-discovered base type.</summary>
public sealed class BlueTuskDomainCodec :
    IBlueTuskCodec,
    IBlueTuskRangeCodecFactory,
    IBlueTuskArrayRangeCodecFactory
{
    private readonly BlueTuskTypeDescriptor _baseType;
    private readonly IBlueTuskCodec _baseCodec;

    public BlueTuskDomainCodec(BlueTuskTypeDescriptor baseType, IBlueTuskCodec baseCodec)
    {
        _baseType = baseType ?? throw new ArgumentNullException(nameof(baseType));
        _baseCodec = baseCodec ?? throw new ArgumentNullException(nameof(baseCodec));
    }

    public Type ClrType => _baseCodec.ClrType;

    // Domains retain their own element OID and delegate wire conversion to the
    // base codec. Array allocation must delegate too so NativeAOT can use the
    // base codec's statically rooted CLR array without runtime type generation.
    internal IBlueTuskArrayFactory? ArrayFactory =>
        _baseCodec is BlueTuskDomainCodec domain
            ? domain.ArrayFactory
            : _baseCodec as IBlueTuskArrayFactory;

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

    IBlueTuskCodec? IBlueTuskRangeCodecFactory.CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        (_baseCodec as IBlueTuskRangeCodecFactory)?.CreateRangeCodec(subtype, subtypeCodec);

    IBlueTuskCodec IBlueTuskArrayRangeCodecFactory.CreateArrayRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        (_baseCodec as IBlueTuskArrayRangeCodecFactory)?.CreateArrayRangeCodec(subtype, subtypeCodec) ??
        throw new InvalidOperationException(
            $"The {_baseType.QualifiedName} codec cannot construct a range over a domain array.");
}
