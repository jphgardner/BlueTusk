using System.Data.Common;
using System.Text;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.Extensions.Citext.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskCitextTypeMapping : RelationalTypeMapping
{
    private readonly string _postgreSqlTypeName;

    public BlueTuskCitextTypeMapping(string schema)
        : this(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(typeof(BlueTuskCitext)),
                BlueTuskSqlIdentifier.Delimit("citext", schema),
                StoreTypePostfix.None,
                System.Data.DbType.Object),
            BlueTuskSqlIdentifier.Delimit("citext", schema))
    {
    }

    private BlueTuskCitextTypeMapping(
        RelationalTypeMappingParameters parameters,
        string postgreSqlTypeName)
        : base(parameters)
    {
        _postgreSqlTypeName = postgreSqlTypeName;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskCitextTypeMapping(parameters, _postgreSqlTypeName);

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
        var text = ((BlueTuskCitext)value).Value.Replace("'", "''", StringComparison.Ordinal);
        return $"'{text}'::{StoreType}";
    }
}

internal sealed class BlueTuskCitextArrayTypeMapping : RelationalTypeMapping
{
    private readonly string _postgreSqlTypeName;
    private readonly RelationalTypeMapping _arrayElementMapping;

    public BlueTuskCitextArrayTypeMapping(string schema, RelationalTypeMapping elementMapping)
        : this(
            CreateParameters(schema, elementMapping),
            $"{BlueTuskSqlIdentifier.Delimit("citext", schema)}[]", elementMapping)
    {
    }

    private BlueTuskCitextArrayTypeMapping(
        RelationalTypeMappingParameters parameters,
        string postgreSqlTypeName, RelationalTypeMapping elementMapping)
        : base(parameters)
    {
        _postgreSqlTypeName = postgreSqlTypeName;
        _arrayElementMapping = elementMapping;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskCitextArrayTypeMapping(parameters, _postgreSqlTypeName, _arrayElementMapping);

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
        var values = (BlueTuskCitext[])value;
        var builder = new StringBuilder("ARRAY[");
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

#if BLUETUSK_EF8_OR_GREATER
            builder.Append(((RelationalTypeMapping)ElementTypeMapping!).GenerateSqlLiteral(values[index]));
#else
            builder.Append(_arrayElementMapping.GenerateSqlLiteral(values[index]));
#endif
        }

        return builder.Append("]::").Append(StoreType).ToString();
    }

    private static RelationalTypeMappingParameters CreateParameters(
        string schema,
        RelationalTypeMapping elementMapping)
    {
        var comparer = new ValueComparer<BlueTuskCitext[]>(
            (left, right) => left != null && right != null && left.SequenceEqual(right),
            value => ValueHashCode(value),
            value => value == null ? null! : value.ToArray());
        return new RelationalTypeMappingParameters(
            new CoreTypeMappingParameters(
                typeof(BlueTuskCitext[]),
                comparer: comparer,
                keyComparer: comparer
#if NET8_0_OR_GREATER
                , elementMapping: elementMapping
#endif
            ),
            $"{BlueTuskSqlIdentifier.Delimit("citext", schema)}[]",
            StoreTypePostfix.None,
            System.Data.DbType.Object);
    }

    private static int ValueHashCode(IEnumerable<BlueTuskCitext>? values)
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
