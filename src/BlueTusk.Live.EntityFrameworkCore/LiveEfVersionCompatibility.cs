using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.Live.EntityFrameworkCore;

internal static class LiveEfVersionCompatibility
{
    public static ValueTask<TContext> OpenContextAsync<TContext>(
        IDbContextFactory<TContext> factory,
        CancellationToken cancellationToken)
        where TContext : DbContext
    {
#if NET5_0
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<TContext>(factory.CreateDbContext());
#else
        return new ValueTask<TContext>(factory.CreateDbContextAsync(cancellationToken));
#endif
    }

    public static bool HasDeclaredQueryFilter(IEntityType entityType)
    {
#if NET10_0_OR_GREATER
        return entityType.GetDeclaredQueryFilters().Count != 0;
#else
        return entityType.GetQueryFilter() is not null;
#endif
    }
}
