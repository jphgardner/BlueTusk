using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlueTusk.Data;

public class BlueTuskParameter : DbParameter
{
    private object? _value;

    public BlueTuskParameter()
    {
    }

    public BlueTuskParameter(object? value)
    {
        Value = value;
    }

    public override DbType DbType { get; set; } = DbType.Object;

    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
            {
                throw new NotSupportedException("Only input parameters are supported by PostgreSQL commands.");
            }
        }
    }

    public override bool IsNullable { get; set; }

    /// <summary>
    /// Gets or sets the PostgreSQL type OID to send during Parse. When unset, BlueTusk infers a built-in OID
    /// from <see cref="DbType"/> or <see cref="Value"/>.
    /// </summary>
    public uint? PostgreSqlTypeOid { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override object? Value
    {
        get => _value;
        set => _value = value;
    }

    public override bool SourceColumnNullMapping { get; set; }

    public override int Size { get; set; }

    public override void ResetDbType()
    {
        DbType = DbType.Object;
        PostgreSqlTypeOid = null;
    }
}

public sealed class BlueTuskParameter<T> : BlueTuskParameter
{
    public BlueTuskParameter()
    {
    }

    public BlueTuskParameter(T? value)
        : base(value)
    {
    }

    public T? TypedValue
    {
        get => Value is null or DBNull ? default : (T)Value;
        set => Value = value;
    }
}
