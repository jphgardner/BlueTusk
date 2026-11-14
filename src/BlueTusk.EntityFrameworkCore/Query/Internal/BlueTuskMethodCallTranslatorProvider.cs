using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskMethodCallTranslatorProvider
    : RelationalMethodCallTranslatorProvider
{
    public BlueTuskMethodCallTranslatorProvider(
        RelationalMethodCallTranslatorProviderDependencies dependencies,
        ISqlGenerationHelper sqlGenerationHelper
#if BLUETUSK_EF6_OR_GREATER
        ,
        IDbContextOptions contextOptions)
        : this(dependencies, sqlGenerationHelper,
            contextOptions.FindExtension<BlueTuskOptionsExtension>()?.DataSource?.TypeRegistry)
#else
        ) : this(dependencies, sqlGenerationHelper, typeRegistry: null)
#endif
    {
    }

    internal BlueTuskMethodCallTranslatorProvider(
        RelationalMethodCallTranslatorProviderDependencies dependencies,
        ISqlGenerationHelper sqlGenerationHelper,
        BlueTuskTypeRegistry? typeRegistry) : base(dependencies)
    {
        var compositeFieldMappingResolver = new BlueTuskCompositeFieldMappingResolver(
            dependencies.RelationalTypeMappingSource,
            sqlGenerationHelper,
            typeRegistry);
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
