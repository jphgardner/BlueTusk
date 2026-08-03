using System.Data.Common;
using System.Text;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.Extensions.PgVector.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskPgVectorTypeMapping<TValue> : RelationalTypeMapping
    where TValue : class
{
    private readonly string _postgreSqlTypeName;

    public BlueTuskPgVectorTypeMapping(
        string schema,
        string typeName,
        string? storeType = null)
        : this(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(
                    typeof(TValue),
                    comparer: CreateComparer(),
                    keyComparer: CreateComparer()),
                storeType ?? BlueTuskSqlIdentifier.Delimit(typeName, schema),
                StoreTypePostfix.None,
                System.Data.DbType.Object),
            BlueTuskSqlIdentifier.Delimit(typeName, schema))
    {
    }

    private BlueTuskPgVectorTypeMapping(
        RelationalTypeMappingParameters parameters,
        string postgreSqlTypeName)
        : base(parameters)
    {
        _postgreSqlTypeName = postgreSqlTypeName;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskPgVectorTypeMapping<TValue>(parameters, _postgreSqlTypeName);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        if (parameter is BlueTuskParameter blueTuskParameter)
        {
            blueTuskParameter.PostgreSqlTypeName = _postgreSqlTypeName;
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value)
    {
        var text = value.ToString()!.Replace("'", "''", StringComparison.Ordinal);
        return $"'{text}'::{StoreType}";
    }

    private static ValueComparer<TValue> CreateComparer() =>
        new(
            (left, right) => object.Equals(left, right),
            value => value.GetHashCode(),
            value => value);
}

internal sealed class BlueTuskPgVectorArrayTypeMapping<TValue> : RelationalTypeMapping
    where TValue : class
{
    private readonly string _postgreSqlTypeName;

    public BlueTuskPgVectorArrayTypeMapping(
        string schema,
        string typeName,
        RelationalTypeMapping elementMapping,
        string? storeType = null)
        : this(
            CreateParameters(schema, typeName, elementMapping, storeType),
            $"{BlueTuskSqlIdentifier.Delimit(typeName, schema)}[]")
    {
    }

    private BlueTuskPgVectorArrayTypeMapping(
        RelationalTypeMappingParameters parameters,
        string postgreSqlTypeName)
        : base(parameters)
    {
        _postgreSqlTypeName = postgreSqlTypeName;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskPgVectorArrayTypeMapping<TValue>(parameters, _postgreSqlTypeName);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        if (parameter is BlueTuskParameter blueTuskParameter)
        {
            blueTuskParameter.PostgreSqlTypeName = _postgreSqlTypeName;
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value)
    {
        var values = (TValue[])value;
        var builder = new StringBuilder("ARRAY[");
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(((RelationalTypeMapping)ElementTypeMapping!).GenerateSqlLiteral(values[index]));
        }

        return builder.Append("]::").Append(StoreType).ToString();
    }

    private static RelationalTypeMappingParameters CreateParameters(
        string schema,
        string typeName,
        RelationalTypeMapping elementMapping,
        string? storeType)
    {
        var comparer = new ValueComparer<TValue[]>(
            (left, right) => left != null && right != null && left.SequenceEqual(right),
            value => ValueHashCode(value),
            value => value == null ? null! : value.ToArray());
        return new RelationalTypeMappingParameters(
            new CoreTypeMappingParameters(
                typeof(TValue[]),
                comparer: comparer,
                keyComparer: comparer,
                elementMapping: elementMapping),
            storeType ?? $"{BlueTuskSqlIdentifier.Delimit(typeName, schema)}[]",
            StoreTypePostfix.None,
            System.Data.DbType.Object);
    }

    private static int ValueHashCode(IEnumerable<TValue>? values)
    {
        if (values is null)
        {
            return 0;
        }

        var hash = new HashCode();
        foreach (var value in values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
