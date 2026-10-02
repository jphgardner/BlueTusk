#pragma warning disable EF9100 // Precompiled-query support APIs, used exactly as EF Core uses them.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskShapedQueryCompilingExpressionVisitor : RelationalShapedQueryCompilingExpressionVisitor
{
    private readonly BlueTuskStructuralTypeMaterializerInjector _materializerInjector;
    private readonly BlueTuskMaterializationConditionConstantLifter _materializationConditionConstantLifter;

    public BlueTuskShapedQueryCompilingExpressionVisitor(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _materializerInjector = new BlueTuskStructuralTypeMaterializerInjector(
            this,
            dependencies.EntityMaterializerSource,
            dependencies.LiftableConstantFactory,
            queryCompilationContext.QueryTrackingBehavior,
            queryCompilationContext.SupportsPrecompiledQuery);
        _materializationConditionConstantLifter = new BlueTuskMaterializationConditionConstantLifter(dependencies.LiftableConstantFactory);
    }

    protected override Expression InjectStructuralTypeMaterializers(Expression expression)
    {
        VerifyNoClientConstant(expression);

        var materializerExpression = _materializerInjector.Inject(expression);
        return QueryCompilationContext.SupportsPrecompiledQuery
            ? _materializationConditionConstantLifter.Visit(materializerExpression)
            : materializerExpression;
    }
}
