# ADR 0017: Keep the EF-to-Data provider SPI internal and minimal

- Status: accepted
- Date: 2026-08-04

## Context

The EF provider needs provider-owned behavior that `DbConnection` and
`DbDataSource` do not expose: unredacted lifecycle configuration, immutable
runtime type-registry snapshots, negotiated server capabilities, diagnostics,
unpooled administration connections, pool clearing and type-catalogue reload.
Using concrete casts and construction throughout EF duplicated policy and made
the implementation boundary difficult to test.

There is no demonstrated third-party provider requirement for this seam.
Publishing it would freeze security- and lifecycle-sensitive implementation
details before usability evidence exists.

## Decision

Data owns three assembly-internal contracts in `BlueTusk.Data.Internal`:

- `IProviderServices` creates and validates provider connections and data
  sources and derives safe database-lifecycle settings;
- `IProviderConnection` supplies connection identity, unredacted internal
  configuration, type and Data-owned capability snapshots, diagnostics, administration
  connections and catalogue reload; and
- `IProviderDataSource` supplies source identity, configuration/type/diagnostic
  snapshots, logical and administration connections, and pool clearing.

The contracts are exposed to the EF assembly only through
`InternalsVisibleTo`. Concrete `BlueTuskConnection` and `BlueTuskDataSource`
implement them explicitly, so no public API is added. EF's public
`UseBlueTusk` overloads remain the only concrete-type boundary.

An EF-created logical connection is owned by its context. A caller-supplied data
source remains caller/container-owned. Administration connections are dedicated
and unpooled, inherit the originating provider configuration and never expose
their unredacted connection string through public diagnostics. Capability
snapshots may be absent until a physical connection is open.

## Consequences

- EF no longer constructs or casts concrete provider connections internally.
- Database lifecycle parsing and safety policy have one Data-owned
  implementation.
- Runtime type mappings consume immutable registry snapshots rather than a
  concrete data source.
- The SPI can change with the two assemblies without a public compatibility
  promise.
- A source architecture test rejects concrete construction/casts outside the
  public configuration file; contract and ownership tests exercise the seam.

If a real third-party provider integration appears, its requirements must be
measured before any subset becomes public.
