namespace BlueTusk.TypeSystem;

/// <summary>An immutable snapshot of catalogue types and their runtime codecs.</summary>
public sealed class BlueTuskTypeRegistry
{
    private readonly IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> _types;
    private readonly IReadOnlyDictionary<BlueTuskTypeId, IBlueTuskCodec> _codecs;

    internal BlueTuskTypeRegistry(
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        IReadOnlyDictionary<BlueTuskTypeId, IBlueTuskCodec> codecs)
    {
        _types = types;
        _codecs = codecs;
    }

    public bool TryGetType(BlueTuskTypeId id, out BlueTuskTypeDescriptor? type) =>
        _types.TryGetValue(id, out type);

    public bool TryGetCodec(BlueTuskTypeId id, out IBlueTuskCodec? codec) =>
        _codecs.TryGetValue(id, out codec);
}

public sealed class BlueTuskTypeRegistryBuilder
{
    private readonly Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> _types = [];
    private readonly Dictionary<BlueTuskTypeId, IBlueTuskCodec> _codecs = [];

    public BlueTuskTypeRegistryBuilder Register(BlueTuskTypeDescriptor type, IBlueTuskCodec? codec = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!_types.TryAdd(type.Id, type))
        {
            throw new InvalidOperationException($"PostgreSQL type OID {type.Id} is already registered.");
        }

        if (codec is not null)
        {
            _codecs.Add(type.Id, codec);
        }

        return this;
    }

    public BlueTuskTypeRegistry Build() =>
        new(
            new Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor>(_types),
            new Dictionary<BlueTuskTypeId, IBlueTuskCodec>(_codecs));
}

