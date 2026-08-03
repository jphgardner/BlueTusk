using System.Data.Common;
using System.Globalization;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskNativeTypeMapping : RelationalTypeMapping
{
    private readonly uint _postgreSqlTypeOid;

    public BlueTuskNativeTypeMapping(string storeType, Type clrType, uint postgreSqlTypeOid)
        : base(storeType, clrType, System.Data.DbType.Object)
    {
        ArgumentOutOfRangeException.ThrowIfZero(postgreSqlTypeOid);

        _postgreSqlTypeOid = postgreSqlTypeOid;
    }

    private BlueTuskNativeTypeMapping(
        RelationalTypeMappingParameters parameters,
        uint postgreSqlTypeOid)
        : base(parameters)
    {
        _postgreSqlTypeOid = postgreSqlTypeOid;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskNativeTypeMapping(parameters, _postgreSqlTypeOid);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        if (parameter is BlueTuskParameter blueTuskParameter)
        {
            blueTuskParameter.PostgreSqlTypeOid = _postgreSqlTypeOid;
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return $"'{text.Replace("'", "''", StringComparison.Ordinal)}'::{StoreType}";
    }
}
