using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

[BlueTuskLiveCondition]
public sealed class BlueTuskCompositeKeyEndToEndTest(
    BlueTuskCompositeKeyEndToEndTest.BlueTuskCompositeKeyEndToEndFixture fixture)
    : CompositeKeyEndToEndTestBase<BlueTuskCompositeKeyEndToEndTest.BlueTuskCompositeKeyEndToEndFixture>(fixture)
{
    public sealed class BlueTuskCompositeKeyEndToEndFixture : CompositeKeyEndToEndFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;
    }
}

[BlueTuskLiveCondition]
public sealed class BlueTuskFieldMappingTest(BlueTuskFieldMappingTest.BlueTuskFieldMappingFixture fixture)
    : FieldMappingTestBase<BlueTuskFieldMappingTest.BlueTuskFieldMappingFixture>(fixture)
{
    protected override void UseTransaction(DatabaseFacade facade, IDbContextTransaction transaction)
        => facade.UseTransaction(transaction.GetDbTransaction());

    public sealed class BlueTuskFieldMappingFixture : FieldMappingFixtureBase
    {
        protected override string StoreName { get; } = "BlueTuskFieldMapping";

        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;
    }
}

[BlueTuskLiveCondition]
public sealed class BlueTuskWithConstructorsTest(BlueTuskWithConstructorsTest.BlueTuskWithConstructorsFixture fixture)
    : WithConstructorsTestBase<BlueTuskWithConstructorsTest.BlueTuskWithConstructorsFixture>(fixture)
{
    protected override void UseTransaction(DatabaseFacade facade, IDbContextTransaction transaction)
        => facade.UseTransaction(transaction.GetDbTransaction());

    public sealed class BlueTuskWithConstructorsFixture : WithConstructorsFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;

        protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
        {
            base.OnModelCreating(modelBuilder, context);
            modelBuilder.Entity<BlogQuery>().HasNoKey().ToSqlQuery("SELECT * FROM \"Blog\"");
        }
    }
}

[BlueTuskLiveCondition]
public sealed class BlueTuskPropertyValuesTest(BlueTuskPropertyValuesTest.BlueTuskPropertyValuesFixture fixture)
    : PropertyValuesRelationalTestBase<BlueTuskPropertyValuesTest.BlueTuskPropertyValuesFixture>(fixture)
{
    public sealed class BlueTuskPropertyValuesFixture : PropertyValuesRelationalFixture
    {
        protected override string StoreName { get; } = "BlueTuskPropertyValues";

        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;

        protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
        {
            base.OnModelCreating(modelBuilder, context);

            modelBuilder.Entity<PastEmployee>()
                .Property(employee => employee.TerminationDate)
                .HasColumnType("timestamp without time zone");
            modelBuilder.Entity<Building>()
                .Property(building => building.Value)
                .HasColumnType("numeric(18,2)");
            modelBuilder.Entity<CurrentEmployee>()
                .Property(employee => employee.LeaveBalance)
                .HasColumnType("numeric(18,2)");
        }
    }
}
