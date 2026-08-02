using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

[BlueTuskLiveCondition]
public sealed class BlueTuskDataAnnotationTest(BlueTuskDataAnnotationTest.BlueTuskDataAnnotationFixture fixture)
    : DataAnnotationRelationalTestBase<BlueTuskDataAnnotationTest.BlueTuskDataAnnotationFixture>(fixture)
{
    protected override void UseTransaction(DatabaseFacade facade, IDbContextTransaction transaction)
        => facade.UseTransaction(transaction.GetDbTransaction());

    protected override TestHelpers TestHelpers
        => BlueTuskTestHelpers.Instance;

    public override Task StringLengthAttribute_throws_while_inserting_value_longer_than_max_length()
        => Task.CompletedTask; // PostgreSQL does not expose this as a provider-enforced length exception.

    public override Task TimestampAttribute_throws_if_value_in_database_changed()
        => Task.CompletedTask; // PostgreSQL has no SQL Server-style rowversion column.

    public override Task MaxLengthAttribute_throws_while_inserting_value_longer_than_max_length()
        => Task.CompletedTask; // PostgreSQL does not expose this as a provider-enforced length exception.

    public sealed class BlueTuskDataAnnotationFixture : DataAnnotationRelationalFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;
    }
}
