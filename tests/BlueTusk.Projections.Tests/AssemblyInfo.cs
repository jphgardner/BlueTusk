// PostgreSQL logical decoding and relation-catalogue DDL are database-global.
// Parallel schema teardown can remove a relation while another test replays WAL.
// Each test still exercises its own concurrent snapshot and change-stream paths.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
