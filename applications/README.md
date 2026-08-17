# BlueTusk production applications

This directory contains three independently deployable package-consumer applications:

- Order Fulfilment Operations;
- Service Topology Centre; and
- Fraud Graph Investigator.

They deliberately consume exact `1.0.0-rc.1` NuGet and npm packages. No application
project references BlueTusk production source. Before public RC publication, run
`eng/build-prerelease-packages.ps1` and restore with the generated temporary NuGet
configuration. RC deployments are staging evidence only; they are not V1 pilot or
production evidence.

Every backend follows Domain -> Application -> Infrastructure -> API/Worker dependency
direction. Browser clients use the same-origin BFF, and Kubernetes secrets provide all
database and OIDC credentials.

The [RC release/support contract](docs/rc-release-and-support.md),
[service-level objectives](docs/service-level-objectives.md), and application runbooks are
checked in beside the source. Deterministic seed SQL is opt-in through
`eng/seed-applications.ps1`; its default mode is a read-only PostgreSQL 19 Beta 3 preflight.
