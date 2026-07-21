# ADR 0004: Test protocol framing independently

- Status: Accepted
- Date: 2026-07-21

## Decision

Protocol tests must exercise fragmentation at every byte boundary, multiple frames in one read, asynchronous frames between operation frames, malformed lengths, unknown identifiers, cancellation races, and mid-frame disconnects.

The in-process fake server and message-stream utilities are test infrastructure, not an alternate PostgreSQL implementation. Real-server and differential tests remain mandatory for behaviours that depend on server semantics.

