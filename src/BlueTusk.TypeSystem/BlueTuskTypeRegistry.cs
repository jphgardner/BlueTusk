namespace BlueTusk.TypeSystem;

/// <summary>An immutable snapshot of catalogue types and their runtime codecs.</summary>
public sealed class BlueTuskTypeRegistry
{
    private readonly IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> _types;
    private readonly IReadOnlyDictionary<BlueTuskTypeId, IBlueTuskCodec> _codecs;
    private readonly IReadOnlyDictionary<BlueTuskTypeName, IBlueTuskCodec> _namedCodecs;
    private readonly Dictionary<BlueTuskTypeName, BlueTuskTypeDescriptor> _namedTypes;
    private readonly IReadOnlyList<BlueTuskTypeDescriptor> _typeList;
    private readonly Dictionary<Type, BlueTuskTypeId> _uniqueClrTypes;
    private readonly Dictionary<Type, BlueTuskTypeId> _uniqueArrayElementTypes;

    internal BlueTuskTypeRegistry(
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        IReadOnlyDictionary<BlueTuskTypeId, IBlueTuskCodec> codecs,
        IReadOnlyDictionary<BlueTuskTypeName, IBlueTuskCodec> namedCodecs)
    {
        _types = types;
        _codecs = codecs;
        _namedCodecs = namedCodecs;
        _namedTypes = types.Values.ToDictionary(
            type => new BlueTuskTypeName(type.Schema, type.Name));
        _typeList = types.Values.ToArray();
        _uniqueClrTypes = codecs
            .Where(registration => IsInferenceCandidate(types, registration.Key))
            .GroupBy(registration => registration.Value.ClrType)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Key);
        _uniqueArrayElementTypes = _uniqueClrTypes
            .Where(registration => registration.Key.IsSZArray)
            .ToDictionary(
                registration => registration.Key.GetElementType()!,
                registration => registration.Value);
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

        if (IsLegacyTransactionSnapshot(type))
        {
            return false;
        }

        if (type.Kind != BlueTuskTypeKind.Array ||
            type.ElementType is not { } elementTypeId ||
            !types.TryGetValue(elementTypeId, out var elementType))
        {
            return true;
        }

        return elementType.Kind != BlueTuskTypeKind.Domain &&
            !IsLegacyTransactionSnapshot(elementType);
    }

    private static bool IsLegacyTransactionSnapshot(BlueTuskTypeDescriptor type) =>
        string.Equals(type.Schema, "pg_catalog", StringComparison.Ordinal) &&
        string.Equals(type.Name, "txid_snapshot", StringComparison.Ordinal);

    public bool TryGetType(BlueTuskTypeId id, out BlueTuskTypeDescriptor? type) =>
        _types.TryGetValue(id, out type);

    public bool TryGetCodec(BlueTuskTypeId id, out IBlueTuskCodec? codec) =>
        _codecs.TryGetValue(id, out codec);

    public bool TryGetType(
        BlueTuskTypeName name,
        out BlueTuskTypeDescriptor? type,
        out IBlueTuskCodec? codec)
    {
        if (_namedTypes.TryGetValue(name, out type))
        {
            _codecs.TryGetValue(type.Id, out codec);
            return true;
        }

        codec = null;
        return false;
    }

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
            _uniqueArrayElementTypes.TryGetValue(elementType, out id);
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
        var text = qualifiedName.AsSpan().Trim();
        var separator = -1;
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                quoted = !quoted;
            }
            else if (text[index] == '.' && !quoted)
            {
                separator = index;
            }
        }

        if (quoted || separator <= 0 || separator == text.Length - 1)
        {
            throw new FormatException(
                $"PostgreSQL type name '{qualifiedName}' must be qualified as schema.name.");
        }

        return new BlueTuskTypeName(
            ParseIdentifier(text[..separator], qualifiedName),
            ParseIdentifier(text[(separator + 1)..], qualifiedName));
    }

    private static string ParseIdentifier(ReadOnlySpan<char> value, string qualifiedName)
    {
        var identifier = value.Trim();
        if (identifier.IsEmpty)
        {
            throw new FormatException(
                $"PostgreSQL type name '{qualifiedName}' must be qualified as schema.name.");
        }

        if (identifier[0] != '"')
        {
            if (identifier.Contains('"'))
            {
                throw new FormatException($"PostgreSQL type name '{qualifiedName}' contains invalid quoting.");
            }

            return identifier.ToString().ToLowerInvariant();
        }

        if (identifier.Length < 2 || identifier[^1] != '"')
        {
            throw new FormatException($"PostgreSQL type name '{qualifiedName}' contains invalid quoting.");
        }

        var result = identifier[1..^1].ToString().Replace("\"\"", "\"", StringComparison.Ordinal);
        if (result.Length == 0)
        {
            throw new FormatException($"PostgreSQL type name '{qualifiedName}' contains an empty identifier.");
        }

        return result;
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
