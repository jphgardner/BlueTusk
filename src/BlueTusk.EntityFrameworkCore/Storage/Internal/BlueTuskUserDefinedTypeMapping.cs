using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
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
        var text = value is Enum enumValue
            ? GetEnumLabel(enumValue)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return $"'{text.Replace("'", "''", StringComparison.Ordinal)}'::{StoreType}";
    }

    // Uses the same default label rules as BlueTuskEnumCodec<TEnum>, which MapEnum registers:
    // [BlueTuskName], then [EnumMember], then the CLR member name.
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "BlueTuskEnumCodec<TEnum> roots the public fields of every mapped enum.")]
    private static string GetEnumLabel(Enum value)
    {
        var enumType = value.GetType();
        var name = Enum.GetName(enumType, value);
        if (name is null)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var member = enumType.GetField(name, BindingFlags.Public | BindingFlags.Static)!;
        return member.GetCustomAttribute<BlueTuskNameAttribute>()?.Name
            ?? member.GetCustomAttribute<EnumMemberAttribute>()?.Value
            ?? name;
    }
}
