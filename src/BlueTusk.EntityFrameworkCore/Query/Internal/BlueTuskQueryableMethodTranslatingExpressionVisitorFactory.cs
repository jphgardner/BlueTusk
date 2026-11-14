using Microsoft.EntityFrameworkCore.Query;
#if !BLUETUSK_EF7_OR_GREATER
using Microsoft.EntityFrameworkCore.Storage;
#endif

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQueryableMethodTranslatingExpressionVisitorFactory(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies
#if !BLUETUSK_EF7_OR_GREATER
    , IRelationalTypeMappingSource typeMappingSource
#endif
)
    : IQueryableMethodTranslatingExpressionVisitorFactory
{
    public QueryableMethodTranslatingExpressionVisitor Create(
        QueryCompilationContext queryCompilationContext)
        => new BlueTuskQueryableMethodTranslatingExpressionVisitor(
            dependencies,
            relationalDependencies,
            (RelationalQueryCompilationContext)queryCompilationContext
#if !BLUETUSK_EF7_OR_GREATER
            , typeMappingSource
#endif
        );
}
