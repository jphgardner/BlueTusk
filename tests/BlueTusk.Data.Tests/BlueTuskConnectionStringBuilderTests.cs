using BlueTusk.Client;
using BlueTusk.Security;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskConnectionStringBuilderTests
{
    [Fact]
    public void Uses_safe_defaults()
    {
        var builder = new BlueTuskConnectionStringBuilder();

        Assert.Equal("localhost", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.True(builder.Pooling);
        Assert.Equal(0, builder.MinimumPoolSize);
        Assert.Equal(100, builder.MaximumPoolSize);
        Assert.Equal(TimeSpan.FromMinutes(5), builder.ConnectionIdleLifetime);
        Assert.Equal(TimeSpan.FromHours(1), builder.ConnectionLifetime);
        Assert.Equal(0, builder.MaxAutoPrepare);
        Assert.Equal(5, builder.AutoPrepareMinUsages);
        Assert.Equal(BlueTuskTargetSessionAttributes.Any, builder.TargetSessionAttributes);
        Assert.Equal(BlueTuskLoadBalanceHosts.Disable, builder.LoadBalanceHosts);
        Assert.Equal([new BlueTuskHostEndpoint("localhost", 5432)], builder.HostEndpoints);
    }

    [Fact]
    public void Redactor_removes_passwords_and_tokens()
    {
        const string connectionString =
            "Host=db.example;Username=app;Password=top-secret;Access Token=also-secret";

        var redacted = BlueTuskConnectionStringRedactor.Redact(connectionString);

        Assert.DoesNotContain("top-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("also-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("db.example", redacted, StringComparison.Ordinal);
        Assert.Equal(2, redacted.Split("<redacted>", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Redactor_handles_quoted_values_without_exposing_secrets()
    {
        const string connectionString =
            "Host=db.example;Password='top;secret';Application Name=\"worker;one\"";

        var redacted = BlueTuskConnectionStringRedactor.Redact(connectionString);

        Assert.DoesNotContain("top;secret", redacted, StringComparison.Ordinal);
        Assert.Contains("Password=<redacted>", redacted, StringComparison.Ordinal);
        Assert.Contains("worker;one", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_invalid_ports()
    {
        var builder = new BlueTuskConnectionStringBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Port = 65_536);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new BlueTuskConnectionStringBuilder("Port=65536").Port);
    }

    [Fact]
    public void Validates_pool_bounds_and_lifetimes()
    {
        var invalidBounds = new BlueTuskConnectionStringBuilder(
            "Minimum Pool Size=3;Maximum Pool Size=2");
        var invalidIdleLifetime = new BlueTuskConnectionStringBuilder("Connection Idle Lifetime=-1");
        var disabledLifetimes = new BlueTuskConnectionStringBuilder
        {
            ConnectionIdleLifetime = TimeSpan.Zero,
            ConnectionLifetime = TimeSpan.Zero,
        };

        Assert.Throws<ArgumentException>(invalidBounds.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = invalidIdleLifetime.ConnectionIdleLifetime);
        Assert.Equal(TimeSpan.Zero, disabledLifetimes.ConnectionIdleLifetime);
        Assert.Equal(TimeSpan.Zero, disabledLifetimes.ConnectionLifetime);
    }

    [Fact]
    public void Uses_secure_tls_defaults_and_parses_explicit_modes()
    {
        var defaults = new BlueTuskConnectionStringBuilder();
        var explicitSettings = new BlueTuskConnectionStringBuilder(
            "SSL Mode=Disable;Channel Binding=Disable;Application Name=test-suite");

        Assert.Equal(BlueTuskSslMode.VerifyFull, defaults.SslMode);
        Assert.Equal(BlueTuskChannelBindingMode.Prefer, defaults.ChannelBinding);
        Assert.Equal(BlueTuskSslMode.Disable, explicitSettings.SslMode);
        Assert.Equal(BlueTuskChannelBindingMode.Disable, explicitSettings.ChannelBinding);
        Assert.Equal("test-suite", explicitSettings.ApplicationName);
    }

    [Fact]
    public void Validates_automatic_preparation_settings()
    {
        var enabled = new BlueTuskConnectionStringBuilder(
            "Max Auto Prepare=20;Auto Prepare Min Usages=3");

        Assert.Equal(20, enabled.MaxAutoPrepare);
        Assert.Equal(3, enabled.AutoPrepareMinUsages);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new BlueTuskConnectionStringBuilder("Max Auto Prepare=-1").MaxAutoPrepare);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new BlueTuskConnectionStringBuilder("Auto Prepare Min Usages=0").AutoPrepareMinUsages);
    }

    [Fact]
    public void Parses_multi_host_ports_targets_and_load_balancing()
    {
        var paired = new BlueTuskConnectionStringBuilder(
            "Host=db-a,db-b;Port=5432,5433;Target Session Attributes=prefer-standby;Load Balance Hosts=random");
        var shared = new BlueTuskConnectionStringBuilder("Host=db-a,db-b;Port=5544");

        Assert.Equal(
            [new BlueTuskHostEndpoint("db-a", 5432), new BlueTuskHostEndpoint("db-b", 5433)],
            paired.HostEndpoints);
        Assert.Equal(BlueTuskTargetSessionAttributes.PreferStandby, paired.TargetSessionAttributes);
        Assert.Equal(BlueTuskLoadBalanceHosts.Random, paired.LoadBalanceHosts);
        Assert.Equal(
            [new BlueTuskHostEndpoint("db-a", 5544), new BlueTuskHostEndpoint("db-b", 5544)],
            shared.HostEndpoints);
        Assert.Throws<InvalidOperationException>(() => _ = paired.Port);
    }

    [Fact]
    public void Rejects_misaligned_multi_host_settings()
    {
        var mismatched = new BlueTuskConnectionStringBuilder(
            "Host=db-a,db-b;Port=5432,5433,5434");
        var emptyHost = new BlueTuskConnectionStringBuilder("Host=db-a,,db-b");
        var invalidTarget = new BlueTuskConnectionStringBuilder(
            "Target Session Attributes=somewhere");

        Assert.Throws<ArgumentException>(mismatched.Validate);
        Assert.Throws<ArgumentException>(emptyHost.Validate);
        Assert.Throws<ArgumentException>(invalidTarget.Validate);
    }
}
