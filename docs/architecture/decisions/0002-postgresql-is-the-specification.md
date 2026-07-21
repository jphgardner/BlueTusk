# ADR 0002: Treat PostgreSQL as the specification

- Status: Accepted
- Date: 2026-07-21

## Decision

PostgreSQL protocol documentation, SQL documentation, system catalogues, source code where necessary, and reproducible server behaviour are authoritative. Npgsql and libpq may be used for differential testing, but neither defines correct behaviour for BlueTusk.

Every protocol or catalogue feature should cite the relevant PostgreSQL major-version documentation in its design notes or tests when behaviour is not obvious.

