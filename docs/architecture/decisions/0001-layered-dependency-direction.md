# ADR 0001: Enforce layered dependency direction

- Status: Accepted
- Date: 2026-07-21

## Context

The provider spans byte transport, PostgreSQL protocol semantics, ADO.NET, replication, EF Core, and optional extensions. Mixing these concerns would make wire behaviour hard to test and force low-level packages to inherit volatile dependencies.

## Decision

Use the dependency direction in `docs/architecture/overview.md`. Protocol code cannot expose ADO.NET concepts; Data cannot expose EF Core concepts. Optional extensions integrate through `BlueTusk.Extensions.Abstractions`.

## Consequences

Some operations require orchestration types in `BlueTusk.Client` even when most of their implementation is lower-level. Cross-layer convenience APIs belong in the highest applicable layer.

