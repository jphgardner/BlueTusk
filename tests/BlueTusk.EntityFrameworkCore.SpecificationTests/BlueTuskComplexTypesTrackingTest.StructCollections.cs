// BlueTusk ports of EF Core 10.0.11 value-type complex collection tests.
//
// Derived from Entity Framework Core 10.0.11 (test/EFCore.Specification.Tests/ComplexTypesTrackingTestBase.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
//
// EF Core skips these tests (dotnet/efcore#31411, #31621) and has never executed them; as written they cannot pass:
// - the four struct-collection data factories insert a default element between "Pub Quiz" and "Music Quiz", while the
//   shared assertions (AssertCollectionPropertyValues and friends, used unchanged) expect "Music Quiz" at index 1 and the
//   reference-type factory they mirror has exactly those two activities; the default element also has null Teams;
// - two change-detection tests read a collection with the reference-only ComplexProperty API, which EF Core 10 rejects
//   for collection properties; they use the equivalent ComplexCollection API.
// The private EF Core helpers below are copied unchanged (renamed with a "Ported" prefix) because they cannot be called
// from a derived class; the factories are copied unchanged except that the default element is omitted.

#pragma warning disable CA1861 // Upstream EF Core test code is kept unchanged.
#pragma warning disable EF1001 // The upstream helper inspects internal complex entries.

using System.Collections;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

public sealed partial class BlueTuskComplexTypesTrackingTest
{
    [ConditionalTheory]
    public override Task Can_change_state_from_Deleted_with_complex_field_readonly_struct_collection(EntityState newState, bool async)
        => PortedChangeStateFromDeletedTest(newState, async, CreateConsistentFieldCollectionPubWithReadonlyStructs);

    [ConditionalTheory]
    public override Task Can_change_state_from_Deleted_with_complex_field_struct_collection(EntityState newState, bool async)
        => PortedChangeStateFromDeletedTest(newState, async, CreateConsistentFieldCollectionPubWithStructs);

    [ConditionalTheory]
    public override Task Can_change_state_from_Deleted_with_complex_readonly_struct_collection(EntityState newState, bool async)
        => PortedChangeStateFromDeletedTest(newState, async, CreateConsistentPubWithReadonlyStructCollections);

    [ConditionalTheory]
    public override Task Can_change_state_from_Deleted_with_complex_struct_collection(EntityState newState, bool async)
        => PortedChangeStateFromDeletedTest(newState, async, CreateConsistentPubWithStructCollections);

    [ConditionalTheory]
    public override void Can_mark_complex_readonly_struct_collection_properties_modified(bool trackFromQuery)
        => PortedMarkModifiedTest(trackFromQuery, CreateConsistentPubWithReadonlyStructCollections);

    [ConditionalTheory]
    public override void Can_mark_complex_readonly_struct_collections_with_fields_properties_modified(bool trackFromQuery)
        => PortedMarkModifiedTest(trackFromQuery, CreateConsistentFieldCollectionPubWithReadonlyStructs);

    [ConditionalTheory]
    public override void Can_mark_complex_struct_collection_properties_modified(bool trackFromQuery)
        => PortedMarkModifiedTest(trackFromQuery, CreateConsistentPubWithStructCollections);

    [ConditionalTheory]
    public override void Can_mark_complex_struct_collections_with_fields_properties_modified(bool trackFromQuery)
        => PortedMarkModifiedTest(trackFromQuery, CreateConsistentFieldCollectionPubWithStructs);

    [ConditionalTheory]
    public override void Can_read_original_values_for_properties_of_complex_readonly_struct_collections_with_fields(bool trackFromQuery)
        => PortedReadOriginalValuesTest(trackFromQuery, CreateConsistentFieldCollectionPubWithReadonlyStructs);

    [ConditionalTheory]
    public override void Can_read_original_values_for_properties_of_complex_struct_collections(bool trackFromQuery)
        => PortedReadOriginalValuesTest(trackFromQuery, CreateConsistentPubWithStructCollections);

    [ConditionalTheory]
    public override void Can_read_original_values_for_properties_of_complex_struct_collections_with_fields(bool trackFromQuery)
        => PortedReadOriginalValuesTest(trackFromQuery, CreateConsistentFieldCollectionPubWithStructs);

