#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using static System.Linq.Expressions.Expression;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

// For JSON-mapped complex types with shadow properties, reads each shadow value inside the materializer block (the JSON
// shaper turns `variable = ValueBufferTryReadValue(property)` into streamed JSON values) and records them against the
// complex instance for BlueTuskComplexShadowValues. Every other structural type is materialized exactly as EF Core does.
internal sealed class BlueTuskStructuralTypeMaterializerSource(StructuralTypeMaterializerSourceDependencies dependencies)
    : RelationalStructuralTypeMaterializerSource(dependencies), IStructuralTypeMaterializerSource
{
    Expression IStructuralTypeMaterializerSource.CreateMaterializeExpression(
        StructuralTypeMaterializerSourceParameters parameters,
        Expression materializationContextExpression)
    {
        var materializer = CreateMaterializeExpression(parameters, materializationContextExpression);
        if (parameters.StructuralType is not IComplexType complexType)
        {
            return materializer;
        }

        var recordedProperties = BlueTuskComplexShadowValues.GetRecordedProperties(complexType);
        if (recordedProperties.Count == 0)
        {
            return materializer;
        }

        if (materializer is not BlockExpression { Expressions: [.., ParameterExpression instanceVariable] } block)
        {
            var instance = Variable(materializer.Type, parameters.InstanceName);
            block = Block([instance], Assign(instance, materializer), instance);
            instanceVariable = instance;
        }

        var valueBuffer = Call(materializationContextExpression, MaterializationContext.GetValueBufferMethod);
        var valueVariables = recordedProperties.Select(p => Variable(p.ClrType, "shadow" + p.Name)).ToArray();
        var expressions = block.Expressions.Take(block.Expressions.Count - 1).ToList();
        for (var i = 0; i < recordedProperties.Count; i++)
        {
            var property = recordedProperties[i];
            expressions.Add(Assign(valueVariables[i], valueBuffer.CreateValueBufferReadValueExpression(property.ClrType, property.GetIndex(), property)));
        }

        expressions.Add(
            Call(
                BlueTuskComplexShadowValues.RecordMethod,
                Convert(instanceVariable, typeof(object)),
                NewArrayInit(typeof(object), valueVariables.Select(v => Convert(v, typeof(object))))));
        expressions.Add(instanceVariable);
        return Block(block.Variables.Concat(valueVariables), expressions);
    }
}
