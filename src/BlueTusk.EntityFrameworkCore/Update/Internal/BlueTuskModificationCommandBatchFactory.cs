using BlueTusk.Data.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskModificationCommandBatchFactory(
    ModificationCommandBatchFactoryDependencies dependencies,
    IDbContextOptions options,
    IInterceptors interceptors) : IModificationCommandBatchFactory
{
    private readonly int _maxBatchSize = RelationalOptionsExtension.Extract(options).MaxBatchSize ?? 42;
    private readonly bool _useCommandCounts = dependencies.UpdateSqlGenerator is BlueTuskUpdateSqlGenerator &&
        interceptors.Aggregate<IDbCommandInterceptor>() is null;

    public ModificationCommandBatch Create()
        => new BlueTuskModificationCommandBatch(dependencies, _maxBatchSize, _useCommandCounts);
}

internal sealed class BlueTuskModificationCommandBatch(
    ModificationCommandBatchFactoryDependencies dependencies, int maxBatchSize, bool useCommandCounts)
    : AffectedCountModificationCommandBatch(dependencies, maxBatchSize)
{
    private static readonly Task<int>[] CompletedIndexes = Enumerable.Range(0, 128)
        .Select(Task.FromResult).ToArray();

    // Reader-replacing/wrapping interceptors must retain their old result shape.
    // A custom SQL generator likewise retains EF's normal row-count protocol.
    protected override IUpdateSqlGenerator UpdateSqlGenerator =>
        !useCommandCounts && base.UpdateSqlGenerator is BlueTuskUpdateSqlGenerator native
            ? native.ReturningRowCountFallback : base.UpdateSqlGenerator;

    // A single unusually wide command remains valid, as required by EF's batch
    // contract. These limits bound aggregation, not the size of one entity.
    protected override bool IsValid()
        => SqlBuilder.Length <= 64 * 1024 && ParameterValues.Count <= short.MaxValue;

    protected override int ConsumeResultSetWithRowsAffectedOnly(int commandIndex, RelationalDataReader reader)
    {
        if (!useCommandCounts || reader.DbDataReader is not IProviderUpdateResult result)
        {
            // Preserve EF's row-based path for interceptor-supplied readers.
            return base.ConsumeResultSetWithRowsAffectedOnly(commandIndex, reader);
        }
        if (reader.Read()) { throw new InvalidOperationException("An affected-count-only write unexpectedly returned a row."); }
        var count = result.CurrentStatementRowsAffected;
        if (count != 1)
        {
            ThrowAggregateUpdateConcurrencyException(reader, commandIndex + 1, 1, count);
        }
        return commandIndex;
    }

    protected override Task<int> ConsumeResultSetWithRowsAffectedOnlyAsync(int commandIndex,
        RelationalDataReader reader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!useCommandCounts || reader.DbDataReader is not IProviderUpdateResult result)
        {
            return base.ConsumeResultSetWithRowsAffectedOnlyAsync(commandIndex, reader, cancellationToken);
        }
        var read = reader.ReadAsync(cancellationToken);
        if (!read.IsCompletedSuccessfully) { return AwaitAffectedCountAsync(commandIndex, reader, result, read, cancellationToken); }
        if (read.Result) { throw new InvalidOperationException("An affected-count-only write unexpectedly returned a row."); }
        return CompleteAffectedCountAsync(commandIndex, reader, result, cancellationToken);
    }

    private Task<int> CompleteAffectedCountAsync(int commandIndex, RelationalDataReader reader,
        IProviderUpdateResult result, CancellationToken cancellationToken)
    {
        var count = result.CurrentStatementRowsAffected;
        return count == 1
            ? commandIndex < CompletedIndexes.Length ? CompletedIndexes[commandIndex] : Task.FromResult(commandIndex)
            : ReportConcurrencyFailureAsync(commandIndex, reader, count, cancellationToken);
    }

    private async Task<int> AwaitAffectedCountAsync(int commandIndex, RelationalDataReader reader,
        IProviderUpdateResult result, Task<bool> read, CancellationToken cancellationToken)
    {
        if (await read.ConfigureAwait(false))
        {
            throw new InvalidOperationException("An affected-count-only write unexpectedly returned a row.");
        }
        return await CompleteAffectedCountAsync(commandIndex, reader, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ReportConcurrencyFailureAsync(int commandIndex, RelationalDataReader reader,
        int count, CancellationToken cancellationToken)
    {
        await ThrowAggregateUpdateConcurrencyExceptionAsync(reader, commandIndex + 1, 1, count, cancellationToken)
            .ConfigureAwait(false);
        return commandIndex;
    }
}
