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
    BlueTuskCompositeFieldMappingResolver fieldMappingResolver)
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

        var fieldName = BlueTuskCompositeFieldName.Get(member);
        var resultType = Nullable.GetUnderlyingType(returnType) ?? returnType;
        var resultMapping = fieldMappingResolver.Resolve(
            instance.TypeMapping,
            fieldName,
            resultType,
            member);
        return new BlueTuskCompositeFieldExpression(
            instance,
            fieldName,
            resultType,
            resultMapping);
    }
}

internal sealed class BlueTuskRecordFieldTranslator(
    BlueTuskCompositeFieldMappingResolver fieldMappingResolver)
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

        if (arguments[1].TypeMapping is not BlueTuskUserDefinedTypeMapping recordMapping)
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
        var resultMapping = fieldMappingResolver.Resolve(
            recordMapping,
            fieldName,
            resultType);
        return new BlueTuskCompositeFieldExpression(
            arguments[1],
            fieldName,
            resultType,
            resultMapping);
    }
}

internal sealed class BlueTuskCompositeFieldMappingResolver(
    IRelationalTypeMappingSource typeMappingSource,
    ISqlGenerationHelper sqlGenerationHelper,
    BlueTuskTypeRegistry? typeRegistry)
{
    public RelationalTypeMapping Resolve(
        RelationalTypeMapping compositeMapping,
        string fieldName,
        Type resultType,
        MemberInfo? member = null)
    {
        if (TryResolveCatalogueMapping(compositeMapping, fieldName, resultType, out var mapping))
        {
            return mapping;
        }

        return member is null
            ? typeMappingSource.FindMapping(resultType)
                ?? throw MissingMapping(fieldName, resultType)
            : typeMappingSource.FindMapping(member)
                ?? typeMappingSource.FindMapping(resultType)
                ?? throw MissingMapping(fieldName, resultType);
    }

    private bool TryResolveCatalogueMapping(
        RelationalTypeMapping compositeMapping,
        string fieldName,
        Type resultType,
        out RelationalTypeMapping mapping)
    {
        mapping = null!;
        var registry = typeRegistry;
        if (registry is null
            || !TryParseTypeName(compositeMapping.StoreType, out var compositeName)
            || !registry.TryGetType(compositeName, out var compositeType, out _)
            || compositeType?.Kind != BlueTuskTypeKind.Composite)
        {
            return false;
        }

        var field = compositeType.CompositeFields.FirstOrDefault(
            candidate => string.Equals(candidate.Name, fieldName, StringComparison.Ordinal));
        if (field is null)
        {
            throw new InvalidOperationException(
                $"PostgreSQL composite '{compositeMapping.StoreType}' has no field named '{fieldName}'.");
        }

        if (!registry.TryGetType(field.Type, out var fieldType)
            || fieldType is null)
        {
            throw new InvalidOperationException(
                $"PostgreSQL composite field '{compositeMapping.StoreType}.{fieldName}' references " +
                $"unknown catalogue type OID {field.Type}.");
        }

        var storeType = GetStoreType(registry, fieldType);
        mapping = typeMappingSource.FindMapping(resultType, storeType, keyOrIndex: false)
            ?? throw new InvalidOperationException(
                $"PostgreSQL composite field '{compositeMapping.StoreType}.{fieldName}' has store type " +
                $"'{storeType}', which cannot map to CLR type '{resultType.Name}'.");
        return true;
    }

    private string GetStoreType(BlueTuskTypeRegistry registry, BlueTuskTypeDescriptor type)
    {
        if (type.Kind == BlueTuskTypeKind.Array && type.ElementType is { } elementTypeId)
        {
            if (!registry.TryGetType(elementTypeId, out var elementType) || elementType is null)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL array type '{type.QualifiedName}' references unknown element OID {elementTypeId}.");
            }

            return $"{GetStoreType(registry, elementType)}[]";
        }

        return string.Equals(type.Schema, "pg_catalog", StringComparison.Ordinal)
            ? type.Name
            : sqlGenerationHelper.DelimitIdentifier(type.Name, type.Schema);
    }

    private static bool TryParseTypeName(string storeType, out BlueTuskTypeName typeName)
    {
        try
        {
            typeName = BlueTuskTypeName.Parse(storeType);
            return true;
        }
        catch (FormatException)
        {
            typeName = default;
            return false;
        }
    }

    private static InvalidOperationException MissingMapping(string fieldName, Type resultType)
        => new(
            $"PostgreSQL composite field '{fieldName}' has no relational mapping for CLR type " +
            $"'{resultType.Name}'. Nested composite fields require a BlueTuskDataSource whose " +
            "runtime type catalogue has been loaded.");
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
