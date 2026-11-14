using BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Query.Internal;
using BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskPostGisOptionsExtension(string schema) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public string Schema { get; } = schema;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.AddSingleton(new BlueTuskPostGisTypeMappingOptions(Schema));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRelationalTypeMappingSourcePlugin, BlueTuskPostGisTypeMappingSourcePlugin>());
#if BLUETUSK_EF6_OR_GREATER
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IMethodCallTranslatorPlugin, BlueTuskPostGisMethodCallTranslatorPlugin>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IMemberTranslatorPlugin, BlueTuskPostGisMemberTranslatorPlugin>());
#else
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMethodCallTranslatorPlugin, BlueTuskPostGisMethodCallTranslatorPlugin>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMemberTranslatorPlugin, BlueTuskPostGisMemberTranslatorPlugin>());
#endif
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(BlueTuskPostGisOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"using PostGIS in {extension.Schema} ";

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
            debugInfo["BlueTusk:PostGIS"] = extension.Schema;

        private new BlueTuskPostGisOptionsExtension Extension =>
            (BlueTuskPostGisOptionsExtension)base.Extension;
    }
}
