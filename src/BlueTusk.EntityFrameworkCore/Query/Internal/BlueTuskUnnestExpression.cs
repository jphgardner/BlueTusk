using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskUnnestExpression :
#if BLUETUSK_EF8_OR_GREATER
    TableValuedFunctionExpression
#else
    TableExpressionBase
#endif
#if BLUETUSK_EF6_OR_GREATER && !BLUETUSK_EF9_OR_GREATER
    , IClonableTableExpressionBase
#endif
{
#if BLUETUSK_EF9_OR_GREATER
    private static ConstructorInfo? _quotingConstructor;
#endif

    public BlueTuskUnnestExpression(string alias, SqlExpression array)
        : this(alias, array, annotations: null)
    {
    }

    private BlueTuskUnnestExpression(
        string alias,
        SqlExpression array,
        IReadOnlyDictionary<string, IAnnotation>? annotations)
#if BLUETUSK_EF8_OR_GREATER
        : base(alias, "unnest", schema: null, builtIn: true, [array],
#if BLUETUSK_EF9_OR_GREATER
            annotations)
#else
            annotations?.Values)
#endif
#else
        : base(alias
#if BLUETUSK_EF6_OR_GREATER
            , annotations?.Values
#endif
        )
#endif
        => Array = array;

#if !BLUETUSK_EF6_OR_GREATER
    private static IReadOnlyDictionary<string, IAnnotation>? Annotations => null;
#elif !BLUETUSK_EF9_OR_GREATER
    private IReadOnlyDictionary<string, IAnnotation> Annotations =>
        GetAnnotations().ToDictionary(annotation => annotation.Name, StringComparer.Ordinal);
#endif

    public SqlExpression Array { get; }

#if !BLUETUSK_EF8_OR_GREATER
    public new string Alias => base.Alias!;
#endif

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update((SqlExpression)visitor.Visit(Array));

    public BlueTuskUnnestExpression Update(SqlExpression array)
        => array == Array
            ? this
            : new BlueTuskUnnestExpression(Alias, array, Annotations);

#if BLUETUSK_EF11_OR_GREATER
    public override BlueTuskUnnestExpression Update(IReadOnlyList<Expression> arguments)
#elif BLUETUSK_EF8_OR_GREATER
    public override BlueTuskUnnestExpression Update(IReadOnlyList<SqlExpression> arguments)
#else
    public BlueTuskUnnestExpression Update(IReadOnlyList<SqlExpression> arguments)
#endif
        => arguments is [SqlExpression array]
            ? Update(array)
            : throw new ArgumentException("PostgreSQL unnest requires exactly one array argument.", nameof(arguments));

#if BLUETUSK_EF9_OR_GREATER
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
#elif BLUETUSK_EF6_OR_GREATER
    public TableExpressionBase Clone() => new BlueTuskUnnestExpression(Alias, Array, Annotations);

    protected override TableExpressionBase CreateWithAnnotations(IEnumerable<IAnnotation> annotations) =>
        new BlueTuskUnnestExpression(Alias, Array,
            annotations.ToDictionary(annotation => annotation.Name, StringComparer.Ordinal));
#endif

#if BLUETUSK_EF9_OR_GREATER
    public override Expression Quote()
        => New(
            _quotingConstructor ??= typeof(BlueTuskUnnestExpression).GetConstructor(
                [typeof(string), typeof(SqlExpression)])!,
            Constant(Alias),
            Array.Quote());
#endif

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("unnest(");
        expressionPrinter.Visit(Array);
        expressionPrinter.Append(") WITH ORDINALITY AS ");
        expressionPrinter.Append(Alias).Append("(value, ordinality)");
    }
}
