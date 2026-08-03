using System.Data.Common;
using System.Globalization;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskUserDefinedTypeMapping : RelationalTypeMapping
{
    private readonly string _postgreSqlTypeName;

    public BlueTuskUserDefinedTypeMapping(string storeType, Type clrType, string postgreSqlTypeName)
        : base(storeType, clrType, System.Data.DbType.Object)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postgreSqlTypeName);
        _postgreSqlTypeName = postgreSqlTypeName;
    }

    private BlueTuskUserDefinedTypeMapping(
        RelationalTypeMappingParameters parameters,
        string postgreSqlTypeName)
        : base(parameters)
    {
        _postgreSqlTypeName = postgreSqlTypeName;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskUserDefinedTypeMapping(parameters, _postgreSqlTypeName);

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
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return $"'{text.Replace("'", "''", StringComparison.Ordinal)}'::{StoreType}";
    }
}
