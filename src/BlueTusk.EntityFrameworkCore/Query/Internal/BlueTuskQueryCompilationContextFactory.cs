using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQueryCompilationContextFactory : IQueryCompilationContextFactory
{
    private readonly QueryCompilationContextDependencies _dependencies;
    private readonly RelationalQueryCompilationContextDependencies _relationalDependencies;

    public BlueTuskQueryCompilationContextFactory(
        QueryCompilationContextDependencies dependencies,
        RelationalQueryCompilationContextDependencies relationalDependencies)
    {
        _dependencies = dependencies;
        _relationalDependencies = relationalDependencies;
    }

    public QueryCompilationContext Create(bool async) =>
        new BlueTuskQueryCompilationContext(_dependencies, _relationalDependencies, async);

    public QueryCompilationContext CreatePrecompiled(bool async) =>
        new BlueTuskQueryCompilationContext(
            _dependencies,
            _relationalDependencies,
            async,
            precompiling: true);
}
