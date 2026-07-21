using System.Text;

namespace BlueTusk.TypeSystem;

public sealed class BlueTuskTextSearchVectorCodec : BlueTuskCodec<BlueTuskTextSearchVector>
{
    public override BlueTuskTextSearchVector ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskTextSearchVector.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining < sizeof(int))
        {
            throw InvalidBinary(type);
        }

        var count = reader.ReadInt32BigEndian();
        if (count < 0 || count > reader.Remaining / 4)
        {
            throw InvalidBinary(type);
        }

        var entries = new BlueTuskTextSearchVectorEntry[count];
        for (var index = 0; index < entries.Length; index++)
        {
            var lexeme = reader.ReadNullTerminatedUtf8();
            if (reader.Remaining < sizeof(ushort))
            {
                throw InvalidBinary(type);
            }

            var positionCount = reader.ReadUInt16BigEndian();
            if (positionCount > 256 || reader.Remaining < positionCount * sizeof(ushort))
            {
                throw InvalidBinary(type);
            }

            var positions = new BlueTuskTextSearchPosition[positionCount];
            for (var positionIndex = 0; positionIndex < positions.Length; positionIndex++)
            {
                var packed = reader.ReadUInt16BigEndian();
                if ((packed & 0x3FFF) == 0)
                {
                    throw InvalidBinary(type);
                }

                positions[positionIndex] = new BlueTuskTextSearchPosition(
                    packed & 0x3FFF,
                    (BlueTuskTextSearchWeight)(packed >> 14));
            }

            entries[index] = new BlueTuskTextSearchVectorEntry(lexeme, positions);
        }

        if (reader.Remaining != 0)
        {
            throw InvalidBinary(type);
        }

        return new BlueTuskTextSearchVector(entries);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskTextSearchVector value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
            return;
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        writer.WriteInt32BigEndian(value.Count);
        foreach (var entry in value)
        {
            writer.WriteUtf8(entry.Lexeme);
            writer.WriteByte(0);
            writer.WriteUInt16BigEndian(checked((ushort)entry.Positions.Count));
            foreach (var position in entry.Positions)
            {
                writer.WriteUInt16BigEndian((ushort)(((int)position.Weight << 14) | position.Position));
            }
        }
    }

    public static int GetBinarySize(BlueTuskTextSearchVector value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var size = sizeof(int);
        foreach (var entry in value)
        {
            size = checked(size + Encoding.UTF8.GetByteCount(entry.Lexeme) + 1 + sizeof(ushort));
            size = checked(size + (entry.Positions.Count * sizeof(ushort)));
        }

        return size;
    }

    private static InvalidOperationException InvalidBinary(BlueTuskTypeDescriptor type) =>
        new($"PostgreSQL {type.QualifiedName} binary value has an invalid layout.");
}

public sealed class BlueTuskTextSearchQueryCodec : BlueTuskCodec<BlueTuskTextSearchQuery>
{
    private const byte OperandItem = 1;
    private const byte OperatorItem = 2;
    private const byte NotOperator = 1;

