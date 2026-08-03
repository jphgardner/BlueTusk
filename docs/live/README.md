# BlueTusk Live

BlueTusk Live is the authorised real-time query layer built on BlueTusk Streams. Trusted server code registers bounded query plans; remote clients can select a registration and supply only its declared scalar parameters. SQL and expression trees are never accepted from clients.

## Security and sharing boundary

Every shared-subscription identity binds the database identity, query-plan fingerprint, canonical typed parameters, tenant/user security scope, authorisation-policy version, and result limit. A change to any field creates a different subscription. This prevents result and replay sharing across security boundaries.

PostgreSQL/EF query results remain authoritative. CDC will be used only to invalidate an affected registration, after which BlueTusk reruns the authorised query and computes a keyed result diff.

## Core delivery contracts

The core package currently provides:

- exact typed parameter binding for a restricted scalar allowlist;
- stable plan, parameter, and subscription fingerprints;
- duplicate-key rejection and keyed initial/add/update/remove/reorder/reset output;
- a bounded diff budget that falls back to an authoritative reset;
- signed, expiring, versioned, subscription-bound resume tokens with signing-key rotation.

The next Live slices connect these contracts to gap-free relay invalidation, EF query registration, PostgreSQL replay retention, ASP.NET transports, and client SDKs. Package publication stays disabled until those vertical gates pass.
