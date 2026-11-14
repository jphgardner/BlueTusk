using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlueTusk.Data;

/// <summary>Represents one PostgreSQL statement in a <see cref="BlueTuskBatch"/>.</summary>
public sealed class BlueTuskBatchCommand : DbBatchCommand
{
    private readonly BlueTuskParameterCollection _parameters = new();
    private string _commandText = string.Empty;
    private int _recordsAffected = -1;

    public BlueTuskBatchCommand()
    {
    }

    public BlueTuskBatchCommand(string commandText)
    {
        CommandText = commandText;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new NotSupportedException("BlueTusk batch commands support text commands only.");
            }
        }
    }

    public override int RecordsAffected => _recordsAffected;

#if NET8_0_OR_GREATER || !NET6_0_OR_GREATER
    public override bool CanCreateParameter => true;
#else
    [SuppressMessage("Performance", "CA1822", Justification = "Retain the instance API of DbBatchCommand on runtimes predating these BCL members.")]
    public bool CanCreateParameter => true;
#endif

    protected override DbParameterCollection DbParameterCollection => _parameters;

    public new BlueTuskParameterCollection Parameters => _parameters;

#if NET8_0_OR_GREATER || !NET6_0_OR_GREATER
    public override DbParameter CreateParameter() => new BlueTuskParameter();
#else
    [SuppressMessage("Performance", "CA1822", Justification = "Retain the instance API of DbBatchCommand on runtimes predating these BCL members.")]
    public DbParameter CreateParameter() => new BlueTuskParameter();
#endif

    internal void SetRecordsAffected(int value) => _recordsAffected = value;
}
