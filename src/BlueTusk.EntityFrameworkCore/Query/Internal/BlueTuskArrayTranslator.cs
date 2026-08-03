using System.Reflection;
using BlueTusk.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskArrayTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource)
    : IMethodCallTranslator
{
    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions))
        {
            return null;
        }

        return method.Name switch
        {
            nameof(BlueTuskDbFunctionsExtensions.Array2D) => TranslateConstructor(method, arguments),
            nameof(BlueTuskDbFunctionsExtensions.ArrayElement) => TranslateSubscript(method, arguments),
            nameof(BlueTuskDbFunctionsExtensions.ArraySlice) => TranslateSlice(method, arguments),
            _ => null,
        };
    }

    private BlueTuskArrayConstructorExpression TranslateConstructor(
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments)
    {
        var operation = nameof(BlueTuskDbFunctionsExtensions.Array2D);
        var inferredStoreType = arguments
            .Skip(1)
            .Select(argument => argument.TypeMapping)
            .OfType<BlueTuskArrayTypeMapping>()
            .Select(mapping => mapping.StoreType)
            .FirstOrDefault();
        var resultMapping = FindArrayMapping(method.ReturnType, operation, inferredStoreType);
        var rows = arguments
            .Skip(1)
            .Select(argument => sqlExpressionFactory.ApplyTypeMapping(
                argument,
                FindArrayMapping(argument.Type, operation, resultMapping.StoreType)))
            .ToArray();
        return new BlueTuskArrayConstructorExpression(rows, method.ReturnType, resultMapping);
    }

    private BlueTuskArraySubscriptExpression TranslateSubscript(
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments)
    {
        var array = arguments[1];
        var arrayMapping = ApplyArrayMapping(
            ref array,
            nameof(BlueTuskDbFunctionsExtensions.ArrayElement));
        var subscripts = arguments
            .Skip(2)
            .Select(argument => sqlExpressionFactory.ApplyDefaultTypeMapping(argument)!)
            .ToArray();
        ValidateRank(array.Type, subscripts.Length, nameof(BlueTuskDbFunctionsExtensions.ArrayElement));
        var elementMapping = (RelationalTypeMapping?)arrayMapping.ElementTypeMapping
            ?? typeMappingSource.FindMapping(method.ReturnType)
            ?? throw new InvalidOperationException(
                $"ArrayElement has no relational mapping for CLR type '{method.ReturnType.Name}'.");
        return new BlueTuskArraySubscriptExpression(
            array,
            subscripts,
            method.ReturnType,
            elementMapping);
    }

    private BlueTuskArraySliceExpression TranslateSlice(
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments)
    {
        var array = arguments[1];
        var arrayMapping = ApplyArrayMapping(
            ref array,
            nameof(BlueTuskDbFunctionsExtensions.ArraySlice));
        var bounds = arguments
            .Skip(2)
            .Select(argument => sqlExpressionFactory.ApplyDefaultTypeMapping(argument)!)
            .ToArray();
        if (bounds.Length % 2 != 0)
        {
            throw new InvalidOperationException("ArraySlice requires one lower and upper bound per dimension.");
        }

        ValidateRank(array.Type, bounds.Length / 2, nameof(BlueTuskDbFunctionsExtensions.ArraySlice));
        return new BlueTuskArraySliceExpression(
            array,
            bounds.Where((_, index) => index % 2 == 0).ToArray(),
            bounds.Where((_, index) => index % 2 != 0).ToArray(),
            method.ReturnType,
            arrayMapping);
    }

    private BlueTuskArrayTypeMapping FindArrayMapping(
        Type arrayType,
        string operation,
        string? storeType = null)
        => (storeType is null
                ? typeMappingSource.FindMapping(arrayType)
                : typeMappingSource.FindMapping(arrayType, storeType, keyOrIndex: false))
            as BlueTuskArrayTypeMapping
            ?? throw new InvalidOperationException(
                $"{operation} has no PostgreSQL array mapping for CLR type '{arrayType.Name}'.");

    private BlueTuskArrayTypeMapping ApplyArrayMapping(ref SqlExpression array, string operation)
    {
        var mapping = array.TypeMapping as BlueTuskArrayTypeMapping
            ?? FindArrayMapping(array.Type, operation);
        array = sqlExpressionFactory.ApplyTypeMapping(array, mapping);
        return mapping;
    }

    private static void ValidateRank(Type arrayType, int suppliedRank, string operation)
    {
        if (!arrayType.IsArray || arrayType.GetArrayRank() != suppliedRank)
        {
            throw new InvalidOperationException(
                $"{operation} received {suppliedRank} dimension(s) for CLR array type '{arrayType.Name}'.");
        }
    }
}
