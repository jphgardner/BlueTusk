namespace BlueTusk.Live.Tests;

public sealed class LiveResultDifferTests
{
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