    public override BlueTuskTextSearchQuery ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskTextSearchQuery.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining < sizeof(int))
        {
            throw InvalidBinary(type);
        }

        var count = reader.ReadInt32BigEndian();
        if (count < 0 || count > reader.Remaining / 2)
        {
            throw InvalidBinary(type);
        }

        if (count == 0)
        {
            if (reader.Remaining != 0)
            {
                throw InvalidBinary(type);
            }

            return BlueTuskTextSearchQuery.Empty;
        }

        var items = new WireItem[count];
        for (var index = 0; index < items.Length; index++)
        {
            if (reader.Remaining < 2)
            {
                throw InvalidBinary(type);
            }

            var itemType = reader.ReadByte();
            if (itemType == OperandItem)
            {
                if (reader.Remaining < 3)
                {
                    throw InvalidBinary(type);
                }

                var weights = reader.ReadByte();
                var prefix = reader.ReadByte();
                if (weights > 0x0F || prefix > 1)
                {
                    throw InvalidBinary(type);
                }

                items[index] = new WireOperand(
                    new BlueTuskTextSearchQueryLexeme(
                        reader.ReadNullTerminatedUtf8(),
                        (BlueTuskTextSearchWeights)weights,
                        prefix == 1));
            }
            else if (itemType == OperatorItem)
            {
                var @operator = reader.ReadByte();
                if (@operator == NotOperator ||
                    @operator is (byte)BlueTuskTextSearchQueryOperator.And or
                        (byte)BlueTuskTextSearchQueryOperator.Or)
                {
                    items[index] = new WireOperator(@operator, 0);
                }
                else if (@operator == (byte)BlueTuskTextSearchQueryOperator.Phrase)
                {
                    if (reader.Remaining < sizeof(short))
                    {
                        throw InvalidBinary(type);
                    }

                    var distance = reader.ReadInt16BigEndian();
                    if (distance is < 0 or > 16_384)
                    {
                        throw InvalidBinary(type);
                    }

                    items[index] = new WireOperator(@operator, distance);
                }
                else
                {
                    throw InvalidBinary(type);
                }
            }
            else
            {
                throw InvalidBinary(type);
            }
        }

        if (reader.Remaining != 0)
        {
            throw InvalidBinary(type);
        }

        var nodes = new Stack<BlueTuskTextSearchQueryNode>();
        for (var index = items.Length - 1; index >= 0; index--)
        {
            if (items[index] is WireOperand operand)
            {
                nodes.Push(operand.Value);
                continue;
            }

            var operation = (WireOperator)items[index];
            if (operation.Operator == NotOperator)
            {
                if (!nodes.TryPop(out var child))
                {
                    throw InvalidBinary(type);
                }

                nodes.Push(new BlueTuskTextSearchQueryNot(child));
                continue;
            }

            if (!nodes.TryPop(out var right) || !nodes.TryPop(out var left))
            {
                throw InvalidBinary(type);
            }

            nodes.Push(new BlueTuskTextSearchQueryBinary(
                (BlueTuskTextSearchQueryOperator)operation.Operator,
                left,
                right,
                operation.Distance));
        }

        if (nodes.Count != 1)
        {
            throw InvalidBinary(type);
        }

        return new BlueTuskTextSearchQuery(nodes.Pop());
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskTextSearchQuery value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
            return;
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        var nodes = GetWireNodes(value);
        writer.WriteInt32BigEndian(nodes.Count);
        foreach (var node in nodes)
        {
            switch (node)
            {
                case BlueTuskTextSearchQueryLexeme lexeme:
                    writer.WriteByte(OperandItem);
                    writer.WriteByte((byte)lexeme.Weights);
                    writer.WriteByte(lexeme.IsPrefix ? (byte)1 : (byte)0);
                    writer.WriteUtf8(lexeme.Lexeme);
                    writer.WriteByte(0);
                    break;
                case BlueTuskTextSearchQueryNot:
                    writer.WriteByte(OperatorItem);
                    writer.WriteByte(NotOperator);
                    break;
                case BlueTuskTextSearchQueryBinary binary:
                    writer.WriteByte(OperatorItem);
                    writer.WriteByte((byte)binary.Operator);
                    if (binary.Operator == BlueTuskTextSearchQueryOperator.Phrase)
                    {
                        writer.WriteInt16BigEndian(checked((short)binary.PhraseDistance));
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unknown text-search query node {node.GetType().FullName}.");
            }
        }
    }

    public static int GetBinarySize(BlueTuskTextSearchQuery value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var size = sizeof(int);
        foreach (var node in GetWireNodes(value))
        {
            size = node switch
            {
                BlueTuskTextSearchQueryLexeme lexeme =>
                    checked(size + 4 + Encoding.UTF8.GetByteCount(lexeme.Lexeme)),
                BlueTuskTextSearchQueryBinary { Operator: BlueTuskTextSearchQueryOperator.Phrase } =>
                    checked(size + 4),
                _ => checked(size + 2),
            };
        }

        return size;
    }

    private static List<BlueTuskTextSearchQueryNode> GetWireNodes(BlueTuskTextSearchQuery value)
    {
        var result = new List<BlueTuskTextSearchQueryNode>();
        if (value.Root is null)
        {
            return result;
        }

        var pending = new Stack<BlueTuskTextSearchQueryNode>();
        pending.Push(value.Root);
        while (pending.TryPop(out var node))
        {
            result.Add(node);
            switch (node)
            {
                case BlueTuskTextSearchQueryNot not:
                    pending.Push(not.Operand);
                    break;
                case BlueTuskTextSearchQueryBinary binary:
                    pending.Push(binary.Left);
                    pending.Push(binary.Right);
                    break;
            }
        }

        return result;
    }

    private static InvalidOperationException InvalidBinary(BlueTuskTypeDescriptor type) =>
        new($"PostgreSQL {type.QualifiedName} binary value has an invalid layout.");

    private abstract record WireItem;

    private sealed record WireOperand(BlueTuskTextSearchQueryLexeme Value) : WireItem;

    private sealed record WireOperator(byte Operator, int Distance) : WireItem;
}
