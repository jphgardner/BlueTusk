namespace BlueTusk.IntegrationTests;

// These tests assert counts from a process-wide MeterListener. Keep other
// integration collections from contributing measurements during the assertion.
[CollectionDefinition("Global multiplexing metrics", DisableParallelization = true)]
public sealed class MultiplexingMetricsIsolation
{
}
