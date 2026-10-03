# Live quick start: a browser list that updates itself

In this quick start you build an ASP.NET Core app that keeps a browser list of
to-do items current. When you insert, update or delete a row in PostgreSQL, the
page changes within a moment, and each user sees only their own rows. It takes
about 15 minutes.

You will:

1. create a table and a publication;
2. register one live query and map the server-sent events (SSE) endpoint;
3. show the result in a small page that uses `@bluetusk/live`;
4. change rows in PostgreSQL and watch the page update.

## Before you start

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- [Node.js](https://nodejs.org/) 20 or later, for `npm` and `npx`;
- a PostgreSQL 15, 16, 17 or 18 server with `wal_level=logical`, and a role
  that can create replication slots. The Docker container from the
  [5-minute first app](../getting-started/quickstart.md#1-start-postgresql)
  has both.

## 1. Create the table and publication

Create a database for this quick start, then the table and a publication that
lists it:

```powershell
docker exec bluetusk-postgres psql -U postgres -c "CREATE DATABASE live_quickstart"
docker exec -it bluetusk-postgres psql -U postgres -d live_quickstart
```

At the `live_quickstart=#` prompt, run:

```sql
CREATE TABLE public.todos (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    owner text NOT NULL,
    title text NOT NULL
);

CREATE PUBLICATION live_todos FOR TABLE public.todos;
```

Leave this `psql` session open. You use it in step 6.

## 2. Create the project

In a second terminal:

```powershell
dotnet new web --framework net10.0 --name LiveQuickstart
cd LiveQuickstart
dotnet add package BlueTusk.Live.EntityFrameworkCore
dotnet add package BlueTusk.Live.DependencyInjection
dotnet add package BlueTusk.Live.ServerSentEvents
dotnet add package BlueTusk.Streams.DependencyInjection
npm init -y
npm install @bluetusk/live esbuild
```

See [Install BlueTusk](../getting-started/install.md) to choose and pin a
version.

## 3. Write the server

Replace the contents of `Program.cs`:

```csharp
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Live.EntityFrameworkCore;
using BlueTusk.Live.ServerSentEvents;
using BlueTusk.Replication;
using BlueTusk.Streams;
using BlueTusk.Streams.DependencyInjection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("App")
    ?? throw new InvalidOperationException("Set ConnectionStrings__App.");
var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
builder.Services.AddSingleton(dataSource);
builder.Services.AddDbContextFactory<TodoContext>(options => options.UseBlueTusk(dataSource));

// Live storage: the invalidation log and the replay window, in schema bluetusk_live.
var store = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
{
    ControlDataSource = dataSource,
    ControlSchema = "bluetusk_live",
});
builder.Services.AddSingleton<ILiveInvalidationLog>(store);
builder.Services.AddSingleton<ILiveReplayStore>(store);
builder.Services.AddSingleton(new LiveQueryRegistry());
builder.Services.AddSingleton<TodoSubscriptions>();
builder.Services.AddSingleton<ILiveTransportSubscriptionResolver>(
    services => services.GetRequiredService<TodoSubscriptions>());
builder.Services.AddHostedService<LiveRefreshWorker>();

// Resume tokens are signed with a secret of at least 32 bytes.
var resumeKey = Convert.FromBase64String(builder.Configuration["Live:ResumeKey"]
    ?? throw new InvalidOperationException("Set Live__ResumeKey."));
builder.Services.AddBlueTuskLiveAspNetCore(new LiveResumeTokenProtector(
    [new LiveResumeTokenKey("local", resumeKey, isPrimary: true)]));

// Streams: every committed change to public.todos becomes a Live invalidation.
ChangeSourceIdentity source;
await using (var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
    dataSource.CreateDedicatedSessionOptions()))
{
    var server = await replication.IdentifySystemAsync();
    source = new ChangeSourceIdentity(
        server.SystemIdentifier, server.DatabaseName!, "live_todos", "live_todos");
}

var todosTable = new ChangeTable(0, "public", "todos", 'd',
    [new ChangeColumn(0, "id", 20, -1, IsKey: true)]);
builder.Services.AddSingleton(new LiveInvalidationConsumer("app", store));
builder.Services.AddBlueTuskStreams().AddHostedConsumer<LiveInvalidationConsumer>(
    "live-invalidations",
    _ => new PostgreSqlConsistentSnapshotSource(dataSource, new PostgreSqlConsistentSnapshotOptions
    {
        Source = source,
        PublicationNames = ["live_todos"],
        Tables = [new PostgreSqlSnapshotTable(todosTable, [0])],
        ExistingSlotMode = PostgreSqlExistingSnapshotSlotMode.RestartSnapshot,
    }));

var app = builder.Build();
await store.InitializeAsync();

// Register the live query. Compilation rejects unsupported shapes, such as a missing Take.
var plan = await LiveEfQueryCompiler.CompileAsync(
    app.Services.GetRequiredService<IDbContextFactory<TodoContext>>(),
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
        tenantBinding: new LiveEfTenantBinding(nameof(Todo.Owner), "owner")));
app.Services.GetRequiredService<LiveQueryRegistry>().Register(plan);

// Development only: trust an X-Demo-User header. Use real authentication in production.
app.Use((context, next) =>
{
    if (context.Request.Headers["X-Demo-User"] is [{ Length: > 0 } user])
    {
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "demo"));
    }

    return next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapBlueTuskLiveServerSentEvents();
app.Run();

// A record compares by value, so unchanged rows are not resent.
public sealed record Todo
{
    public long Id { get; set; }
    public string Owner { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class TodoContext(DbContextOptions<TodoContext> options) : DbContext(options)
{
    public DbSet<Todo> Todos => Set<Todo>();

    protected override void OnModelCreating(ModelBuilder model) =>
        model.Entity<Todo>(todo =>
        {
            todo.ToTable("todos", "public");
            todo.Property(t => t.Id).HasColumnName("id");
            todo.Property(t => t.Owner).HasColumnName("owner");
            todo.Property(t => t.Title).HasColumnName("title");
        });
}

// Turns a request into a running shared subscription for the signed-in user.
public sealed class TodoSubscriptions(
    LiveQueryRegistry queries,
    ILiveInvalidationLog invalidations,
    ILiveReplayStore replay) : ILiveTransportSubscriptionResolver
{
    private readonly ConcurrentDictionary<string, LiveSharedSubscription<Todo, long>> _running = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<ILiveSharedSubscription> ResolveAsync(
        string query,
        JsonElement parameters,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        if (query != "my-todos")
        {
            throw new LiveTransportRequestException($"Unknown live query '{query}'.");
        }

        // The owner comes from the authenticated user, never from the browser.
        var owner = principal.Identity?.Name
            ?? throw new LiveTransportAuthorizationException("A user name is required.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_running.TryGetValue(owner, out var existing))
            {
                return existing;
            }

            var plan = queries.Get<Todo, long>("my-todos");
            var session = new LiveQuerySession<Todo, long>(
                plan,
                plan.Bind(new Dictionary<string, object?> { ["owner"] = owner }),
                new LiveSecurityScope($"user:{owner}", "todos-policy-v1"),
                invalidations);
            var subscription = new LiveSharedSubscription<Todo, long>(session, replay);
            try
            {
                await subscription.StartAsync(cancellationToken);
            }
            catch
            {
                await subscription.DisposeAsync();
                throw;
            }

            _running[owner] = subscription;
            return subscription;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _running.Values)
        {
            await subscription.RefreshAsync(cancellationToken);
        }
    }
}

// Re-runs a subscription's query only when its tables have new invalidations.
public sealed class LiveRefreshWorker(TodoSubscriptions subscriptions, ILogger<LiveRefreshWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await subscriptions.RefreshAllAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Live refresh failed; it will be retried.");
            }
        }
    }
}
```

What the pieces do:

- **The live query** `my-todos` is compiled at startup. It filters by the
  `owner` parameter, has a deterministic order that includes the key, and a
  hard limit of 100 rows.
- **`TodoSubscriptions`** is the authorization point. It ignores any owner the
  browser might send and binds the signed-in user's name instead.
- **Streams** reads committed changes from the `live_todos` publication and
  records which tables changed. **`LiveRefreshWorker`** re-runs a query only
  when one of its tables changed, then sends the difference.

[Concepts](concepts.md) explains each step.

## 4. Write the page

Create `app.ts` next to `Program.cs`:

```typescript
import { BlueTuskLiveClient } from "@bluetusk/live";

interface Todo {
  Id: number;
  Owner: string;
  Title: string;
}

const client = new BlueTuskLiveClient({
  endpoint: "/bluetusk/live/sse",
  headers: { "X-Demo-User": "alice" }
});

const query = client.createQuery<Todo, number, object>({
  query: "my-todos",
  parameters: {}
});

const status = document.querySelector("#status")!;
const list = document.querySelector("#todos")!;

query.subscribe((state) => {
  status.textContent = state.error ? `${state.phase}: ${state.error.message}` : state.phase;
  list.replaceChildren(
    ...state.rows.map((todo) => {
      const item = document.createElement("li");
      item.textContent = `${todo.Id}: ${todo.Title}`;
      return item;
    })
  );
});

query.start();
```

> **Note:** With the published 1.0.0 or 1.1.0-rc.1 client, add
> `fetch: (input, init) => fetch(input, init)` to the options, or the page shows
> `Illegal invocation`. See [troubleshooting](troubleshooting.md#the-page-says-illegal-invocation).

Rows arrive with the C# property names (`Id`, `Title`), because the server
serializes them with default `System.Text.Json` settings.

Create `wwwroot/index.html`:

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <title>Live todos</title>
  </head>
  <body>
    <h1>Alice's todos</h1>
    <p>Connection: <span id="status">idle</span></p>
    <ul id="todos"></ul>
    <script type="module" src="app.js"></script>
  </body>
</html>
```

Bundle the script into `wwwroot/app.js`:

```powershell
npx esbuild app.ts --bundle --format=esm --outfile=wwwroot/app.js
```

## 5. Run the app

Set the connection string and a resume-token signing key, then start the app:

```powershell
$env:ConnectionStrings__App = "Host=localhost;Port=5432;Username=postgres;Password=local-dev-only;Database=live_quickstart;SSL Mode=Disable;Channel Binding=Disable"
$env:Live__ResumeKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --urls http://localhost:5080
```

On Linux or macOS, use `export ConnectionStrings__App="..."` and
`export Live__ResumeKey=$(openssl rand -base64 32)`.

> **Warning:** `SSL Mode=Disable` is only for a local test container. Keep the
> default `SSL Mode=VerifyFull` everywhere else.

Open <http://localhost:5080>. The page shows `Connection: live` and an empty
list.

## 6. Change the table

In the `psql` session from step 1:

```sql
INSERT INTO public.todos (owner, title) VALUES ('alice', 'Write the docs'), ('bob', 'Not for alice');
UPDATE public.todos SET title = 'Write the Live docs' WHERE id = 1;
INSERT INTO public.todos (owner, title) VALUES ('alice', 'Ship it');
DELETE FROM public.todos WHERE title = 'Ship it';
```

Expected result: after each statement the page changes within about a
second. Alice's item appears, is renamed, a second item appears and then
disappears. Bob's row never appears, because the query is bound to the
signed-in user.

Stop the app with Ctrl+C and start it again with the same `Live__ResumeKey`.
The open page reconnects by itself and catches up, including rows changed
while the app was down.

## 7. Clean up

Stop the app first. Then remove the replication slot, because an unused slot
keeps PostgreSQL from removing old WAL:

```powershell
docker exec bluetusk-postgres psql -U postgres -d live_quickstart -c "SELECT pg_drop_replication_slot('live_todos')"
docker exec bluetusk-postgres psql -U postgres -c "DROP DATABASE live_quickstart"
```

## Next steps

- [Concepts](concepts.md): what happened between the `INSERT` and the page.
- [Framework guides](clients.md): the same query in Angular, React, Vue or
  Svelte.
- [Configuration](configuration.md): limits, token lifetime and transports.
- [Troubleshooting](troubleshooting.md): what to check when updates do not
  arrive.
- Before production: replace the demo header with real authentication, keep
  the signing key in a secret store, and call `PruneAsync` on the store on a
  schedule (see [configuration](configuration.md#postgresqllivestoreoptions)).
