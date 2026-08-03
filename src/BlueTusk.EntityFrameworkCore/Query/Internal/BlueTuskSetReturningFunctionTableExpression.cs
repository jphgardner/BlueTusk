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
        IReadOnlyList<string> columnNames,
        bool withOrdinality)
        : this(
            alias,
            name,
            arguments,
            columnNames,
            Enumerable.Repeat<string?>(null, columnNames.Count).ToArray(),
            withOrdinality,
            annotations: null)
    {
    }

    public BlueTuskSetReturningFunctionTableExpression(
        string alias,
        string name,
        IReadOnlyList<SqlExpression> arguments,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<string?> columnStoreTypes,
        bool withOrdinality)
        : this(
            alias,
            name,
            arguments,
            columnNames,
            columnStoreTypes,
            withOrdinality,
            annotations: null)
    {
    }

    private BlueTuskSetReturningFunctionTableExpression(
        string alias,
        string name,
        IReadOnlyList<SqlExpression> arguments,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<string?> columnStoreTypes,
        bool withOrdinality,
        IReadOnlyDictionary<string, IAnnotation>? annotations)
        : base(alias, name, schema: null, builtIn: true, arguments, annotations)
    {
        if (columnNames.Count != columnStoreTypes.Count)
        {
            throw new ArgumentException("Every output column must have a matching store type entry.");
        }

        ColumnNames = columnNames;
        ColumnStoreTypes = columnStoreTypes;
        WithOrdinality = withOrdinality;
    }

    public IReadOnlyList<string> ColumnNames { get; }

    public IReadOnlyList<string?> ColumnStoreTypes { get; }

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
                    ColumnNames,
                    ColumnStoreTypes,
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
            ColumnNames,
            ColumnStoreTypes,
            WithOrdinality,
            Annotations);

    protected override TableValuedFunctionExpression WithAnnotations(
        IReadOnlyDictionary<string, IAnnotation> annotations)
        => new BlueTuskSetReturningFunctionTableExpression(
            Alias,
            Name,
            Arguments,
            ColumnNames,
            ColumnStoreTypes,
            WithOrdinality,
            annotations);

    public override BlueTuskSetReturningFunctionTableExpression WithAlias(string newAlias)
        => new(
            newAlias,
            Name,
            Arguments,
            ColumnNames,
            ColumnStoreTypes,
            WithOrdinality,
            Annotations);

    public override Expression Quote()
        => New(
            _quotingConstructor ??= typeof(BlueTuskSetReturningFunctionTableExpression).GetConstructor(
                [
                    typeof(string),
                    typeof(string),
                    typeof(IReadOnlyList<SqlExpression>),
                    typeof(IReadOnlyList<string>),
                    typeof(IReadOnlyList<string>),
                    typeof(bool),
                ])!,
            Constant(Alias),
            Constant(Name),
            NewArrayInit(typeof(SqlExpression), Arguments.Select(argument => argument.Quote())),
            NewArrayInit(typeof(string), ColumnNames.Select(Constant)),
            NewArrayInit(
                typeof(string),
                ColumnStoreTypes.Select(storeType => Constant(storeType, typeof(string)))),
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

        expressionPrinter.Append(" AS ").Append(Alias).Append("(");
        for (var index = 0; index < ColumnNames.Count; index++)
        {
            if (index > 0)
            {
                expressionPrinter.Append(", ");
            }

            expressionPrinter.Append(ColumnNames[index]);
            if (ColumnStoreTypes[index] is { } storeType)
            {
                expressionPrinter.Append(" ").Append(storeType);
            }
        }

        if (WithOrdinality)
        {
            if (ColumnNames.Count > 0)
            {
                expressionPrinter.Append(", ");
            }

            expressionPrinter.Append("ordinality");
        }

        expressionPrinter.Append(")");
    }
}
