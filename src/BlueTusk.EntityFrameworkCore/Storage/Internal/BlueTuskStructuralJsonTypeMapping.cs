using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskStructuralJsonTypeMapping : JsonTypeMapping
{
    private static readonly MethodInfo GetStringMethod =
        typeof(DbDataReader).GetRuntimeMethod(nameof(DbDataReader.GetString), [typeof(int)])!;
    private static readonly PropertyInfo Utf8Property =
        typeof(Encoding).GetProperty(nameof(Encoding.UTF8))!;
    private static readonly MethodInfo GetBytesMethod =
        typeof(Encoding).GetMethod(nameof(Encoding.GetBytes), [typeof(string)])!;
    private static readonly ConstructorInfo MemoryStreamConstructor =
        typeof(MemoryStream).GetConstructor([typeof(byte[])])!;

    public BlueTuskStructuralJsonTypeMapping(string storeType)
        : base(storeType, typeof(JsonTypePlaceholder), dbType: null)
    {
        if (storeType is not "json" and not "jsonb")
        {
            throw new ArgumentException("A structural JSON mapping requires the PostgreSQL json or jsonb type.", nameof(storeType));
        }
    }

    private BlueTuskStructuralJsonTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    public override MethodInfo GetDataReaderMethod()
        => GetStringMethod;

    public override Expression CustomizeDataReaderExpression(Expression expression)
        => Expression.New(
            MemoryStreamConstructor,
            Expression.Call(
                Expression.Property(null, Utf8Property),
                GetBytesMethod,
                expression));

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        if (parameter is BlueTuskParameter blueTuskParameter)
        {
            blueTuskParameter.PostgreSqlTypeOid = StoreType == "json" ? 114u : 3802u;
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value)
        => $"'{((string)value).Replace("'", "''", StringComparison.Ordinal)}'";

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new BlueTuskStructuralJsonTypeMapping(parameters);
}
