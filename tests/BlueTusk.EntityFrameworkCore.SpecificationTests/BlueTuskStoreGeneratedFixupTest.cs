using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

[BlueTuskLiveCondition]
public sealed class BlueTuskStoreGeneratedFixupTest(
    BlueTuskStoreGeneratedFixupTest.BlueTuskStoreGeneratedFixupFixture fixture)
    : StoreGeneratedFixupRelationalTestBase<BlueTuskStoreGeneratedFixupTest.BlueTuskStoreGeneratedFixupFixture>(fixture)
{
    [ConditionalFact]
    public Task Temporary_values_are_replaced_on_save()
        => ExecuteWithStrategyInTransactionAsync(
            async context =>
            {
                var entry = context.Add(new TestTemp());

                Assert.True(entry.Property(entity => entity.Id).IsTemporary);
                Assert.False(entry.Property(entity => entity.NotId).IsTemporary);
                var temporaryValue = entry.Property(entity => entity.Id).CurrentValue;

                await context.SaveChangesAsync();

                Assert.False(entry.Property(entity => entity.Id).IsTemporary);
                Assert.NotEqual(temporaryValue, entry.Property(entity => entity.Id).CurrentValue);
            });

    protected override void MarkIdsTemporary(DbContext context, object dependent, object principal)
    {
        var entry = context.Entry(dependent);
        entry.Property("Id1").IsTemporary = true;
        entry.Property("Id2").IsTemporary = true;

        foreach (var property in entry.Properties.Where(property => property.Metadata.IsForeignKey()))
        {
            property.IsTemporary = true;
        }

        entry = context.Entry(principal);
        entry.Property("Id1").IsTemporary = true;
        entry.Property("Id2").IsTemporary = true;
    }

    protected override void MarkIdsTemporary(DbContext context, object game, object level, object item)
    {
        context.Entry(game).Property("Id").IsTemporary = true;
        context.Entry(item).Property("Id").IsTemporary = true;
    }

    protected override bool EnforcesFKs
        => true;

    protected override void UseTransaction(DatabaseFacade facade, IDbContextTransaction transaction)
        => facade.UseTransaction(transaction.GetDbTransaction());

    public sealed class BlueTuskStoreGeneratedFixupFixture : StoreGeneratedFixupRelationalFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;

        protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
        {
            base.OnModelCreating(modelBuilder, context);

            Type[] compositeGeneratedEntityTypes =
            [
                typeof(Parent), typeof(Child), typeof(ParentPN), typeof(ChildPN),
                typeof(ParentDN), typeof(ChildDN), typeof(ParentNN), typeof(ChildNN),
                typeof(CategoryDN), typeof(ProductDN), typeof(CategoryPN), typeof(ProductPN),
                typeof(CategoryNN), typeof(ProductNN), typeof(Category), typeof(Product),
            ];
            foreach (var entityType in compositeGeneratedEntityTypes)
            {
                var entity = modelBuilder.Entity(entityType);
                entity.Property("Id1").ValueGeneratedOnAdd();
                entity.Property("Id2").ValueGeneratedOnAdd().HasDefaultValueSql("gen_random_uuid()");
            }

            modelBuilder.Entity<Item>().Property(entity => entity.Id).ValueGeneratedOnAdd();
            modelBuilder.Entity<Game>()
                .Property(entity => entity.Id)
                .ValueGeneratedOnAdd()
                .HasDefaultValueSql("gen_random_uuid()");
        }
    }
}
