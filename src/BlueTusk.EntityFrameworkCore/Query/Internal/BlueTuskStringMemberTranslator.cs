using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskStringMemberTranslator(ISqlExpressionFactory sqlExpressionFactory)
    : IMemberTranslator
{
    private static readonly MemberInfo Length = typeof(string).GetRuntimeProperty(nameof(string.Length))!;

    public SqlExpression? Translate(
        SqlExpression? instance,
        MemberInfo member,
        Type returnType,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        => instance is not null && member == Length
            ? sqlExpressionFactory.Function(
                "char_length",
                [instance],
                nullable: true,
                argumentsPropagateNullability: [true],
                returnType)
            : null;
}
