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
/// connection in <see cref="AuthorizeAsync"/> before returning. BlueTusk owns the lease.
/// </remarks>
public sealed class ContinuousGraphScopedConnection
{
    public ContinuousGraphScopedConnection(
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<BlueTuskDataSource>> resolveDataSourceAsync,
        Func<BlueTuskConnection, LiveQueryExecutionContext, CancellationToken, ValueTask>? authorizeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(resolveDataSourceAsync);
        ResolveDataSourceAsync = resolveDataSourceAsync;
        AuthorizeAsync = authorizeAsync;
    }

    /// <summary>Resolves the subscriber-authorized data source for the registered database on each read.</summary>
    public Func<LiveQueryExecutionContext, CancellationToken, ValueTask<BlueTuskDataSource>> ResolveDataSourceAsync { get; }

    /// <summary>Applies any additional subscriber authorization on the open connection before the query runs.</summary>
    public Func<BlueTuskConnection, LiveQueryExecutionContext, CancellationToken, ValueTask>? AuthorizeAsync { get; }
}
