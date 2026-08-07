# Streams format compatibility

BlueTusk records every durable or externally visible Streams format in
`eng/streams-formats.json`. A test binds that registry to the constants used by
the implementation and verifies that its named compatibility evidence still
exists. A format version cannot be changed silently.

| Format | Current | Minimum readable | Upgrade policy |
| --- | ---: | ---: | --- |
| Transaction envelope | 2 | 1 | Readers accept v1 and v2; writers emit v2. |
| Checkpoint | 1 | 1 | Backward-readable identity and position record. |
| File state store | 1 | 1 | Current version only; unknown versions fail closed. |
| PostgreSQL relay schema | 2 | 1 | Transactional in-place migration from v1 to v2. |
| Relay backup | 1 | 1 | Current version only; restore validates format, schema, framing, and integrity before replacing data. |
| Relay snapshot run | 1 | 1 | Current version only; SHA-256 integrity failure or incompatible bootstrap identity fails closed. |
| Transaction CloudEvent | 1 | 1 | Current event contract containing the independently versioned transaction envelope. |
| Transaction spool | 1 | 1 | Current process only; unknown versions and damaged records fail closed. |

The transaction spool bounds memory while assembling a transaction. It is not a
durable resume log and a `.ready` file is owned by its live reader. After a
worker/session failure, BlueTusk abandons the incomplete transaction assembly
and obtains safe redelivery from PostgreSQL or the durable relay. Artifacts found
at startup still consume the configured disk budget, so a restart cannot bypass
the storage ceiling. An operator may delete confirmed-orphan artifacts only
while that worker is stopped. BlueTusk never claims that a spool file can replace
a checkpoint.

Before increasing a version, add the new reader/writer or migration behavior,
retain fixtures for every supported older version, update the registry, and add
upgrade and future-version rejection tests. Removing a readable version is a
breaking storage compatibility change and requires an explicit migration tool
and release note.
