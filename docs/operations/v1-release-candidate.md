# V1 release candidate

The existing BlueTusk products are being consolidated for release. Core retains
its 1.2.0 version in the V1 line; a release bundle does not change the stability
classification of a product that is still undergoing qualification.

| Track | Products | Packaging | Publication |
| --- | --- | --- | --- |
| Core | Provider, Streams, Sync, Live, Control Plane | 1.2.0 candidate packages, symbols, browser clients, SBOM and source provenance | Stable remains gated on qualification |
| Ecosystem | Events, Jobs, Documents, Projections, Search, Schema, SQL, Studio, Edge, Workflows | Existing 0.1.0-preview.1 candidates and symbols | Preview pending production qualification |
| Graph | Continuous Graph | Existing preview track | Requires a supported SQL/PGQ server and separate qualification |

Core supports stable PostgreSQL 15–18. PostgreSQL 19 and SQL/PGQ preview fixtures
do not gate Core package qualification. The local reference performance capture
uses the stable PostgreSQL 18 image and excludes only the two Graph preview
fixtures; all other benchmark methods and existing allocation and latency
ceilings remain mandatory.

On 30 September 2026 the repository owner requested temporary removal of the
main ruleset and sole-maintainer release approval. The main ruleset was removed,
and the three release environments permit owner self-review while retaining
required approval and disabled administrator bypass. The candidate environment
uses an explicit main branch deployment policy. Source and remote governance
checks describe this policy explicitly. Restore the archived ruleset and the
previous environment settings when independent review is available again.

The owner selected local Docker for qualification because the DigitalOcean
account is suspended. Endurance evidence must retain the exact source commit,
package hashes, stable image digest, complete duration and actual results.
Streams requires 72 hours; Sync and Live/Control Plane each require 24 hours.
Local capture does not claim a successful GitHub workflow identity or replace
the remaining release evidence with a synthetic pass.