    [ConditionalTheory]
    public override void Can_read_original_values_for_properties_of_readonly_struct_collections(bool trackFromQuery)
        => PortedReadOriginalValuesTest(trackFromQuery, CreateConsistentPubWithReadonlyStructCollections);

    [ConditionalTheory]
    public override void Can_remove_from_complex_readonly_struct_collection_with_nested_complex_collection(bool trackFromQuery)
        => PortedRemoveFromComplexCollectionWithNestedCollectionTest(trackFromQuery, CreateConsistentPubWithReadonlyStructCollections);

    [ConditionalTheory]
    public override void Can_remove_from_complex_readonly_struct_field_collection_with_nested_complex_collection(bool trackFromQuery)
        => PortedRemoveFromComplexCollectionWithNestedCollectionTest(trackFromQuery, CreateConsistentFieldCollectionPubWithReadonlyStructs);

    [ConditionalTheory]
    public override void Can_remove_from_complex_struct_collection_with_nested_complex_collection(bool trackFromQuery)
        => PortedRemoveFromComplexCollectionWithNestedCollectionTest(trackFromQuery, CreateConsistentPubWithStructCollections);

    [ConditionalTheory]
    public override void Can_remove_from_complex_struct_field_collection_with_nested_complex_collection(bool trackFromQuery)
        => PortedRemoveFromComplexCollectionWithNestedCollectionTest(trackFromQuery, CreateConsistentFieldCollectionPubWithStructs);

    [ConditionalTheory]
    public override Task Can_track_entity_with_complex_readonly_struct_collections(EntityState state, bool async)
        => PortedTrackAndSaveTest(state, async, CreateConsistentPubWithReadonlyStructCollections);

    [ConditionalTheory]
    public override Task Can_track_entity_with_complex_readonly_struct_collections_with_fields(EntityState state, bool async)
        => PortedTrackAndSaveTest(state, async, CreateConsistentFieldCollectionPubWithReadonlyStructs);

    [ConditionalTheory]
    public override Task Can_track_entity_with_complex_struct_collections(EntityState state, bool async)
        => PortedTrackAndSaveTest(state, async, CreateConsistentPubWithStructCollections);

    [ConditionalTheory]
    public override Task Can_track_entity_with_complex_struct_collections_with_fields(EntityState state, bool async)
        => PortedTrackAndSaveTest(state, async, CreateConsistentFieldCollectionPubWithStructs);

    [ConditionalTheory]
    public override void Can_write_original_values_for_properties_of_complex_readonly_struct_collections_with_fields(bool trackFromQuery)
        => PortedWriteOriginalValuesTest(trackFromQuery, CreateConsistentFieldCollectionPubWithReadonlyStructs);

    [ConditionalTheory]
    public override void Can_write_original_values_for_properties_of_complex_struct_collections(bool trackFromQuery)
        => PortedWriteOriginalValuesTest(trackFromQuery, CreateConsistentPubWithStructCollections);

    [ConditionalTheory]
    public override void Can_write_original_values_for_properties_of_complex_struct_collections_with_fields(bool trackFromQuery)
        => PortedWriteOriginalValuesTest(trackFromQuery, CreateConsistentFieldCollectionPubWithStructs);

    [ConditionalTheory]
    public override void Can_write_original_values_for_properties_of_readonly_struct_collections(bool trackFromQuery)
        => PortedWriteOriginalValuesTest(trackFromQuery, CreateConsistentPubWithReadonlyStructCollections);

    [ConditionalTheory]
    public override void Can_detect_changes_to_struct_collection_elements(bool trackFromQuery)
    {
        using var context = CreateContext();
        var pub = CreateConsistentPubWithStructCollections(context);

        var entry = trackFromQuery ? TrackFromQuery(context, pub) : context.Attach(pub);
        Assert.Equal(EntityState.Unchanged, entry.State);

        var activity = pub.Activities[0];
        activity.CoverCharge = 12.5m;
        pub.Activities[0] = activity;

        context.ChangeTracker.DetectChanges();

        var activitiesEntry = entry.ComplexCollection(e => e.Activities);

        if (Fixture.UseProxies)
        {
            Assert.Equal(EntityState.Unchanged, entry.State);
            activitiesEntry.IsModified = true;
        }

        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(activitiesEntry.IsModified);

        Assert.Equal(12.5m, pub.Activities[0].CoverCharge);
    }

