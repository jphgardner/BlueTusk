using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskMemberTranslatorProvider : RelationalMemberTranslatorProvider
{
    public BlueTuskMemberTranslatorProvider(RelationalMemberTranslatorProviderDependencies dependencies)
        : base(dependencies)
    {
        AddTranslators([new BlueTuskStringMemberTranslator(dependencies.SqlExpressionFactory)]);
    }
}
