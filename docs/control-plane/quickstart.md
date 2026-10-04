# Control Plane quick start

In this quick start you host the BlueTusk dashboard and its JSON API in an
ASP.NET Core app, require sign-in, register a Streams relay source so there is
something to see, and read the inventory with `curl`. It takes about 10
minutes.

## Before you start

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- Docker, or a PostgreSQL 15, 16, 17 or 18 test server with
  `wal_level=logical`;
- `curl` (Windows 10 and later include it as `curl.exe`).

## 1. Start PostgreSQL

Skip this step if the container from the
[5-minute first app](../getting-started/quickstart.md) is still running.

```powershell
docker run --name bluetusk-postgres `
  -e POSTGRES_PASSWORD=local-dev-only `
  -p 5432:5432 `
  -d postgres:18 `
  -c wal_level=logical
```

## 2. Create a replication slot

The dashboard reports the state of each source's logical replication slot.
Create one to look at:

```powershell
docker exec bluetusk-postgres psql -U postgres -c "SELECT pg_create_logical_replication_slot('orders_slot', 'pgoutput');"
```

## 3. Create the app

```powershell
dotnet new web --framework net10.0 --name ControlPlaneQuickstart
cd ControlPlaneQuickstart
dotnet add package BlueTusk.Dashboard
dotnet add package BlueTusk.Data
```

See [Install BlueTusk](../getting-started/install.md) to choose and pin a
version.

## 4. Set the connection string

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5432;Username=postgres;Password=local-dev-only;Database=postgres;SSL Mode=Disable;Channel Binding=Disable"
```

On Linux or macOS, use `export BLUETUSK_CONNECTION_STRING="..."`.

> **Warning:** `SSL Mode=Disable` is only for a local test container. Keep the
> default, `SSL Mode=VerifyFull`, everywhere else.

## 5. Write the code

Replace the contents of `Program.cs`:

```csharp
using System.Security.Claims;
using BlueTusk.ControlPlane;
using BlueTusk.Dashboard;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration["BLUETUSK_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("Set BLUETUSK_CONNECTION_STRING first.");
var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();

// 1. Register something for the dashboard to show: a relay source and a consumer group.
//    In production the Streams relay worker does this when it starts.
var relay = new PostgreSqlDurableChangeRelay(
    new PostgreSqlStreamsStorageOptions { ControlDataSource = dataSource });
await relay.InitializeAsync();

await using (var command = dataSource.CreateCommand(
    "SELECT system_identifier::text, current_database() FROM pg_control_system()"))
await using (var reader = await command.ExecuteReaderAsync())
{
    await reader.ReadAsync();
    var source = await relay.RegisterSourceAsync(new ChangeSourceIdentity(
        systemIdentifier: reader.GetString(0),
        databaseName: reader.GetString(1),
        slotName: "orders_slot",
        publicationFingerprint: "quickstart"));
    await relay.CreateConsumerGroupAsync(source, "search-index");
}

// 2. Authentication. DEVELOPMENT ONLY: a cookie that /dev/login hands to anyone.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // Return 401/403 instead of redirecting to a login page.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

// 3. The three policies the dashboard requires, with their default names.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("BlueTusk.ControlPlane.Read", policy => policy.RequireRole(
        "BlueTuskViewer", "BlueTuskOperator", "BlueTuskAdministrator"))
    .AddPolicy("BlueTusk.ControlPlane.Mutate", policy => policy.RequireRole(
        "BlueTuskOperator", "BlueTuskAdministrator"))
    .AddPolicy("BlueTusk.ControlPlane.GraphExecute", policy => policy.RequireRole(
        "BlueTuskOperator", "BlueTuskAdministrator"));

// 4. Inventory services. The dashboard needs one of each.
builder.Services.AddSingleton<IControlPlaneQueryService>(
    new PostgreSqlControlPlaneQueryService(
        [new ControlPlanePostgreSqlSource("local", dataSource, dataSource)]));
builder.Services.AddSingleton<IControlPlaneFleetQueryService>(
    new ManagedDeploymentFleetQueryService(new InMemoryManagedDeploymentStore()));
builder.Services.AddSingleton<NotConnected>();
builder.Services.AddSingleton<IControlPlaneSyncQueryService>(
    services => services.GetRequiredService<NotConnected>());