    [ConditionalTheory]
    public override void Can_detect_changes_to_nested_struct_teams_in_complex_type_collections(bool trackFromQuery)
    {
        using var context = CreateContext();
        var pub = CreateConsistentPubWithStructCollections(context);

        var entry = trackFromQuery ? TrackFromQuery(context, pub) : context.Attach(pub);
        Assert.Equal(EntityState.Unchanged, entry.State);

        var teams = pub.Activities[0].Teams.ToList();
        teams[0] = new TeamStruct { Name = teams[0].Name, Members = [.. teams[0].Members, "Additional Member"] };
        var activity = pub.Activities[0];
        activity.Teams = teams;
        pub.Activities[0] = activity;

        context.ChangeTracker.DetectChanges();

        var activitiesEntry = entry.ComplexCollection(e => e.Activities);

        if (Fixture.UseProxies)
        {
            Assert.Equal(EntityState.Unchanged, entry.State);
            activitiesEntry.IsModified = true;
        }

        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(activitiesEntry.IsModified);

        Assert.Contains("Additional Member", pub.Activities[0].Teams[0].Members);
    }

    private async Task PortedChangeStateFromDeletedTest<TEntity>(
        EntityState newState,
        bool async,
        Func<DbContext, TEntity> createPub)
        where TEntity : class
    {
        await ExecuteWithStrategyInTransactionAsync(
            async context =>
            {
                var pub = createPub(context);
                context.Add(pub);
                _ = async ? await context.SaveChangesAsync() : context.SaveChanges();
            },
            async context =>
            {
                var pub = async
                    ? await context.Set<TEntity>().Where(e => EF.Property<string>(e, "Name") == "The FBI").FirstAsync()
                    : context.Set<TEntity>().Where(e => EF.Property<string>(e, "Name") == "The FBI").First();
                var entry = context.Entry(pub);

                entry.State = EntityState.Deleted;

                // Change to target state - this should not throw an exception
                entry.State = newState;
                Assert.Equal(newState, entry.State);

                // Verify the complex collection is still accessible
                var activitiesEntry = entry.ComplexCollection("Activities");
                Assert.NotNull(activitiesEntry);
                var activitiesValue = activitiesEntry.CurrentValue;
                Assert.Equal(2, ((System.Collections.IList)activitiesValue!).Count);
            });
    }

    private async Task PortedTrackAndSaveTest<TEntity>(EntityState state, bool async, Func<DbContext, TEntity> createPub)
        where TEntity : class
        => await ExecuteWithStrategyInTransactionAsync(async context =>
        {
            var pub = createPub(context);
            var entry = state switch
            {
                EntityState.Unchanged => context.Attach(pub),
                EntityState.Deleted => context.Remove(pub),
                EntityState.Modified => context.Update(pub),
                EntityState.Added => async ? await context.AddAsync(pub) : context.Add(pub),
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
            };

            Assert.Equal(state, entry.State);

            var hasCollections = entry.Metadata.GetComplexProperties().Any(p => p.Name == "Activities");
            if (hasCollections)
            {
                AssertCollectionPropertyValues(entry);
                AssertCollectionPropertiesModified(entry, state);
            }
            else
            {
                AssertPropertyValues(entry);
                AssertPropertiesModified(entry, state == EntityState.Modified);
            }

            if (state == EntityState.Added || state == EntityState.Unchanged)
            {
                _ = async ? await context.SaveChangesAsync() : context.SaveChanges();

                Assert.Equal(EntityState.Unchanged, entry.State);

                if (hasCollections)
                {
                    AssertCollectionPropertyValues(entry);
                }
                else
                {
                    AssertPropertyValues(entry);
                }
            }
        });

