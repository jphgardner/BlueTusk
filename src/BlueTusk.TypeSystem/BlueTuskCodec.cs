using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace BlueTusk.TypeSystem;

internal interface IBlueTuskArrayFactory
{
    Type ArrayClrType { get; }

    Array CreateArray(ReadOnlySpan<int> lengths, ReadOnlySpan<int> lowerBounds);
}

internal interface IBlueTuskRangeCodecFactory
{
    IBlueTuskCodec? CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec);
}

internal interface IBlueTuskArrayRangeCodecFactory
{
    IBlueTuskCodec CreateArrayRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec);
}

internal interface IBlueTuskMultirangeCodecFactory
{
    IBlueTuskCodec CreateMultirangeCodec(BlueTuskTypeDescriptor rangeType);
}

internal static class BlueTuskDynamicRangeCodecFactory
{
    public static IBlueTuskCodec Create(
        Type subtypeClrType,
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return new BlueTuskUnsupportedNativeAotRangeCodec(
                subtypeClrType,
                subtype);
        }

        return CreateDynamic(subtypeClrType, subtype, subtypeCodec);
    }

    [RequiresDynamicCode(
        "Constructing a range codec for a nested range or multirange subtype requires " +
        "a closed generic type selected from the runtime PostgreSQL catalogue.")]
    private static IBlueTuskCodec CreateDynamic(
        Type subtypeClrType,
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec)
    {
        var codecType = typeof(BlueTuskRangeCodec<>).MakeGenericType(subtypeClrType);
        return (IBlueTuskCodec)(Activator.CreateInstance(
                codecType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [subtype, subtypeCodec],
                culture: null) ??
            throw new InvalidOperationException(
                $"Could not create a range codec for {subtypeClrType.FullName}."));
    }
}

internal sealed class BlueTuskUnsupportedNativeAotRangeCodec :
    IBlueTuskCodec,
    IBlueTuskArrayFactory,
    IBlueTuskMultirangeCodecFactory
{
    private readonly Type _subtypeClrType;
    private readonly BlueTuskTypeDescriptor _subtype;

    public BlueTuskUnsupportedNativeAotRangeCodec(
        Type subtypeClrType,
        BlueTuskTypeDescriptor subtype)
    {
        _subtypeClrType = subtypeClrType;
        _subtype = subtype;
    }

    public Type ClrType => typeof(object);

    Type IBlueTuskArrayFactory.ArrayClrType => typeof(object[]);

    public object? Read(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        throw Unsupported(type);

    public void Write(
        ref BlueTuskWriter writer,
        object? value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        throw Unsupported(type);

    Array IBlueTuskArrayFactory.CreateArray(
        ReadOnlySpan<int> lengths,
        ReadOnlySpan<int> lowerBounds) =>
        throw Unsupported(_subtype);

    IBlueTuskCodec IBlueTuskMultirangeCodecFactory.CreateMultirangeCodec(
        BlueTuskTypeDescriptor rangeType) =>
        new BlueTuskUnsupportedNativeAotRangeCodec(
            _subtypeClrType,
            rangeType);

    private NotSupportedException Unsupported(BlueTuskTypeDescriptor type) =>
        new(
            $"NativeAOT cannot materialize PostgreSQL range '{type.QualifiedName}' because " +
            $"its catalogue subtype '{_subtype.QualifiedName}' maps to " +
            $"'{_subtypeClrType.FullName}'. Register a statically implemented codec or use a JIT deployment.");
}

internal static class BlueTuskBuiltInRangeCodecFactory<T>
{
    public static IBlueTuskCodec Create(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        new BlueTuskRangeCodec<T>(subtype, subtypeCodec);
}

/// <summary>Provides the non-generic dispatch required by a strongly typed BlueTusk codec.</summary>
public abstract class BlueTuskCodec<T> :
    IBlueTuskCodec<T>,
    IBlueTuskRangeCodecFactory,
    IBlueTuskArrayRangeCodecFactory,
    IBlueTuskArrayFactory
{
    public Type ClrType => typeof(T);

    Type IBlueTuskArrayFactory.ArrayClrType => typeof(T[]);

    public abstract T ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type);

    public abstract void WriteTyped(
        ref BlueTuskWriter writer,
        T value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type);

    object? IBlueTuskCodec.Read(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => ReadTyped(ref reader, format, type);

    void IBlueTuskCodec.Write(
        ref BlueTuskWriter writer,
        object? value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (value is not T typedValue)
        {
            throw new InvalidCastException(
                $"The {type.QualifiedName} codec requires a {typeof(T).FullName} value.");
        }

        WriteTyped(ref writer, typedValue, format, type);
    }

    IBlueTuskCodec? IBlueTuskRangeCodecFactory.CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        BlueTuskDynamicRangeCodecFactory.Create(typeof(T), subtype, subtypeCodec);

    IBlueTuskCodec IBlueTuskArrayRangeCodecFactory.CreateArrayRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        BlueTuskDynamicRangeCodecFactory.Create(typeof(T[]), subtype, subtypeCodec);

    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification =
            "Dynamic array construction is reached only when dynamic code is available; " +
            "NativeAOT uses statically rooted one-dimensional arrays and rejects other shapes.")]
    Array IBlueTuskArrayFactory.CreateArray(
        ReadOnlySpan<int> lengths,
        ReadOnlySpan<int> lowerBounds)
    {
        if (lengths.Length != lowerBounds.Length)
        {
            throw new ArgumentException(
                "Array lengths and lower bounds must have the same rank.",
                nameof(lowerBounds));
        }

        if (lengths.Length == 1 && lowerBounds[0] == 0)
        {
            return new T[lengths[0]];
        }

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            if (lowerBounds.ContainsAnyExcept(0))
            {
                throw new NotSupportedException(
                    "NativeAOT supports PostgreSQL arrays with the standard lower bound of 1. " +
                    "Arrays with non-standard lower bounds require a JIT deployment.");
            }

            throw new NotSupportedException(
                "NativeAOT supports one-dimensional PostgreSQL arrays. " +
                "Multidimensional arrays require a JIT deployment.");
        }

        return Array.CreateInstance(
            typeof(T),
            lengths.ToArray(),
            lowerBounds.ToArray());
    }
}
