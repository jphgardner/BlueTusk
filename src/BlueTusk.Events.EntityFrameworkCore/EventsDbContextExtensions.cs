using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.Events.EntityFrameworkCore;

public static class EventsDbContextExtensions
{
    /// <summary>
    /// Append events in the context's already active relational transaction. This method does not save
    /// tracked changes or commit. Begin a transaction, save business changes, append, then commit together.
    /// When execution strategies retry a transaction, reuse stable event IDs and timestamps.
    /// </summary>
    public static ValueTask<IReadOnlyList<EventAppendReceipt>> AppendEventsAsync(this DbContext context,
        PostgreSqlEventStore store, EventStreamKey stream, IReadOnlyList<EventWrite> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var transaction = context.Database.CurrentTransaction ??
            throw new InvalidOperationException("Begin an explicit EF Core relational transaction before appending events.");
        return store.AppendAsync(context.Database.GetDbConnection(), transaction.GetDbTransaction(), stream, events, cancellationToken);
    }
}
