// This suite exercises database-global PostgreSQL objects such as event triggers,
// subscriptions, foreign servers, and tablespaces. Running those fixtures in
// parallel makes otherwise isolated schemas observe or remove each other's DDL.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
