using System.Reflection;
using System.Text;
using BlueTusk.EntityFrameworkCore.Storage.Internal;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskCompositeMemberTranslator(
    IRelationalTypeMappingSource typeMappingSource)
    : IMemberTranslator
{
    public SqlExpression? Translate(
        SqlExpression? instance,
        MemberInfo member,
        Type returnType,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (instance?.TypeMapping is not BlueTuskUserDefinedTypeMapping
            || member.DeclaringType is null
            || !member.DeclaringType.IsAssignableFrom(instance.Type)
            || member is not PropertyInfo and not FieldInfo)
        {
            return null;
        }

        var resultType = Nullable.GetUnderlyingType(returnType) ?? returnType;
        var resultMapping = typeMappingSource.FindMapping(member)
            ?? typeMappingSource.FindMapping(resultType)
            ?? throw new InvalidOperationException(
                $"PostgreSQL composite field '{member.Name}' has no relational mapping for CLR type '{returnType.Name}'.");
        return new BlueTuskCompositeFieldExpression(
            instance,
            BlueTuskCompositeFieldName.Get(member),
            resultType,
            resultMapping);
    }
}

internal sealed class BlueTuskRecordFieldTranslator(
    IRelationalTypeMappingSource typeMappingSource)
    : IMethodCallTranslator
{
    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions)
            || method.Name != nameof(BlueTuskDbFunctionsExtensions.RecordField))
        {
            return null;
        }

        if (arguments[1].TypeMapping is not BlueTuskUserDefinedTypeMapping)
        {
            throw new InvalidOperationException(
                "RecordField requires a BlueTuskRecord property mapped to a schema-qualified PostgreSQL composite type.");
        }

        if (arguments[2] is not SqlConstantExpression { Value: string fieldName })
        {
            throw new InvalidOperationException(
                "RecordField requires a constant PostgreSQL composite field name.");
        }

        BlueTuskCompositeFieldName.Validate(fieldName);
        var resultType = Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType;
        var resultMapping = typeMappingSource.FindMapping(resultType)
            ?? throw new InvalidOperationException(
                $"RecordField has no relational mapping for CLR type '{method.ReturnType.Name}'.");
        return new BlueTuskCompositeFieldExpression(
            arguments[1],
            fieldName,
            resultType,
            resultMapping);
    }
}

internal static class BlueTuskCompositeFieldName
{
    public static string Get(MemberInfo member)
    {
        var fieldName = member.GetCustomAttribute<BlueTuskNameAttribute>()?.Name
            ?? ToSnakeCase(member.Name);
        Validate(fieldName);
        return fieldName;
    }

    public static void Validate(string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        if (fieldName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A PostgreSQL composite field name cannot contain a null character.",
                nameof(fieldName));
        }

        if (Encoding.UTF8.GetByteCount(fieldName) > 63)
        {
            throw new ArgumentException(
                "A PostgreSQL composite field name cannot exceed 63 UTF-8 bytes.",
                nameof(fieldName));
        }
    }

    private static string ToSnakeCase(string name)
    {
        var result = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index != 0
                && (char.IsLower(name[index - 1])
                    || char.IsDigit(name[index - 1])
                    || index + 1 < name.Length && char.IsLower(name[index + 1])))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}
