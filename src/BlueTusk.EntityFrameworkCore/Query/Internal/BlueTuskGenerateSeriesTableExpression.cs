using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskGenerateSeriesTableExpression : TableValuedFunctionExpression
{
    private static ConstructorInfo? _quotingConstructor;

    public BlueTuskGenerateSeriesTableExpression(
        string alias,
        IReadOnlyList<SqlExpression> arguments)
        : this(alias, arguments, annotations: null)
    {
    }

    private BlueTuskGenerateSeriesTableExpression(
        string alias,
        IReadOnlyList<SqlExpression> arguments,
        IReadOnlyDictionary<string, IAnnotation>? annotations)
        : base(alias, "generate_series", schema: null, builtIn: true, arguments, annotations)
    {
    }

    public override BlueTuskGenerateSeriesTableExpression Update(
        IReadOnlyList<SqlExpression> arguments)
        => arguments.Count == Arguments.Count
            && arguments.Zip(Arguments, (left, right) => left == right).All(equal => equal)
                ? this
                : new BlueTuskGenerateSeriesTableExpression(Alias, arguments, Annotations);

    public override TableExpressionBase Clone(
        string? alias,
        ExpressionVisitor cloningExpressionVisitor)
        => new BlueTuskGenerateSeriesTableExpression(
            alias!,
            Arguments
                .Select(argument => (SqlExpression)cloningExpressionVisitor.Visit(argument))
                .ToArray(),
            Annotations);

    protected override TableValuedFunctionExpression WithAnnotations(
        IReadOnlyDictionary<string, IAnnotation> annotations)
        => new BlueTuskGenerateSeriesTableExpression(Alias, Arguments, annotations);

    public override BlueTuskGenerateSeriesTableExpression WithAlias(string newAlias)
        => new(newAlias, Arguments, Annotations);

    public override Expression Quote()
        => New(
            _quotingConstructor ??= typeof(BlueTuskGenerateSeriesTableExpression).GetConstructor(
                [typeof(string), typeof(IReadOnlyList<SqlExpression>)])!,
            Constant(Alias),
            NewArrayInit(typeof(SqlExpression), Arguments.Select(argument => argument.Quote())));

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("generate_series(");
        for (var index = 0; index < Arguments.Count; index++)
        {
            if (index > 0)
            {
                expressionPrinter.Append(", ");
            }

            expressionPrinter.Visit(Arguments[index]);
        }

        expressionPrinter.Append(") AS ").Append(Alias).Append("(value)");
    }
}
