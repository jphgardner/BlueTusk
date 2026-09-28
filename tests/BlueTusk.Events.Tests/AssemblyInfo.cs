// Logical decoding observes database-global WAL and relation-catalogue changes.
// Parallel test schema teardown can remove a relation still needed by replay.
// Individual tests retain their own concurrent writer and delivery coverage.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
