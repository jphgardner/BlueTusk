using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskMethodCallTranslatorProvider
    : RelationalMethodCallTranslatorProvider
{
    public BlueTuskMethodCallTranslatorProvider(
        RelationalMethodCallTranslatorProviderDependencies dependencies)
        : base(dependencies)
    {
        AddTranslators(
        [
            new BlueTuskWindowFunctionTranslator(
                dependencies.SqlExpressionFactory,
                dependencies.RelationalTypeMappingSource),
            new BlueTuskRowValueTranslator(
                dependencies.SqlExpressionFactory,
                dependencies.RelationalTypeMappingSource),
            new BlueTuskQuantifiedComparisonTranslator(
                dependencies.SqlExpressionFactory,
                dependencies.RelationalTypeMappingSource),
            new BlueTuskArrayTranslator(
                dependencies.SqlExpressionFactory,
                dependencies.RelationalTypeMappingSource),
            new BlueTuskRecordFieldTranslator(dependencies.RelationalTypeMappingSource),
            new BlueTuskPostgreSqlFunctionTranslator(
                dependencies.SqlExpressionFactory,
                dependencies.RelationalTypeMappingSource),
            new BlueTuskPostgreSqlOperatorTranslator(
                dependencies.SqlExpressionFactory,
                dependencies.RelationalTypeMappingSource),
            new BlueTuskStringMethodTranslator(dependencies.SqlExpressionFactory),
        ]);
    }
}
