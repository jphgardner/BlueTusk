using BlueTusk.Extensions.Citext.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlueTusk.Extensions.Citext.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskCitextOptionsExtension(string schema) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public string Schema { get; } = schema;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.AddSingleton(new BlueTuskCitextTypeMappingOptions(Schema));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRelationalTypeMappingSourcePlugin, BlueTuskCitextTypeMappingSourcePlugin>());
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(BlueTuskCitextOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"using citext in {extension.Schema} ";

        public override int GetServiceProviderHashCode() =>
            StringComparer.Ordinal.GetHashCode(extension.Schema);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo otherInfo &&
            string.Equals(extension.Schema, otherInfo.Extension.Schema, StringComparison.Ordinal);

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["BlueTusk:Citext"] = extension.Schema;

        private new BlueTuskCitextOptionsExtension Extension =>
            (BlueTuskCitextOptionsExtension)base.Extension;
    }
}