    private void PortedMarkModifiedTest<TEntity>(bool trackFromQuery, Func<DbContext, TEntity> createPub)
        where TEntity : class
    {
        using var context = CreateContext();

        var pub = createPub(context);
        var entry = trackFromQuery ? TrackFromQuery(context, pub) : context.Attach(pub);

        Assert.Equal(EntityState.Unchanged, entry.State);

        var hasCollections = entry.Metadata.GetComplexProperties().Any(p => p.Name == "Activities");
        if (hasCollections)
        {
            AssertCollectionPropertyValues(entry);
            AssertCollectionPropertiesModified(entry, EntityState.Unchanged);
        }
        else
        {
            AssertPropertyValues(entry);
            AssertPropertiesModified(entry, false);
        }

        var membersEntry = hasCollections
            ? entry.ComplexCollection("Activities")[0].ComplexCollection("Teams")[1].Property("Members")
            : entry.ComplexProperty("LunchtimeActivity").ComplexProperty("RunnersUp").Property("Members");
        membersEntry.IsModified = true;
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(membersEntry.IsModified);

        var dayEntry = hasCollections
            ? entry.ComplexCollection("Activities")[0].Property("Day")
            : entry.ComplexProperty("LunchtimeActivity").Property("Day");
        dayEntry.IsModified = true;
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(dayEntry.IsModified);

        var coverChargeEntry = hasCollections
            ? entry.ComplexCollection("Activities")[1].Property("CoverCharge")
            : entry.ComplexProperty("EveningActivity").Property("CoverCharge");
        coverChargeEntry.IsModified = true;
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(coverChargeEntry.IsModified);
        if (hasCollections)
        {
            var activitiesCollection = entry.ComplexCollection("Activities");
            var firstActivityEntry = activitiesCollection[0];
            var secondActivityEntry = activitiesCollection[1];
            var firstTeamOfFirstActivity = firstActivityEntry.ComplexCollection("Teams")[0];
            var lastTeamOfFirstActivity = firstActivityEntry.ComplexCollection("Teams")[1];
            var firstTeamOfSecondActivity = secondActivityEntry.ComplexCollection("Teams")[0];
            var lastTeamOfSecondActivity = secondActivityEntry.ComplexCollection("Teams")[1];
            var featuredTeamEntry = entry.ComplexProperty("FeaturedTeam");

            Assert.False(firstActivityEntry.Property("Name").IsModified);
            Assert.False(firstActivityEntry.Property("Description").IsModified);
            Assert.True(firstActivityEntry.Property("Day").IsModified);
            Assert.False(firstActivityEntry.Property("Notes").IsModified);
            Assert.False(firstActivityEntry.Property("CoverCharge").IsModified);
            Assert.False(firstActivityEntry.Property("IsTeamBased").IsModified);
            Assert.False(firstTeamOfFirstActivity.Property("Name").IsModified);
            Assert.False(firstTeamOfFirstActivity.Property("Members").IsModified);
            Assert.False(lastTeamOfFirstActivity.Property("Name").IsModified);
            Assert.True(lastTeamOfFirstActivity.Property("Members").IsModified);

            Assert.False(secondActivityEntry.Property("Name").IsModified);
            Assert.False(secondActivityEntry.Property("Description").IsModified);
            Assert.False(secondActivityEntry.Property("Day").IsModified);
            Assert.False(secondActivityEntry.Property("Notes").IsModified);
            Assert.True(secondActivityEntry.Property("CoverCharge").IsModified);
            Assert.False(secondActivityEntry.Property("IsTeamBased").IsModified);
            Assert.False(firstTeamOfSecondActivity.Property("Name").IsModified);
            Assert.False(firstTeamOfSecondActivity.Property("Members").IsModified);
            Assert.False(lastTeamOfSecondActivity.Property("Name").IsModified);
            Assert.False(lastTeamOfSecondActivity.Property("Members").IsModified);

            Assert.False(featuredTeamEntry.Property("Name").IsModified);
            Assert.False(featuredTeamEntry.Property("Members").IsModified);
        }
        else
        {
            var lunchtimeEntry = entry.ComplexProperty("LunchtimeActivity");
            var lunchtimeChampionsEntry = lunchtimeEntry.ComplexProperty("Champions");
            var lunchtimeRunnersUpEntry = lunchtimeEntry.ComplexProperty("RunnersUp");
            var eveningEntry = entry.ComplexProperty("EveningActivity");
            var eveningChampionsEntry = eveningEntry.ComplexProperty("Champions");
            var eveningRunnersUpEntry = eveningEntry.ComplexProperty("RunnersUp");
            var teamEntry = entry.ComplexProperty("FeaturedTeam");

            Assert.False(lunchtimeEntry.Property("Name").IsModified);
            Assert.False(lunchtimeEntry.Property("Description").IsModified);
            Assert.True(lunchtimeEntry.Property("Day").IsModified);
            Assert.False(lunchtimeEntry.Property("Notes").IsModified);
            Assert.False(lunchtimeEntry.Property("CoverCharge").IsModified);
            Assert.False(lunchtimeEntry.Property("IsTeamBased").IsModified);
            Assert.False(lunchtimeChampionsEntry.Property("Name").IsModified);
            Assert.False(lunchtimeChampionsEntry.Property("Members").IsModified);
            Assert.False(lunchtimeRunnersUpEntry.Property("Name").IsModified);
            Assert.True(lunchtimeRunnersUpEntry.Property("Members").IsModified);

            Assert.False(eveningEntry.Property("Name").IsModified);
            Assert.False(eveningEntry.Property("Description").IsModified);
            Assert.False(eveningEntry.Property("Day").IsModified);
            Assert.False(eveningEntry.Property("Notes").IsModified);
            Assert.True(eveningEntry.Property("CoverCharge").IsModified);
            Assert.False(eveningEntry.Property("IsTeamBased").IsModified);
            Assert.False(eveningChampionsEntry.Property("Name").IsModified);
            Assert.False(eveningChampionsEntry.Property("Members").IsModified);
            Assert.False(eveningRunnersUpEntry.Property("Name").IsModified);
            Assert.False(eveningRunnersUpEntry.Property("Members").IsModified);

            Assert.False(teamEntry.Property("Name").IsModified);
            Assert.False(teamEntry.Property("Members").IsModified);
        }

        membersEntry.IsModified = false;
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.False(membersEntry.IsModified);

        dayEntry.IsModified = false;
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.False(dayEntry.IsModified);

        coverChargeEntry.IsModified = false;
        Assert.Equal(EntityState.Unchanged, entry.State);
        Assert.False(coverChargeEntry.IsModified);

        if (hasCollections)
        {
            AssertCollectionPropertyValues(entry);
            AssertCollectionPropertiesModified(entry, EntityState.Unchanged);
        }
        else
        {
            AssertPropertyValues(entry);
            AssertPropertiesModified(entry, false);
        }
    }

