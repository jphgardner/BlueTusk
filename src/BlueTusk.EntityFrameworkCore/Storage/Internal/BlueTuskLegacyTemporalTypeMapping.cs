#if !BLUETUSK_EF7_OR_GREATER
using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskLegacyTemporalTypeMapping : RelationalTypeMapping
{
    private readonly string _format;

    public BlueTuskLegacyTemporalTypeMapping(string storeType, Type clrType, DbType dbType, string format)
        : base(storeType, clrType, dbType) => _format = format;

    private BlueTuskLegacyTemporalTypeMapping(RelationalTypeMappingParameters parameters, string format)
        : base(parameters) => _format = format;

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskLegacyTemporalTypeMapping(parameters, _format);

    protected override string GenerateNonNullSqlLiteral(object value) =>
        $"{StoreType} '{((IFormattable)value).ToString(_format, CultureInfo.InvariantCulture)}'";
}
#endif
