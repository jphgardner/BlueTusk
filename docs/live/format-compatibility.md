# Live format compatibility

Every durable or externally visible Live format is registered in
`eng/live-formats.json`. A test binds the registry to implementation constants,
the versioned protobuf package, and named compatibility evidence.

| Format | Current | Minimum readable | Upgrade policy |
| --- | ---: | ---: | --- |
| gRPC contract | 1 | 1 | Protobuf v1; additive fields retain their numbers and incompatible changes require a new package. |
| Replay JSON | 1 | 1 | Current media type only; unknown event media types fail closed at the client boundary. |
| Resume token | 1 | 1 | Signed current version only; unknown versions cannot authenticate or resume. |
| PostgreSQL Live schema | 2 | 1 | Transactional in-place migration from v1; future versions fail closed without being rewritten. |

Before increasing a version, retain fixtures or a live migration test for every
supported older version, add future-version rejection, update the registry, and
document client and storage compatibility. Removing a readable version requires
an explicit migration or reconnect/reset path and a release note.
