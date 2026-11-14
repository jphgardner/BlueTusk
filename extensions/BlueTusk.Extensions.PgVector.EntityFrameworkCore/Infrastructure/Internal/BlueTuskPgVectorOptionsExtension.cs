using BlueTusk.Extensions.PgVector.EntityFrameworkCore.Query.Internal;
using BlueTusk.Extensions.PgVector.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlueTusk.Extensions.PgVector.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskPgVectorOptionsExtension(string schema) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public string Schema { get; } = schema;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.AddSingleton(new BlueTuskPgVectorTypeMappingOptions(Schema));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRelationalTypeMappingSourcePlugin, BlueTuskPgVectorTypeMappingSourcePlugin>());
        services.TryAddEnumerable(
#if BLUETUSK_EF6_OR_GREATER
            ServiceDescriptor.Scoped<IMethodCallTranslatorPlugin, BlueTuskPgVectorMethodCallTranslatorPlugin>());
#else
            ServiceDescriptor.Singleton<IMethodCallTranslatorPlugin, BlueTuskPgVectorMethodCallTranslatorPlugin>());
#endif
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(BlueTuskPgVectorOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"using pgvector in {extension.Schema} ";

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
            debugInfo["BlueTusk:PgVector"] = extension.Schema;

        private new BlueTuskPgVectorOptionsExtension Extension =>
            (BlueTuskPgVectorOptionsExtension)base.Extension;
    }
}
