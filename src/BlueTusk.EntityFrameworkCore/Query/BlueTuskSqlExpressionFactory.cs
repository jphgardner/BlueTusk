using BlueTusk.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query;

/// <summary>Creates PostgreSQL-specific SQL expressions for optional provider plugins.</summary>
public static class BlueTuskSqlExpressionFactory
{
    /// <summary>Creates a schema-safe PostgreSQL aggregate expression for an optional provider plugin.</summary>
    public static SqlExpression AggregateFunction(
        string? schema,
        string name,
        IReadOnlyList<SqlExpression> arguments,
        bool isDistinct,
        IReadOnlyList<OrderingExpression> orderings,
        IReadOnlyList<OrderingExpression> withinGroupOrderings,
        SqlExpression? predicate,
        Type resultType,
        RelationalTypeMapping resultTypeMapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(orderings);
        ArgumentNullException.ThrowIfNull(withinGroupOrderings);
        ArgumentNullException.ThrowIfNull(resultType);
        ArgumentNullException.ThrowIfNull(resultTypeMapping);
        ValidateIdentifier(name, nameof(name));
        if (schema is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);
            ValidateIdentifier(schema, nameof(schema));
        }
        else if (!IsSafeUnquotedIdentifier(name))
        {
            throw new ArgumentException(
                "An unqualified PostgreSQL aggregate name must be a lowercase ASCII identifier.",
                nameof(name));
        }

        return new BlueTuskAggregateExpression(
            schema,
            name,
            arguments,
            isDistinct,
            orderings,
            withinGroupOrderings,
            predicate,
            resultType,
            resultTypeMapping);
    }

    /// <summary>Exposes the typed fields of a provider row-value expression to optional translator plugins.</summary>
    public static bool TryGetRowValue(
        SqlExpression expression,
        out IReadOnlyList<SqlExpression> values)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression is BlueTuskRowValueExpression row)
        {
            values = row.Values;
            return true;
        }

        values = [];
        return false;
    }

    /// <summary>Creates a binary PostgreSQL operator expression from a trusted operator token.</summary>
    public static SqlExpression BinaryOperator(
        SqlExpression left,
        SqlExpression right,
        string operatorToken,
        Type resultType,
        RelationalTypeMapping resultTypeMapping)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorToken);
        ArgumentNullException.ThrowIfNull(resultType);
        ArgumentNullException.ThrowIfNull(resultTypeMapping);
        if (operatorToken.Length > 63 || !operatorToken.All(IsOperatorCharacter))
        {
            throw new ArgumentException(
                "A PostgreSQL operator token can contain only PostgreSQL operator characters.",
                nameof(operatorToken));
        }

        return new BlueTuskBinaryExpression(
            left,
            right,
            operatorToken,
            resultType,
            resultTypeMapping);
    }

    private static bool IsOperatorCharacter(char value) =>
        value is '+' or '-' or '*' or '/' or '<' or '>' or '=' or '~' or '!' or '@' or '#'
            or '%' or '^' or '&' or '|' or '`' or '?';

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A PostgreSQL identifier cannot contain a null character.",
                parameterName);
        }
    }

    private static bool IsSafeUnquotedIdentifier(string value) =>
        value.Length > 0 &&
        (value[0] is >= 'a' and <= 'z' or '_') &&
        value.Skip(1).All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '$');
}
