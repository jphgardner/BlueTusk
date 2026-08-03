using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskBoolTypeMapping : RelationalTypeMapping
{
    public BlueTuskBoolTypeMapping()
        : base(
            "boolean",
            typeof(bool),
            System.Data.DbType.Boolean,
            jsonValueReaderWriter: JsonBoolReaderWriter.Instance)
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
