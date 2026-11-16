using BlueTusk.Data;
using BlueTusk.Live;

namespace BlueTusk.ContinuousGraph;

/// <summary>
/// Authorizes an eligible single-key graph read without creating an EF context for every change.
/// </summary>
/// <remarks>
/// Use this option when the registered graph query and its bound filter values are invariant
/// for the subscription. Resolve the same database and enforce the same subscriber access
/// as the EF registration on every invocation, including policy changes and revocation.
/// If authorization uses a PostgreSQL role or session settings, apply them to the supplied
/// connection in <see cref="AuthorizeAsync"/> or acquire an already authorized lease.
/// </remarks>
public sealed class ContinuousGraphScopedConnection
{
    public ContinuousGraphScopedConnection(
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<BlueTuskDataSource>> resolveDataSourceAsync,
        Func<BlueTuskConnection, LiveQueryExecutionContext, CancellationToken, ValueTask>? authorizeAsync = null)
    {
        BlueTuskGuard.NotNull(resolveDataSourceAsync);
        ResolveDataSourceAsync = resolveDataSourceAsync;
        AuthorizeAsync = authorizeAsync;
    }

    /// <summary>Uses an application-owned lease that is authorized anew on every read.</summary>
    public ContinuousGraphScopedConnection(
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<IContinuousGraphScopedConnectionLease>> acquireAuthorizedLeaseAsync)
    {
        BlueTuskGuard.NotNull(acquireAuthorizedLeaseAsync);
        AcquireAuthorizedLeaseAsync = acquireAuthorizedLeaseAsync;
    }

    /// <summary>Resolves the subscriber-authorized data source for the registered database on each read.</summary>
    public Func<LiveQueryExecutionContext, CancellationToken, ValueTask<BlueTuskDataSource>>? ResolveDataSourceAsync { get; }

    /// <summary>Applies any additional subscriber authorization on the open connection before the query runs.</summary>
    public Func<BlueTuskConnection, LiveQueryExecutionContext, CancellationToken, ValueTask>? AuthorizeAsync { get; }

    internal Func<LiveQueryExecutionContext, CancellationToken, ValueTask<IContinuousGraphScopedConnectionLease>>?
        AcquireAuthorizedLeaseAsync
    { get; }
}

/// <summary>An exclusive lease for a subscriber-authorized graph read.</summary>
/// <remarks>
/// Supply an already open connection to the registered database, with the subscriber's role
/// and session settings in place and no active transaction. Keep it exclusive until disposal.
/// The application owns authorization, isolation, and return or disposal of the connection.
/// </remarks>
public interface IContinuousGraphScopedConnectionLease : IAsyncDisposable
{
    BlueTuskConnection Connection { get; }
}
