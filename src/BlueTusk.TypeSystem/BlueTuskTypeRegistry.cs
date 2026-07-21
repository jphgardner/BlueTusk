namespace BlueTusk.TypeSystem;

/// <summary>An immutable snapshot of catalogue types and their runtime codecs.</summary>
public sealed class BlueTuskTypeRegistry
{
    private readonly IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> _types;
    private readonly IReadOnlyDictionary<BlueTuskTypeId, IBlueTuskCodec> _codecs;
    private readonly IReadOnlyDictionary<BlueTuskTypeName, IBlueTuskCodec> _namedCodecs;
    private readonly IReadOnlyList<BlueTuskTypeDescriptor> _typeList;
    private readonly Dictionary<Type, BlueTuskTypeId> _uniqueClrTypes;

    internal BlueTuskTypeRegistry(
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        IReadOnlyDictionary<BlueTuskTypeId, IBlueTuskCodec> codecs,
        IReadOnlyDictionary<BlueTuskTypeName, IBlueTuskCodec> namedCodecs)
    {
        _types = types;
        _codecs = codecs;
        _namedCodecs = namedCodecs;
        _typeList = types.Values.ToArray();
        _uniqueClrTypes = codecs
            .Where(registration => IsInferenceCandidate(types, registration.Key))
            .GroupBy(registration => registration.Value.ClrType)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Key);
    }

    private static bool IsInferenceCandidate(
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        BlueTuskTypeId id)
    {
        var type = types[id];
        if (type.Kind == BlueTuskTypeKind.Domain)
        {
            return false;
        }

        return type.Kind != BlueTuskTypeKind.Array ||
            type.ElementType is not { } elementTypeId ||
            !types.TryGetValue(elementTypeId, out var elementType) ||
            elementType.Kind != BlueTuskTypeKind.Domain;
    }

    public bool TryGetType(BlueTuskTypeId id, out BlueTuskTypeDescriptor? type) =>
        _types.TryGetValue(id, out type);

    public bool TryGetCodec(BlueTuskTypeId id, out IBlueTuskCodec? codec) =>
        _codecs.TryGetValue(id, out codec);

    public IReadOnlyList<BlueTuskTypeDescriptor> Types => _typeList;

    public bool TryGetType(
        Type clrType,
        out BlueTuskTypeDescriptor? type,
        out IBlueTuskCodec? codec)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        if (!_uniqueClrTypes.TryGetValue(clrType, out var id) && clrType.IsArray)
        {
            var elementType = clrType.GetElementType()!;
            elementType = Nullable.GetUnderlyingType(elementType) ?? elementType;
            _uniqueClrTypes.TryGetValue(elementType.MakeArrayType(), out id);
        }

        if (id != default)
        {
            type = _types[id];
            codec = _codecs[id];
            return true;
        }

        type = null;
        codec = null;
        return false;
    }

    internal IEnumerable<KeyValuePair<BlueTuskTypeId, IBlueTuskCodec>> Codecs => _codecs;

    internal IEnumerable<KeyValuePair<BlueTuskTypeName, IBlueTuskCodec>> NamedCodecs => _namedCodecs;
}

public readonly record struct BlueTuskTypeName
{
    public BlueTuskTypeName(string schema, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Schema = schema;
        Name = name;
    }

    public string Schema { get; }

    public string Name { get; }

    public static BlueTuskTypeName Parse(string qualifiedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
        var separator = qualifiedName.LastIndexOf('.');
        if (separator <= 0 || separator == qualifiedName.Length - 1)
        {
            throw new FormatException(
                $"PostgreSQL type name '{qualifiedName}' must be qualified as schema.name.");
        }

        return new BlueTuskTypeName(
            qualifiedName[..separator],
            qualifiedName[(separator + 1)..]);
    }

    public override string ToString() => $"{Schema}.{Name}";
}

public sealed class BlueTuskTypeRegistryBuilder
{
    private readonly Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> _types = [];
    private readonly Dictionary<BlueTuskTypeId, IBlueTuskCodec> _codecs = [];
    private readonly Dictionary<BlueTuskTypeName, IBlueTuskCodec> _namedCodecs = [];

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

    public BlueTuskTypeRegistryBuilder RegisterCodec(BlueTuskTypeId type, IBlueTuskCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (!_types.ContainsKey(type))
        {
            throw new InvalidOperationException($"PostgreSQL type OID {type} is not registered.");
        }

        if (!_codecs.TryAdd(type, codec))
        {
            throw new InvalidOperationException($"PostgreSQL type OID {type} already has a codec.");
        }

        return this;
    }

    public BlueTuskTypeRegistryBuilder Register<T>(
        string schema,
        string name,
        IBlueTuskCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var typeName = new BlueTuskTypeName(schema, name);
        if (!_namedCodecs.TryAdd(typeName, codec))
        {
            throw new InvalidOperationException($"PostgreSQL type {typeName} already has a named codec registration.");
        }

        return this;
    }

    public BlueTuskTypeRegistryBuilder Register<T>(
        string schema,
        string name,
        IBlueTuskCodec<T> binaryCodec,
        IBlueTuskCodec<T> textCodec) =>
        Register(schema, name, new BlueTuskSplitCodec<T>(binaryCodec, textCodec));

    internal BlueTuskTypeRegistryBuilder RegisterOrReplaceCodec(BlueTuskTypeId type, IBlueTuskCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (!_types.ContainsKey(type))
        {
            throw new InvalidOperationException($"PostgreSQL type OID {type} is not registered.");
        }

        _codecs[type] = codec;
        return this;
    }

    internal BlueTuskTypeRegistryBuilder RegisterNamedCodec(BlueTuskTypeName type, IBlueTuskCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _namedCodecs[type] = codec;
        return this;
    }

    internal bool ContainsCodec(BlueTuskTypeId type) => _codecs.ContainsKey(type);

    internal bool TryGetCodec(BlueTuskTypeId type, out IBlueTuskCodec? codec) =>
        _codecs.TryGetValue(type, out codec);

    public BlueTuskTypeRegistry Build() =>
        new(
            new Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor>(_types),
            new Dictionary<BlueTuskTypeId, IBlueTuskCodec>(_codecs),
            new Dictionary<BlueTuskTypeName, IBlueTuskCodec>(_namedCodecs));
}
