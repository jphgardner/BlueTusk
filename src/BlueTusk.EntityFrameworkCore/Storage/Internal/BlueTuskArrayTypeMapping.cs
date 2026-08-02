using System.Data.Common;
using System.Text;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskArrayTypeMapping : RelationalTypeMapping
{
    private readonly uint? _postgreSqlTypeOid;
    private readonly string? _postgreSqlTypeName;

    public BlueTuskArrayTypeMapping(
        string storeType,
        Type arrayType,
        uint postgreSqlTypeOid,
        RelationalTypeMapping elementTypeMapping)
        : base(CreateParameters(storeType, arrayType, elementTypeMapping))
    {
        ArgumentOutOfRangeException.ThrowIfZero(postgreSqlTypeOid);
        _postgreSqlTypeOid = postgreSqlTypeOid;
    }

    public BlueTuskArrayTypeMapping(
        string storeType,
        Type arrayType,
        string postgreSqlTypeName,
        RelationalTypeMapping elementTypeMapping)
        : base(CreateParameters(storeType, arrayType, elementTypeMapping))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postgreSqlTypeName);
        _postgreSqlTypeName = postgreSqlTypeName;
    }

    private BlueTuskArrayTypeMapping(
        RelationalTypeMappingParameters parameters,
        uint? postgreSqlTypeOid,
        string? postgreSqlTypeName)
        : base(parameters)
    {
        _postgreSqlTypeOid = postgreSqlTypeOid;
        _postgreSqlTypeName = postgreSqlTypeName;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskArrayTypeMapping(parameters, _postgreSqlTypeOid, _postgreSqlTypeName);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        if (parameter is BlueTuskParameter blueTuskParameter)
        {
            if (_postgreSqlTypeOid is { } postgreSqlTypeOid)
            {
                blueTuskParameter.PostgreSqlTypeOid = postgreSqlTypeOid;
            }
            else
            {
                blueTuskParameter.PostgreSqlTypeName = _postgreSqlTypeName;
            }
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value)
    {
        var array = (Array)value;
        var builder = new StringBuilder("ARRAY");
        var indexes = Enumerable.Range(0, array.Rank)
            .Select(array.GetLowerBound)
            .ToArray();
        AppendDimension(builder, array, indexes, dimension: 0);
        return builder.Append("::").Append(StoreType).ToString();
    }

    private void AppendDimension(StringBuilder builder, Array array, int[] indexes, int dimension)
    {
        builder.Append('[');
        var lowerBound = array.GetLowerBound(dimension);
        var upperBound = array.GetUpperBound(dimension);
        for (var index = lowerBound; index <= upperBound; index++)
        {
            if (index != lowerBound)
            {
                builder.Append(',');
            }

            indexes[dimension] = index;
            if (dimension == array.Rank - 1)
            {
                var item = array.GetValue(indexes);
                builder.Append(
                    item is null
                        ? "NULL"
                        : ((RelationalTypeMapping)ElementTypeMapping!).GenerateSqlLiteral(item));
            }
            else
            {
                AppendDimension(builder, array, indexes, dimension + 1);
            }
        }

        builder.Append(']');
    }

    private static RelationalTypeMappingParameters CreateParameters(
        string storeType,
        Type collectionType,
        RelationalTypeMapping elementTypeMapping)
    {
        var elementType = BlueTuskTypeMappingSource.FindSequenceElementType(collectionType)
            ?? throw new ArgumentException(
                $"A PostgreSQL array mapping requires an array or a supported generic collection, not '{collectionType}'.",
                nameof(collectionType));
        ValueConverter? converter = null;
        ValueComparer comparer;
        if (collectionType.IsArray)
        {
            comparer = (ValueComparer)Activator.CreateInstance(
                typeof(BlueTuskArrayValueComparer<>).MakeGenericType(collectionType))!;
        }
        else
        {
            var converterType = typeof(BlueTuskCollectionToArrayConverter<,>).MakeGenericType(collectionType, elementType);
            converter = (ValueConverter)Activator.CreateInstance(converterType)!;
            comparer = (ValueComparer)Activator.CreateInstance(
                typeof(BlueTuskCollectionValueComparer<,>).MakeGenericType(collectionType, elementType))!;
        }

        return new RelationalTypeMappingParameters(
            new CoreTypeMappingParameters(
                collectionType,
                converter: converter,
                comparer: comparer,
                keyComparer: comparer,
                elementMapping: elementTypeMapping,
                jsonValueReaderWriter: CreateJsonValueReaderWriter(collectionType, elementType, elementTypeMapping)),
            storeType,
            StoreTypePostfix.None,
            System.Data.DbType.Object);
    }

    private static JsonValueReaderWriter? CreateJsonValueReaderWriter(
        Type collectionType,
        Type elementType,
        RelationalTypeMapping elementTypeMapping)
    {
        var elementReaderWriter = elementTypeMapping.JsonValueReaderWriter;
        if (elementReaderWriter is null
            || collectionType.IsArray && collectionType.GetArrayRank() > 1)
        {
            return null;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var concreteCollectionType = collectionType.IsArray
            ? collectionType
            : collectionType.IsAssignableFrom(listType)
                ? listType
                : collectionType;
        var nullableElementType = Nullable.GetUnderlyingType(elementType);
        var readerWriterType = nullableElementType is not null
            ? typeof(JsonCollectionOfNullableStructsReaderWriter<,>)
                .MakeGenericType(concreteCollectionType, nullableElementType)
            : elementType.IsValueType
                ? typeof(JsonCollectionOfStructsReaderWriter<,>)
                    .MakeGenericType(concreteCollectionType, elementType)
                : typeof(JsonCollectionOfReferencesReaderWriter<,>)
                    .MakeGenericType(concreteCollectionType, elementType);
        return (JsonValueReaderWriter)Activator.CreateInstance(readerWriterType, elementReaderWriter)!;
    }

    internal static bool ArraysEqual(Array? left, Array? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Rank != right.Rank || left.Length != right.Length)
        {
            return false;
        }

        for (var dimension = 0; dimension < left.Rank; dimension++)
        {
            if (left.GetLength(dimension) != right.GetLength(dimension)
                || left.GetLowerBound(dimension) != right.GetLowerBound(dimension))
            {
                return false;
            }
        }

        var leftItems = left.GetEnumerator();
        var rightItems = right.GetEnumerator();
        while (leftItems.MoveNext())
        {
            _ = rightItems.MoveNext();
            if (!Equals(leftItems.Current, rightItems.Current))
            {
                return false;
            }
        }

        return true;
    }

    internal static int ArrayHashCode(Array? value)
    {
        if (value is null)
        {
            return 0;
        }

        var hash = new HashCode();
        hash.Add(value.Rank);
        for (var dimension = 0; dimension < value.Rank; dimension++)
        {
            hash.Add(value.GetLength(dimension));
            hash.Add(value.GetLowerBound(dimension));
        }

        foreach (var item in value)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    internal static Array? SnapshotArray(Array? value) => (Array?)value?.Clone();
}

internal sealed class BlueTuskArrayValueComparer<TArray> : ValueComparer<TArray>
{
    public BlueTuskArrayValueComparer()
        : base(
            (left, right) => BlueTuskArrayTypeMapping.ArraysEqual(
                (Array?)(object?)left,
                (Array?)(object?)right),
            value => BlueTuskArrayTypeMapping.ArrayHashCode((Array?)(object?)value),
            value => (TArray)(object?)BlueTuskArrayTypeMapping.SnapshotArray((Array?)(object?)value)!)
    {
    }
}

internal sealed class BlueTuskCollectionToArrayConverter<TCollection, TElement> : ValueConverter<TCollection, TElement[]>
    where TCollection : IEnumerable<TElement>
{
    public BlueTuskCollectionToArrayConverter()
        : base(
            collection => collection.ToArray(),
            array => BlueTuskCollectionFactory<TCollection, TElement>.Create(array))
    {
    }
}

internal sealed class BlueTuskCollectionValueComparer<TCollection, TElement> : ValueComparer<TCollection>
    where TCollection : IEnumerable<TElement>
{
    public BlueTuskCollectionValueComparer()
        : base(
            (left, right) => BlueTuskCollectionFactory<TCollection, TElement>.Equal(left, right),
            value => BlueTuskCollectionFactory<TCollection, TElement>.Hash(value),
            value => BlueTuskCollectionFactory<TCollection, TElement>.Snapshot(value))
    {
    }
}

internal static class BlueTuskCollectionFactory<TCollection, TElement>
    where TCollection : IEnumerable<TElement>
{
    public static TCollection Create(IEnumerable<TElement> values)
    {
        if (typeof(TCollection).IsAssignableFrom(typeof(List<TElement>)))
        {
            return (TCollection)(object)new List<TElement>(values);
        }

        var enumerableConstructor = typeof(TCollection).GetConstructor([typeof(IEnumerable<TElement>)]);
        if (enumerableConstructor is not null)
        {
            return (TCollection)enumerableConstructor.Invoke([values]);
        }

        throw new InvalidOperationException(
            $"Collection type '{typeof(TCollection)}' cannot be materialized from PostgreSQL array values.");
    }

    public static bool Equal(TCollection? left, TCollection? right)
        => ReferenceEquals(left, right)
            || left is not null && right is not null && left.SequenceEqual(right);

    public static int Hash(TCollection? value)
    {
        if (value is null)
        {
            return 0;
        }

        var hash = new HashCode();
        foreach (var element in value)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    public static TCollection Snapshot(TCollection value)
        => Create(value);
}
