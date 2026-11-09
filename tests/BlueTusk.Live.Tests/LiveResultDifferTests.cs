namespace BlueTusk.Live.Tests;

public sealed class LiveResultDifferTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10)]
    [InlineData(1_000)]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void Sparse_replacement_allocations_do_not_scale_with_the_complete_row_payload(int count)
    {
        var initial = LiveResultDiffer.Initial<Row, int>(
            Enumerable.Range(0, count).Select(index => new Row(index, "original")).ToArray(),
            static row => row.Id).Snapshot;
        Row[] replacements = [new(count - 1, "changed")];
        // Warm generic/JIT paths before measuring only the incremental operation.
        _ = LiveResultDiffer.DiffAffected(initial, replacements, static row => row.Id);
        Func<Row, int> keySelector = static row => row.Id;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var changed = LiveResultDiffer.DiffAffected(initial, replacements, keySelector);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"{count} rows, one replacement: {allocated} allocated bytes");
        Assert.InRange(allocated, 1, 32_768);
        Assert.Equal("original", initial.Rows[count - 1].Value);
        Assert.Equal("changed", changed.Snapshot.Rows[count - 1].Value);
        Assert.Single(changed.Events);
    }

    [Fact]
    public void Persistent_rows_preserve_history_across_chunk_boundaries_and_authoritative_changes()
    {
        var rows = Enumerable.Range(0, 1_025).Select(index => new Row(index, "original")).ToArray();
        var initial = LiveResultDiffer.Initial<Row, int>(rows, static row => row.Id).Snapshot;
        int[] keys = [0, 255, 256, 511, 512, 1_024];
        var changed = LiveResultDiffer.DiffAffected(initial,
            keys.Select(key => new Row(key, "changed")).ToArray(), static row => row.Id);
        var historical = changed.Snapshot.Rows.ToArray();
        var again = LiveResultDiffer.DiffAffected(changed.Snapshot,
            keys.Select(key => new Row(key, "again")).ToArray(), static row => row.Id,
            options: new LiveDiffOptions { MaximumEventsPerRefresh = 1 });

        Assert.Equal(rows, initial.Rows);
        Assert.Equal(historical, changed.Snapshot.Rows);
        Assert.Equal(1_025, Assert.Single(again.Events).Rows!.Count);
        Assert.All(keys, key => Assert.Equal("again", again.Snapshot.Rows[key].Value));
        var authoritative = LiveResultDiffer.Diff(again.Snapshot,
            again.Snapshot.Rows.Reverse().Append(new Row(2_000, "new")).ToArray(), static row => row.Id);
        Assert.Equal(2_000, authoritative.Snapshot.Keys[^1]);
        Assert.Equal(1_024, authoritative.Snapshot.Keys[0]);
        Assert.Equal(historical, changed.Snapshot.Rows);
    }

    [Fact]
    public void Failed_affected_validation_does_not_mutate_shared_chunks_and_noop_reuses_snapshot()
    {
        var rows = new[] { new Row(1, "one"), new Row(2, "two") };
        var initial = LiveResultDiffer.Initial<Row, int>(rows, static row => row.Id).Snapshot;
        Assert.Throws<InvalidOperationException>(() => LiveResultDiffer.DiffAffected(initial,
            [new Row(1, "changed"), new Row(1, "duplicate")], static row => row.Id));
        Assert.Throws<InvalidOperationException>(() => LiveResultDiffer.DiffAffected(initial,
            [new Row(1, "changed"), new Row(3, "unknown")], static row => row.Id));
        Assert.Equal(rows, initial.Rows);
        Assert.Same(initial, LiveResultDiffer.DiffAffected(initial, rows, static row => row.Id).Snapshot);
    }

    [Fact]
    public void Diff_preserves_keys_and_emits_add_update_remove_and_reorder()
    {
        var initial = LiveResultDiffer.Initial<Row, int>(
            [new Row(1, "one"), new Row(2, "two"), new Row(3, "three")],
            static row => row.Id,
            sequence: 7);

        var diff = LiveResultDiffer.Diff(
            initial.Snapshot,
            [new Row(3, "THREE"), new Row(2, "two"), new Row(4, "four")],
            static row => row.Id,
            nextSequence: 8);

        Assert.Collection(
            diff.Events,
            removed =>
            {
                Assert.Equal(LiveEventKind.RowRemoved, removed.Kind);
                Assert.Equal(1, removed.Key);
                Assert.Equal(8, removed.Sequence);
            },
            updated =>
            {
                Assert.Equal(LiveEventKind.RowUpdated, updated.Kind);
                Assert.Equal(3, updated.Key);
                Assert.Equal("THREE", updated.Row!.Value);
            },
            added =>
            {
                Assert.Equal(LiveEventKind.RowAdded, added.Kind);
                Assert.Equal(4, added.Key);
                Assert.Equal(2, added.CurrentIndex);
            },
            reordered =>
            {
                Assert.Equal(LiveEventKind.ResultReordered, reordered.Kind);
                Assert.Equal([3, 2, 4], reordered.Order);
            });
    }

    [Fact]
    public void Excessive_diff_becomes_one_authoritative_reset()
    {
        var initial = LiveResultDiffer.Initial<Row, int>(
            [new Row(1, "one"), new Row(2, "two")],
            static row => row.Id);
        var diff = LiveResultDiffer.Diff(
            initial.Snapshot,
            [new Row(3, "three"), new Row(4, "four")],
            static row => row.Id,
            options: new LiveDiffOptions { MaximumEventsPerRefresh = 1 },
            nextSequence: 2);

        var reset = Assert.Single(diff.Events);
        Assert.Equal(LiveEventKind.ResultReset, reset.Kind);
        Assert.Equal(LiveResetReason.DiffLimitExceeded, reset.ResetReason);
        Assert.Equal([3, 4], reset.Rows!.Select(row => row.Id));
    }

    [Fact]
    public void Duplicate_keys_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => LiveResultDiffer.Initial<Row, int>(
            [new Row(1, "one"), new Row(1, "duplicate")],
            static row => row.Id));
    }

    [Fact]
    public void Snapshot_defensively_copies_rows_and_remains_stable_for_later_diffs()
    {
        var rows = new[] { new Row(1, "one"), new Row(2, "two") };
        var initial = LiveResultDiffer.Initial<Row, int>(rows, static row => row.Id);
        rows[0] = new Row(99, "mutated");

        var diff = LiveResultDiffer.Diff(
            initial.Snapshot,
            [new Row(1, "ONE"), new Row(2, "two")],
            static row => row.Id,
            nextSequence: 2);

        Assert.Equal(1, initial.Snapshot.Keys[0]);
        var updated = Assert.Single(diff.Events);
        Assert.Equal(LiveEventKind.RowUpdated, updated.Kind);
        Assert.Equal(1, updated.Key);
    }

    [Fact]
    public void DiffAffected_updates_only_named_rows_and_preserves_result_order()
    {
        var initial = LiveResultDiffer.Initial<Row, int>(
            [new Row(1, "one"), new Row(2, "two"), new Row(3, "three")],
            static row => row.Id,
            sequence: 3);

        var diff = LiveResultDiffer.DiffAffected(
            initial.Snapshot,
            [new Row(2, "TWO")],
            static row => row.Id,
            nextSequence: 4);

        var updated = Assert.Single(diff.Events);
        Assert.Equal(LiveEventKind.RowUpdated, updated.Kind);
        Assert.Equal(1, updated.PreviousIndex);
        Assert.Equal(1, updated.CurrentIndex);
        Assert.Equal([1, 2, 3], diff.Snapshot.Keys);
        Assert.Equal(["one", "TWO", "three"], diff.Snapshot.Rows.Select(static row => row.Value));
    }

    [Fact]
    public void DiffAffected_rejects_membership_changes()
    {
        var initial = LiveResultDiffer.Initial<Row, int>(
            [new Row(1, "one")],
            static row => row.Id);

        var error = Assert.Throws<InvalidOperationException>(() => LiveResultDiffer.DiffAffected(
            initial.Snapshot,
            [new Row(2, "two")],
            static row => row.Id));

        Assert.Contains("cannot add result key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffAffected_matches_full_authoritative_diff_after_randomized_updates()
    {
        var random = new Random(0xB1_1E);
        var expectedRows = Enumerable.Range(1, 256)
            .Select(id => new Row(id, $"value-{id}-0"))
            .ToArray();
        var affectedSnapshot = LiveResultDiffer.Initial<Row, int>(
            expectedRows,
            static row => row.Id).Snapshot;

        for (var transaction = 1; transaction <= 200; transaction++)
        {
            var replacements = new Dictionary<int, Row>();
            var replacementCount = random.Next(1, 17);
            while (replacements.Count < replacementCount)
            {
                var id = random.Next(1, expectedRows.Length + 1);
                replacements[id] = new Row(id, $"value-{id}-{transaction}");
            }

            foreach (var replacement in replacements.Values)
            {
                expectedRows[replacement.Id - 1] = replacement;
            }

            var affected = LiveResultDiffer.DiffAffected(
                affectedSnapshot,
                replacements.Values.ToArray(),
                static row => row.Id,
                nextSequence: transaction + 1);
            var authoritative = LiveResultDiffer.Diff(
                affectedSnapshot,
                expectedRows,
                static row => row.Id,
                nextSequence: transaction + 1);

            Assert.Equal(authoritative.Snapshot.Keys, affected.Snapshot.Keys);
            Assert.Equal(authoritative.Snapshot.Rows, affected.Snapshot.Rows);
            Assert.Equal(
                authoritative.Events.OrderBy(static change => change.Key).Select(static change =>
                    (change.Kind, change.Key, change.PreviousIndex, change.CurrentIndex, change.Row)),
                affected.Events.OrderBy(static change => change.Key).Select(static change =>
                    (change.Kind, change.Key, change.PreviousIndex, change.CurrentIndex, change.Row)));
            affectedSnapshot = affected.Snapshot;
        }
    }

    private sealed record Row(int Id, string Value);
}
