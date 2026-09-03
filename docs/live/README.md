# Push live updates to applications

BlueTusk Live keeps a bounded query result current for authenticated clients.
The server owns and authorizes the query; browsers send a registered query name
and typed parameters, not arbitrary SQL.

Use Live for dashboards, order tracking, operations screens, and collaborative
views. Use [Sync](../sync/README.md) when the destination is another data
system rather than a connected user.

## How Live works

1. Trusted server code registers a bounded EF query.
2. The first request runs that query under the caller's security scope.
3. Streams records relevant committed table changes.
4. Live reruns the authorized query and emits keyed add, update, remove,
   reorder, or reset events.
5. The client applies events locally and reconnects with a signed resume token.

PostgreSQL and EF remain authoritative. CDC data does not bypass row-level
security or application authorization.

## 1. Register a bounded query

The query must have deterministic ordering, include its key in that ordering,
and end with a bounded `Take`:

```csharp
var definition = new LiveEfQueryDefinition<OrdersContext, Order, long>(
    name: "recent-orders",
    databaseIdentity: "orders-primary",
    version: "v1",
    parameters: [new LiveQueryParameter("tenant", typeof(string))],
    validationArguments: new Dictionary<string, object?>
    {
        ["tenant"] = "example-tenant",
    },
    maximumResultCount: 100,
    queryFactory: (db, arguments) =>
    {
        var tenant = arguments.Get<string>("tenant")!;
        return db.Orders
            .Where(order => order.TenantId == tenant)
            .OrderByDescending(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .Take(100);
    },
    keySelector: order => order.Id,
    rowComparer: EqualityComparer<Order>.Default,
    tenantIsolationMode: LiveEfTenantIsolationMode.RegisteredPredicate,
    tenantBinding: new LiveEfTenantBinding(nameof(Order.TenantId), "tenant"));

var plan = await LiveEfQueryCompiler.CompileAsync(
    contextFactory,
    definition,
    cancellationToken);

queryRegistry.Register(plan);
```

Compilation happens at startup, so unsupported or unbounded query shapes fail
before clients connect.

## 2. Expose one transport

Server-sent events are the simplest browser transport:

```csharp
builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthorization();
builder.Services.AddSingleton<ILiveTransportSubscriptionResolver, AppLiveResolver>();
builder.Services.AddBlueTuskLiveAspNetCore(resumeTokenProtector);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapBlueTuskLiveServerSentEvents();
```

`AppLiveResolver` maps the authenticated caller and request to a registered
plan, validates parameters, and creates the `LiveSecurityScope`. SignalR and
gRPC expose the same delivery contract when those transports are a better fit.

## 3. Connect a browser

```typescript
const query = new BlueTuskLiveClient({
  endpoint: "/bluetusk/live/sse",
}).createQuery<Order, string>({
  query: "recent-orders",
  parameters: { tenant: "acme" },
});

const unsubscribe = query.subscribe((state) => render(state.rows));
query.start();

// When the owning view is destroyed:
unsubscribe();
query.stop();
```

Use `@bluetusk/live-angular`, `@bluetusk/live-react`,
`@bluetusk/live-vue`, or `@bluetusk/live-svelte` for framework lifecycle and
batched state updates. The framework-neutral client owns protocol validation,
reconnect, replay, and resume tokens.

## Security checklist

- Authenticate before resolving a subscription.
- Bind tenant/user scope and policy version into `LiveSecurityScope`.
- Enforce RLS, a global query filter, or a compiler-verified tenant predicate.
- Set a hard result limit and deterministic ordering.
- Keep parameter values and result rows out of logs and control-plane metadata.
- Bound each subscriber queue and choose an explicit slow-client policy.

The [full Live reference](reference.md) covers projections, client-query
capabilities, resume-token rotation, shared subscriptions, transports,
framework adapters, quotas, backpressure, and load gates.
