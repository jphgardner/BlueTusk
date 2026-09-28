using BlueTusk.Client;
using BlueTusk.Data;
using System.Net;
using System.Net.Sockets;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskMultiHostIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pooled_routing_defers_real_startup_disconnect_and_explicit_clear_rechecks(bool asynchronous)
    {
        var settings = CreateSettings();
        var configured = settings.HostEndpoints[0];
        await using var failingEndpoint = new DisconnectingEndpoint();
        settings.Host = $"127.0.0.1,{configured.Host}";
        settings.Ports = $"{failingEndpoint.Port},{configured.Port}";
        settings.Pooling = true;
        settings.MinimumPoolSize = 0;
        settings.MaximumPoolSize = 1;
        settings.TargetSessionAttributes = BlueTuskTargetSessionAttributes.ReadWrite;
        await using var source = BlueTuskDataSource.Create(settings.ConnectionString);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (var checkout = 0; checkout < 12; checkout++)
        {
            await using var connection = asynchronous ? await source.OpenConnectionAsync(deadline.Token) : source.OpenConnection();
            Assert.Equal(configured, connection.ConnectedEndpoint);
            await using var command = new BlueTuskCommand("SELECT 42::int4", connection);
            Assert.Equal(42, await command.ExecuteScalarAsync<int>(deadline.Token));
        }
        Assert.Equal(1, failingEndpoint.Accepted);
        if (asynchronous) { await source.ClearPoolAsync(); } else { source.ClearPool(); }
        await using (var connection = asynchronous ? await source.OpenConnectionAsync(deadline.Token) : source.OpenConnection())
        {
            Assert.Equal(configured, connection.ConnectedEndpoint);
            Assert.Equal(2, failingEndpoint.Accepted);
        }
        Assert.Equal(0, source.GetPoolStatistics().Busy);
    }

    [Theory]
    [InlineData(BlueTuskTargetSessionAttributes.Any)]
    [InlineData(BlueTuskTargetSessionAttributes.Primary)]
    [InlineData(BlueTuskTargetSessionAttributes.ReadWrite)]
    [InlineData(BlueTuskTargetSessionAttributes.PreferPrimary)]
    [InlineData(BlueTuskTargetSessionAttributes.PreferStandby)]
    public async Task Multi_host_open_fails_over_and_selects_an_acceptable_server(
        BlueTuskTargetSessionAttributes target)
    {
        var settings = CreateSettings();
        var configured = settings.HostEndpoints[0];
        settings.Host = $"{configured.Host},{configured.Host}";
        settings.Ports = $"1,{configured.Port}";
        settings.Pooling = false;
        settings.TargetSessionAttributes = target;
        await using var connection = new BlueTuskConnection(settings.ConnectionString);

        await connection.OpenAsync(CancellationToken.None);

        Assert.Equal(configured, connection.ConnectedEndpoint);
        await using var command = new BlueTuskCommand("SELECT 42::int4", connection);
        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Theory]
    [InlineData(BlueTuskTargetSessionAttributes.Standby)]
    [InlineData(BlueTuskTargetSessionAttributes.ReadOnly)]
    public async Task Strict_target_session_selection_rejects_an_incompatible_server(
        BlueTuskTargetSessionAttributes target)
    {
        var settings = CreateSettings();
        var configured = settings.HostEndpoints[0];
        settings.Host = configured.Host;
        settings.Port = configured.Port;
        settings.Pooling = false;
        settings.TargetSessionAttributes = target;
        await using var connection = new BlueTuskConnection(settings.ConnectionString);

        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => connection.OpenAsync(CancellationToken.None));

        Assert.Contains(target.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Multi_host_failure_reports_endpoints_without_credentials()
    {
        var settings = CreateSettings();
        settings.Host = "localhost,localhost";
        settings.Ports = "1,2";
        settings.Pooling = false;
        await using var connection = new BlueTuskConnection(settings.ConnectionString);

        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => connection.OpenAsync(CancellationToken.None));

        Assert.Contains("2 configured host(s)", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(settings.Password!, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multi_host_data_source_partitions_capacity_and_statistics_per_endpoint()
    {
        var settings = CreateSettings();
        var configured = settings.HostEndpoints[0];
        await using var forwarded = new ForwardingEndpoint(configured);
        settings.Host = $"127.0.0.1,{configured.Host}";
        settings.Ports = $"{forwarded.Port},{configured.Port}";
        settings.Pooling = true;
        settings.MinimumPoolSize = 1;
        settings.MaximumPoolSize = 1;
        settings.TargetSessionAttributes = BlueTuskTargetSessionAttributes.Primary;
        await using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);

        await dataSource.WarmUpAsync(CancellationToken.None);
        var warmed = dataSource.GetHostPoolStatistics();
        Assert.Equal(2, warmed.Count);
        Assert.All(warmed.Values, statistics =>
        {
            Assert.Equal(1, statistics.Total);
            Assert.Equal(1, statistics.Idle);
            Assert.Equal(1, statistics.MaximumSize);
        });
        Assert.Equal(2, dataSource.GetPoolStatistics().MaximumSize);

        await using (var first = await dataSource.OpenConnectionAsync(CancellationToken.None))
        await using (var second = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Equal(
                new[] { new BlueTuskHostEndpoint("127.0.0.1", forwarded.Port), configured }.OrderBy(endpoint => endpoint.Port),
                new[]
                {
                    first.ConnectedEndpoint!.Value,
                    second.ConnectedEndpoint!.Value,
                }.OrderBy(endpoint => endpoint.Port));
            Assert.All(
                dataSource.GetHostPoolStatistics().Values,
                statistics => Assert.Equal(1, statistics.Busy));
        }

        Assert.All(
            dataSource.GetHostPoolStatistics().Values,
            statistics => Assert.Equal(1, statistics.Idle));
        await dataSource.ClearPoolAsync();
        Assert.Equal(0, dataSource.GetPoolStatistics().Total);
    }

    private sealed class ForwardingEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _forwarding;
        internal ForwardingEndpoint(BlueTuskHostEndpoint target)
        { _listener.Start(); _forwarding = ForwardAsync(target); }
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        private async Task ForwardAsync(BlueTuskHostEndpoint target)
        {
            try
            {
                using var downstream = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(target.Host, target.Port, _stop.Token);
                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var request = downstream.GetStream().CopyToAsync(upstream.GetStream(), transfer.Token);
                var response = upstream.GetStream().CopyToAsync(downstream.GetStream(), transfer.Token);
                await Task.WhenAny(request, response);
                await transfer.CancelAsync();
                upstream.Dispose(); downstream.Dispose();
                try { await Task.WhenAll(request, response); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            }
            catch (Exception exception) when (_stop.IsCancellationRequested &&
                exception is OperationCanceledException or SocketException or ObjectDisposedException)
            { }
        }
        public async ValueTask DisposeAsync()
        { await _stop.CancelAsync(); _listener.Stop(); await _forwarding; _stop.Dispose(); }
    }

    private sealed class DisconnectingEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private int _accepted;
        internal DisconnectingEndpoint() { _listener.Start(); _accepting = AcceptAsync(); }
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal int Accepted => Volatile.Read(ref _accepted);
        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted);
                    // A real socket accepted and closed before startup completes.
                }
            }
            catch (Exception exception) when (_stop.IsCancellationRequested &&
                exception is OperationCanceledException or SocketException or ObjectDisposedException)
            { }
        }
        public async ValueTask DisposeAsync()
        { await _stop.CancelAsync(); _listener.Stop(); await _accepting; _stop.Dispose(); }
    }

    private static BlueTuskConnectionStringBuilder CreateSettings()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
            Timeout = TimeSpan.FromSeconds(2),
        };
    }
}
