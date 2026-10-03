using BlueTusk.Client;
using BlueTusk.Security;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskConnectionStringBuilderTests
{
    // The documented AppContext switch name is a compatibility contract.
    private const string RejectUnknownKeywordsSwitch =
        "BlueTusk.Data.RejectUnknownConnectionStringKeywords";

    [Fact]
    public void Uses_safe_defaults()
    {
        var builder = new BlueTuskConnectionStringBuilder();

        Assert.Equal("localhost", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.True(builder.Pooling);
        Assert.False(builder.Multiplexing);
        Assert.Equal(0, builder.MinimumPoolSize);
        Assert.Equal(100, builder.MaximumPoolSize);
        Assert.Equal(TimeSpan.FromMinutes(5), builder.ConnectionIdleLifetime);
        Assert.Equal(TimeSpan.FromHours(1), builder.ConnectionLifetime);
        Assert.Equal(0, builder.MaxAutoPrepare);
        Assert.Equal(5, builder.AutoPrepareMinUsages);
        Assert.Equal(BlueTuskTargetSessionAttributes.Any, builder.TargetSessionAttributes);
        Assert.Equal(BlueTuskLoadBalanceHosts.Disable, builder.LoadBalanceHosts);
        Assert.False(builder.AllowUnencryptedPassword);
        Assert.False(builder.PersistSecurityInfo);
        Assert.Equal("postgres", builder.KerberosServiceName);
        Assert.Null(builder.Password);
        Assert.Null(builder.Passfile);
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
    public void Parses_and_removes_an_explicit_password_file()
    {
        var builder = new BlueTuskConnectionStringBuilder("Passfile=C:\\credentials\\pgpass.conf");

        Assert.Equal("C:\\credentials\\pgpass.conf", builder.Passfile);

        builder.Passfile = null;

        Assert.Null(builder.Passfile);
        Assert.DoesNotContain("Passfile", builder.ConnectionString, StringComparison.OrdinalIgnoreCase);
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
            "SSL Mode=Disable;Channel Binding=Disable;Allow Unencrypted Password=true;" +
            "Application Name=test-suite;Kerberos Service Name=postgresql");

        Assert.Equal(BlueTuskSslMode.VerifyFull, defaults.SslMode);
        Assert.Equal(BlueTuskChannelBindingMode.Prefer, defaults.ChannelBinding);
        Assert.Equal(BlueTuskSslMode.Disable, explicitSettings.SslMode);
        Assert.Equal(BlueTuskChannelBindingMode.Disable, explicitSettings.ChannelBinding);
        Assert.True(explicitSettings.AllowUnencryptedPassword);
        Assert.Equal("test-suite", explicitSettings.ApplicationName);
        Assert.Equal("postgresql", explicitSettings.KerberosServiceName);
        Assert.Throws<ArgumentException>(
            () => new BlueTuskConnectionStringBuilder("Kerberos Service Name=bad/name").Validate());
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
    public void Unknown_keywords_remain_accepted_by_default_for_1_0_compatibility()
    {
        var builder = new BlueTuskConnectionStringBuilder(
            "Host=db.example;Username=app;Command Timeout=30;Include Error Detail=true");

        builder.Validate();

        Assert.Equal("db.example", builder.Host);
    }

    [Fact]
    public void Unknown_keywords_are_rejected_by_name_when_the_compatibility_switch_is_enabled()
    {
        var known = new BlueTuskConnectionStringBuilder(
            "Host=db-a,db-b;Port=5432;Database=app;Username=app;Password=secret;Passfile=;" +
            "Timeout=5;Pooling=true;Multiplexing=false;Persist Security Info=false;" +
            "Application Name=app;SSL Mode=Disable;Channel Binding=Disable;" +
            "Kerberos Service Name=postgres;Allow Unencrypted Password=false;" +
            "Target Session Attributes=any;Load Balance Hosts=disable;Minimum Pool Size=0;" +
            "Maximum Pool Size=10;Connection Idle Lifetime=60;Connection Lifetime=600;" +
            "Max Auto Prepare=0;Auto Prepare Min Usages=5");
        var typo = new BlueTuskConnectionStringBuilder(
            "Host=db.example;Usernme=app;Password=do-not-echo");

        AppContext.SetSwitch(RejectUnknownKeywordsSwitch, true);
        try
        {
            known.Validate();
            var error = Assert.Throws<ArgumentException>(typo.Validate);

            Assert.Contains("'usernme'", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("do-not-echo", error.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(
                () => new BlueTuskConnection("Host=db.example;Server=other"));
        }
        finally
        {
            AppContext.SetSwitch(RejectUnknownKeywordsSwitch, false);
        }
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
