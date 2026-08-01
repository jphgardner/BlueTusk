using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.LTree;

public abstract class BlueTuskLTreeTextCodec<T> : BlueTuskCodec<T>
    where T : class
{
    private const byte BinaryVersion = 1;

    protected abstract string GetValue(T value);

    protected abstract T Create(string value);

    public override T ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            if (reader.Remaining < 1)
            {
                throw InvalidBinary(type, "the version byte is missing");
            }

            var version = reader.ReadByte();
            if (version != BinaryVersion)
            {
                throw InvalidBinary(type, $"version {version} is unsupported");
            }
        }
        else if (format != BlueTuskDataFormat.Text)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        try
        {
            return Create(reader.ReadRemainingUtf8());
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidBinary(type, "the value is not valid UTF-8", exception);
        }
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        T value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteByte(BinaryVersion);
        }
        else if (format != BlueTuskDataFormat.Text)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        writer.WriteUtf8(GetValue(value));
    }

    protected static int GetBinarySize(string value) =>
        checked(1 + Encoding.UTF8.GetByteCount(value));

    private static InvalidOperationException InvalidBinary(
        BlueTuskTypeDescriptor type,
        string reason,
        Exception? innerException = null) =>
        new(
            $"PostgreSQL {type.QualifiedName} contains an invalid versioned binary value: {reason}.",
            innerException);
}

/// <summary>Encodes PostgreSQL ltree values using the versioned text binary protocol.</summary>
public sealed class BlueTuskLTreeCodec : BlueTuskLTreeTextCodec<BlueTuskLTree>
{
    public static int GetBinarySize(BlueTuskLTree value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return GetBinarySize(value.Value);
    }

    protected override string GetValue(BlueTuskLTree value) => value.Value;

    protected override BlueTuskLTree Create(string value) => new(value);
}

/// <summary>Encodes PostgreSQL lquery values using the versioned text binary protocol.</summary>
public sealed class BlueTuskLQueryCodec : BlueTuskLTreeTextCodec<BlueTuskLQuery>
{
    public static int GetBinarySize(BlueTuskLQuery value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return GetBinarySize(value.Value);
    }

    protected override string GetValue(BlueTuskLQuery value) => value.Value;

    protected override BlueTuskLQuery Create(string value) => new(value);
}

/// <summary>Encodes PostgreSQL ltxtquery values using the versioned text binary protocol.</summary>
public sealed class BlueTuskLTxtQueryCodec : BlueTuskLTreeTextCodec<BlueTuskLTxtQuery>
{
    public static int GetBinarySize(BlueTuskLTxtQuery value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return GetBinarySize(value.Value);
    }

    protected override string GetValue(BlueTuskLTxtQuery value) => value.Value;

    protected override BlueTuskLTxtQuery Create(string value) => new(value);
}
