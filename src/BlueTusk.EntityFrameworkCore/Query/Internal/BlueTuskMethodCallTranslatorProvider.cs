using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskMethodCallTranslatorProvider
    : RelationalMethodCallTranslatorProvider
{
    public BlueTuskMethodCallTranslatorProvider(
        RelationalMethodCallTranslatorProviderDependencies dependencies,
        ISqlGenerationHelper sqlGenerationHelper,
        IDbContextOptions contextOptions)
        : base(dependencies)
    {
        var compositeFieldMappingResolver = new BlueTuskCompositeFieldMappingResolver(
            dependencies.RelationalTypeMappingSource,
            sqlGenerationHelper,
            contextOptions.FindExtension<BlueTuskOptionsExtension>()?.DataSource?.TypeRegistry);
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
            new BlueTuskRecordFieldTranslator(compositeFieldMappingResolver),
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
