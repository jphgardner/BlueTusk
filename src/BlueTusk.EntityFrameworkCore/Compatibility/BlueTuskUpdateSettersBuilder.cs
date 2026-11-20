#if !BLUETUSK_EF10_OR_GREATER
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace BlueTusk.EntityFrameworkCore;

/// <summary>Builds PostgreSQL UPDATE RETURNING setters on EF Core versions before 10.</summary>
public sealed class BlueTuskUpdateSettersBuilder<TSource>
{
    private readonly List<NewExpression> _setters = [];

    public BlueTuskUpdateSettersBuilder<TSource> SetProperty<TProperty>(
        Expression<Func<TSource, TProperty>> propertySelector,
        Expression<Func<TSource, TProperty>> valueSelector)
    {
        BlueTuskGuard.NotNull(propertySelector);
        BlueTuskGuard.NotNull(valueSelector);
        AddSetter(propertySelector, valueSelector);
        return this;
    }

    public BlueTuskUpdateSettersBuilder<TSource> SetProperty<TProperty>(
        Expression<Func<TSource, TProperty>> propertySelector,
        TProperty value)
    {
        BlueTuskGuard.NotNull(propertySelector);
        AddSetter(propertySelector, Expression.Constant(value, typeof(TProperty)));
        return this;
    }

    internal NewArrayExpression BuildSettersExpression() => Expression.NewArrayInit(typeof(ITuple), _setters);

    private void AddSetter(LambdaExpression propertySelector, Expression valueSelector)
    {
        _setters.Add(Expression.New(
            typeof(Tuple<Delegate, object>).GetConstructors().Single(),
            propertySelector,
            Expression.Convert(valueSelector, typeof(object))));
    }
}
#endif
