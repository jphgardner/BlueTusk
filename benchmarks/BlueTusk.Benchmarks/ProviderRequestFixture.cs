using System.Data.Common;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueTusk.Benchmarks;

internal sealed class ProviderRequestFixture : IAsyncDisposable
{
    internal static readonly string[] Features =
    [
        "warm-pool-checkout", "parameterized-scalar", "prepared-scalar",
        "sequential-1000-rows", "sequential-1mib-bytea", "empty-begin-rollback",
        "batch-16-scalars", "copy-import-1000", "copy-export-1000", "prepared-typed-row",
        "notification-delivery", "large-object-read-1mib", "ef-compiled-query",
        "ef-materialize-100", "ef-insert", "ef-update",
    ];

    // Additional contention probes do not replace the frozen 16-feature matrix.
    internal static readonly string[] ContentionFeatures =
    [
        "pooled-scalar", "pooled-reused-scalar", "multiplexed-scalar", "multiplexed-reused-scalar",
    ];
    internal static readonly string[] CaptureFeatures = [.. Features, .. ContentionFeatures];

    internal static bool IsContentionFeature(string feature) =>
        ContentionFeatures.Contains(feature, StringComparer.Ordinal);

    internal static int GetPoolSize(ProviderRequestCapture.Options options) =>
        IsContentionFeature(options.Feature) ? 4 : Math.Max(4, options.Concurrency * 2 + 2);

    private readonly bool _usesSchema;
    private readonly X509Certificate2? _customRoot;

    private ProviderRequestFixture(ProviderRequestCapture.Options options, DbDataSource source, int poolSize,
        X509Certificate2? customRoot)
    {
        Options = options;
        Source = source;
        PoolSize = poolSize;
        _customRoot = customRoot;
        _usesSchema = options.Feature is "sequential-1mib-bytea" or "copy-import-1000" ||
            options.Feature.StartsWith("ef-", StringComparison.Ordinal);
    }

    public ProviderRequestCapture.Options Options { get; }
    public DbDataSource Source { get; }
    public bool IsBlueTusk => Options.Provider == "bluetusk";
    public int PoolSize { get; }
    public bool IsContention => IsContentionFeature(Options.Feature);
    public bool MultiplexingConfigured => Options.Feature.StartsWith("multiplexed-", StringComparison.Ordinal);
    public string Schema { get; } = $"bt_capture_{Guid.NewGuid():N}";
    public string ServerVersion { get; private set; } = string.Empty;
    public bool TlsActive { get; private set; }
    public string CertificatePolicy { get; private set; } = string.Empty;
    public DbContextOptions<OrderContext>? EfOptions { get; private set; }
    public Func<OrderContext, int, IAsyncEnumerable<int>>? CompiledQuery { get; private set; }
    public string Durability => Options.Feature switch
    {
        "copy-import-1000" or "ef-insert" or "ef-update" =>
            "transaction completes by rollback; no commit durability claim",
        "empty-begin-rollback" => "empty transaction; no application writes",
        "notification-delivery" => "autocommit NOTIFY through listener receipt; no replay durability claim",
        _ => "read-only operation or connection checkout",
    };

