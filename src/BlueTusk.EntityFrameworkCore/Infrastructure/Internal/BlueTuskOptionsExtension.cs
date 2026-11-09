using BlueTusk.Data.Internal;
using BlueTusk.EntityFrameworkCore.Graphs;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

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
    }

    internal string? AdminDatabase { get; private set; }

    internal IProviderDataSource? DataSource { get; private set; }

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

        public override int GetServiceProviderHashCode() => 0;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["BlueTusk"] = "1";
            debugInfo["BlueTusk:DataSource"] = extension.DataSource is null ? "0" : "1";
            debugInfo["BlueTusk:AdminDatabase"] = extension.AdminDatabase ?? string.Empty;
        }
    }
}
