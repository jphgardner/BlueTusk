using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskAggregateMethodCallTranslatorProvider
    : RelationalAggregateMethodCallTranslatorProvider
{
    public BlueTuskAggregateMethodCallTranslatorProvider(
        RelationalAggregateMethodCallTranslatorProviderDependencies dependencies)
        : base(dependencies)
    {
        AddTranslators(
        [
            new BlueTuskPostgreSqlAggregateTranslator(
                dependencies.SqlExpressionFactory,
                dependencies.RelationalTypeMappingSource),
        ]);
    }
}
