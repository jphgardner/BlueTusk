// Derived from Entity Framework Core 10.0.11 (src/EFCore/ChangeTracking/Internal/ChangeDetector.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
// BlueTusk change: complex collections of value types are diffed by position and value. EF Core matches collection
// elements by reference (dotnet/efcore#31411), and a value-type element is copied into a new box on every read, so no
// element would ever match. Models without value-type complex collections are handled by EF Core's own detector.

#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Collections;
using System.Diagnostics;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;

internal sealed class BlueTuskChangeDetector(
    IDiagnosticsLogger<DbLoggerCategory.ChangeTracking> logger,
    ILoggingOptions loggingOptions)
    : ChangeDetector(logger, loggingOptions)
{
    private const string ValueTypeComplexCollectionsAnnotation = "BlueTusk:HasValueTypeComplexCollections";

    private readonly IDiagnosticsLogger<DbLoggerCategory.ChangeTracking> _logger = logger;
    private readonly ILoggingOptions _loggingOptions = loggingOptions;
    private bool _inCascadeDelete;

    private static bool HasValueTypeComplexCollections(IModel model)
        => model.GetOrAddRuntimeAnnotationValue(
            ValueTypeComplexCollectionsAnnotation,
            static m => m!.GetEntityTypes().Any(BlueTuskValueTypeCollectionAccessors.HasValueTypeCollection),
            model);

    private static readonly bool UseOldBehavior37387 =
        AppContext.TryGetSwitch("Microsoft.EntityFrameworkCore.Issue37387", out var enabled) && enabled;

    private static readonly bool UseOldBehavior37890 =
        AppContext.TryGetSwitch("Microsoft.EntityFrameworkCore.Issue37890", out var enabled) && enabled;

    public override void DetectChanges(IStateManager stateManager)
    {
        if (!HasValueTypeComplexCollections(stateManager.Model))
        {
            base.DetectChanges(stateManager);
            return;
        }

        if (_inCascadeDelete)
        {
            return;
        }

        try
        {
            _inCascadeDelete = true;

            OnDetectingAllChanges(stateManager);
            var changesFound = false;

            _logger.DetectChangesStarting(stateManager.Context);

            foreach (var entry in stateManager.ToList()) // Might be too big, but usually _all_ entities are using Snapshot tracking
            {
                switch (entry.EntityState)
                {
                    case EntityState.Detached:
                        break;
                    case EntityState.Deleted:
                        if (entry.SharedIdentityEntry != null)
                        {
                            continue;
                        }

                        goto default;
                    default:
                        if (LocalDetectChanges(entry))
                        {
                            changesFound = true;
                        }

                        break;
                }
            }

            _logger.DetectChangesCompleted(stateManager.Context);

            OnDetectedAllChanges(stateManager, changesFound);
        }
        finally
        {
            _inCascadeDelete = false;
        }
    }

    public override void DetectChanges(InternalEntityEntry entry)
    {
        if (!HasValueTypeComplexCollections(entry.EntityType.Model))
        {
            base.DetectChanges(entry);
            return;
        }

        if (_inCascadeDelete)
        {
            return;
        }

        try
        {
            _inCascadeDelete = true;
            DetectChanges(entry, [entry]);
        }
        finally
        {
            _inCascadeDelete = false;
        }
    }

    public override void DetectChanges(InternalComplexEntry entry)
    {
        if (!HasValueTypeComplexCollections(entry.ComplexType.Model))
        {
            base.DetectChanges(entry);
            return;
        }

        if (entry.EntityState == EntityState.Detached)
        {
            return;
        }

        LocalDetectChanges(entry);
    }

    private void DetectChanges(InternalEntityEntry entry, HashSet<InternalEntityEntry> visited)
    {
        if (entry.EntityState == EntityState.Detached)
        {
            return;
        }

        foreach (var foreignKey in entry.EntityType.GetForeignKeys())
        {
            var principalEntry = entry.StateManager.FindPrincipal(entry, foreignKey);

            if (principalEntry != null
                && !visited.Contains(principalEntry))
            {
                visited.Add(principalEntry);

                DetectChanges(principalEntry, visited);
            }
        }

        LocalDetectChanges(entry);
    }

    private bool LocalDetectChanges(InternalEntityEntry entry)
    {
        var changesFound = false;

        var entityType = entry.EntityType;
        if (entityType.GetChangeTrackingStrategy() != ChangeTrackingStrategy.Snapshot)
        {
            return false;
        }

        OnDetectingEntityChanges(entry);

        changesFound |= LocalDetectChanges((InternalEntryBase)entry);

        if (entry.HasRelationshipSnapshot)
        {
            foreach (var navigation in entityType.GetNavigations())
            {
                if (DetectNavigationChange(entry, navigation))
                {
                    changesFound = true;
                }
            }

            foreach (var navigation in entityType.GetSkipNavigations())
            {
                if (DetectNavigationChange(entry, navigation))
                {
                    changesFound = true;
                }
            }
        }

        OnDetectedEntityChanges(entry, changesFound);

        return changesFound;
    }

    private bool LocalDetectChanges(InternalEntryBase entry)
    {
        var changesFound = false;
        foreach (var property in entry.StructuralType.GetFlattenedProperties())
        {
            if (property.GetOriginalValueIndex() >= 0
                && !entry.IsModified(property)
                && !entry.IsConceptualNull(property))
            {
                if (DetectValueChange(entry, property))
                {
                    changesFound = true;
                }
            }

            if (DetectKeyChange(entry, property))
            {
                changesFound = true;
            }
        }

        foreach (var complexProperty in entry.StructuralType.GetFlattenedComplexProperties())
        {
            if (complexProperty.IsCollection)
            {
                if (DetectComplexCollectionChangesCore(entry, complexProperty))
                {
                    changesFound = true;
                }
            }
            else if (!UseOldBehavior37387
                && complexProperty.IsNullable
                && complexProperty.GetOriginalValueIndex() >= 0)
            {
                if (DetectComplexPropertyChange(entry, complexProperty))
                {
                    changesFound = true;
                }
            }
        }

        return changesFound;
    }

    private bool DetectComplexCollectionChangesCore(InternalEntryBase entry, IComplexProperty complexProperty)
    {
        Debug.Assert(complexProperty.IsCollection, $"Expected {complexProperty.Name} to be a collection.");

        if (!entry.HasOriginalValuesSnapshot || complexProperty.GetOriginalValueIndex() < 0)
        {
            foreach (var complexEntry in entry.GetComplexCollectionEntries(complexProperty))
            {
                if (complexEntry != null)
                {
                    LocalDetectChanges(complexEntry);
                }
            }

            return false;
        }

        var originalEntries = new Dictionary<object, InternalComplexEntry>(ReferenceEqualityComparer.Instance);
        var currentCollection = (IList?)entry[complexProperty];
        // The elements in the original collection might be the same instances as in the current collection, so their properties shouldn't be used for comparison.
        var originalCollection = (IList?)entry.GetOriginalValue(complexProperty);
        if (complexProperty.ComplexType.ClrType.IsValueType)
        {
            (currentCollection, originalCollection) = BlueTuskValueTypeElementIdentity.Create(
                complexProperty.ComplexType, currentCollection, originalCollection);
        }

        var changesFound = (currentCollection == null) != (originalCollection == null);

        entry.EnsureComplexCollectionEntriesCapacity(
            complexProperty, currentCollection?.Count ?? 0, originalCollection?.Count ?? 0, trim: false);
        var originalNulls = new HashSet<int>();
        var removed = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        if (originalCollection != null
            && entry.EntityState != EntityState.Added)
        {
            var originalEntriesList = entry.GetComplexCollectionOriginalEntries(complexProperty);
            for (var i = 0; i < originalCollection.Count; i++)
            {
                var element = originalCollection[i];
                if (element == null)
                {
                    originalNulls.Add(i);
                    continue;
                }

                removed[element] = i;

                var originalEntry = originalEntriesList[i];
                if (originalEntry == null)
                {
                    // The entry hasn't been created for this element yet, so assuming it's going to be deleted
                    // unless it's matched with a current element below.
                    originalEntry = entry.GetComplexCollectionOriginalEntry(complexProperty, i);
                    originalEntry.SetEntityState(EntityState.Deleted);
                }

                Debug.Assert(
                    originalEntry.OriginalOrdinal == i, $"Expected original ordinal {originalEntry.OriginalOrdinal} to be equal to {i}.");

                originalEntries[element] = originalEntry;
            }
        }

        var currentNulls = new HashSet<int>();
        var added = new List<int>();
        var currentEntries = entry.GetComplexCollectionEntries(complexProperty);
        if (entry.EntityState != EntityState.Added
            && entry.EntityState != EntityState.Deleted
            && currentCollection != null)
        {
            for (var i = 0; i < currentCollection.Count; i++)
            {
                var element = currentCollection[i];
                if (element == null)
                {
                    currentNulls.Add(i);
                    continue;
                }

                if (removed.Remove(element, out var originalOrdinal))
                {
                    // Existing instance found, fix ordinal and check for changes.
                    var originalEntry = originalEntries[element];

                    Debug.Assert(
                        originalEntry.OriginalOrdinal == originalOrdinal,
                        $"The OriginalOrdinal for entry of the element at original ordinal {originalOrdinal} is {originalEntry.OriginalOrdinal}.");
                    if (originalEntry.Ordinal != i)
                    {
                        if (originalEntry.EntityState is EntityState.Detached or EntityState.Deleted)
                        {
                            var currentEntry = entry.GetComplexCollectionEntry(complexProperty, i);
                            if (currentEntry.EntityState is EntityState.Added)
                            {
                                // Prefer to use the current entry if possible
                                currentEntry.OriginalOrdinal = originalEntry.OriginalOrdinal;
                                originalEntry.SetEntityState(EntityState.Detached);
                                currentEntry.SetEntityState(EntityState.Unchanged);
                                originalEntry = currentEntry;
                            }
                            else
                            {
                                originalEntry.Ordinal = i;
                                originalEntry.SetEntityState(EntityState.Unchanged);
                            }
                        }
                        else
                        {
                            Debug.Assert(
                                originalEntry.Ordinal > i || currentNulls.Contains(originalEntry.Ordinal),
                                $"Expected the entry that was previously at {originalEntry.Ordinal} to have been encountered at an ordinal before {i}.");
                            entry.MoveComplexCollectionEntry(complexProperty, originalEntry.Ordinal, i);

                            changesFound = true;
                        }
                    }

                    if (LocalDetectChanges(originalEntry))
                    {
                        if (originalEntry.EntityState == EntityState.Unchanged)
                        {
                            originalEntry.SetEntityState(EntityState.Modified);
                            changesFound = true;
                        }
                    }
                }
                else
                {
                    // The instance was not found in the original collection, so it could be a replacement or an addition.
                    var currentEntry = entry.GetComplexCollectionEntry(complexProperty, i);
                    if (originalNulls.Remove(currentEntry.OriginalOrdinal))
                    {
                        var nullEntry = entry.GetComplexCollectionOriginalEntry(complexProperty, currentEntry.OriginalOrdinal);
                        if (nullEntry != currentEntry)
                        {
                            currentEntry.SetEntityState(EntityState.Detached);
                        }

                        if (nullEntry.Ordinal == -1)
                        {
                            nullEntry.Ordinal = i;
                        }
                        else
                        {
                            entry.MoveComplexCollectionEntry(complexProperty, nullEntry.Ordinal, i);
                        }

                        nullEntry.SetEntityState(EntityState.Modified);
                        changesFound = true;
                    }
                    else
                    {
                        added.Add(i);
                        if (currentEntry.EntityState is not EntityState.Detached and not EntityState.Added)
                        {
                            // If the element was newly added then there should be a null entry at some ordinal,
                            // otherwise it will be treated as a replacement.
                            var nullEntryOrdinal = -1;
                            for (var j = originalCollection?.Count ?? i + 1; j < currentEntries.Count; j++)
                            {
                                var newEntry = currentEntries[j];
                                if (newEntry == null)
                                {
                                    nullEntryOrdinal = j;
                                    break;
                                }

                                if (newEntry.OriginalOrdinal >= (originalCollection?.Count ?? 0)
                                    || newEntry.EntityState == EntityState.Detached)
                                {
                                    nullEntryOrdinal = j;
                                    newEntry.SetEntityState(EntityState.Detached);
                                    break;
                                }
                            }

                            if (nullEntryOrdinal != -1)
                            {
                                entry.MoveComplexCollectionEntry(complexProperty, nullEntryOrdinal, i);
                            }
                        }
                    }
                }
            }
        }

        // Try to match up the added entries with the original nulls or removed elements.
        foreach (var addedOrdinal in added)
        {
            var currentEntry = entry.GetComplexCollectionEntry(complexProperty, addedOrdinal);
            if (currentEntry.EntityState is EntityState.Detached or EntityState.Added)
            {
                var originalOrdinal = -1;
                var originalWasNull = false;
                if (originalNulls.Count > 0)
                {
                    originalWasNull = true;
                    originalOrdinal = originalNulls.First();
                    originalNulls.Remove(originalOrdinal);
                }
                else if (removed.Count > 0)
                {
                    (var removedElement, originalOrdinal) = removed.First();
                    removed.Remove(removedElement);
                }
                else
                {
                    currentEntry.SetEntityState(EntityState.Added);
                    continue;
                }

                if (originalOrdinal != -1)
                {
                    // An unmatched null or original element was found, so use it as the original and discard the extra entry.
                    var originalEntry = entry.GetComplexCollectionOriginalEntry(complexProperty, originalOrdinal);
                    if (originalEntry != currentEntry)
                    {
                        if (currentEntry.EntityState is EntityState.Detached)
                        {
                            currentEntry = originalEntry;
                            if (currentEntry.Ordinal == -1)
                            {
                                currentEntry.Ordinal = addedOrdinal;
                            }
                            else
                            {
                                entry.MoveComplexCollectionEntry(complexProperty, currentEntry.Ordinal, addedOrdinal);
                            }
                        }
                        else
                        {
                            originalEntry.SetEntityState(EntityState.Detached);
                            if (currentEntry.OriginalOrdinal == -1)
                            {
                                currentEntry.OriginalOrdinal = originalOrdinal;
                            }
                            else
                            {
                                entry.MoveComplexCollectionEntry(
                                    complexProperty, currentEntry.OriginalOrdinal, originalOrdinal, original: true);
                            }
                        }
                    }

                    currentEntry.SetEntityState(originalWasNull ? EntityState.Modified : EntityState.Unchanged);
                }
            }
            else if (!originalNulls.Remove(currentEntry.OriginalOrdinal))
            {
                var removedPair = removed.FirstOrDefault(r => r.Value == currentEntry.OriginalOrdinal);
                if (removedPair.Key != null)
                {
                    removed.Remove(removedPair.Key);
                }
                else if (removed.Count > 0)
                {
                    var (removedElement, originalOrdinal) = removed.First();
                    removed.Remove(removedElement);

                    if (currentEntry.OriginalOrdinal != originalOrdinal)
                    {
                        var movedEntry = entry.GetComplexCollectionOriginalEntry(complexProperty, currentEntry.OriginalOrdinal);
                        var movedOrdinal = movedEntry.OriginalOrdinal;
                        entry.MoveComplexCollectionEntry(complexProperty, movedEntry.Ordinal, addedOrdinal);
                        movedEntry.SetEntityState(EntityState.Unchanged);
                        entry.MoveComplexCollectionEntry(complexProperty, currentEntry.Ordinal, movedOrdinal);
                    }
                    else
                    {
                        currentEntry.SetEntityState(EntityState.Unchanged);
                    }
                }
                else
                {
                    Debug.Assert(
                        false,
                        $"Expected the entry at {addedOrdinal} to have been removed or matched with a null entry. Current state: {currentEntry.EntityState}.");

                    currentEntry.SetEntityState(EntityState.Added);
                }
            }

            if (currentEntry.EntityState is EntityState.Unchanged or EntityState.Modified)
            {
                if (LocalDetectChanges(currentEntry))
                {
                    currentEntry.SetEntityState(EntityState.Modified);
                    changesFound = true;
                }
            }
        }

        if (entry.EntityState != EntityState.Added
            && removed.Count > 0)
        {
            foreach (var (removedElement, originalOrdinal) in removed)
            {
                var originalEntry = originalEntries[removedElement];
                Debug.Assert(
                    originalEntry.OriginalOrdinal == originalOrdinal,
                    $"The OriginalOrdinal for entry of the element at original ordinal {originalOrdinal} is {originalEntry.OriginalOrdinal}.");
                var newCurrentOrdinal = originalEntry.Ordinal;
                if (originalEntry.EntityState is EntityState.Unchanged or EntityState.Modified or EntityState.Detached)
                {
                    // Try to match removed elements with nulls to mark the entry as modified
                    if (!currentNulls.Remove(newCurrentOrdinal))
                    {
                        if (currentNulls.Count > 0)
                        {
                            newCurrentOrdinal = currentNulls.First();
                            currentNulls.Remove(newCurrentOrdinal);
                        }
                        else
                        {
                            newCurrentOrdinal = -1;
                        }
                    }
                }
                else
                {
                    newCurrentOrdinal = -1;
                }

                if (newCurrentOrdinal == -1
                    || newCurrentOrdinal >= (currentCollection?.Count ?? 0))
                {
                    // If the are no unmatched nulls left, treat the original entry as deleted
                    originalEntry.SetEntityState(EntityState.Deleted);
                }
                else
                {
                    var existingEntry = entry.GetComplexCollectionEntry(complexProperty, newCurrentOrdinal);
                    if (existingEntry != originalEntry)
                    {
                        existingEntry.SetEntityState(EntityState.Detached);
                    }

                    if (originalEntry.EntityState is EntityState.Deleted or EntityState.Detached)
                    {
                        originalEntry.Ordinal = newCurrentOrdinal;
                        originalEntry.SetEntityState(EntityState.Modified);
                    }
                    else
                    {
                        originalEntry.SetEntityState(EntityState.Modified);
                        entry.MoveComplexCollectionEntry(complexProperty, originalEntry.Ordinal, newCurrentOrdinal);
                    }
                }
            }

            changesFound = true;
        }

        if (originalNulls.Count > 0)
        {
            // If there are any unmatched original nulls left, they should be treated as deleted
            // unless they can be matched with a current null.
            foreach (var originalNull in originalNulls)
            {
                var nullEntry = entry.GetComplexCollectionOriginalEntry(complexProperty, originalNull);
                if (currentNulls.Contains(nullEntry.Ordinal))
                {
                    currentNulls.Remove(nullEntry.Ordinal);
                    nullEntry.SetEntityState(EntityState.Unchanged);
                    changesFound |= nullEntry.Ordinal != nullEntry.OriginalOrdinal;
                    continue;
                }

                if (currentNulls.Count > 0)
                {
                    var currentNullOrdinal = currentNulls.First();
                    currentNulls.Remove(currentNullOrdinal);
                    if (nullEntry.Ordinal != -1)
                    {
                        entry.MoveComplexCollectionEntry(complexProperty, nullEntry.Ordinal, currentNullOrdinal);
                        nullEntry.SetEntityState(EntityState.Unchanged);
                    }
                    else
                    {
                        entry.GetComplexCollectionEntry(complexProperty, currentNullOrdinal).SetEntityState(EntityState.Detached);
                        nullEntry.Ordinal = currentNullOrdinal;
                    }

                    changesFound |= nullEntry.Ordinal != nullEntry.OriginalOrdinal;
                    continue;
                }

                Debug.Assert(
                    nullEntry.EntityState is EntityState.Deleted or EntityState.Detached,
                    $"Expected null entry at {originalNull} to be deleted or detached, current state {nullEntry.EntityState}.");
                nullEntry.SetEntityState(EntityState.Deleted);
                changesFound = true;
            }
        }

        if (currentNulls.Count > 0)
        {
            // If there are any unmatched current nulls left, they should be treated as added.
            foreach (var currentNull in currentNulls)
            {
                var nullEntry = entry.GetComplexCollectionEntry(complexProperty, currentNull);
                if (nullEntry.EntityState is EntityState.Added)
                {
                    continue;
                }

                Debug.Assert(
                    nullEntry.EntityState is EntityState.Added or EntityState.Detached,
                    $"Expected null entry at {currentNull} to be added or detached, current state {nullEntry.EntityState}.");
                nullEntry.SetEntityState(EntityState.Added);
                changesFound = true;
            }
        }

        // Trim excess entries
        entry.EnsureComplexCollectionEntriesCapacity(
            complexProperty, currentCollection?.Count ?? 0, originalCollection?.Count ?? 0, trim: true);

        if (changesFound)
        {
            entry.SetPropertyModified(complexProperty);
        }

        return changesFound;
    }

    private bool DetectKeyChange(IInternalEntry entry, IProperty property)
    {
        if (property.GetRelationshipIndex() < 0)
        {
            return false;
        }

        var entityEntry = entry as InternalEntityEntry ?? throw new UnreachableException("Complex type entry with a navigation");
        var snapshotValue = entityEntry.GetRelationshipSnapshotValue(property);
        var currentValue = entityEntry[property];

        var comparer = property.GetKeyValueComparer();

        // Note that mutation of a byte[] key is not supported or detected, but two different instances
        // of byte[] with the same content must be detected as equal.
        if (!comparer.Equals(currentValue, snapshotValue))
        {
            var keys = property.GetContainingKeys();
            var foreignKeys = property.GetContainingForeignKeys()
                .Where(fk => fk.DeclaringEntityType.IsAssignableFrom(entityEntry.EntityType));

            if (_loggingOptions.IsSensitiveDataLoggingEnabled)
            {
                _logger.ForeignKeyChangeDetectedSensitive(entityEntry, property, snapshotValue, currentValue);
            }
            else
            {
                _logger.ForeignKeyChangeDetected(entityEntry, property, snapshotValue, currentValue);
            }

            entityEntry.StateManager.InternalEntityEntryNotifier.KeyPropertyChanged(
                entityEntry, property, keys, foreignKeys, snapshotValue, currentValue);

            return true;
        }

        return false;
    }
}