    private void PortedReadOriginalValuesTest<TEntity>(bool trackFromQuery, Func<DbContext, TEntity> createPub)
        where TEntity : class
    {
        using var context = CreateContext();

        var pub = createPub(context);
        var entry = trackFromQuery ? TrackFromQuery(context, pub) : context.Attach(pub);

        Assert.Equal(EntityState.Unchanged, entry.State);

        var hasCollections = entry.Metadata.GetComplexProperties().Any(p => p.Name == "Activities");
        if (hasCollections)
        {
            AssertCollectionPropertyValues(entry);
            AssertCollectionPropertiesModified(entry, EntityState.Unchanged);
        }
        else
        {
            AssertPropertyValues(entry);
            AssertPropertiesModified(entry, false);
        }

        var membersEntry = hasCollections
            ? entry.ComplexCollection("Activities")[0].ComplexCollection("Teams")[0].Property("Members")
            : entry.ComplexProperty("LunchtimeActivity").ComplexProperty("Champions").Property("Members");
        membersEntry.CurrentValue = new List<string>
        {
            "1",
            "2",
            "3"
        };
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(membersEntry.IsModified);
        Assert.Equal(new[] { "1", "2", "3" }, membersEntry.CurrentValue);

        if (Fixture.UseProxies)
        {
            Assert.Equal(
                CoreStrings.OriginalValueNotTracked(membersEntry.Metadata.Name, membersEntry.Metadata.DeclaringType.DisplayName()),
                Assert.Throws<InvalidOperationException>(() => membersEntry.OriginalValue).Message);
        }
        else
        {
            Assert.Equal(new[] { "Boris", "David", "Theresa" }, membersEntry.OriginalValue);

            var dayEntry = hasCollections
                ? entry.ComplexCollection("Activities")[0].Property("Day")
                : entry.ComplexProperty("LunchtimeActivity").Property("Day");
            dayEntry.CurrentValue = DayOfWeek.Wednesday;
            Assert.Equal(EntityState.Modified, entry.State);
            Assert.True(dayEntry.IsModified);
            Assert.Equal(DayOfWeek.Wednesday, dayEntry.CurrentValue);
            Assert.Equal(DayOfWeek.Monday, dayEntry.OriginalValue);

            var coverChargeEntry = hasCollections
                ? entry.ComplexCollection("Activities")[1].Property("CoverCharge")
                : entry.ComplexProperty("EveningActivity").Property("CoverCharge");
            coverChargeEntry.CurrentValue = 3.0m;
            Assert.Equal(EntityState.Modified, entry.State);
            Assert.True(coverChargeEntry.IsModified);
            Assert.Equal(3.0m, coverChargeEntry.CurrentValue);
            Assert.Equal(5.0m, coverChargeEntry.OriginalValue);
        }
    }

