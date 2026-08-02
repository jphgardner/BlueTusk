namespace BlueTusk.TypeSystem;

internal interface IBlueTuskBoundCompositeMapping<T>
{
    T Create(BlueTuskRecord record, BlueTuskTypeDescriptor type);

    BlueTuskRecord Decompose(T value, BlueTuskTypeDescriptor type);
}

/// <summary>Describes one source-generated CLR member in a PostgreSQL composite mapping.</summary>
public sealed class BlueTuskCompositeMember<T>
{
    private BlueTuskCompositeMember(string name, Type clrType, Func<T, object?> getter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentNullException.ThrowIfNull(getter);
        Name = name;
        ClrType = clrType;
        Getter = getter;
    }

    public string Name { get; }

    public Type ClrType { get; }

    internal Func<T, object?> Getter { get; }

    internal static BlueTuskCompositeMember<T> Create<TMember>(
        string name,
        Func<T, TMember> getter) =>
        new(name, typeof(TMember), value => getter(value));
}

/// <summary>Creates strongly typed source-generated PostgreSQL composite members.</summary>
public static class BlueTuskCompositeMember
{
    public static BlueTuskCompositeMember<T> Create<T, TMember>(
        string name,
        Func<T, TMember> getter) =>
        BlueTuskCompositeMember<T>.Create(name, getter);
}

/// <summary>
/// Supplies compile-time generated member access and construction for a PostgreSQL composite codec.
/// </summary>
public sealed class BlueTuskCompositeMapping<T>
{
    private readonly BlueTuskCompositeMember<T>[] _members;
    private readonly Func<IReadOnlyList<object?>, T> _factory;

    public BlueTuskCompositeMapping(
        IEnumerable<BlueTuskCompositeMember<T>> members,
        Func<IReadOnlyList<object?>, T> factory)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(factory);
        _members = members.ToArray();
        if (_members.Length == 0)
        {
            throw new ArgumentException("A generated composite mapping requires at least one member.", nameof(members));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in _members)
        {
            ArgumentNullException.ThrowIfNull(member);
            if (!names.Add(member.Name))
            {
                throw new ArgumentException(
                    $"Generated composite mapping contains duplicate PostgreSQL member '{member.Name}'.",
                    nameof(members));
            }
        }

        _factory = factory;
    }

    internal IBlueTuskBoundCompositeMapping<T> Bind(
        BlueTuskTypeDescriptor type,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        BlueTuskTypeRegistryBuilder registry)
    {
        if (type.Kind != BlueTuskTypeKind.Composite)
        {
            throw new InvalidOperationException(
                $"PostgreSQL type {type.QualifiedName} is not a composite type.");
        }

        var membersByName = _members
            .Select((member, index) => (member, index))
            .ToDictionary(item => item.member.Name, item => item, StringComparer.Ordinal);
        var fields = new BoundField[type.CompositeFields.Count];
        var usedMembers = new HashSet<int>();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = type.CompositeFields[index];
            if (!membersByName.TryGetValue(field.Name, out var mapped))
            {
                throw new InvalidOperationException(
                    $"Source-generated CLR type {typeof(T).FullName} has no member mapped to " +
                    $"PostgreSQL composite field '{field.Name}'.");
            }

            var fieldType = types[field.Type];
            registry.TryGetCodec(field.Type, out var fieldCodec);
            ValidateCompatibleType(mapped.member.ClrType, fieldCodec!.ClrType, field.Name);
            fields[index] = new BoundField(field, fieldType, mapped.member, mapped.index);
            _ = usedMembers.Add(mapped.index);
        }

        if (usedMembers.Count != _members.Length)
        {
            var unused = _members
                .Where((_, index) => !usedMembers.Contains(index))
                .Select(member => member.Name);
            throw new InvalidOperationException(
                $"Source-generated CLR type {typeof(T).FullName} contains members not present in " +
                $"PostgreSQL composite {type.QualifiedName}: {string.Join(", ", unused)}.");
        }

        return new BoundMapping(_members.Length, fields, _factory);
    }

    private static void ValidateCompatibleType(Type target, Type source, string field)
    {
        if (!target.IsAssignableFrom(source) && Nullable.GetUnderlyingType(target) != source)
        {
            throw new InvalidOperationException(
                $"CLR member for PostgreSQL field '{field}' has type {target.FullName}; " +
                $"{source.FullName} was expected.");
        }
    }

    private static object? ValidateValue(
        object? value,
        Type target,
        string field,
        BlueTuskTypeDescriptor type)
    {
        if (value is null)
        {
            if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL composite {type.QualifiedName} field '{field}' is null, " +
                    $"but CLR type {target.FullName} is not nullable.");
            }

            return null;
        }

        if (!target.IsInstanceOfType(value) && Nullable.GetUnderlyingType(target) != value.GetType())
        {
            throw new InvalidOperationException(
                $"PostgreSQL composite {type.QualifiedName} field '{field}' decoded as " +
                $"{value.GetType().FullName}; CLR type {target.FullName} was expected.");
        }

        return value;
    }

    private sealed class BoundMapping(
        int memberCount,
        BoundField[] fields,
        Func<IReadOnlyList<object?>, T> factory) : IBlueTuskBoundCompositeMapping<T>
    {
        public T Create(BlueTuskRecord record, BlueTuskTypeDescriptor type)
        {
            var values = new object?[memberCount];
            for (var index = 0; index < fields.Length; index++)
            {
                var field = fields[index];
                values[field.MemberIndex] = ValidateValue(
                    record[index].Value,
                    field.Member.ClrType,
                    field.Field.Name,
                    type);
            }

            return factory(values);
        }

        public BlueTuskRecord Decompose(T value, BlueTuskTypeDescriptor type)
        {
            if (value is null)
            {
                throw new InvalidOperationException(
                    $"A null CLR value cannot be encoded inside {type.QualifiedName}.");
            }

            var recordFields = new BlueTuskRecordField[fields.Length];
            for (var index = 0; index < fields.Length; index++)
            {
                var field = fields[index];
                recordFields[index] = new BlueTuskRecordField(
                    field.Field.Name,
                    field.Type,
                    field.Member.Getter(value));
            }

            return new BlueTuskRecord(recordFields);
        }
    }

    private sealed record BoundField(
        BlueTuskCompositeField Field,
        BlueTuskTypeDescriptor Type,
        BlueTuskCompositeMember<T> Member,
        int MemberIndex);
}