    public static async Task<ProviderRequestFixture> CreateAsync(
        ProviderRequestCapture.Options options, string connectionString, CancellationToken token)
    {
        // A shared pool is essential: per-worker pools would avoid the contention being measured.
        var poolSize = GetPoolSize(options);
        var multiplexing = options.Feature.StartsWith("multiplexed-", StringComparison.Ordinal);
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        builder["Pooling"] = true;
        builder["Multiplexing"] = multiplexing;
        builder["Minimum Pool Size"] = 0;
        builder["Maximum Pool Size"] = poolSize;
        var referenceSettings = new NpgsqlConnectionStringBuilder(builder.ConnectionString);
        X509Certificate2? customRoot = null;
        DbDataSource source;
        try
        {
            if (options.Provider == "bluetusk")
            {
                var blue = new BlueTuskDataSourceBuilder(builder.ConnectionString);
                if (multiplexing)
                {
                    // Match the existing four-worker / 64-command burst benchmark.
                    blue.EnableMultiplexing(settings =>
                    {
                        settings.WorkerCount = 4;
                        settings.QueueCapacity = 256;
                        settings.MaxPipelineCommands = 64;
                        settings.MaxCommandsPerLease = 65_536;
                    });
                }
                if (!string.IsNullOrWhiteSpace(referenceSettings.RootCertificate))
                {
                    // Translate the shared private-CA setting; never silently ignore it.
                    customRoot = X509Certificate2.CreateFromPem(File.ReadAllText(referenceSettings.RootCertificate));
                    var trustedRoot = customRoot;
                    var revocation = referenceSettings.CheckCertificateRevocation
                        ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
                    blue.UseRemoteCertificateValidationCallback((_, certificate, chain, errors) =>
                        ValidatePrivateCertificate(certificate, chain, errors, trustedRoot, revocation));
                }
                source = blue.Build();
            }
            else
            {
                source = NpgsqlDataSource.Create(builder.ConnectionString);
            }
        }
        catch
        {
            customRoot?.Dispose();
            throw;
        }
        var fixture = new ProviderRequestFixture(options, source, poolSize, customRoot)
        {
            CertificatePolicy = $"SSL mode: {referenceSettings.SslMode}; explicit root: " +
                $"{!string.IsNullOrWhiteSpace(referenceSettings.RootCertificate)}; " +
                $"revocation requested: {referenceSettings.CheckCertificateRevocation}",
        };
        try
        {
            await using var connection = await source.OpenConnectionAsync(token);
            await using (var metadata = connection.CreateCommand())
            {
                metadata.CommandText = "SELECT current_setting('server_version'), " +
                    "COALESCE((SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()), false), " +
                    "current_setting('max_connections')::int4";
                await using var reader = await metadata.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException("PostgreSQL did not return server/transport metadata.");
                }
                fixture.ServerVersion = reader.GetString(0);
                fixture.TlsActive = reader.GetBoolean(1);
                if (fixture.TlsActive != options.RequireTls)
                {
                    throw new InvalidOperationException("Actual PostgreSQL TLS state does not match the requested capture variant.");
                }
                var requiredConnections = fixture.IsContention ? poolSize + 10 : options.Concurrency *
                    (options.Feature == "notification-delivery" ? 2 : 1) + 10;
                if (reader.GetInt32(2) < requiredConnections)
                {
                    throw new InvalidOperationException($"Dedicated PostgreSQL needs max_connections >= {requiredConnections} for this workload.");
                }
            }
            if (fixture._usesSchema)
            {
                await ExecuteAsync(connection, $"CREATE SCHEMA \"{fixture.Schema}\"", token);
            }
            if (options.Feature == "sequential-1mib-bytea")
            {
                await ExecuteAsync(connection,
                    $"CREATE UNLOGGED TABLE \"{fixture.Schema}\".payload (value bytea); " +
                    $"ALTER TABLE \"{fixture.Schema}\".payload ALTER COLUMN value SET STORAGE EXTERNAL; " +
                    $"INSERT INTO \"{fixture.Schema}\".payload SELECT decode(repeat('ab', 1048576), 'hex')", token);
            }
            if (options.Feature == "copy-import-1000")
            {
                await ExecuteAsync(connection,
                    $"CREATE UNLOGGED TABLE \"{fixture.Schema}\".bulk " +
                    "(id int4 NOT NULL, name text NOT NULL, active bool NOT NULL, token uuid NOT NULL)", token);
            }
            if (options.Feature.StartsWith("ef-", StringComparison.Ordinal))
            {
                await ExecuteAsync(connection,
                    $"CREATE UNLOGGED TABLE \"{fixture.Schema}\".orders (" +
                    "id int4 GENERATED ALWAYS AS IDENTITY PRIMARY KEY, customer text NOT NULL, " +
                    "total numeric(12,2) NOT NULL, updated_at timestamptz NOT NULL); " +
                    $"INSERT INTO \"{fixture.Schema}\".orders (customer, total, updated_at) " +
                    "SELECT 'customer-' || value::text, value::numeric / 10, " +
                    "'2026-01-01 00:00:00+00'::timestamptz FROM generate_series(1, 1000) value", token);
                var ef = new DbContextOptionsBuilder<OrderContext>();
                fixture.EfOptions = fixture.IsBlueTusk
                    ? ef.UseBlueTusk((BlueTuskDataSource)source).Options
                    : ef.UseNpgsql((NpgsqlDataSource)source).Options;
                fixture.CompiledQuery = EF.CompileAsyncQuery((OrderContext context, int minimum) =>
                    context.Orders.AsNoTracking().Where(order => order.Id >= minimum)
                        .OrderBy(order => order.Id).Select(order => order.Id).Take(1));
            }
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public OrderContext CreateContext() => new(EfOptions!, Schema);

    public static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_usesSchema)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var connection = await Source.OpenConnectionAsync(timeout.Token);
                // Schema is generated by this instance, never supplied by options or the caller.
                await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", timeout.Token);
            }
        }
        finally
        {
            try { await Source.DisposeAsync(); }
            finally { _customRoot?.Dispose(); }
        }
    }

    internal static bool ValidatePrivateCertificate(X509Certificate? certificate, X509Chain? suppliedChain,
        SslPolicyErrors errors, X509Certificate2 trustedRoot, X509RevocationMode revocationMode)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable |
            SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
        {
            return false;
        }
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.RevocationMode = revocationMode;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        if (suppliedChain is not null)
        {
            foreach (var element in suppliedChain.ChainElements)
            {
                if (!element.Certificate.Equals(leaf)) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }
        return chain.Build(leaf);
    }

    internal sealed class OrderContext(DbContextOptions<OrderContext> options, string schema) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var order = modelBuilder.Entity<Order>();
            order.ToTable("orders", schema);
            order.HasKey(item => item.Id);
            order.Property(item => item.Id).HasColumnName("id").ValueGeneratedOnAdd();
            order.Property(item => item.Customer).HasColumnName("customer");
            order.Property(item => item.Total).HasColumnName("total").HasPrecision(12, 2);
            order.Property(item => item.UpdatedAt).HasColumnName("updated_at");
        }
    }

    internal sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public decimal Total { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
