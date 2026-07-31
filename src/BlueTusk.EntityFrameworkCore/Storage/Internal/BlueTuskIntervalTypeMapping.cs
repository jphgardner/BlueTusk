using System.Data;
using System.Data.Common;
using System.Globalization;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskIntervalTypeMapping : RelationalTypeMapping
{
    private const uint IntervalOid = 1186;

    public BlueTuskIntervalTypeMapping()
        : base("interval", typeof(TimeSpan), System.Data.DbType.Object)
    {
    }

    private BlueTuskIntervalTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new BlueTuskIntervalTypeMapping(parameters);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        if (parameter is BlueTuskParameter blueTuskParameter
            && blueTuskParameter.Value is TimeSpan value)
        {
            blueTuskParameter.PostgreSqlTypeOid = IntervalOid;
            blueTuskParameter.Value = new BlueTuskInterval(
                months: 0,
                value.Days,
                checked((value.Ticks % TimeSpan.TicksPerDay) / 10));
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value)
        => $"INTERVAL '{((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture)}'";
}
