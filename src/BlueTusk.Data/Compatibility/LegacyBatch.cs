#if !NET6_0_OR_GREATER
global using DbBatch = BlueTusk.Data.Compatibility.BlueTuskLegacyBatch;
global using DbBatchCommand = BlueTusk.Data.Compatibility.BlueTuskLegacyBatchCommand;
global using DbBatchCommandCollection = BlueTusk.Data.Compatibility.BlueTuskLegacyBatchCommandCollection;

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlueTusk.Data.Compatibility;

/// <summary>Provides native BlueTusk batching on runtimes predating the ADO.NET batch abstractions.</summary>
public abstract class BlueTuskLegacyBatch : IDisposable, IAsyncDisposable
{
    public BlueTuskLegacyBatchCommandCollection BatchCommands => DbBatchCommands;
    protected abstract BlueTuskLegacyBatchCommandCollection DbBatchCommands { get; }
    public DbConnection? Connection { get => DbConnection; set => DbConnection = value; }
    protected abstract DbConnection? DbConnection { get; set; }
    public DbTransaction? Transaction { get => DbTransaction; set => DbTransaction = value; }
    protected abstract DbTransaction? DbTransaction { get; set; }
    public abstract int Timeout { get; set; }
    public abstract void Cancel();
    public abstract int ExecuteNonQuery();
    public abstract Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default);
    public abstract object? ExecuteScalar();
    public abstract Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default);
    public abstract void Prepare();
    public abstract Task PrepareAsync(CancellationToken cancellationToken = default);
    public BlueTuskLegacyBatchCommand CreateBatchCommand() => CreateDbBatchCommand();
    protected abstract BlueTuskLegacyBatchCommand CreateDbBatchCommand();
    public DbDataReader ExecuteReader(CommandBehavior behavior = CommandBehavior.Default) => ExecuteDbDataReader(behavior);
    public Task<DbDataReader> ExecuteReaderAsync() => ExecuteDbDataReaderAsync(CommandBehavior.Default, CancellationToken.None);
    public Task<DbDataReader> ExecuteReaderAsync(CancellationToken cancellationToken) => ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken);
    public Task<DbDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken = default) => ExecuteDbDataReaderAsync(behavior, cancellationToken);
    protected abstract DbDataReader ExecuteDbDataReader(CommandBehavior behavior);
    protected abstract Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken);
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        GC.SuppressFinalize(this);
        return default;
    }
}

/// <summary>Provides batch-command operations on .NET 5.</summary>
public abstract class BlueTuskLegacyBatchCommand
{
    [AllowNull]
    public abstract string CommandText { get; set; }
    public abstract CommandType CommandType { get; set; }
    public abstract int RecordsAffected { get; }
    public virtual bool CanCreateParameter => false;
    public DbParameterCollection Parameters => DbParameterCollection;
    protected abstract DbParameterCollection DbParameterCollection { get; }
    public abstract DbParameter CreateParameter();
}

/// <summary>Provides the batch-command collection contract on .NET 5.</summary>
public abstract class BlueTuskLegacyBatchCommandCollection : IList<BlueTuskLegacyBatchCommand>, IReadOnlyList<BlueTuskLegacyBatchCommand>
{
    public abstract int Count { get; }
    public abstract bool IsReadOnly { get; }
    public BlueTuskLegacyBatchCommand this[int index] { get => GetBatchCommand(index); set => SetBatchCommand(index, value); }
    protected abstract BlueTuskLegacyBatchCommand GetBatchCommand(int index);
    protected abstract void SetBatchCommand(int index, BlueTuskLegacyBatchCommand batchCommand);
    public abstract void Add(BlueTuskLegacyBatchCommand item);
    public abstract void Clear();
    public abstract bool Contains(BlueTuskLegacyBatchCommand item);
    public abstract void CopyTo(BlueTuskLegacyBatchCommand[] array, int arrayIndex);
    public abstract IEnumerator<BlueTuskLegacyBatchCommand> GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public abstract int IndexOf(BlueTuskLegacyBatchCommand item);
    public abstract void Insert(int index, BlueTuskLegacyBatchCommand item);
    public abstract bool Remove(BlueTuskLegacyBatchCommand item);
    public abstract void RemoveAt(int index);
}
#endif