builder.Services.AddSingleton<IControlPlaneLiveQueryService>(
    services => services.GetRequiredService<NotConnected>());
builder.Services.AddSingleton<IControlPlaneContinuousGraphQueryService>(
    services => services.GetRequiredService<NotConnected>());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapGet("/dev/login", async (HttpContext context) =>
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "dev-user"),
             new Claim(ClaimTypes.Role, "BlueTuskViewer")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(new ClaimsPrincipal(identity));
        return Results.Redirect("/bluetusk/overview");
    });
}

app.MapBlueTuskDashboard();
app.Run();

// Reports "nothing connected" for the products this quick start does not run.
sealed class NotConnected :
    IControlPlaneSyncQueryService,
    IControlPlaneLiveQueryService,
    IControlPlaneContinuousGraphQueryService
{
    public ValueTask<ControlPlaneSyncOverview> GetSyncOverviewAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ControlPlaneSyncOverview(DateTimeOffset.UtcNow, []));

    public ValueTask<ControlPlaneLiveOverview> GetLiveOverviewAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ControlPlaneLiveOverview(
            DateTimeOffset.UtcNow, new ControlPlaneLiveRegistrySnapshot(0, 0, 0), []));

    public ValueTask<ControlPlaneContinuousGraphOverview> GetContinuousGraphOverviewAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ControlPlaneContinuousGraphOverview(DateTimeOffset.UtcNow, []));
}
```

What the code does:

- **Relay source.** The dashboard reads Streams state from the durable relay
  tables. `InitializeAsync` creates them in the `bluetusk_streams` schema, and
  the code registers one source and one consumer group.
- **Authentication.** `/dev/login` signs anyone in as a viewer. It exists only
  when the app runs in the `Development` environment. Replace it with your
  real identity provider before you deploy.
- **Policies.** `MapBlueTuskDashboard` protects every route with a named
  policy. You must define those policies.
- **Inventory services.** The overview page asks for every product's
  inventory. `NotConnected` reports Sync, Live and Continuous Graph as empty.

## 6. Run it

```powershell
dotnet run --urls http://127.0.0.1:5217
```

## 7. Read the inventory

In a second terminal, call the API without signing in:

```powershell
curl.exe -i http://127.0.0.1:5217/bluetusk/api/v1/overview
```

```text
HTTP/1.1 401 Unauthorized
```

Sign in, keep the cookie, and call it again:

```powershell
curl.exe -c cookies.txt http://127.0.0.1:5217/dev/login
curl.exe -b cookies.txt http://127.0.0.1:5217/bluetusk/api/v1/overview
```

On Linux or macOS, use `curl` instead of `curl.exe`. The response (shortened)
is:

```json
{"contractVersion":1,"data":{"observedAt":"2026-10-03T09:40:29.62+00:00","sources":[{
  "sourceKey":"local:6029439887c9...","instanceName":"local","databaseName":"postgres",
  "slotName":"orders_slot","sourceEpoch":1,
  "slot":{"sourceReachable":true,"exists":true,"active":false,"outputPlugin":"pgoutput",
          "walStatus":"reserved","walLagBytes":316552,"diagnosticCode":null},
  "consumerGroups":[{"name":"search-index","checkpointSequence":0,"isActive":true,"isLeased":false}]}]}}
```

Now open <http://127.0.0.1:5217/dev/login> in a browser. You land on the
overview page. It shows one source, `local / orders_slot`, under **Needs
attention** with the message "Replication slot is not active". That is
correct: nothing is reading from the slot yet.

## 8. Clean up

Stop the app with Ctrl+C. Then drop the slot. An unused slot makes PostgreSQL
keep WAL files and fills the disk over time.

```powershell
docker exec bluetusk-postgres psql -U postgres -c "SELECT pg_drop_replication_slot('orders_slot');" -c "DROP SCHEMA bluetusk_streams CASCADE;"
```

If you created the container only for this quick start, remove it instead:
`docker rm -f bluetusk-postgres`.

## Next steps

- [Concepts](concepts.md): what the inventory, health and roles mean.
- [Enable operator actions](operations.md): add the audit log and let
  operators act.
- [Configuration](configuration.md): change the route prefix, policy names and
  roles.
- [Durable relay](../streams/durable-relay.md): run a real relay worker that
  keeps the slot active.
