# ADR 0015: Gate client-authored queries with database capabilities

- Status: Accepted
- Date: 2026-08-03

## Context

ADR 0011 made trusted server query registration the only Live query path. V1
also needs authenticated applications to offer controlled exploratory queries
without turning the Live endpoint into an unrestricted database proxy.

SQL text cannot be made safe by keyword matching alone. A read-only PostgreSQL
transaction blocks database writes but cannot prevent every external side
effect of a user-defined or extension function. Uploaded CLR expression trees,
dynamic compilation, and provider-side client evaluation also create code
execution, trimming, denial-of-service, and policy-bypass risks.

## Decision

Trusted registration remains the default. Client-authored queries are a
separate opt-in capability:

- the application authorizes every request and returns a complete
  `LiveClientQueryGrant`;
- the grant selects an application-owned `DbDataSource`, immutable policy, and
  caller-specific `LiveSecurityScope`;
- raw SQL is enabled only when the policy requires both PostgreSQL row-level
  security and a dedicated read-only, non-owner, non-superuser,
  non-`BYPASSRLS` role;
- operators revoke function execution from `PUBLIC` and grant only the
  side-effect-free functions that capability needs;
- SQL runs as one parameterized query inside a read-only transaction with
  `row_security` enabled, statement/lock/idle timeouts, cancellation, and
  bounded result rows, columns, and bytes;
- comments, multiple statements, positional parameters, state-changing
  commands, row locks, and known side-effecting server functions are rejected
  before execution as defense in depth;
- remote LINQ is a finite JSON relational document over policy-allowlisted
  relations, columns, filters, projections, deterministic ordering, and a
  mandatory result bound; CLR expression trees and dynamically compiled code
  are never accepted;
- SQL invalidation conservatively covers every relation in the grant. Remote
  LINQ records its exact allowlisted relation;
- the query text/shape, grant version, limits, dependencies, parameter types,
  security scope, and result limit participate in the existing plan and shared
  subscription identities.

CDC remains an invalidation signal. PostgreSQL query execution remains the
authoritative source of every client-visible row.

## Consequences

The raw SQL path is a database capability, not a language sandbox. Deployments
must use least-privilege roles and function grants; the application authorizer
is part of the security boundary. Denylist validation is not described as a SQL
parser or the primary isolation mechanism.

Exploratory SQL invalidates more conservatively than registered or remote-LINQ
queries. The bounded remote LINQ document is intentionally less expressive than
CLR LINQ, but it is portable, serializable, AOT-friendly, and cannot execute
uploaded application code.
