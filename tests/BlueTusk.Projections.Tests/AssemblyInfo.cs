// PostgreSQL logical decoding and relation-catalogue DDL are database-global.
// Parallel schema teardown can remove a relation while another test replays WAL.
// Each test still exercises its own concurrent snapshot and change-stream paths.
#if NET8_0_OR_GREATER
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
#else
[assembly: CollectionBehavior(DisableTestParallelization = true)]
#endif
