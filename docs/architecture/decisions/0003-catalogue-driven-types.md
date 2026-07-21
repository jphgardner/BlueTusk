# ADR 0003: Discover types from catalogues

- Status: Accepted
- Date: 2026-07-21

## Context

PostgreSQL databases can contain user-defined and extension-provided types unknown when BlueTusk was compiled.

## Decision

OID is the connection-local type identity. Load descriptors from PostgreSQL catalogues, bind codecs separately, and preserve unknown values with their descriptor, format, and bytes. Built-in OIDs are bootstrap information, not a closed type list.

## Consequences

Pools require catalogue cache invalidation. Values cannot be interpreted by OID alone across unrelated servers. Extension codecs use the same registry as built-in codecs.

