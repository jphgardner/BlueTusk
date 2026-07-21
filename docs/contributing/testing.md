# Testing

## Test levels

- Unit tests cover deterministic framing, codecs, state transitions, and configuration.
- Conformance tests use a scriptable fake server to force network and protocol edge cases.
- Integration tests run against every supported PostgreSQL major version.
- Compatibility tests compare selected outcomes with libpq or other providers, then resolve differences against PostgreSQL behaviour.
- Stress tests cover cancellation, pool churn, concurrent readers, and long-running replication.

Tests requiring a server read `BLUETUSK_TEST_CONNECTION_STRING` and must skip with a clear reason when it is absent. Credentials must never be printed, including in failed test output.

