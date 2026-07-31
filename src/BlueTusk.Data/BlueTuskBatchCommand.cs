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

    public override bool CanCreateParameter => true;

    protected override DbParameterCollection DbParameterCollection => _parameters;

    public new BlueTuskParameterCollection Parameters => _parameters;

    public override DbParameter CreateParameter() => new BlueTuskParameter();

    internal void SetRecordsAffected(int value) => _recordsAffected = value;
}
