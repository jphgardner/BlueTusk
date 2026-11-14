using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskModelCustomizer(ModelCustomizerDependencies dependencies)
    : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        var graphs = BlueTuskPropertyGraphMetadata.Get(modelBuilder.Model);
        if (graphs.Count == 0)
        {
            return;
        }

        var backend = BlueTuskOptionsExtension.GetGraphBackend(context.GetService<IDbContextOptions>());
        modelBuilder.Model.SetAnnotation(
            BlueTuskPropertyGraphMetadata.AnnotationName,
            BlueTuskPropertyGraphMetadata.Serialize(graphs.Select(graph => graph with { Backend = backend })));
    }
}

internal sealed class BlueTuskModelCacheKeyFactory : IModelCacheKeyFactory
{
#if BLUETUSK_EF6_OR_GREATER
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), designTime, BlueTuskOptionsExtension.GetGraphBackend(context.GetService<IDbContextOptions>()));
#else
    public object Create(DbContext context)
    {
        var options = context.GetService<IDbContextOptions>();
        return (context.GetType(), BlueTuskOptionsExtension.GetGraphBackend(options),
            options.FindExtension<BlueTuskOptionsExtension>()?.DataSource?.TypeRegistry);
    }
#endif
}
