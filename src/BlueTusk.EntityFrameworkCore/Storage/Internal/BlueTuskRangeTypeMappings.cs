using System.Data.Common;
using System.Globalization;
using System.Text;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskRangeTypeMapping<T> : RelationalTypeMapping
{
    private readonly uint _postgreSqlTypeOid;

    public BlueTuskRangeTypeMapping(string storeType, uint postgreSqlTypeOid)
        : base(storeType, typeof(BlueTuskRange<T>), System.Data.DbType.Object)
    {
        ArgumentOutOfRangeException.ThrowIfZero(postgreSqlTypeOid);
        _postgreSqlTypeOid = postgreSqlTypeOid;
    }

    private BlueTuskRangeTypeMapping(RelationalTypeMappingParameters parameters, uint postgreSqlTypeOid)
        : base(parameters)
    {
        _postgreSqlTypeOid = postgreSqlTypeOid;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskRangeTypeMapping<T>(parameters, _postgreSqlTypeOid);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        if (parameter is BlueTuskParameter blueTuskParameter)
        {
            blueTuskParameter.PostgreSqlTypeOid = _postgreSqlTypeOid;
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value) =>
        BlueTuskRangeText.GenerateSqlLiteral(BlueTuskRangeText.Format((BlueTuskRange<T>)value), StoreType);
}

internal sealed class BlueTuskMultirangeTypeMapping<T> : RelationalTypeMapping
{
    private readonly uint _postgreSqlTypeOid;

    public BlueTuskMultirangeTypeMapping(string storeType, uint postgreSqlTypeOid)
        : base(storeType, typeof(BlueTuskMultirange<T>), System.Data.DbType.Object)
    {
        ArgumentOutOfRangeException.ThrowIfZero(postgreSqlTypeOid);
        _postgreSqlTypeOid = postgreSqlTypeOid;
    }

    private BlueTuskMultirangeTypeMapping(RelationalTypeMappingParameters parameters, uint postgreSqlTypeOid)
        : base(parameters)
    {
        _postgreSqlTypeOid = postgreSqlTypeOid;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new BlueTuskMultirangeTypeMapping<T>(parameters, _postgreSqlTypeOid);

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
        var multirange = (BlueTuskMultirange<T>)value;
        var text = string.Join(',', multirange.Select(BlueTuskRangeText.Format));
        return BlueTuskRangeText.GenerateSqlLiteral($"{{{text}}}", StoreType);
    }
}

internal static class BlueTuskRangeText
{
    public static string Format<T>(BlueTuskRange<T> range)
    {
        if (range.IsEmpty)
        {
            return "empty";
        }

        var builder = new StringBuilder();
        builder.Append(range.LowerBound.IsInclusive ? '[' : '(');
        if (range.LowerBound.HasValue)
        {
            builder.Append(FormatBound(range.LowerBound.Value));
        }

        builder.Append(',');
        if (range.UpperBound.HasValue)
        {
            builder.Append(FormatBound(range.UpperBound.Value));
        }

        builder.Append(range.UpperBound.IsInclusive ? ']' : ')');
        return builder.ToString();
    }

    public static string GenerateSqlLiteral(string value, string storeType) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'::{storeType}";

    private static string FormatBound<T>(T value)
    {
        var text = value switch
        {
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime timestamp => timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            DateTimeOffset timestamp => timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? string.Empty,
        };

        if (!text.Any(character => character is ',' or '"' or '\\' or '(' or ')' or '[' or ']' || char.IsWhiteSpace(character)))
        {
            return text;
        }

        return $"\"{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
