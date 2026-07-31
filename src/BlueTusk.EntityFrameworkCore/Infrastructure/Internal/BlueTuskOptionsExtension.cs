using BlueTusk.Data;
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
        DataSource = copyFrom.DataSource;
    }

    internal BlueTuskDataSource? DataSource { get; private set; }

    public override DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    protected override RelationalOptionsExtension Clone() => new BlueTuskOptionsExtension(this);

    internal BlueTuskOptionsExtension WithDataSource(BlueTuskDataSource? dataSource)
    {
        var clone = new BlueTuskOptionsExtension(this)
        {
            DataSource = dataSource,
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
        }
    }
}
