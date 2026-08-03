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

## Gap-free initial delivery

`LiveQuerySession<T, TKey>` reserves the current durable invalidation cursor before executing the authorised query. It then checks the log through the cursor observed after that query. If an affected table changed, the result is discarded and queried again. Only a result that reaches a quiet cursor boundary is emitted as `InitialResult`; subsequent refreshes start strictly after that cursor.

Refreshes coalesce every invalidation since the last cursor into at most one authoritative query. Unrelated-table activity advances the cursor without querying. A backward cursor, an over-limit result, duplicate keys, or perpetual initial churn fails closed with a specific diagnostic.

`BlueTusk.Live.DependencyInjection` supplies a PostgreSQL invalidation store in the relay control schema. It atomically deduplicates source transactions, records the distinct affected tables, and acknowledges the Streams delivery only after the invalidation commit succeeds. Typed and dynamic row changes use the same dependency extraction path. Failed writes are nacked for safe redelivery.

The next Live slices connect the invalidation contract to the PostgreSQL relay, add EF query registration, replay retention, ASP.NET transports, and client SDKs. Package publication stays disabled until those vertical gates pass.
