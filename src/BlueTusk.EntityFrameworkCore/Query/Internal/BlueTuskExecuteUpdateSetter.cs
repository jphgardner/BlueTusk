#if !BLUETUSK_EF10_OR_GREATER
using System.Linq.Expressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal readonly record struct ExecuteUpdateSetter(LambdaExpression PropertySelector, Expression ValueExpression);
#endif
