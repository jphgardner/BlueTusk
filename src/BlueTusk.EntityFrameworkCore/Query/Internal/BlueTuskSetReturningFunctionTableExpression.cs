using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskSetReturningFunctionTableExpression : TableValuedFunctionExpression
{
    private static ConstructorInfo? _quotingConstructor;

    public BlueTuskSetReturningFunctionTableExpression(
        string alias,
        string name,
        IReadOnlyList<SqlExpression> arguments,
        bool withOrdinality)
        : this(alias, name, arguments, withOrdinality, annotations: null)
    {
    }

    private BlueTuskSetReturningFunctionTableExpression(
        string alias,
        string name,
        IReadOnlyList<SqlExpression> arguments,
        bool withOrdinality,
        IReadOnlyDictionary<string, IAnnotation>? annotations)
        : base(alias, name, schema: null, builtIn: true, arguments, annotations)
        => WithOrdinality = withOrdinality;

    public bool WithOrdinality { get; }

    public override BlueTuskSetReturningFunctionTableExpression Update(
        IReadOnlyList<SqlExpression> arguments)
        => arguments.Count == Arguments.Count
            && arguments.Zip(Arguments, (left, right) => left == right).All(equal => equal)
                ? this
                : new BlueTuskSetReturningFunctionTableExpression(
                    Alias,
                    Name,
                    arguments,
                    WithOrdinality,
                    Annotations);

    public override TableExpressionBase Clone(
        string? alias,
        ExpressionVisitor cloningExpressionVisitor)
        => new BlueTuskSetReturningFunctionTableExpression(
            alias!,
            Name,
            Arguments
                .Select(argument => (SqlExpression)cloningExpressionVisitor.Visit(argument))
                .ToArray(),
            WithOrdinality,
            Annotations);

    protected override TableValuedFunctionExpression WithAnnotations(
        IReadOnlyDictionary<string, IAnnotation> annotations)
        => new BlueTuskSetReturningFunctionTableExpression(
            Alias,
            Name,
            Arguments,
            WithOrdinality,
            annotations);

    public override BlueTuskSetReturningFunctionTableExpression WithAlias(string newAlias)
        => new(newAlias, Name, Arguments, WithOrdinality, Annotations);

    public override Expression Quote()
        => New(
            _quotingConstructor ??= typeof(BlueTuskSetReturningFunctionTableExpression).GetConstructor(
                [typeof(string), typeof(string), typeof(IReadOnlyList<SqlExpression>), typeof(bool)])!,
            Constant(Alias),
            Constant(Name),
            NewArrayInit(typeof(SqlExpression), Arguments.Select(argument => argument.Quote())),
            Constant(WithOrdinality));

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append(Name).Append("(");
        for (var index = 0; index < Arguments.Count; index++)
        {
            if (index > 0)
            {
                expressionPrinter.Append(", ");
            }

            expressionPrinter.Visit(Arguments[index]);
        }

        expressionPrinter.Append(")");
        if (WithOrdinality)
        {
            expressionPrinter.Append(" WITH ORDINALITY");
        }

        expressionPrinter.Append(" AS ").Append(Alias).Append("(value");
        if (WithOrdinality)
        {
            expressionPrinter.Append(", ordinality");
        }

        expressionPrinter.Append(")");
    }
}
