#pragma warning disable EF9100 // Precompiled-query support APIs, used exactly as EF Core uses them.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using static System.Linq.Expressions.Expression;

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

    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        var shaper = BlueTuskValueTypeJsonProjectionExpression.Wrap(
            shapedQueryExpression.ShaperExpression,
            Dependencies.LiftableConstantFactory);
        return base.VisitShapedQuery(
            shaper == shapedQueryExpression.ShaperExpression
                ? shapedQueryExpression
                : shapedQueryExpression.UpdateShaperExpression(shaper));
    }

    protected override Expression InjectStructuralTypeMaterializers(Expression expression)
    {
        VerifyNoClientConstant(expression);

        var materializerExpression = _materializerInjector.Inject(expression);
        return QueryCompilationContext.SupportsPrecompiledQuery
            ? _materializationConditionConstantLifter.Visit(materializerExpression)
            : materializerExpression;
    }

    // EF Core injects the JSON shapers of every top-level JSON complex property here, and fails for any whose value holds a
    // value-type complex collection. Those properties are withheld from EF Core's pass for the duration of the call and are
    // read by BlueTuskValueTypeJsonMaterializer from the same JSON column reader EF Core would have used.
    public override void AddStructuralTypeInitialization(
        StructuralTypeShaperExpression shaper,
        ParameterExpression instanceVariable,
        List<ParameterExpression> variables,
        List<Expression> expressions)
    {
        if (!BlueTuskValueTypeJsonMaterializer.ContainsValueTypeCollection(shaper.StructuralType)
            || shaper is not RelationalStructuralTypeShaperExpression { ValueBufferExpression: ProjectionBindingExpression projectionBinding })
        {
            base.AddStructuralTypeInitialization(shaper, instanceVariable, variables, expressions);
            return;
        }

        var shaperProcessor = ShaperProcessorAccess.Current(this);
        if (ShaperProcessorAccess.GetProjectionIndex(shaperProcessor, projectionBinding) is not Dictionary<IPropertyBase, int> propertyMap)
        {
            base.AddStructuralTypeInitialization(shaper, instanceVariable, variables, expressions);
            return;
        }

        var projections = propertyMap.ToArray();
        var valueTypeJsonProperties = projections
            .Where(p => p.Key is IComplexProperty complexProperty
                && complexProperty.ComplexType.IsMappedToJson()
                && BlueTuskValueTypeJsonMaterializer.ContainsValueTypeCollection(complexProperty))
            .ToArray();
        foreach (var (property, _) in valueTypeJsonProperties)
        {
            propertyMap.Remove(property);
        }

        try
        {
            base.AddStructuralTypeInitialization(shaper, instanceVariable, variables, expressions);
        }
        finally
        {
            propertyMap.Clear();
            foreach (var (property, projectionIndex) in projections)
            {
                propertyMap.Add(property, projectionIndex);
            }
        }

        foreach (var (property, projectionIndex) in valueTypeJsonProperties)
        {
            var complexProperty = (IComplexProperty)property;
            BlueTuskValueTypeJsonMaterializer.Prepare(complexProperty);
            var jsonReaderData = ShaperProcessorAccess.GenerateJsonReader(shaperProcessor, projectionIndex, complexProperty.ComplexType);
            var include = Call(
                BlueTuskValueTypeJsonMaterializer.IncludeMethod,
                QueryCompilationContext.QueryContextParameter,
                jsonReaderData,
                Convert(instanceVariable, typeof(object)),
                Dependencies.LiftableConstantFactory.CreateLiftableConstant(
                    complexProperty,
                    LiftableConstantExpressionHelpers.BuildStructuralPropertyAccessLambda(complexProperty),
                    complexProperty.Name + "StructuralProperty",
                    typeof(IPropertyBase)));
            expressions.Add(
                instanceVariable.Type.IsValueType
                    ? Assign(instanceVariable, Convert(include, instanceVariable.Type))
                    : include);
        }
    }
}
