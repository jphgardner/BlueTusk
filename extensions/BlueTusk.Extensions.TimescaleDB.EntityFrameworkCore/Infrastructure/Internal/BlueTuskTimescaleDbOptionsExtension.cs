using BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Query.Internal;
#if !BLUETUSK_EF7_OR_GREATER
using BlueTusk.EntityFrameworkCore.Query.Internal;
#pragma warning disable EF1001 // BlueTusk's legacy provider and extension are versioned together.
#endif
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskTimescaleDbOptionsExtension(string schema) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public string Schema { get; } = schema;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.AddSingleton(new BlueTuskTimescaleDbQueryOptions(Schema));
#if BLUETUSK_EF7_OR_GREATER
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IMethodCallTranslatorPlugin, BlueTuskTimescaleDbMethodCallTranslatorPlugin>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAggregateMethodCallTranslatorPlugin, BlueTuskTimescaleDbAggregateTranslatorPlugin>());
#else
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMethodCallTranslatorPlugin, BlueTuskTimescaleDbMethodCallTranslatorPlugin>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBlueTuskLegacyAggregateTranslatorPlugin, BlueTuskTimescaleDbAggregateTranslatorPlugin>());
#endif
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(BlueTuskTimescaleDbOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"using TimescaleDB in {extension.Schema} ";

#if BLUETUSK_EF6_OR_GREATER
        public override int GetServiceProviderHashCode() =>
#else
        public override long GetServiceProviderHashCode() =>
#endif
            StringComparer.Ordinal.GetHashCode(extension.Schema);

#if BLUETUSK_EF6_OR_GREATER
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo otherInfo &&
            string.Equals(extension.Schema, otherInfo.Extension.Schema, StringComparison.Ordinal);
#endif

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["BlueTusk:TimescaleDB"] = extension.Schema;

        private new BlueTuskTimescaleDbOptionsExtension Extension =>
            (BlueTuskTimescaleDbOptionsExtension)base.Extension;
    }
}

internal sealed record BlueTuskTimescaleDbQueryOptions(string Schema);
