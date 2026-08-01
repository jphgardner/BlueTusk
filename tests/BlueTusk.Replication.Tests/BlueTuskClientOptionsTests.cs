using BlueTusk.Client;

namespace BlueTusk.Replication.Tests;

public sealed class BlueTuskClientOptionsTests
{
    [Fact]
    public void Parses_connection_strings_for_low_level_clients()
    {
        var options = BlueTuskClientOptions.FromConnectionString(
            """
            Host=db.example.test;Port=5544;Database=app;Username=replicator;
            Password=secret;Application Name=wal-reader;Timeout=7;
            SSL Mode=Require;Channel Binding=Require;Allow Unencrypted Password=true;
            Passfile=C:\credentials\pgpass.conf
            """);

        Assert.Equal("db.example.test", options.Host);
        Assert.Equal(5544, options.Port);
        Assert.Equal("app", options.Database);
        Assert.Equal("replicator", options.Username);
        Assert.Equal("secret", options.Password);
        Assert.Equal("wal-reader", options.ApplicationName);
        Assert.Equal(TimeSpan.FromSeconds(7), options.ConnectTimeout);
        Assert.Equal(BlueTuskSslMode.Require, options.SslMode);
        Assert.Equal(BlueTuskChannelBindingMode.Require, options.ChannelBinding);
        Assert.True(options.AllowUnencryptedPassword);
        Assert.Equal("C:\\credentials\\pgpass.conf", options.Passfile);
        Assert.Equal(BlueTuskReplicationMode.None, options.ReplicationMode);
    }

    [Fact]
    public void Uses_the_same_defaults_as_the_data_provider()
    {
        var options = BlueTuskClientOptions.FromConnectionString(
            "Database=app;Username=postgres;Password=postgres");

        Assert.Equal("localhost", options.Host);
        Assert.Equal(5432, options.Port);
        Assert.Equal("BlueTusk", options.ApplicationName);
        Assert.Equal(TimeSpan.FromSeconds(15), options.ConnectTimeout);
        Assert.Equal(BlueTuskSslMode.VerifyFull, options.SslMode);
        Assert.Equal(BlueTuskChannelBindingMode.Prefer, options.ChannelBinding);
        Assert.False(options.AllowUnencryptedPassword);
        Assert.Equal("postgres", options.Password);
        Assert.Null(options.Passfile);
    }

    [Fact]
    public void Parses_quoted_connection_string_values_without_ADO_NET_dependencies()
    {
        var options = BlueTuskClientOptions.FromConnectionString(
            "Database=app;Username=replicator;Password='s;ecret';Application Name=\"wal;reader\"");

        Assert.Equal("s;ecret", options.Password);
        Assert.Equal("wal;reader", options.ApplicationName);
    }

    [Fact]
    public void Diagnostic_text_never_exposes_passwords_or_password_file_paths()
    {
        var options = BlueTuskClientOptions.FromConnectionString(
            "Host=db.example;Database=app;Username=worker;Password=top-secret;Passfile=C:\\secret\\pgpass.conf");

        var diagnosticText = options.ToString();

        Assert.DoesNotContain("top-secret", diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\secret", diagnosticText, StringComparison.Ordinal);
        Assert.Contains("Password = <redacted>", diagnosticText, StringComparison.Ordinal);
    }
}
