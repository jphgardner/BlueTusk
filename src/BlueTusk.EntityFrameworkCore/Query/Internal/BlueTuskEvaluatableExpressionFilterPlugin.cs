using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskEvaluatableExpressionFilterPlugin : IEvaluatableExpressionFilterPlugin
{
    public bool IsEvaluatableExpression(Expression expression)
        => expression is not MethodCallExpression methodCall
            || methodCall.Method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions)
                && !(methodCall.Method.DeclaringType == typeof(BlueTuskQueryableExtensions)
                    && methodCall.Method.Name == nameof(BlueTuskQueryableExtensions.InsertValueCore))
                && !(methodCall.Method.DeclaringType == typeof(ValueTuple)
                    && methodCall.Method is { IsStatic: true, Name: nameof(ValueTuple.Create) });
}
