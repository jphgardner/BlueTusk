using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskEvaluatableExpressionFilterPlugin : IEvaluatableExpressionFilterPlugin
{
    public bool IsEvaluatableExpression(Expression expression)
        => expression is not MethodCallExpression
        {
            Method.DeclaringType: { } declaringType,
        } || declaringType != typeof(BlueTuskDbFunctionsExtensions);
}
