using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Abstractions;

namespace Microsoft.EntityFrameworkCore;

[BlueTuskLiveCondition]
public sealed class BlueTuskComplexTypesTrackingTest(
    BlueTuskComplexTypesTrackingTest.BlueTuskComplexTypesTrackingFixture fixture,
    ITestOutputHelper testOutputHelper)
    : ComplexTypesTrackingRelationalTestBase<BlueTuskComplexTypesTrackingTest.BlueTuskComplexTypesTrackingFixture>(
        fixture,
        testOutputHelper)
{
    [ConditionalFact]
    public void JSON_mapped_complex_properties_have_value_reader_writers()
    {
        using var context = Fixture.CreateContext();
        var properties = context.Model.GetEntityTypes()
            .SelectMany(GetProperties)
            .ToArray();
        var missing = properties
            .Where(property => property.DeclaringType.IsMappedToJson())
            .Where(property => property.GetJsonValueReaderWriter() is null
                && property.GetTypeMapping().JsonValueReaderWriter is null)
            .Select(property =>
            {
                var mapping = (RelationalTypeMapping)property.GetTypeMapping();
                return $"{property.DeclaringType.DisplayName()}.{property.Name} ({property.ClrType}): "
                    + $"{mapping.GetType().Name}/{mapping.StoreType}";
            })
            .ToArray();

        Assert.Empty(missing);

        static IEnumerable<IProperty> GetProperties(ITypeBase typeBase)
        {
            foreach (var property in typeBase.GetProperties())
            {
                yield return property;
            }

            foreach (var complexProperty in typeBase.GetComplexProperties())
            {
                foreach (var property in GetProperties(complexProperty.ComplexType))
                {
                    yield return property;
                }
            }
        }
    }

    protected override void UseTransaction(DatabaseFacade facade, IDbContextTransaction transaction)
        => facade.UseTransaction(transaction.GetDbTransaction());

    public sealed class BlueTuskComplexTypesTrackingFixture : RelationalFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;
    }
}
