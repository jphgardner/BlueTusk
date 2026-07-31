using System.Data.Common;

namespace BlueTusk.Data;

/// <summary>A mutable collection of BlueTusk batch commands.</summary>
public sealed class BlueTuskBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<BlueTuskBatchCommand> _commands = [];

    public override int Count => _commands.Count;

    public override bool IsReadOnly => false;

    public BlueTuskBatchCommand Add(string commandText)
    {
        var command = new BlueTuskBatchCommand(commandText);
        Add(command);
        return command;
    }

    public void Add(BlueTuskBatchCommand command) => Add((DbBatchCommand)command);

    public override void Add(DbBatchCommand item) => _commands.Add(RequireCommand(item));

    public override void Clear() => _commands.Clear();

    public override bool Contains(DbBatchCommand item) =>
        item is BlueTuskBatchCommand command && _commands.Contains(command);

    public override void CopyTo(DbBatchCommand[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var index = 0; index < _commands.Count; index++)
        {
            array[arrayIndex + index] = _commands[index];
        }
    }

    public override IEnumerator<DbBatchCommand> GetEnumerator() => _commands.Cast<DbBatchCommand>().GetEnumerator();

    public override int IndexOf(DbBatchCommand item) =>
        item is BlueTuskBatchCommand command ? _commands.IndexOf(command) : -1;

    public override void Insert(int index, DbBatchCommand item) =>
        _commands.Insert(index, RequireCommand(item));

    public override bool Remove(DbBatchCommand item) =>
        item is BlueTuskBatchCommand command && _commands.Remove(command);

    public override void RemoveAt(int index) => _commands.RemoveAt(index);

    protected override DbBatchCommand GetBatchCommand(int index) => _commands[index];

    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand) =>
        _commands[index] = RequireCommand(batchCommand);

    internal IReadOnlyList<BlueTuskBatchCommand> Items => _commands;

    private static BlueTuskBatchCommand RequireCommand(DbBatchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command as BlueTuskBatchCommand ??
            throw new ArgumentException(
                "Only BlueTuskBatchCommand instances can be added.",
                nameof(command));
    }
}
