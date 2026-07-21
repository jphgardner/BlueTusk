using System.Text;

namespace BlueTusk.TypeSystem;

/// <summary>Reads and writes named PostgreSQL composites and reads anonymous records.</summary>
public sealed class BlueTuskRecordCodec : BlueTuskCodec<BlueTuskRecord>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly FieldBinding[]? _namedFields;
    private readonly IReadOnlyDictionary<BlueTuskTypeId, FieldCodec> _knownTypes;

    private BlueTuskRecordCodec(
        FieldBinding[]? namedFields,
        IReadOnlyDictionary<BlueTuskTypeId, FieldCodec> knownTypes)
    {
        _namedFields = namedFields;
        _knownTypes = knownTypes;
    }

    public override BlueTuskRecord ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary => ReadBinary(ref reader, type),
            BlueTuskDataFormat.Text => ReadText(ref reader, type),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskRecord value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_namedFields is null)
        {
            throw new NotSupportedException("PostgreSQL does not support binary input for an anonymous record type.");
        }

        ValidateRecord(value, type);
        switch (format)
        {
            case BlueTuskDataFormat.Binary:
                WriteBinary(ref writer, value);
                break;
            case BlueTuskDataFormat.Text:
                WriteText(ref writer, value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    internal static bool TryCreate(
        BlueTuskTypeDescriptor type,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        BlueTuskTypeRegistryBuilder registry,
        out BlueTuskRecordCodec codec)
    {
        var fields = new FieldBinding[type.CompositeFields.Count];
        for (var index = 0; index < fields.Length; index++)
        {
            var field = type.CompositeFields[index];
            if (!types.TryGetValue(field.Type, out var fieldType) ||
                !registry.TryGetCodec(field.Type, out var fieldCodec) ||
                fieldCodec is null)
            {
                codec = null!;
                return false;
            }

            fields[index] = new FieldBinding(field, fieldType, fieldCodec);
        }

        codec = new BlueTuskRecordCodec(fields, CreateKnownTypes(types, registry));
        return true;
    }

    internal static BlueTuskRecordCodec CreateAnonymous(
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        BlueTuskTypeRegistryBuilder registry) =>
        new(namedFields: null, CreateKnownTypes(types, registry));

    private BlueTuskRecord ReadBinary(ref BlueTuskReader reader, BlueTuskTypeDescriptor recordType)
    {
        var fieldCount = reader.ReadInt32BigEndian();
        if (fieldCount < 0 || fieldCount > reader.Remaining / (sizeof(uint) + sizeof(int)))
        {
            throw new InvalidOperationException(
                $"The {recordType.QualifiedName} binary record has invalid field count {fieldCount}.");
        }

        if (_namedFields is not null && fieldCount != _namedFields.Length)
        {
            throw new InvalidOperationException(
                $"The {recordType.QualifiedName} binary record contains {fieldCount} fields; " +
                $"{_namedFields.Length} were expected.");
        }

        var fields = new BlueTuskRecordField[fieldCount];
        for (var index = 0; index < fieldCount; index++)
        {
            var wireTypeId = new BlueTuskTypeId(reader.ReadUInt32BigEndian());
            var binding = _namedFields?[index];
            if (binding is not null && wireTypeId != binding.Type.Id)
            {
                throw new InvalidOperationException(
                    $"The {recordType.QualifiedName} binary field '{binding.Field.Name}' has OID {wireTypeId}; " +
                    $"OID {binding.Type.Id} was expected.");
            }

            var length = reader.ReadInt32BigEndian();
            if (length < -1)
            {
                throw new InvalidOperationException(
                    $"The {recordType.QualifiedName} binary record has invalid field length {length}.");
            }

            var fieldType = binding?.Type;
            var fieldCodec = binding?.Codec;
            if (binding is null && _knownTypes.TryGetValue(wireTypeId, out var known))
            {
                fieldType = known.Type;
                fieldCodec = known.Codec;
            }

            var value = length == -1
                ? null
                : DecodeField(ref reader, length, wireTypeId, fieldType, fieldCodec);
            fields[index] = new BlueTuskRecordField(binding?.Field.Name, fieldType, value);
        }

        return new BlueTuskRecord(fields);
    }

    private BlueTuskRecord ReadText(ref BlueTuskReader reader, BlueTuskTypeDescriptor recordType)
    {
        var parsed = BlueTuskRecordTextParser.Parse(
            reader.ReadRemainingUtf8(),
            _namedFields?.Length);
        var fields = new BlueTuskRecordField[parsed.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            var binding = _namedFields?[index];
            object? value;
            if (parsed[index] is null)
            {
                value = null;
            }
            else if (binding is null)
            {
                value = parsed[index];
            }
            else
            {
                var bytes = StrictUtf8.GetBytes(parsed[index]!);
                var fieldReader = new BlueTuskReader(bytes);
                value = binding.Codec.Read(ref fieldReader, BlueTuskDataFormat.Text, binding.Type);
                EnsureFieldConsumed(fieldReader.Remaining, binding.Field.Name);
            }

            fields[index] = new BlueTuskRecordField(binding?.Field.Name, binding?.Type, value);
        }

        return new BlueTuskRecord(fields);
    }

    private static object DecodeField(
        ref BlueTuskReader reader,
        int length,
        BlueTuskTypeId wireTypeId,
        BlueTuskTypeDescriptor? fieldType,
        IBlueTuskCodec? fieldCodec)
    {
        var bytes = reader.ReadBytes(length);
        if (fieldType is null || fieldCodec is null)
        {
            var unknownType = fieldType ?? new BlueTuskTypeDescriptor
            {
                Id = wireTypeId,
                Schema = string.Empty,
                Name = $"oid_{wireTypeId}",
                Kind = BlueTuskTypeKind.Unknown,
            };
            return new BlueTuskUnknownValue(
                unknownType,
                BlueTuskDataFormat.Binary,
                bytes.ToArray());
        }

        var fieldReader = new BlueTuskReader(bytes);
        var value = fieldCodec.Read(ref fieldReader, BlueTuskDataFormat.Binary, fieldType);
        EnsureFieldConsumed(fieldReader.Remaining, fieldType.QualifiedName);
        return value!;
    }

    private void WriteBinary(ref BlueTuskWriter writer, BlueTuskRecord value)
    {
        writer.WriteInt32BigEndian(value.Count);
        for (var index = 0; index < value.Count; index++)
        {
            var binding = _namedFields![index];
            writer.WriteUInt32BigEndian(binding.Type.Id.Oid);
            var fieldValue = value[index].Value;
            if (fieldValue is null or DBNull)
            {
                writer.WriteInt32BigEndian(-1);
                continue;
            }

            var lengthOffset = writer.WrittenCount;
            writer.WriteInt32BigEndian(0);
            var valueOffset = writer.WrittenCount;
            binding.Codec.Write(ref writer, fieldValue, BlueTuskDataFormat.Binary, binding.Type);
            writer.WriteInt32BigEndianAt(lengthOffset, writer.WrittenCount - valueOffset);
        }
    }

    private void WriteText(ref BlueTuskWriter writer, BlueTuskRecord value)
    {
        writer.WriteByte((byte)'(');
        for (var index = 0; index < value.Count; index++)
        {
            if (index != 0)
            {
                writer.WriteByte((byte)',');
            }

            var fieldValue = value[index].Value;
            if (fieldValue is null or DBNull)
            {
                continue;
            }

            var binding = _namedFields![index];
            WriteTextField(ref writer, EncodeTextField(binding, fieldValue));
        }

        writer.WriteByte((byte)')');
    }

    private static void WriteTextField(ref BlueTuskWriter writer, string text)
    {
        var requiresQuotes = text.Length == 0 || text.Any(character =>
            character is '(' or ')' or ',' or '"' or '\\' || char.IsWhiteSpace(character));
        if (!requiresQuotes)
        {
            writer.WriteUtf8(text);
            return;
        }

        var escaped = new StringBuilder(text.Length + 2);
        escaped.Append('"');
        foreach (var character in text)
        {
            if (character is '"' or '\\')
            {
                escaped.Append(character);
            }

            escaped.Append(character);
        }

        escaped.Append('"');
        writer.WriteUtf8(escaped.ToString());
    }

    private static string EncodeTextField(FieldBinding binding, object value)
    {
        var length = 64;
        while (true)
        {
            var bytes = new byte[length];
            var writer = new BlueTuskWriter(bytes);
            try
            {
                binding.Codec.Write(ref writer, value, BlueTuskDataFormat.Text, binding.Type);
                return StrictUtf8.GetString(bytes, 0, writer.WrittenCount);
            }
            catch (BlueTuskWriteBufferTooSmallException) when (length < Array.MaxLength)
            {
                length = length > Array.MaxLength / 2 ? Array.MaxLength : length * 2;
            }
        }
    }

    private void ValidateRecord(BlueTuskRecord value, BlueTuskTypeDescriptor type)
    {
        if (value.Count != _namedFields!.Length)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} record contains {value.Count} fields; {_namedFields.Length} were expected.");
        }

        for (var index = 0; index < value.Count; index++)
        {
            var suppliedName = value[index].Name;
            if (suppliedName is not null &&
                !string.Equals(suppliedName, _namedFields[index].Field.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Record field {index} is named '{suppliedName}'; " +
                    $"'{_namedFields[index].Field.Name}' was expected.");
            }
        }
    }

    private static Dictionary<BlueTuskTypeId, FieldCodec> CreateKnownTypes(
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> types,
        BlueTuskTypeRegistryBuilder registry)
    {
        var knownTypes = new Dictionary<BlueTuskTypeId, FieldCodec>();
        foreach (var type in types.Values)
        {
            if (registry.TryGetCodec(type.Id, out var codec) && codec is not null)
            {
                knownTypes.Add(type.Id, new FieldCodec(type, codec));
            }
        }

        return knownTypes;
    }

    private static void EnsureFieldConsumed(int remaining, string field)
    {
        if (remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {field} codec left {remaining} unread record-field bytes.");
        }
    }

    private sealed record FieldBinding(
        BlueTuskCompositeField Field,
        BlueTuskTypeDescriptor Type,
        IBlueTuskCodec Codec);

    private sealed record FieldCodec(
        BlueTuskTypeDescriptor Type,
        IBlueTuskCodec Codec);
}