    private void PortedWriteOriginalValuesTest<TEntity>(bool trackFromQuery, Func<DbContext, TEntity> createPub)
        where TEntity : class
    {
        using var context = CreateContext();
        var pub = createPub(context);
        var entry = trackFromQuery ? TrackFromQuery(context, pub) : context.Attach(pub);

        Assert.Equal(EntityState.Unchanged, entry.State);

        var hasCollections = entry.Metadata.GetComplexProperties().Any(p => p.Name == "Activities");
        if (hasCollections)
        {
            AssertCollectionPropertyValues(entry);
            AssertCollectionPropertiesModified(entry, EntityState.Unchanged);
        }
        else
        {
            AssertPropertyValues(entry);
            AssertPropertiesModified(entry, false);
        }

        var membersEntry = hasCollections
            ? entry.ComplexCollection("Activities")[1].ComplexCollection("Teams")[0].Property("Members")
            : entry.ComplexProperty("EveningActivity").ComplexProperty("Champions").Property("Members");

        if (Fixture.UseProxies)
        {
            Assert.Equal(
                CoreStrings.OriginalValueNotTracked(membersEntry.Metadata.Name, membersEntry.Metadata.DeclaringType.DisplayName()),
                Assert.Throws<InvalidOperationException>(() => membersEntry.OriginalValue = new List<string>()).Message);
        }
        else
        {
            membersEntry.OriginalValue = new List<string>
            {
                "1",
                "2",
                "3"
            };
            Assert.Equal(EntityState.Modified, entry.State);
            Assert.True(membersEntry.IsModified);
            Assert.Equal(new[] { "Robert", "Jimmy", "John", "Jason" }, membersEntry.CurrentValue);
            Assert.Equal(new[] { "1", "2", "3" }, membersEntry.OriginalValue);

            var dayEntry = hasCollections
                ? entry.ComplexCollection("Activities")[0].Property("Day")
                : entry.ComplexProperty("LunchtimeActivity").Property("Day");
            dayEntry.OriginalValue = DayOfWeek.Wednesday;
            Assert.Equal(EntityState.Modified, entry.State);
            Assert.True(dayEntry.IsModified);
            Assert.Equal(DayOfWeek.Monday, dayEntry.CurrentValue);
            Assert.Equal(DayOfWeek.Wednesday, dayEntry.OriginalValue);

            var coverChargeEntry = hasCollections
                ? entry.ComplexCollection("Activities")[1].Property("CoverCharge")
                : entry.ComplexProperty("EveningActivity").Property("CoverCharge");
            coverChargeEntry.OriginalValue = 3.0m;
            Assert.Equal(EntityState.Modified, entry.State);
            Assert.True(coverChargeEntry.IsModified);
            Assert.Equal(5.0m, coverChargeEntry.CurrentValue);
            Assert.Equal(3.0m, coverChargeEntry.OriginalValue);
        }
    }

