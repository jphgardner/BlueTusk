using System.Reflection;
using System.Text;

namespace BlueTusk.TypeSystem;

internal interface IBlueTuskDeferredCodec : IBlueTuskCodec
{
    bool TryBind(
        BlueTuskTypeDescriptor type,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        BlueTuskTypeRegistryBuilder registry,
        out IBlueTuskCodec codec);
}

/// <summary>Maps a catalogue-discovered PostgreSQL composite to a CLR object.</summary>
public sealed class BlueTuskCompositeCodec<T> : BlueTuskCodec<T>, IBlueTuskDeferredCodec
{
    private readonly BlueTuskRecordCodec? _recordCodec;
    private readonly CompositeMapping? _mapping;

    public BlueTuskCompositeCodec()
    {
    }

    private BlueTuskCompositeCodec(BlueTuskRecordCodec recordCodec, CompositeMapping mapping)
    {
        _recordCodec = recordCodec;
        _mapping = mapping;
    }

    public override T ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        EnsureBound(type);
        var record = _recordCodec!.ReadTyped(ref reader, format, type);
        return _mapping!.Create(record, type);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        T value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        EnsureBound(type);
        var record = _mapping!.Decompose(value, type);
        _recordCodec!.WriteTyped(ref writer, record, format, type);
    }

    bool IBlueTuskDeferredCodec.TryBind(
        BlueTuskTypeDescriptor type,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        BlueTuskTypeRegistryBuilder registry,
        out IBlueTuskCodec codec)
    {
        if (!BlueTuskRecordCodec.TryCreate(type, types, registry, out var recordCodec))
        {
            codec = null!;
            return false;
        }

        codec = new BlueTuskCompositeCodec<T>(
            recordCodec,
            CompositeMapping.Create(type, types, registry));
        return true;
    }

    private void EnsureBound(BlueTuskTypeDescriptor type)
    {
        if (_recordCodec is null || _mapping is null)
        {
            throw new InvalidOperationException(
                $"The CLR composite codec for {typeof(T).FullName} has not been bound to " +
                $"the catalogue metadata for {type.QualifiedName}.");
        }
    }

    private sealed class CompositeMapping
    {
        private readonly ConstructorInfo? _constructor;
        private readonly int[]? _constructorFields;
        private readonly FieldMapping[] _fields;

        private CompositeMapping(
            ConstructorInfo? constructor,
            int[]? constructorFields,
            FieldMapping[] fields)
        {
            _constructor = constructor;
            _constructorFields = constructorFields;
            _fields = fields;
        }

        public static CompositeMapping Create(
            BlueTuskTypeDescriptor type,
            IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
            BlueTuskTypeRegistryBuilder registry)
        {
            if (type.Kind != BlueTuskTypeKind.Composite)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL type {type.QualifiedName} is not a composite type.");
            }

            var members = GetMembers();
            var fields = new FieldMapping[type.CompositeFields.Count];
            for (var index = 0; index < fields.Length; index++)
            {
                var field = type.CompositeFields[index];
                if (!members.TryGetValue(field.Name, out var member))
                {
                    throw new InvalidOperationException(
                        $"CLR type {typeof(T).FullName} has no public member mapped to " +
                        $"PostgreSQL composite field '{field.Name}'.");
                }

                var fieldType = types[field.Type];
                registry.TryGetCodec(field.Type, out var fieldCodec);
                ValidateCompatibleType(member.ValueType, fieldCodec!.ClrType, field.Name);
                fields[index] = new FieldMapping(field, fieldType, member);
            }

            var constructorMatches = typeof(T)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Select(constructor => TryMapConstructor(constructor, type.CompositeFields, registry))
                .Where(match => match is not null)
                .ToArray();
            if (constructorMatches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"CLR type {typeof(T).FullName} has multiple public constructors matching " +
                    $"PostgreSQL composite {type.QualifiedName}.");
            }

            if (constructorMatches.Length == 1)
            {
                var match = constructorMatches[0]!;
                return new CompositeMapping(match.Constructor, match.FieldIndexes, fields);
            }

            var parameterlessConstructor = typeof(T).IsValueType
                ? null
                : typeof(T).GetConstructor(Type.EmptyTypes) ??
                    throw new InvalidOperationException(
                        $"CLR type {typeof(T).FullName} requires either a matching public constructor " +
                        "or a public parameterless constructor.");
            foreach (var field in fields)
            {
                if (!field.Member.CanWrite)
                {
                    throw new InvalidOperationException(
                        $"CLR member {typeof(T).FullName}.{field.Member.Member.Name} must be writable " +
                        $"to map PostgreSQL field '{field.Field.Name}'.");
                }
            }

            return new CompositeMapping(parameterlessConstructor, constructorFields: null, fields);
        }

        public T Create(BlueTuskRecord record, BlueTuskTypeDescriptor type)
        {
            if (_constructorFields is not null)
            {
                var parameters = _constructor!.GetParameters();
                var arguments = new object?[parameters.Length];
                for (var index = 0; index < arguments.Length; index++)
                {
                    var fieldIndex = _constructorFields[index];
                    arguments[index] = ValidateValue(
                        record[fieldIndex].Value,
                        parameters[index].ParameterType,
                        _fields[fieldIndex].Field.Name,
                        type);
                }

                return (T)_constructor.Invoke(arguments);
            }

            object instance = typeof(T).IsValueType
                ? Activator.CreateInstance<T>()!
                : _constructor!.Invoke(null);
            for (var index = 0; index < _fields.Length; index++)
            {
                var field = _fields[index];
                var value = ValidateValue(
                    record[index].Value,
                    field.Member.ValueType,
                    field.Field.Name,
                    type);
                field.Member.SetValue(instance, value);
            }

            return (T)instance;
        }

        public BlueTuskRecord Decompose(T value, BlueTuskTypeDescriptor type)
        {
            if (value is null)
            {
                throw new InvalidOperationException(
                    $"A null CLR value cannot be encoded inside {type.QualifiedName}.");
            }

            var fields = new BlueTuskRecordField[_fields.Length];
            for (var index = 0; index < fields.Length; index++)
            {
                var mapping = _fields[index];
                fields[index] = new BlueTuskRecordField(
                    mapping.Field.Name,
                    mapping.Type,
                    mapping.Member.GetValue(value));
            }

            return new BlueTuskRecord(fields);
        }

        private static Dictionary<string, MemberAccessor> GetMembers()
        {
            var members = new Dictionary<string, MemberAccessor>(StringComparer.Ordinal);
            foreach (var property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetMethod is null || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                AddMember(
                    members,
                    GetPostgreSqlName(property, property.Name),
                    new MemberAccessor(property));
            }

            foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                AddMember(
                    members,
                    GetPostgreSqlName(field, field.Name),
                    new MemberAccessor(field));
            }

            return members;
        }

        private static void AddMember(
            Dictionary<string, MemberAccessor> members,
            string name,
            MemberAccessor accessor)
        {
            if (!members.TryAdd(name, accessor))
            {
                throw new InvalidOperationException(
                    $"CLR type {typeof(T).FullName} maps more than one public member to PostgreSQL name '{name}'.");
            }
        }

        private static ConstructorMatch? TryMapConstructor(
            ConstructorInfo constructor,
            IReadOnlyList<BlueTuskCompositeField> fields,
            BlueTuskTypeRegistryBuilder registry)
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != fields.Count)
            {
                return null;
            }

            var indexes = new int[parameters.Length];
            var usedFields = new HashSet<int>();
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                if (parameter.Name is null)
                {
                    return null;
                }

                var name = GetPostgreSqlName(parameter, parameter.Name);
                var fieldIndex = -1;
                for (var candidate = 0; candidate < fields.Count; candidate++)
                {
                    if (string.Equals(fields[candidate].Name, name, StringComparison.Ordinal))
                    {
                        fieldIndex = candidate;
                        break;
                    }
                }

                if (fieldIndex < 0 || !usedFields.Add(fieldIndex))
                {
                    return null;
                }

                registry.TryGetCodec(fields[fieldIndex].Type, out var fieldCodec);
                if (!IsCompatibleType(parameter.ParameterType, fieldCodec!.ClrType))
                {
                    return null;
                }

                indexes[index] = fieldIndex;
            }

            return new ConstructorMatch(constructor, indexes);
        }

        private static string GetPostgreSqlName(ICustomAttributeProvider member, string clrName) =>
            member.GetCustomAttributes(typeof(BlueTuskNameAttribute), inherit: false)
                .Cast<BlueTuskNameAttribute>()
                .SingleOrDefault()?.Name ?? ToSnakeCase(clrName);

        private static string ToSnakeCase(string name)
        {
            var result = new StringBuilder(name.Length + 4);
            for (var index = 0; index < name.Length; index++)
            {
                var character = name[index];
                if (char.IsUpper(character) && index != 0 &&
                    (char.IsLower(name[index - 1]) ||
                     char.IsDigit(name[index - 1]) ||
                     index + 1 < name.Length && char.IsLower(name[index + 1])))
                {
                    result.Append('_');
                }

                result.Append(char.ToLowerInvariant(character));
            }

            return result.ToString();
        }

        private static void ValidateCompatibleType(Type target, Type source, string field)
        {
            if (!IsCompatibleType(target, source))
            {
                throw new InvalidOperationException(
                    $"CLR member for PostgreSQL field '{field}' has type {target.FullName}; " +
                    $"{source.FullName} was expected.");
            }
        }

        private static bool IsCompatibleType(Type target, Type source) =>
            target.IsAssignableFrom(source) || Nullable.GetUnderlyingType(target) == source;

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

        private sealed record FieldMapping(
            BlueTuskCompositeField Field,
            BlueTuskTypeDescriptor Type,
            MemberAccessor Member);

        private sealed record ConstructorMatch(ConstructorInfo Constructor, int[] FieldIndexes);

        private sealed class MemberAccessor
        {
            private readonly PropertyInfo? _property;
            private readonly FieldInfo? _field;

            public MemberAccessor(PropertyInfo property)
            {
                _property = property;
                Member = property;
                ValueType = property.PropertyType;
                CanWrite = property.SetMethod?.IsPublic == true;
            }

            public MemberAccessor(FieldInfo field)
            {
                _field = field;
                Member = field;
                ValueType = field.FieldType;
                CanWrite = !field.IsInitOnly;
            }

            public MemberInfo Member { get; }

            public Type ValueType { get; }

            public bool CanWrite { get; }

            public object? GetValue(object instance) =>
                _property is not null ? _property.GetValue(instance) : _field!.GetValue(instance);

            public void SetValue(object instance, object? value)
            {
                if (_property is not null)
                {
                    _property.SetValue(instance, value);
                }
                else
                {
                    _field!.SetValue(instance, value);
                }
            }
        }
    }
}
