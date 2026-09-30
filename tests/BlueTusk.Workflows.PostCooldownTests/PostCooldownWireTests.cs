using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Diagnostics;

namespace BlueTusk.Workflows.PostCooldownTests;

public sealed class PostCooldownWireTests
{
    private const int ReadsDuringCooldown = 8;
    private const int ReadsDuringProbe = 32;
    private const int ReadsAfterProbe = 8;
    private const long ExpectedValue = 42;

    [Fact]
    public async Task RealWireSingleProbeDoesNotBlockHealthyHostAfterCooldown()
    {
        string healthyConnection = Required("HEALTHY_CONNECTION_STRING");
        string reportPath = Required("REPORT");
        var healthy = new BlueTuskConnectionStringBuilder(healthyConnection);
        Assert.Single(healthy.HostEndpoints);
        var healthyEndpoint = healthy.HostEndpoints[0];
        Assert.Equal("127.0.0.1", healthyEndpoint.Host);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var dead = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        dead.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // Reserve the exact port while it refuses connections.
        int deadPort = ((IPEndPoint)dead.LocalEndPoint!).Port;
        var settings = new BlueTuskConnectionStringBuilder(healthyConnection)
        {
            Host = "127.0.0.1," + healthyEndpoint.Host,
            Ports = deadPort + "," + healthyEndpoint.Port,
            TargetSessionAttributes = BlueTuskTargetSessionAttributes.ReadWrite,
            LoadBalanceHosts = BlueTuskLoadBalanceHosts.Disable,
            MaximumPoolSize = 8,
            MinimumPoolSize = 0,
            Timeout = TimeSpan.FromSeconds(1),
            ApplicationName = "BlueTuskPostCooldownWire",
        };

        long retryCount = 0;
        long failoverCount = 0;
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == BlueTuskDiagnostics.InstrumentationName &&
                instrument.Name is "bluetusk.connections.retries" or "bluetusk.connections.failovers")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!HasPort(tags, healthyEndpoint.Port)) { return; }
            if (instrument.Name == "bluetusk.connections.retries") { Interlocked.Add(ref retryCount, value); }
            else if (instrument.Name == "bluetusk.connections.failovers") { Interlocked.Add(ref failoverCount, value); }
        });
        metrics.Start();

        await using var source = BlueTuskDataSource.Create(settings.ConnectionString);
        using var acceptLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var firstAccepted = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? acceptLoop = null;
        Task<long>? probe = null;
        int acceptedCount = 0;
        long healthyMarker = 0;
        double blockedServiceMilliseconds = 0;
        var duringProbeDurations = new List<double>(ReadsDuringProbe);
        try
        {
            // The first endpoint is bound but not listening, so the first actual
            // network attempt fails without letting another process take its port.
            Assert.Equal(ExpectedValue, await QueryAsync());
            healthyMarker = Stopwatch.GetTimestamp();
            Assert.Equal(1L, Volatile.Read(ref retryCount));
            Assert.Equal(1L, Volatile.Read(ref failoverCount));

            // It is now listening at the same port, but never speaks PostgreSQL.
            // No connection should reach it before the provider's ten-second
            // monotonic failed-host cooldown expires.
            dead.Listen(16);
            acceptLoop = CountAcceptsAsync();
            for (int index = 0; index < ReadsDuringCooldown; index++)
            {
                Assert.Equal(ExpectedValue, await QueryAsync());
            }
            Assert.Equal(0, Volatile.Read(ref acceptedCount));
            Assert.Equal(1L, Volatile.Read(ref retryCount));
            Assert.Equal(1 + ReadsDuringCooldown, Volatile.Read(ref failoverCount));
            await Task.Delay(TimeSpan.FromSeconds(10.25), deadline.Token);
            double firstRecheckAge = Stopwatch.GetElapsedTime(healthyMarker).TotalMilliseconds;
            Assert.True(firstRecheckAge > 10_000);
            Assert.Equal(0, Volatile.Read(ref acceptedCount));

            probe = QueryAsync(); // One real TCP startup attempt reaches the dead PostgreSQL endpoint.
            Socket heldProbe = await firstAccepted.Task.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            Assert.False(probe.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref acceptedCount));

            // The single recheck is still blocked in wire startup. Contending
            // callers must bypass it and complete on the healthy host.
            long serviceStarted = Stopwatch.GetTimestamp();
            using var concurrency = new SemaphoreSlim(4, 4);
            Task<long>[] served = Enumerable.Range(0, ReadsDuringProbe).Select(async _ =>
            {
                await concurrency.WaitAsync(deadline.Token);
                try
                {
                    long started = Stopwatch.GetTimestamp();
                    long value = await QueryAsync();
                    lock (duringProbeDurations) { duringProbeDurations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
                    return value;
                }
                finally { concurrency.Release(); }
            }).ToArray();
            long[] values = await Task.WhenAll(served).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            blockedServiceMilliseconds = Stopwatch.GetElapsedTime(serviceStarted).TotalMilliseconds;
            Assert.All(values, value => Assert.Equal(ExpectedValue, value));
            Assert.False(probe.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref acceptedCount));
            Assert.Equal(1 + ReadsDuringProbe, Volatile.Read(ref retryCount));
            Assert.Equal(1 + ReadsDuringCooldown + ReadsDuringProbe, Volatile.Read(ref failoverCount));

            // A reset completes the one failed probe. It must fall back, mark
            // this endpoint unavailable again, and leave later reads healthy.
            heldProbe.LingerState = new LingerOption(true, 0);
            heldProbe.Dispose();
            Assert.Equal(ExpectedValue, await probe.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));
            for (int index = 0; index < ReadsAfterProbe; index++)
            {
                Assert.Equal(ExpectedValue, await QueryAsync());
            }
            Assert.Equal(1, Volatile.Read(ref acceptedCount));
            Assert.Equal(2 + ReadsDuringProbe, Volatile.Read(ref retryCount));
            Assert.Equal(2 + ReadsDuringCooldown + ReadsDuringProbe + ReadsAfterProbe, Volatile.Read(ref failoverCount));
            Assert.Equal(ReadsDuringProbe, duringProbeDurations.Count);

            BlueTuskPoolStatistics pool = source.GetPoolStatistics();
            Assert.Equal(16, pool.MaximumSize); // Eight per host, not eight aggregate.
            Assert.InRange(pool.Total, 1, 16);
            Assert.Equal(0, pool.Busy);
            Assert.Equal(0, pool.Waiting);
            Assert.True(pool.Reused > 0);

            var sorted = duringProbeDurations.Order().ToArray();
            var report = new PostCooldownWireReport(
                DateTimeOffset.UtcNow, "Owned loopback socket with no PostgreSQL responder; second endpoint is the supplied healthy disposable PostgreSQL fixture",
                healthyEndpoint.Port, deadPort, firstRecheckAge, ReadsDuringCooldown, ReadsDuringProbe, ReadsAfterProbe,
                Volatile.Read(ref acceptedCount), Volatile.Read(ref retryCount), Volatile.Read(ref failoverCount),
                blockedServiceMilliseconds, sorted[(int)Math.Ceiling(.99 * sorted.Length) - 1], pool.Total, pool.Busy, pool.Waiting,
                "One real post-cooldown TCP startup probe held while 32 healthy-host queries succeeded; exact accepts and provider retry/failover counters; no source replacement/clear. Local wire test, not a production availability or repeated multi-interval endurance claim.");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, PostCooldownJsonContext.Default.PostCooldownWireReport), deadline.Token);
        }
        finally
        {
            if (firstAccepted.Task.IsCompletedSuccessfully) { (await firstAccepted.Task).Dispose(); }
            await acceptLifetime.CancelAsync();
            dead.Dispose();
            if (acceptLoop is not null) { await acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)); }
            if (probe is not null)
            {
                try { _ = await probe.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception) { /* Keep the original assertion or probe failure. */ }
            }
        }

        async Task<long> QueryAsync()
        {
            await using var connection = await source.OpenConnectionAsync(deadline.Token);
            await using var command = new BlueTuskCommand("SELECT 42::bigint", connection);
            return (await command.ExecuteScalarAsync<long>(deadline.Token))!;
        }

        async Task CountAcceptsAsync()
        {
            try
            {
                while (true)
                {
                    Socket accepted = await dead.AcceptAsync(acceptLifetime.Token);
                    _ = Interlocked.Increment(ref acceptedCount);
                    if (!firstAccepted.TrySetResult(accepted)) { accepted.Dispose(); }
                }
            }
            catch (OperationCanceledException) when (acceptLifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (acceptLifetime.IsCancellationRequested) { }
            catch (SocketException) when (acceptLifetime.IsCancellationRequested) { }
        }
    }

    private static bool HasPort(ReadOnlySpan<KeyValuePair<string, object?>> tags, int port)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "server.port" && tag.Value is int observed && observed == port) { return true; }
        }
        return false;
    }

    private static string Required(string suffix) =>
        Environment.GetEnvironmentVariable("BLUETUSK_POST_COOLDOWN_" + suffix) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException("Run the bounded real-wire post-cooldown test with a healthy disposable PostgreSQL fixture and report path; no fixture means failure.");
}

internal sealed record PostCooldownWireReport(
    DateTimeOffset CapturedAtUtc, string Fixture, int HealthyPort, int ReservedDeadPort, double FirstRecheckAgeMilliseconds,
    int HealthyReadsDuringCooldown, int HealthyReadsDuringProbe, int HealthyReadsAfterProbe,
    int DeadEndpointAccepts, long RetryEvents, long FailoverEvents, double HealthyServiceWhileProbeBlockedMilliseconds,
    double HealthyReadP99WhileProbeBlockedMilliseconds, int FinalPoolTotal, int FinalPoolBusy, int FinalPoolWaiting,
    string Qualification);

[JsonSerializable(typeof(PostCooldownWireReport))]
internal sealed partial class PostCooldownJsonContext : JsonSerializerContext;
