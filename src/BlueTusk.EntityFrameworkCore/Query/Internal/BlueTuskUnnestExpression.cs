using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskUnnestExpression : TableValuedFunctionExpression
{
    private static ConstructorInfo? _quotingConstructor;

    public BlueTuskUnnestExpression(string alias, SqlExpression array)
        : this(alias, array, annotations: null)
    {
    }

    private BlueTuskUnnestExpression(
        string alias,
        SqlExpression array,
        IReadOnlyDictionary<string, IAnnotation>? annotations)
        : base(alias, "unnest", schema: null, builtIn: true, [array], annotations)
        => Array = array;

    public SqlExpression Array { get; }

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update((SqlExpression)visitor.Visit(Array));

    public BlueTuskUnnestExpression Update(SqlExpression array)
        => array == Array
            ? this
            : new BlueTuskUnnestExpression(Alias, array, Annotations);

    public override BlueTuskUnnestExpression Update(IReadOnlyList<SqlExpression> arguments)
        => arguments is [var array]
            ? Update(array)
            : throw new ArgumentException("PostgreSQL unnest requires exactly one array argument.", nameof(arguments));

    public override TableExpressionBase Clone(
        string? alias,
        ExpressionVisitor cloningExpressionVisitor)
        => new BlueTuskUnnestExpression(
            alias!,
            (SqlExpression)cloningExpressionVisitor.Visit(Array),
            Annotations);

    protected override TableValuedFunctionExpression WithAnnotations(
        IReadOnlyDictionary<string, IAnnotation> annotations)
        => new BlueTuskUnnestExpression(Alias, Array, annotations);

    public override BlueTuskUnnestExpression WithAlias(string newAlias)
        => new(newAlias, Array, Annotations);

    public override Expression Quote()
        => New(
            _quotingConstructor ??= typeof(BlueTuskUnnestExpression).GetConstructor(
                [typeof(string), typeof(SqlExpression)])!,
            Constant(Alias),
            Array.Quote());

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("unnest(");
        expressionPrinter.Visit(Array);
        expressionPrinter.Append(") WITH ORDINALITY AS ");
        expressionPrinter.Append(Alias).Append("(value, ordinality)");
    }
}
