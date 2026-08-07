# Sync format compatibility

Every durable or externally visible Sync format is registered in
`eng/sync-formats.json`. A test binds the registry to implementation constants
and named compatibility evidence.

| Format | Current | Minimum readable | Upgrade policy |
| --- | ---: | ---: | --- |
| NATS envelope | 1 | 1 | Current integrity-checked binary envelope only; unknown versions fail closed. |
| OpenSearch storage | 2 | 2 | Current generation/control format only; incompatible changes require an explicit rebuild. |
| PostgreSQL Sync schema | 2 | 1 | Transactional in-place migration from v1; future versions fail closed without being rewritten. |
| Redis document | 1 | 1 | Current integrity-checked binary document only; unknown versions fail closed. |
| Redis storage | 2 | 2 | Current pipeline storage format only; incompatible changes require an explicit rebuild. |
| Transform fingerprint | 1 | 1 | Canonical length-prefixed SHA-256 input; the checked fixture prevents silent identity drift. |

Before increasing a version, retain fixtures or a live migration test for every
supported older version, add future-version rejection, update the registry, and
document destination compatibility. Removing a readable version requires an
explicit migration, rebuild, or replay boundary and a release note.
