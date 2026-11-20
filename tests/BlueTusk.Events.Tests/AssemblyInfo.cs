// Logical decoding observes database-global WAL and relation-catalogue changes.
// Parallel test schema teardown can remove a relation still needed by replay.
// Individual tests retain their own concurrent writer and delivery coverage.
#if NET8_0_OR_GREATER
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
#else
[assembly: CollectionBehavior(DisableTestParallelization = true)]
#endif
