using System.Collections.Frozen;
using System.Data.Common;

namespace BlueTusk.Events;

public delegate ValueTask TypedEventTransactionHandler<T>(T body, StoredEvent envelope, DbConnection connection,
    DbTransaction transaction, CancellationToken cancellationToken);

/// <summary>
/// Registers exact type/version contracts without runtime reflection. Register historical contracts with
/// typed upgrade handlers to evolve wire formats; the application explicitly owns upgrade semantics.
/// </summary>
public sealed class EventRouterBuilder
{
    private readonly Dictionary<(string Name, int Version), EventTransactionHandler> _handlers = [];

    public EventRouterBuilder Register<T>(EventContract<T> contract, TypedEventTransactionHandler<T> handler)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(handler);
        if (_handlers.Count >= 4096)
        {
            throw new InvalidOperationException("An event router supports at most 4096 registered wire contracts.");
        }

        if (!_handlers.TryAdd((contract.Name, contract.Version), (value, connection, transaction, cancellationToken) =>
        {
            var body = contract.Deserialize(value);
            if (body is null)
            {
                throw new InvalidOperationException($"Contract {contract.Name} v{contract.Version} deserialized a null event body.");
            }

            return handler(body, value, connection, transaction, cancellationToken);
        }))
        {
            throw new InvalidOperationException($"Event contract {contract.Name} v{contract.Version} is already registered.");
        }

        return this;
    }

    public EventRouter Build() => new(_handlers.ToFrozenDictionary());
}

/// <summary>Immutable concurrent router. Unknown wire versions fail the transaction instead of losing an event.</summary>
public sealed class EventRouter
{
    private readonly FrozenDictionary<(string Name, int Version), EventTransactionHandler> _handlers;

    internal EventRouter(FrozenDictionary<(string Name, int Version), EventTransactionHandler> handlers) => _handlers = handlers;

    public ValueTask HandleAsync(StoredEvent value, DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        EventValidation.Transaction(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        return _handlers.TryGetValue((value.EventType, value.Version), out var handler)
            ? handler(value, connection, transaction, cancellationToken)
            : throw new InvalidOperationException($"No handler is registered for event contract {value.EventType} v{value.Version}.");
    }
}