    private void PortedRemoveFromComplexCollectionWithNestedCollectionTest<TEntity>(bool trackFromQuery, Func<DbContext, TEntity> createPub)
        where TEntity : class
    {
        using var context = CreateContext();
        var pub = createPub(context);

        var entry = trackFromQuery ? TrackFromQuery(context, pub) : context.Attach(pub);

        Assert.Equal(EntityState.Unchanged, entry.State);

        var activitiesProperty = entry.Metadata.FindComplexProperty("Activities")!;
        var activities = (IList)activitiesProperty.GetGetter().GetClrValue(pub)!;
        var originalCount = activities.Count;
        Assert.True(originalCount > 0);

        activities.RemoveAt(0);

        context.ChangeTracker.DetectChanges();

        var collectionEntry = entry.ComplexCollection("Activities");
        var internalEntry = entry.GetInfrastructure();

        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(collectionEntry.IsModified);
        Assert.Equal([-1, 0], internalEntry.GetComplexCollectionOriginalEntries(collectionEntry.Metadata).Select(e => e?.Ordinal));
        Assert.Equal([1], internalEntry.GetComplexCollectionEntries(collectionEntry.Metadata).Select(e => e?.OriginalOrdinal));

        context.ChangeTracker.AcceptAllChanges();

        Assert.Equal(EntityState.Unchanged, entry.State);
        Assert.False(collectionEntry.IsModified);
        Assert.Equal([0], internalEntry.GetComplexCollectionOriginalEntries(collectionEntry.Metadata).Select(e => e?.Ordinal));
        Assert.Equal([0], internalEntry.GetComplexCollectionEntries(collectionEntry.Metadata).Select(e => e?.OriginalOrdinal));
    }

    private PubWithStructCollections CreateConsistentPubWithStructCollections(DbContext context)
    {
        var pub = Fixture.UseProxies
            ? context.CreateProxy<PubWithStructCollections>()
            : new PubWithStructCollections();

        pub.Id = Guid.NewGuid();
        pub.Name = "The FBI";

        pub.Activities =
        [
            new ActivityStructWithCollection
            {
                Name = "Pub Quiz",
                Day = DayOfWeek.Monday,
                Description = "A general knowledge pub quiz.",
                Notes = ["One", "Two", "Three"],
                CoverCharge = 2.0m,
                IsTeamBased = true,
                Teams =
                [
                    new TeamStruct
                    {
                        Name = "Clueless",
                        Members =
                        [
                            "Boris",
                            "David",
                            "Theresa"
                        ]
                    },
                    new TeamStruct
                    {
                        Name = "ZZ",
                        Members =
                        [
                            "Has Beard",
                            "Has Beard",
                            "Is Called Beard"
                        ]
                    }
                ]
            },
            new ActivityStructWithCollection
            {
                Name = "Music Quiz",
                Day = DayOfWeek.Friday,
                Description = "A music pub quiz.",
                Notes = [],
                CoverCharge = 5.0m,
                IsTeamBased = true,
                Teams =
                [
                    new TeamStruct
                    {
                        Name = "Dazed and Confused",
                        Members =
                        [
                            "Robert",
                            "Jimmy",
                            "John",
                            "Jason"
                        ]
                    },
                    new TeamStruct { Name = "Banksy", Members = [] }
                ]
            }
        ];

        pub.FeaturedTeam = new TeamStruct { Name = "Not In This Lifetime", Members = ["Slash", "Axl"] };

        return pub;
    }

