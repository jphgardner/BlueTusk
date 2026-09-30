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

    [ConditionalTheory]
    public override Task Same_complex_type_projected_twice_with_pushdown_as_part_of_another_projection(bool async)
        => base.Same_complex_type_projected_twice_with_pushdown_as_part_of_another_projection(async);

    public sealed class BlueTuskComplexTypeQueryFixture : ComplexTypeQueryRelationalFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;
    }
}
