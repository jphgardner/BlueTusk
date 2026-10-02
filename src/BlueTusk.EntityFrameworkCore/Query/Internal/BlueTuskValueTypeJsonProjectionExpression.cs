#pragma warning disable EF1001 // Internal EF Core API usage.
#pragma warning disable EF9100 // Precompiled-query support APIs, used exactly as EF Core uses them.

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

// A JSON complex collection, or a JSON complex value, projected on its own whose value holds a value-type complex
// collection. EF Core's shaper processor cannot build its JSON shaper (dotnet/efcore#31411); it leaves unknown nodes to
// their VisitChildren, where this node asks the processor for the reader over the same JSON column and materializes
// the value with BlueTuskValueTypeJsonMaterializer. Every other visitor sees the node unchanged.
internal sealed class BlueTuskValueTypeJsonProjectionExpression : Expression
{
    private static readonly MethodInfo ValueOrDefaultMethod
        = typeof(BlueTuskValueTypeJsonProjectionExpression).GetMethod(nameof(ValueOrDefault), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly ProjectionBindingExpression _projectionBinding;
    private readonly IComplexType _complexType;
    private readonly IComplexProperty? _collectionProperty;
    private readonly ILiftableConstantFactory _liftableConstantFactory;

    private BlueTuskValueTypeJsonProjectionExpression(
        Type type,
        ProjectionBindingExpression projectionBinding,
        IComplexType complexType,
        IComplexProperty? collectionProperty,
        ILiftableConstantFactory liftableConstantFactory)
    {
        Type = type;
        _projectionBinding = projectionBinding;
        _complexType = complexType;
        _collectionProperty = collectionProperty;
        _liftableConstantFactory = liftableConstantFactory;
    }

    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public override Type Type { get; }

    public static Expression Wrap(Expression shaper, ILiftableConstantFactory liftableConstantFactory)
        => new Wrapper(liftableConstantFactory).Visit(shaper);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        if (!ShaperProcessorAccess.IsProcessor(visitor)
            || ShaperProcessorAccess.GetProjectionIndex(visitor, _projectionBinding) is not JsonProjectionInfo jsonProjectionInfo)
        {
            return this;
        }

        var jsonReaderData = ShaperProcessorAccess.GenerateJsonReader(visitor, jsonProjectionInfo.JsonColumnIndex, _complexType);
        var materialize = _collectionProperty is null
            ? Call(
                BlueTuskValueTypeJsonMaterializer.MaterializeComplexValueMethod,
                QueryCompilationContext.QueryContextParameter,
                jsonReaderData,
                _liftableConstantFactory.CreateLiftableConstant(
                    _complexType,
                    LiftableConstantExpressionHelpers.BuildMemberAccessLambdaForStructuralType(_complexType),
                    _complexType.ShortName() + "ComplexType",
                    typeof(IComplexType)))
            : Call(
                BlueTuskValueTypeJsonMaterializer.MaterializeCollectionMethod,
                QueryCompilationContext.QueryContextParameter,
                jsonReaderData,
                _liftableConstantFactory.CreateLiftableConstant(
                    _collectionProperty,
                    LiftableConstantExpressionHelpers.BuildStructuralPropertyAccessLambda(_collectionProperty),
                    _collectionProperty.Name + "StructuralProperty",
                    typeof(IPropertyBase)));
        return Call(ValueOrDefaultMethod.MakeGenericMethod(Type), materialize);
    }

    private static T ValueOrDefault<T>(object? value)
        => value is null ? default! : (T)value;

    private sealed class Wrapper(ILiftableConstantFactory liftableConstantFactory) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            switch (node)
            {
                case CollectionResultExpression
                {
                    QueryExpression: ProjectionBindingExpression projectionBinding,
                    StructuralProperty: IComplexProperty { ComplexType: var complexType } complexProperty,
                }
                    when complexType.IsMappedToJson() && BlueTuskValueTypeJsonMaterializer.ContainsValueTypeCollection(complexProperty):
                    BlueTuskValueTypeJsonMaterializer.Prepare(complexProperty);
                    return new BlueTuskValueTypeJsonProjectionExpression(
                        node.Type, projectionBinding, complexType, complexProperty, liftableConstantFactory);

                case RelationalStructuralTypeShaperExpression
                {
                    StructuralType: IComplexType complexType,
                    ValueBufferExpression: ProjectionBindingExpression projectionBinding,
                }
                    when complexType.IsMappedToJson()
                        && complexType.GetComplexProperties().Any(BlueTuskValueTypeJsonMaterializer.ContainsValueTypeCollection):
                    BlueTuskValueTypeJsonMaterializer.Prepare(complexType);
                    return new BlueTuskValueTypeJsonProjectionExpression(
                        node.Type, projectionBinding, complexType, collectionProperty: null, liftableConstantFactory);

                default:
                    return base.VisitExtension(node);
            }
        }
    }
}
