using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Abstractions;

namespace Microsoft.EntityFrameworkCore.Query;

[BlueTuskLiveCondition]
public sealed class BlueTuskComplexTypeQueryTest
    : ComplexTypeQueryRelationalTestBase<BlueTuskComplexTypeQueryTest.BlueTuskComplexTypeQueryFixture>
{
    public BlueTuskComplexTypeQueryTest(
        BlueTuskComplexTypeQueryFixture fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        Fixture.TestSqlLoggerFactory.Clear();
        Fixture.TestSqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    public sealed class BlueTuskComplexTypeQueryFixture : ComplexTypeQueryRelationalFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;
    }
}
