using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQueryTranslationPreprocessorFactory(
    QueryTranslationPreprocessorDependencies dependencies,
    RelationalQueryTranslationPreprocessorDependencies relationalDependencies)
    : IQueryTranslationPreprocessorFactory
{
    public QueryTranslationPreprocessor Create(QueryCompilationContext queryCompilationContext)
        => new BlueTuskQueryTranslationPreprocessor(
            dependencies,
            relationalDependencies,
            queryCompilationContext);
}
