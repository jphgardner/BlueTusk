# RC application service-level objectives

These are operational targets for RC staging, not production claims or formal V1 pilot
evidence. The evaluation window is a rolling 24 hours and planned maintenance is excluded
only when declared before the window.

| Application | Availability | Request P95 | Live or graph P95 | Integrity objective |
|---|---:|---:|---:|---|
| Order Fulfilment Operations | 99.9% successful authenticated requests | 500 ms | Live lifecycle <= 1 s; durable relay lag <= 2 s | no lost or duplicate state transitions |
| Service Topology Centre | 99.9% successful ingestion and query requests | 500 ms | lifecycle/evaluation <= 1 s | zero unreconciled or incorrectly ordered topology results |
| Fraud Graph Investigator | 99.9% successful case and transfer requests | 750 ms | lifecycle/evaluation <= 1 s | zero missing audit decisions or stale unrepaired graph results |

Common objectives are zero cross-tenant disclosures, zero unrecovered stream settlement
failures, and 100% successful scheduled backups. Page when the five-minute HTTP 5xx ratio
exceeds 1%, Live P95 remains above one second for 15 minutes, a Streams settlement failure
occurs, or ContinuousGraph reports a non-committed outcome for five minutes. The checked-in
Prometheus rules implement these signals; application teams own dashboards and incident
records for their service.
