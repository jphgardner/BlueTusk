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
    [ConditionalTheory]
    public override void Can_mark_complex_record_array_collection_properties_modified(System.Boolean trackFromQuery)
        => base.Can_mark_complex_record_array_collection_properties_modified(trackFromQuery);

    [ConditionalTheory]
    public override void Can_mark_complex_type_array_collection_properties_modified(System.Boolean trackFromQuery)
        => base.Can_mark_complex_type_array_collection_properties_modified(trackFromQuery);

    [ConditionalTheory]
    public override void Can_read_original_values_for_properties_of_complex_record_array_collections(System.Boolean trackFromQuery)
        => base.Can_read_original_values_for_properties_of_complex_record_array_collections(trackFromQuery);

    [ConditionalTheory]
    public override void Can_read_original_values_for_properties_of_complex_type_array_collections(System.Boolean trackFromQuery)
        => base.Can_read_original_values_for_properties_of_complex_type_array_collections(trackFromQuery);

    [ConditionalTheory]
    public override void Can_remove_from_complex_record_collection_with_nested_complex_collection(System.Boolean trackFromQuery)
        => base.Can_remove_from_complex_record_collection_with_nested_complex_collection(trackFromQuery);

    [ConditionalTheory]
    public override void Can_remove_from_complex_record_field_collection_with_nested_complex_collection(System.Boolean trackFromQuery)
        => base.Can_remove_from_complex_record_field_collection_with_nested_complex_collection(trackFromQuery);

    [ConditionalTheory]
    public override Task Can_track_entity_with_complex_record_array_collections(Microsoft.EntityFrameworkCore.EntityState state, System.Boolean async)
        => base.Can_track_entity_with_complex_record_array_collections(state, async);

    [ConditionalTheory]
    public override Task Can_track_entity_with_complex_type_array_collections(Microsoft.EntityFrameworkCore.EntityState state, System.Boolean async)
        => base.Can_track_entity_with_complex_type_array_collections(state, async);

    [ConditionalTheory]
    public override void Can_write_original_values_for_properties_of_complex_record_array_collections(System.Boolean trackFromQuery)
        => base.Can_write_original_values_for_properties_of_complex_record_array_collections(trackFromQuery);

    [ConditionalTheory]
    public override void Can_write_original_values_for_properties_of_complex_type_array_collections(System.Boolean trackFromQuery)
        => base.Can_write_original_values_for_properties_of_complex_type_array_collections(trackFromQuery);

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
