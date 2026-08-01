using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskRowValueTypeMapping : RelationalTypeMapping
{
    public BlueTuskRowValueTypeMapping(Type clrType)
        : base(new RelationalTypeMappingParameters(
            new CoreTypeMappingParameters(clrType),
            storeType: "record"))
    {
    }

    private BlueTuskRowValueTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new BlueTuskRowValueTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value)
        => throw new InvalidOperationException(
            "PostgreSQL row values are SQL syntax nodes and cannot be emitted as scalar literals.");

    protected override void ConfigureParameter(DbParameter parameter)
        => throw new InvalidOperationException(
            "PostgreSQL row values are SQL syntax nodes and cannot be bound as scalar parameters.");
}