    private PubWithReadonlyStructCollections CreateConsistentPubWithReadonlyStructCollections(DbContext context)
    {
        var pub = Fixture.UseProxies
            ? context.CreateProxy<PubWithReadonlyStructCollections>()
            : new PubWithReadonlyStructCollections();

        pub.Id = Guid.NewGuid();
        pub.Name = "The FBI";

        pub.Activities =
        [
            new ActivityReadonlyStructWithCollection
            {
                Name = "Pub Quiz",
                Day = DayOfWeek.Monday,
                Description = "A general knowledge pub quiz.",
                Notes = ["One", "Two", "Three"],
                CoverCharge = 2.0m,
                IsTeamBased = true,
                Teams =
                [
                    new TeamReadonlyStruct
                    {
                        Name = "Clueless",
                        Members =
                        [
                            "Boris",
                            "David",
                            "Theresa"
                        ]
                    },
                    new TeamReadonlyStruct
                    {
                        Name = "ZZ",
                        Members =
                        [
                            "Has Beard",
                            "Has Beard",
                            "Is Called Beard"
                        ]
                    }
                ]
            },
            new ActivityReadonlyStructWithCollection
            {
                Name = "Music Quiz",
                Day = DayOfWeek.Friday,
                Description = "A music pub quiz.",
                Notes = [],
                CoverCharge = 5.0m,
                IsTeamBased = true,
                Teams =
                [
                    new TeamReadonlyStruct
                    {
                        Name = "Dazed and Confused",
                        Members =
                        [
                            "Robert",
                            "Jimmy",
                            "John",
                            "Jason"
                        ]
                    },
                    new TeamReadonlyStruct { Name = "Banksy", Members = [] }
                ]
            }
        ];

        pub.FeaturedTeam = new TeamReadonlyStruct { Name = "Not In This Lifetime", Members = ["Slash", "Axl"] };

        return pub;
    }

    private static FieldPubWithStructCollections CreateConsistentFieldCollectionPubWithStructs(DbContext context)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "The FBI",
            Activities =
            [
                new ActivityStructWithCollection
                {
                    Name = "Pub Quiz",
                    Day = DayOfWeek.Monday,
                    Description = "A general knowledge pub quiz.",
                    Notes = ["One", "Two", "Three"],
                    CoverCharge = 2.0m,
                    IsTeamBased = true,
                    Teams =
                    [
                        new TeamStruct
                        {
                            Name = "Clueless",
                            Members =
                            [
                                "Boris",
                                "David",
                                "Theresa"
                            ]
                        },
                        new TeamStruct
                        {
                            Name = "ZZ",
                            Members =
                            [
                                "Has Beard",
                                "Has Beard",
                                "Is Called Beard"
                            ]
                        }
                    ]
                },
                new ActivityStructWithCollection
                {
                    Name = "Music Quiz",
                    Day = DayOfWeek.Friday,
                    Description = "A music pub quiz.",
                    Notes = [],
                    CoverCharge = 5.0m,
                    IsTeamBased = true,
                    Teams =
                    [
                        new TeamStruct
                        {
                            Name = "Dazed and Confused",
                            Members =
                            [
                                "Robert",
                                "Jimmy",
                                "John",
                                "Jason"
                            ]
                        },
                        new TeamStruct { Name = "Banksy", Members = [] }
                    ]
                }
            ],
            FeaturedTeam = new TeamStruct { Name = "Not In This Lifetime", Members = ["Slash", "Axl"] }
        };

    private static FieldPubWithReadonlyStructCollections CreateConsistentFieldCollectionPubWithReadonlyStructs(DbContext context)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "The FBI",
            Activities =
            [
                new ActivityReadonlyStructWithCollection
                {
                    Name = "Pub Quiz",
                    Day = DayOfWeek.Monday,
                    Description = "A general knowledge pub quiz.",
                    Notes = ["One", "Two", "Three"],
                    CoverCharge = 2.0m,
                    IsTeamBased = true,
                    Teams =
                    [
                        new TeamReadonlyStruct("Clueless", ["Boris", "David", "Theresa"]),
                        new TeamReadonlyStruct("ZZ", ["Has Beard", "Has Beard", "Is Called Beard"])
                    ]
                },
                new ActivityReadonlyStructWithCollection
                {
                    Name = "Music Quiz",
                    Day = DayOfWeek.Friday,
                    Description = "A music pub quiz.",
                    Notes = [],
                    CoverCharge = 5.0m,
                    IsTeamBased = true,
                    Teams =
                    [
                        new TeamReadonlyStruct("Dazed and Confused", ["Robert", "Jimmy", "John", "Jason"]),
                        new TeamReadonlyStruct("Banksy", [])
                    ]
                }
            ],
            FeaturedTeam = new TeamReadonlyStruct("Not In This Lifetime", ["Slash", "Axl"])
        };
}
