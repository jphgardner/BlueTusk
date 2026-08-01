using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BlueTusk.Client;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskDataSourceBuilderTests
{
    [Fact]
    public void MapEnum_registers_codec_by_qualified_catalogue_name()
    {
        var builder = new BlueTuskDataSourceBuilder("Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.MapEnum<OrderStatus>("app.order_status"));
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(90_300),
                Schema = "app",
                Name = "order_status",
                PostgreSqlKind = 'e',
                PostgreSqlCategory = 'E',
                EnumLabels = ["Pending", "Complete"],
            },
        ], builder.Types.Build());

        Assert.True(registry.TryGetCodec(new BlueTuskTypeId(90_300), out var codec));
        Assert.IsType<BlueTuskEnumCodec<OrderStatus>>(codec);
    }

    [Theory]
    [InlineData("order_status")]
    [InlineData("app.")]
    [InlineData(".order_status")]
    public void MapEnum_requires_schema_qualified_type_name(string name)
    {
        var builder = new BlueTuskDataSourceBuilder("Host=localhost;Username=test;Password=test");

        Assert.Throws<FormatException>(() => builder.MapEnum<OrderStatus>(name));
    }

    [Fact]
    public void MapComposite_registers_codec_by_qualified_catalogue_name()
    {
        var builder = new BlueTuskDataSourceBuilder("Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.MapComposite<Address>("app.address"));
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(90_400),
                Schema = "app",
                Name = "address",
                PostgreSqlKind = 'c',
                PostgreSqlCategory = 'C',
                CompositeFields =
                [
                    new BlueTuskCompositeField
                    {
                        Position = 1,
                        Name = "house_number",
                        Type = BlueTuskBuiltInTypes.Int4.Id,
                    },
                ],
            },
        ], builder.Types.Build());

        Assert.True(registry.TryGetCodec(new BlueTuskTypeId(90_400), out var codec));
        Assert.IsType<BlueTuskCompositeCodec<Address>>(codec);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("app.")]
    [InlineData(".address")]
    public void MapComposite_requires_schema_qualified_type_name(string name)
    {
        var builder = new BlueTuskDataSourceBuilder("Host=localhost;Username=test;Password=test");

        Assert.Throws<FormatException>(() => builder.MapComposite<Address>(name));
    }

    [Fact]
    public void Dedicated_session_options_preserve_connection_security_without_using_the_pool()
    {
        var settings = new BlueTuskConnectionStringBuilder
        {
            Host = "db.example.test",
            Port = 5544,
            Database = "app",
            Username = "replicator",
            Password = "secret",
            Passfile = "C:\\credentials\\pgpass.conf",
            ApplicationName = "wal-reader",
            Timeout = TimeSpan.FromSeconds(7),
            Pooling = true,
            SslMode = BlueTuskSslMode.Require,
            ChannelBinding = BlueTuskChannelBindingMode.Require,
            AllowUnencryptedPassword = true,
            KerberosServiceName = "postgresql",
        };
        using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);

        var options = dataSource.CreateDedicatedSessionOptions();

        Assert.Equal("db.example.test", options.Host);
        Assert.Equal(5544, options.Port);
        Assert.Equal("app", options.Database);
        Assert.Equal("replicator", options.Username);
        Assert.Equal("secret", options.Password);
        Assert.Equal("C:\\credentials\\pgpass.conf", options.Passfile);
        Assert.Equal("wal-reader", options.ApplicationName);
        Assert.Equal(TimeSpan.FromSeconds(7), options.ConnectTimeout);
        Assert.Equal(BlueTuskSslMode.Require, options.SslMode);
        Assert.Equal(BlueTuskChannelBindingMode.Require, options.ChannelBinding);
        Assert.True(options.AllowUnencryptedPassword);
        Assert.Equal("postgresql", options.KerberosServiceName);
        Assert.Equal(BlueTuskReplicationMode.None, options.ReplicationMode);
        Assert.Equal(0, dataSource.GetPoolStatistics().Total);
    }

    [Fact]
    public void Multi_host_dedicated_sessions_require_a_configured_endpoint()
    {
        using var dataSource = BlueTuskDataSource.Create(
            "Host=primary,standby;Port=5432,5433;Database=app;Username=test;Password=test");

        Assert.Throws<InvalidOperationException>(dataSource.CreateDedicatedSessionOptions);

        var options = dataSource.CreateDedicatedSessionOptions(
            new BlueTuskHostEndpoint("standby", 5433));
        Assert.Equal("standby", options.Host);
        Assert.Equal(5433, options.Port);
        Assert.Throws<ArgumentException>(
            () => dataSource.CreateDedicatedSessionOptions(
                new BlueTuskHostEndpoint("other", 5432)));
    }

    [Fact]
    public void Data_source_builder_propagates_password_callbacks_to_dedicated_sessions()
    {
        BlueTuskPasswordProvider synchronous = request => $"{request.Username}-sync";
        BlueTuskPasswordProviderAsync asynchronous = (request, _) =>
            ValueTask.FromResult($"{request.Username}-async");
        using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=db.example.test;Database=app;Username=worker;Passfile=")
            .UsePasswordProvider(synchronous)
            .UsePasswordProvider(asynchronous)
            .Build();

        var options = dataSource.CreateDedicatedSessionOptions();

        Assert.Same(synchronous, options.PasswordProvider);
        Assert.Same(asynchronous, options.PasswordProviderAsync);
        Assert.Null(options.Password);
        Assert.Null(options.Passfile);
    }

    [Fact]
    public void Data_source_builder_rejects_ambiguous_password_and_access_token_callbacks()
    {
        var builder = new BlueTuskDataSourceBuilder(
                "Host=db.example.test;Database=app;Username=worker")
            .UsePasswordProvider(_ => "password")
            .UseAccessTokenProvider(_ => "token");

        Assert.Throws<InvalidOperationException>(builder.Build);
    }

    [Fact]
    public void Data_source_builder_propagates_access_token_TLS_policy()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=db.example.test;Database=app;Username=worker")
            .UseAccessTokenProvider(_ => "token")
            .RequireTlsForAccessTokens()
            .Build();

        Assert.True(dataSource.CreateDedicatedSessionOptions().AccessTokenRequiresTls);
    }

    [Fact]
    public void Data_source_builder_propagates_TLS_client_identity_configuration()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=bluetusk-client",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        LocalCertificateSelectionCallback selection = (_, _, certificates, _, _) => certificates[0];
        RemoteCertificateValidationCallback validation = (_, _, _, errors) =>
            errors == SslPolicyErrors.None;
        using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=db.example.test;Database=app;Username=worker")
            .UseClientCertificate(certificate)
            .UseClientCertificateSelectionCallback(selection)
            .UseRemoteCertificateValidationCallback(validation)
            .Build();

        var options = dataSource.CreateDedicatedSessionOptions();

        Assert.Same(certificate, Assert.Single(options.ClientCertificates));
        Assert.Same(selection, options.LocalCertificateSelectionCallback);
        Assert.Same(validation, options.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void Data_source_builder_propagates_an_explicit_GSSAPI_credential()
    {
        var credential = new NetworkCredential("worker", "credential-secret", "BLUETUSK.TEST");
        using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=db.example.test;Database=app;Username=worker;Kerberos Service Name=postgresql")
            .UseGssCredential(credential)
            .Build();

        var options = dataSource.CreateDedicatedSessionOptions();

        Assert.Same(credential, options.GssCredential);
        Assert.Equal("postgresql", options.KerberosServiceName);
        Assert.DoesNotContain("credential-secret", options.ToString(), StringComparison.Ordinal);
    }

    private enum OrderStatus
    {
        Pending,
        Complete,
    }

    private sealed record Address(int HouseNumber);
}
