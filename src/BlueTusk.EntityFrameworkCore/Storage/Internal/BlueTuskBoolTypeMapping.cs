using Microsoft.EntityFrameworkCore.Storage;
#if BLUETUSK_EF8_OR_GREATER
using Microsoft.EntityFrameworkCore.Storage.Json;
#endif

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskBoolTypeMapping : RelationalTypeMapping
{
    public BlueTuskBoolTypeMapping()
        : base(
            "boolean",
            typeof(bool),
            System.Data.DbType.Boolean
#if BLUETUSK_EF8_OR_GREATER
            , jsonValueReaderWriter: JsonBoolReaderWriter.Instance
#endif
        )
    {
    }

    private BlueTuskBoolTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new BlueTuskBoolTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value)
        => (bool)value ? "TRUE" : "FALSE";
}
