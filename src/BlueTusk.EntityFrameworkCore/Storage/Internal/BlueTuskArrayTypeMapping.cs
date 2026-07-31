using System.Data.Common;
using System.Text;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

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
        if (!arrayType.IsArray)
        {
            throw new ArgumentException("A PostgreSQL array mapping requires a CLR array type.", nameof(arrayType));
        }

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
        if (!arrayType.IsArray)
        {
            throw new ArgumentException("A PostgreSQL array mapping requires a CLR array type.", nameof(arrayType));
        }

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
        Type arrayType,
        RelationalTypeMapping elementTypeMapping)
    {
        var comparer = (ValueComparer)Activator.CreateInstance(
            typeof(BlueTuskArrayValueComparer<>).MakeGenericType(arrayType))!;
        return new RelationalTypeMappingParameters(
            new CoreTypeMappingParameters(
                arrayType,
                comparer: comparer,
                keyComparer: comparer,
                elementMapping: elementTypeMapping),
            storeType,
            StoreTypePostfix.None,
            System.Data.DbType.Object);
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
