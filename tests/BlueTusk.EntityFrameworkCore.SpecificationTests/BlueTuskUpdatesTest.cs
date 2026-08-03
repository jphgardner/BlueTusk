using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.TestModels.UpdatesModel;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.EntityFrameworkCore.Update;
using Xunit.Abstractions;

namespace Microsoft.EntityFrameworkCore;

[BlueTuskLiveCondition]
public sealed class BlueTuskUpdatesTest : UpdatesRelationalTestBase<BlueTuskUpdatesTest.BlueTuskUpdatesFixture>
{
    public BlueTuskUpdatesTest(BlueTuskUpdatesFixture fixture, ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        Fixture.TestSqlLoggerFactory.Clear();
    }

    public override void Identifiers_are_generated_correctly()
    {
        using var context = CreateContext();

        var entityType = context.Model.FindEntityType(
            typeof(
                LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameThatIsUsedToVerifyThatTheStoreIdentifierGenerationLengthLimitIsWorkingCorrectly
            ))!;
        Assert.Equal(
            "LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameThatI~",
            entityType.GetTableName());
        Assert.Equal(
            "PK_LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameTh~",
            entityType.GetKeys().Single().GetName());
        Assert.Equal(
            "FK_LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameTh~",
            entityType.GetForeignKeys().Single().GetConstraintName());
        Assert.Equal(
            "IX_LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameTh~",
            entityType.GetIndexes().Single().GetDatabaseName());

        var entityType2 = context.Model.FindEntityType(
            typeof(
                LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameThatIsUsedToVerifyThatTheStoreIdentifierGenerationLengthLimitIsWorkingCorrectlyDetails
            ))!;
        var storeObject = StoreObjectIdentifier.Table(entityType2.GetTableName()!);
        Assert.Equal(
            "LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameThat~1",
            entityType2.GetTableName());
        Assert.Equal("PK_LoginDetails", entityType2.GetKeys().Single().GetName());
        Assert.Equal(
            "ExtraPropertyWithAnExtremelyLongAndOverlyConvolutedNameThatIsU~",
            entityType2.FindProperty(
                    nameof(
                        LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameThatIsUsedToVerifyThatTheStoreIdentifierGenerationLengthLimitIsWorkingCorrectlyDetails
                            .ExtraPropertyWithAnExtremelyLongAndOverlyConvolutedNameThatIsUsedToVerifyThatTheStoreIdentifierGenerationLengthLimitIsWorkingCorrectly))!
                .GetColumnName(StoreObjectIdentifier.Table(entityType2.GetTableName()!)));
        Assert.Equal(
            "ExtraPropertyWithAnExtremelyLongAndOverlyConvolutedNameThatIs~1",
            entityType2.FindProperty(
                    nameof(
                        LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameThatIsUsedToVerifyThatTheStoreIdentifierGenerationLengthLimitIsWorkingCorrectlyDetails
                            .ExtraPropertyWithAnExtremelyLongAndOverlyConvolutedNameThatIsUsedToVerifyThatTheStoreIdentifierGenerationLengthLimitIsWorkingCorrectlyWhenTruncatedNamesCollide))!
                .GetColumnName(storeObject));
        Assert.Equal(
            "IX_LoginEntityTypeWithAnExtremelyLongAndOverlyConvolutedNameT~1",
            entityType2.GetIndexes().Single().GetDatabaseName());
    }

    public sealed class BlueTuskUpdatesFixture : UpdatesRelationalFixture
    {
        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;

        protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
        {
            base.OnModelCreating(modelBuilder, context);

            modelBuilder.Entity<ProductBase>()
                .Property(product => product.Id)
                .HasDefaultValueSql("gen_random_uuid()");
            modelBuilder.Entity<Product>()
                .HasIndex(product => new { product.Name, product.Price })
                .IsUnique()
                .HasFilter("\"Name\" IS NOT NULL");
            modelBuilder.Entity<Rodney>()
                .Property(rodney => rodney.Concurrency)
                .HasColumnType("timestamp without time zone");
        }
    }
}
