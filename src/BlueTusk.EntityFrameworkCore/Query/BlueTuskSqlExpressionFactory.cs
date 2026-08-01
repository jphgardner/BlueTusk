using BlueTusk.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query;

/// <summary>Creates PostgreSQL-specific SQL expressions for optional provider plugins.</summary>
public static class BlueTuskSqlExpressionFactory
{
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
}
