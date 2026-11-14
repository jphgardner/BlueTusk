using BlueTusk.Data.Internal;
using BlueTusk.EntityFrameworkCore.Graphs;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
#if !BLUETUSK_EF8_OR_GREATER
using System.Data.Common;
#endif

namespace BlueTusk.EntityFrameworkCore.Infrastructure.Internal;

public sealed class BlueTuskOptionsExtension : RelationalOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public BlueTuskOptionsExtension()
    {
    }

    private BlueTuskOptionsExtension(BlueTuskOptionsExtension copyFrom)
        : base(copyFrom)
    {
        AdminDatabase = copyFrom.AdminDatabase;
        DataSource = copyFrom.DataSource;
        GraphBackend = copyFrom.GraphBackend;
#if !BLUETUSK_EF8_OR_GREATER
        LegacyContextOwnsConnection = copyFrom.LegacyContextOwnsConnection;
#endif
    }

    internal string? AdminDatabase { get; private set; }

    internal IProviderDataSource? DataSource { get; private set; }

#if !BLUETUSK_EF8_OR_GREATER
    internal bool LegacyContextOwnsConnection { get; private set; }

    internal BlueTuskOptionsExtension WithConnection(DbConnection connection, bool contextOwnsConnection)
    {
        var clone = (BlueTuskOptionsExtension)base.WithConnection(connection);
        clone.LegacyContextOwnsConnection = contextOwnsConnection;
        return clone;
    }
#endif

    internal BlueTuskPropertyGraphBackend GraphBackend { get; private set; } =
        BlueTuskPropertyGraphBackend.Relational;

    internal static BlueTuskPropertyGraphBackend GetGraphBackend(IDbContextOptions options) =>
        options.FindExtension<BlueTuskOptionsExtension>()?.GraphBackend
        ?? BlueTuskPropertyGraphBackend.Relational;

    internal BlueTuskOptionsExtension WithGraphBackend(BlueTuskPropertyGraphBackend backend) =>
        new(this) { GraphBackend = backend };

    public override DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    protected override RelationalOptionsExtension Clone() => new BlueTuskOptionsExtension(this);

    internal BlueTuskOptionsExtension WithDataSource(IProviderDataSource? dataSource)
    {
        var clone = new BlueTuskOptionsExtension(this)
        {
            DataSource = dataSource,
        };
        return clone;
    }

    internal BlueTuskOptionsExtension WithAdminDatabase(string? adminDatabase)
    {
        var clone = new BlueTuskOptionsExtension(this)
        {
            AdminDatabase = adminDatabase,
        };
        return clone;
    }

    public override void ApplyServices(IServiceCollection services)
        => services.AddEntityFrameworkBlueTusk();

    private sealed class ExtensionInfo(BlueTuskOptionsExtension extension)
        : RelationalExtensionInfo(extension)
    {
        public override string LogFragment => extension.DataSource is null
            ? "using BlueTusk "
            : "using BlueTusk data source ";

#if BLUETUSK_EF6_OR_GREATER
        public override int GetServiceProviderHashCode() => 0;
#else
        public override long GetServiceProviderHashCode() => 0;
#endif

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["BlueTusk"] = "1";
            debugInfo["BlueTusk:DataSource"] = extension.DataSource is null ? "0" : "1";
            debugInfo["BlueTusk:AdminDatabase"] = extension.AdminDatabase ?? string.Empty;
        }
    }
}
