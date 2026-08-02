using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Query;

[BlueTuskLiveCondition]
public sealed class BlueTuskAdHocComplexTypeQueryTest(NonSharedFixture fixture)
    : AdHocComplexTypeQueryRelationalTestBase(fixture)
{
    // This test is SQL Server-specific and is being removed upstream:
    // https://github.com/dotnet/efcore/pull/37177
    public override Task Complex_type_equality_with_non_default_type_mapping()
        => Task.CompletedTask;

    protected override ITestStoreFactory TestStoreFactory
        => BlueTuskTestStoreFactory.Instance;
}
