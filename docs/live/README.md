# BlueTusk Live

BlueTusk Live keeps query results on a user's screen up to date as PostgreSQL
data changes. You register a query on the server; a browser subscribes to it by
name and receives the rows, then only the changes.

## When to use Live

Use Live when a connected user should see current data without refreshing:
dashboards, order tracking, operations screens, shared lists.

- The server owns every query. Browsers send a registered query name and
  typed parameters, never SQL.
- Each subscriber gets the rows that their own authorization allows. Raw change
  data never reaches a browser; Live runs the query again and sends the
  difference.
- Many users with the same query and scope share one query execution.
- A dropped connection resumes from where it stopped.

Use something else when:

- the destination is another system, such as a search index or a warehouse:
  use [Sync](../sync/README.md);
- your own code must react to each committed change: use
  [Streams](../streams/README.md);
- a result is too large to keep in a browser. Every live query has a hard row
  limit.

## How it works

```text
PostgreSQL ──► Streams ──► invalidation log ──► re-run the query ──► diff ──► browser
              (which tables changed)          (with the user's scope)    (keyed events)
```

[Concepts](concepts.md) explains each step.

## Packages

| Package | Use it for |
| --- | --- |
| `BlueTusk.Live` | Query plans, subscriptions, diffs, replay, resume tokens. |
| `BlueTusk.Live.EntityFrameworkCore` | Register live queries from EF Core LINQ. |
| `BlueTusk.Live.DependencyInjection` | PostgreSQL invalidation and replay storage, and the Streams consumer that feeds it. |
| `BlueTusk.Live.AspNetCore` | The authenticated request and resolver contract shared by all transports. |
| `BlueTusk.Live.ServerSentEvents` | Server-sent events endpoint for browsers. |
| `BlueTusk.Live.SignalR` | SignalR streaming hub. |
| `BlueTusk.Live.Grpc` | gRPC streaming service and its .NET client. |
| `BlueTusk.Live.Aspire` | Pass Live settings from an Aspire AppHost. |
| `BlueTusk.Live.Testing` | In-memory invalidation log and replay store for tests, and a conformance kit for custom replay stores. |
| `@bluetusk/live` (npm) | Framework-neutral browser client. |
| `@bluetusk/live-angular`, `@bluetusk/live-react` (npm) | Angular signals and React hooks. |
| `@bluetusk/live-vue`, `@bluetusk/live-svelte` (npm) | New in 1.1.0. Vue composables and Svelte stores. |

Install with `dotnet add package BlueTusk.Live.EntityFrameworkCore` (and the
others you need) and `npm install @bluetusk/live`. See
[Install BlueTusk](../getting-started/install.md) to choose and pin a version.

**Status:** Live is part of the BlueTusk 1.1.0 core release, with the same
version as Provider, Streams, Sync and Control Plane. 1.1.0 is not published
yet; the published versions are 1.0.0 (stable) and 1.1.0-rc.1 (release
candidate). It supports .NET 10 and PostgreSQL 15, 16, 17 and 18.

## A taste of the code

The server registers a query that is limited to 100 rows, ordered, and scoped
to a user:

```csharp
new LiveEfQueryDefinition<TodoContext, Todo, long>(
    name: "my-todos",
    databaseIdentity: "app",
    version: "v1",
    parameters: [new LiveQueryParameter("owner", typeof(string))],
    validationArguments: new Dictionary<string, object?> { ["owner"] = "example" },
    maximumResultCount: 100,
    queryFactory: (db, arguments) =>
    {
        var owner = arguments.Get<string>("owner")!;
        return db.Todos
            .Where(todo => todo.Owner == owner)
            .OrderBy(todo => todo.Id)
            .Take(100);
    },
    keySelector: todo => todo.Id,
    rowComparer: EqualityComparer<Todo>.Default,
    tenantIsolationMode: LiveEfTenantIsolationMode.RegisteredPredicate,
    tenantBinding: new LiveEfTenantBinding(nameof(Todo.Owner), "owner"))
```

Your resolver binds `owner` from the signed-in user, and the browser
subscribes by name:

```typescript
const query = client.createQuery<Todo, number, object>({
  query: "my-todos",
  parameters: {}
});

query.subscribe((state) => render(state.rows));
query.start();
```

The [quick start](quickstart.md) puts these together into a running app.

## Framework guides

`@bluetusk/live` works in any page. For component lifecycles, use the adapter
for your framework: see [Angular, React, Vue and Svelte](clients.md).

## Security checklist

- Authenticate before the Live endpoint. Anonymous requests are refused.
- In your resolver, take tenant and user values from claims, not from request
  parameters.
- Put the tenant or user in the `LiveSecurityScope`, and change its policy
  version when your authorization rules change.
- Enforce tenant isolation in the plan: a registered predicate, an EF global
  query filter, or PostgreSQL row-level security.
- Keep a hard row limit and an order that includes the key.
- Load resume-token signing keys from a secret store, and use the same keys on
  every instance.
- Keep parameter values and rows out of your logs. Live's own telemetry never
  records them.

## Next steps

- [Quick start](quickstart.md): a browser list that updates itself.
- [Concepts](concepts.md): queries, scope, re-query and diff, resume tokens,
  limits and transports.
- [Framework guides](clients.md): Angular, React, Vue and Svelte.
- [Configuration](configuration.md): every option and default.
- [Troubleshooting](troubleshooting.md): no updates, errors and reconnects.
- [Delivery guarantees](../realtime-platform/contracts.md#live) across Streams,
  Sync and Live.
- [Full engineering reference](reference.md), the
  [1.0.0 release notes](release-notes-1.0.0.md) and the
  [public API policy](api-compatibility.md).
