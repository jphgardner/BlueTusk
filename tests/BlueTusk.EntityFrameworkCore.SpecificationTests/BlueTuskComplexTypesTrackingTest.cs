using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Abstractions;

namespace Microsoft.EntityFrameworkCore;

[BlueTuskLiveCondition]
public sealed partial class BlueTuskComplexTypesTrackingTest(
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

    [ConditionalTheory]
    public override Task Can_track_entity_with_complex_readonly_structs_with_fields(Microsoft.EntityFrameworkCore.EntityState state, System.Boolean async)
        => base.Can_track_entity_with_complex_readonly_structs_with_fields(state, async);

    [ConditionalTheory]
    public override void Can_mark_complex_readonly_readonly_struct_properties_modified_with_fields(System.Boolean trackFromQuery)
        => base.Can_mark_complex_readonly_readonly_struct_properties_modified_with_fields(trackFromQuery);

    [ConditionalTheory]
    public override void Can_read_original_values_for_properties_of_readonly_structs_with_fields(System.Boolean trackFromQuery)
        => base.Can_read_original_values_for_properties_of_readonly_structs_with_fields(trackFromQuery);

    [ConditionalTheory]
    public override void Can_write_original_values_for_properties_of_readonly_structs_with_fields(System.Boolean trackFromQuery)
        => base.Can_write_original_values_for_properties_of_readonly_structs_with_fields(trackFromQuery);

    [ConditionalTheory]
    public override void Can_detect_changes_to_nested_readonly_struct_teams_in_complex_type_collections(System.Boolean trackFromQuery)
        => base.Can_detect_changes_to_nested_readonly_struct_teams_in_complex_type_collections(trackFromQuery);

    [ConditionalTheory]
    public override void Can_detect_changes_to_readonly_struct_collection_elements(System.Boolean trackFromQuery)
        => base.Can_detect_changes_to_readonly_struct_collection_elements(trackFromQuery);

    [ConditionalTheory]
    public override void Can_mark_complex_readonly_struct_array_collection_properties_modified(System.Boolean trackFromQuery)
        => base.Can_mark_complex_readonly_struct_array_collection_properties_modified(trackFromQuery);

    [ConditionalTheory]
    public override void Can_mark_complex_struct_array_collection_properties_modified(System.Boolean trackFromQuery)
        => base.Can_mark_complex_struct_array_collection_properties_modified(trackFromQuery);

    [ConditionalTheory]
    public override void Can_read_original_values_for_properties_of_complex_readonly_struct_array_collections(System.Boolean trackFromQuery)
        => base.Can_read_original_values_for_properties_of_complex_readonly_struct_array_collections(trackFromQuery);

    [ConditionalTheory]
    public override void Can_read_original_values_for_properties_of_complex_struct_array_collections(System.Boolean trackFromQuery)
        => base.Can_read_original_values_for_properties_of_complex_struct_array_collections(trackFromQuery);

    [ConditionalTheory]
    public override Task Can_track_entity_with_complex_readonly_struct_array_collections(Microsoft.EntityFrameworkCore.EntityState state, System.Boolean async)
        => base.Can_track_entity_with_complex_readonly_struct_array_collections(state, async);

    [ConditionalTheory]
    public override Task Can_track_entity_with_complex_struct_array_collections(Microsoft.EntityFrameworkCore.EntityState state, System.Boolean async)
        => base.Can_track_entity_with_complex_struct_array_collections(state, async);

    [ConditionalTheory]
    public override void Can_write_original_values_for_properties_of_complex_readonly_struct_array_collections(System.Boolean trackFromQuery)
        => base.Can_write_original_values_for_properties_of_complex_readonly_struct_array_collections(trackFromQuery);

    [ConditionalTheory]
    public override void Can_write_original_values_for_properties_of_complex_struct_array_collections(System.Boolean trackFromQuery)
        => base.Can_write_original_values_for_properties_of_complex_struct_array_collections(trackFromQuery);

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

        // EF Core comments this entity out until it can bind complex values to constructors (dotnet/efcore#31621);
        // BlueTusk materializes such readonly structs by member assignment, so the upstream configuration is enabled.
        protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
        {
            base.OnModelCreating(modelBuilder, context);
            modelBuilder.Entity<FieldPubWithReadonlyStructs>(b =>
            {
                b.ComplexProperty(
                    e => e.LunchtimeActivity, b =>
                    {
                        b.ComplexProperty(e => e!.Champions);
                        b.ComplexProperty(e => e!.RunnersUp);
                    });
                b.ComplexProperty(
                    e => e.EveningActivity, b =>
                    {
                        b.ComplexProperty(e => e.Champions);
                        b.ComplexProperty(e => e.RunnersUp);
                    });
                b.ComplexProperty(e => e.FeaturedTeam);
            });

            // EF Core comments these value-type complex collections out (dotnet/efcore#31411); BlueTusk supports them.
            // The shapes are EF Core's own commented-out configuration, mapped to JSON as relational collections require.
            modelBuilder.Entity<PubWithStructCollections>(b =>
            {
                b.ComplexCollection(
                    e => e.Activities, b =>
                    {
                        b.ComplexCollection(e => e.Teams);
                        b.ToJson();
                    });
                b.ComplexProperty(e => e.FeaturedTeam);
            });

            modelBuilder.Entity<PubWithReadonlyStructCollections>(b =>
            {
                b.ComplexCollection(
                    e => e.Activities, b =>
                    {
                        b.ComplexCollection(e => e.Teams);
                        b.ToJson();
                    });
                b.ComplexProperty(e => e.FeaturedTeam);
            });

            modelBuilder.Entity<PubWithStructArrayCollections>(b =>
            {
                b.ComplexCollection(
                    e => e.Activities, b =>
                    {
                        b.ComplexCollection(e => e.Teams);
                        b.ToJson();
                    });
                b.ComplexProperty(e => e.FeaturedTeam);
            });

            modelBuilder.Entity<PubWithReadonlyStructArrayCollections>(b =>
            {
                b.ComplexCollection(
                    e => e.Activities, b =>
                    {
                        b.ComplexCollection(e => e.Teams);
                        b.ToJson();
                    });
                b.ComplexProperty(e => e.FeaturedTeam);
            });

            modelBuilder.Entity<FieldPubWithStructCollections>(b =>
            {
                b.ComplexCollection(
                    e => e.Activities, b =>
                    {
                        b.ComplexCollection(e => e.Teams);
                        b.ToJson();
                    });
                b.ComplexProperty(e => e.FeaturedTeam);
            });

            modelBuilder.Entity<FieldPubWithReadonlyStructCollections>(b =>
            {
                b.ComplexCollection(
                    e => e.Activities, b =>
                    {
                        b.ComplexCollection(e => e.Teams);
                        b.ToJson();
                    });
                b.ComplexProperty(e => e.FeaturedTeam);
            });
        }
    }
}
