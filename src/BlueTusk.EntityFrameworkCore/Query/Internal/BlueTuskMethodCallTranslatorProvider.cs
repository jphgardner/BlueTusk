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
            new BlueTuskPostgreSqlOperatorTranslator(
                dependencies.SqlExpressionFactory,
                dependencies.RelationalTypeMappingSource),
            new BlueTuskStringMethodTranslator(dependencies.SqlExpressionFactory),
        ]);
    }
}
