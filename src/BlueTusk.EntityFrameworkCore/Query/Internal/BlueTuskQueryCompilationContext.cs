using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

#pragma warning disable EF9100 // Provider query compilation must opt split queries into EF's buffered-reader path.
internal sealed class BlueTuskQueryCompilationContext : RelationalQueryCompilationContext
{
    internal BlueTuskQueryCompilationContext(
        QueryCompilationContextDependencies dependencies,
        RelationalQueryCompilationContextDependencies relationalDependencies,
        bool async,
        bool precompiling = false)
        : base(dependencies, relationalDependencies, async, precompiling)
    {
    }

    public override bool IsBuffering =>
        base.IsBuffering || QuerySplittingBehavior == Microsoft.EntityFrameworkCore.QuerySplittingBehavior.SplitQuery;
}
#pragma warning restore EF9100
