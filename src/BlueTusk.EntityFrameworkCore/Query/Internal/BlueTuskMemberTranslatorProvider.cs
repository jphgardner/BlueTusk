using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskMemberTranslatorProvider : RelationalMemberTranslatorProvider
{
    public BlueTuskMemberTranslatorProvider(
        RelationalMemberTranslatorProviderDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource)
        : base(dependencies)
    {
        AddTranslators(
        [
            new BlueTuskCompositeMemberTranslator(typeMappingSource),
            new BlueTuskStringMemberTranslator(dependencies.SqlExpressionFactory),
        ]);
    }
}
