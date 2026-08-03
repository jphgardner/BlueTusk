using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Live.Grpc;
using BlueTusk.Live.Grpc.Protocol;
using BlueTusk.Live.ServerSentEvents;
using BlueTusk.Live.SignalR;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.Sdk;

namespace BlueTusk.Live.Tests;

public sealed class LivePostgreSqlTransportMatrixTests
{
    private const string DatabaseIdentity = "live-transport-database";
    private const string AuthenticationScheme = "live-test";
    private static readonly ChangeSourceIdentity Source =
        new("live-transport-system", DatabaseIdentity, "live-transport-slot", "public:orders");

    [Theory]
    [InlineData(LiveTransportKind.ServerSentEvents)]
    [InlineData(LiveTransportKind.SignalR)]
    [InlineData(LiveTransportKind.Grpc)]
    public async Task PostgreSQL_store_replays_signed_events_over_transport(
        LiveTransportKind transportKind)
    {
        var schema = "bluetusk_live_transport_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        var store = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
        {
            ControlDataSource = dataSource,
            ControlSchema = schema,
            ReplayRetentionWindow = TimeSpan.FromMinutes(5),
        });
        IReadOnlyList<Row> currentRows = [new Row(1, "before")];
        var plan = new LiveQueryPlan<Row, int>(
            "orders",
            DatabaseIdentity,
            new string('a', 64),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("sales", "orders")],
            [],
            10,
            (_, _) => ValueTask.FromResult(currentRows),
            static row => row.Id);
        var session = new LiveQuerySession<Row, int>(
            plan,
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope("tenant:transport", "policy:v1"),
            store);
        await using var shared = new LiveSharedSubscription<Row, int>(session, store);
        var tokenProtector = new LiveResumeTokenProtector(
            [new LiveResumeTokenKey("primary", new byte[32], isPrimary: true)]);

        try
        {
            await shared.StartAsync(TestContext.Current.CancellationToken);
            await using var host = await LiveTransportHost.StartAsync(
                transportKind,
                shared,
                tokenProtector,
                TestContext.Current.CancellationToken);

            var initial = await ReadOneAsync(
                transportKind,
                host.Address,
                CreateRequest(),
                TestContext.Current.CancellationToken);
            Assert.Equal(1, initial.Sequence);
            Assert.Equal("InitialResult", initial.Event.GetProperty("kind").GetString());
            Assert.Equal(
                LiveResumeTokenValidationStatus.Valid,
                tokenProtector.Validate(initial.ResumeToken, shared.Identity).Status);

            currentRows = [new Row(1, "after")];
            await using (var delivery = Delivery(transactionId: 42, position: 100))
            {
                var consumer = new LiveInvalidationConsumer(DatabaseIdentity, store);
                await consumer.ConsumeTransactionAsync(
                    delivery,
                    TestContext.Current.CancellationToken);
                Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
            }

            Assert.Equal(
                1,
                await shared.RefreshAsync(TestContext.Current.CancellationToken));

            var resumed = await ReadOneAsync(
                transportKind,
                host.Address,
                CreateRequest(initial.ResumeToken),
                TestContext.Current.CancellationToken);
            Assert.Equal(2, resumed.Sequence);
            Assert.Equal("RowUpdated", resumed.Event.GetProperty("kind").GetString());
            Assert.Equal("after", resumed.Event.GetProperty("row").GetProperty("Value").GetString());
            Assert.Equal(
                LiveResumeTokenValidationStatus.Valid,
                tokenProtector.Validate(resumed.ResumeToken, shared.Identity).Status);
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    private static async ValueTask<TransportObservation> ReadOneAsync(
        LiveTransportKind transportKind,
        Uri address,
        LiveSubscriptionRequest request,
        CancellationToken cancellationToken) =>
        transportKind switch
        {
            LiveTransportKind.ServerSentEvents => await ReadSseAsync(
                address,
                request,
                cancellationToken),
            LiveTransportKind.SignalR => await ReadSignalRAsync(
                address,
                request,
                cancellationToken),
            LiveTransportKind.Grpc => await ReadGrpcAsync(
                address,
                request,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(transportKind),
                transportKind,
                "Unknown Live transport."),
        };

    private static async ValueTask<TransportObservation> ReadSseAsync(
        Uri address,
        LiveSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(10),
        };
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/bluetusk/live/sse")
            {
                Content = JsonContent.Create(request),
            },
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(line[6..]);
                return ReadJsonObservation(document.RootElement);
            }
        }

        throw new InvalidOperationException("The Live SSE stream ended before an event arrived.");
    }

    private static async ValueTask<TransportObservation> ReadSignalRAsync(
        Uri address,
        LiveSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(address, "/bluetusk/live"),
                options => options.Transports = HttpTransportType.WebSockets)
            .Build();
        await connection.StartAsync(cancellationToken);
        try
        {
            await foreach (var message in connection.StreamAsync<JsonElement>(
                               "SubscribeAsync",
                               request,
                               cancellationToken))
            {
                return ReadJsonObservation(message);
            }
        }
        finally
        {
            await connection.StopAsync(cancellationToken);
        }

        throw new InvalidOperationException("The Live SignalR stream ended before an event arrived.");
    }

    private static async ValueTask<TransportObservation> ReadGrpcAsync(
        Uri address,
        LiveSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(address);
        var client = new BlueTuskLive.BlueTuskLiveClient(channel);
        using var call = client.Subscribe(
            new LiveGrpcSubscriptionRequest
            {
                Query = request.Query,
                ParametersJson = request.Parameters.GetRawText(),
                ResumeToken = request.ResumeToken ?? string.Empty,
            },
            cancellationToken: cancellationToken);
        if (!await call.ResponseStream.MoveNext(cancellationToken))
        {
            throw new InvalidOperationException("The Live gRPC stream ended before an event arrived.");
        }

        var message = call.ResponseStream.Current;
        using var document = JsonDocument.Parse(message.EventJson);
        return new TransportObservation(
            message.Sequence,
            message.ResumeToken,
            document.RootElement.Clone());
    }

    private static TransportObservation ReadJsonObservation(JsonElement message)
    {
        var eventElement = message.GetProperty("event");
        return new TransportObservation(
            message.GetProperty("sequence").GetInt64(),
            message.GetProperty("resumeToken").GetString() ??
                throw new InvalidOperationException("The Live transport omitted its resume token."),
            eventElement.Clone());
    }

    private static LiveSubscriptionRequest CreateRequest(string? resumeToken = null)
    {
        using var parameters = JsonDocument.Parse("{}");
        return new LiveSubscriptionRequest
        {
            Query = "orders",
            Parameters = parameters.RootElement.Clone(),
            ResumeToken = resumeToken,
        };
    }

    private static ChangeTransactionDelivery Delivery(uint transactionId, ulong position)
    {
        var table = new ChangeTable(
            1,
            "sales",
            "orders",
            'd',
            [new ChangeColumn(0, "id", 23, -1, IsKey: true)]);
        var id = new ChangeId(
            Source,
            new BlueTuskLogSequenceNumber(position),
            transactionId,
            0);
        var columns = new ChangeRow(
            table,
            [ChangeColumnValue.FromValue("1"u8, ChangeValueEncoding.Text)]);
        var change = new InsertChange<Row>(
            id,
            new ChangeRow<Row>(columns, new Row(1, "after"), hasValue: true));
        return ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId,
            new BlueTuskLogSequenceNumber(position),
            [change]);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }

    private static async ValueTask DropSchemaAsync(DbDataSource dataSource, string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        _ = await command.ExecuteNonQueryAsync();
    }

    public enum LiveTransportKind
    {
        ServerSentEvents,
        SignalR,
        Grpc,
    }

    private sealed record Row(int Id, string Value);

    private sealed record TransportObservation(
        long Sequence,
        string ResumeToken,
        JsonElement Event);

    private sealed class Resolver(ILiveSharedSubscription subscription) :
        ILiveTransportSubscriptionResolver
    {
        public ValueTask<ILiveSharedSubscription> ResolveAsync(
            string query,
            JsonElement parameters,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("orders", query);
            Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
            Assert.True(principal.Identity?.IsAuthenticated);
            return ValueTask.FromResult(subscription);
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) :
        AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "transport-user")],
                AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme)));
        }
    }

    private sealed class LiveTransportHost(
        WebApplication application,
        Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public static async ValueTask<LiveTransportHost> StartAsync(
            LiveTransportKind transportKind,
            ILiveSharedSubscription subscription,
            LiveResumeTokenProtector tokenProtector,
            CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(
                    IPAddress.Loopback,
                    0,
                    listen => listen.Protocols = transportKind is LiveTransportKind.Grpc
                        ? HttpProtocols.Http2
                        : HttpProtocols.Http1));
            builder.Services
                .AddAuthentication(AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    AuthenticationScheme,
                    _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<ILiveTransportSubscriptionResolver>(
                new Resolver(subscription));
            builder.Services.AddBlueTuskLiveAspNetCore(tokenProtector);
            if (transportKind is LiveTransportKind.SignalR)
            {
                builder.Services.AddSignalR();
            }
            else if (transportKind is LiveTransportKind.Grpc)
            {
                builder.Services.AddGrpc();
            }

            var application = builder.Build();
            application.UseAuthentication();
            application.UseAuthorization();
            switch (transportKind)
            {
                case LiveTransportKind.ServerSentEvents:
                    application.MapBlueTuskLiveServerSentEvents();
                    break;
                case LiveTransportKind.SignalR:
                    application.MapBlueTuskLiveHub();
                    break;
                case LiveTransportKind.Grpc:
                    application.MapBlueTuskLiveGrpc();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(transportKind),
                        transportKind,
                        "Unknown Live transport.");
            }

            await application.StartAsync(cancellationToken);
            var server = application.Services.GetRequiredService<IServer>();
            var addressFeature = server.Features.Get<IServerAddressesFeature>() ??
                throw new InvalidOperationException("Kestrel did not report a bound address.");
            var address = new Uri(addressFeature.Addresses.Single());
            return new LiveTransportHost(application, address);
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
