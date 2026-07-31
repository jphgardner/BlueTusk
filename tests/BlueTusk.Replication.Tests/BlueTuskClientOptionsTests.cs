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
            SSL Mode=Require;Channel Binding=Require
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
    }

    [Fact]
    public void Parses_quoted_connection_string_values_without_ADO_NET_dependencies()
    {
        var options = BlueTuskClientOptions.FromConnectionString(
            "Database=app;Username=replicator;Password='s;ecret';Application Name=\"wal;reader\"");

        Assert.Equal("s;ecret", options.Password);
        Assert.Equal("wal;reader", options.ApplicationName);
    }
}
