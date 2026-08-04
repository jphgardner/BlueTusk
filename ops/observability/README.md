# BlueTusk V1 observability pack

This directory is the deployable reference pack for the machine-readable
[telemetry contract](../../eng/telemetry-contract.json) and
[V1 SLO profile](../../eng/v1-production-slos.json).

It contains:

- `otel-collector.yaml`: an OpenTelemetry Collector Contrib configuration with
  OTLP ingest, bounded memory, batching, a Prometheus endpoint and a required
  TLS OTLP trace destination;
- `prometheus-rules.yml`: a primary alert for every reference V1 SLO plus
  sustained six-times slow-burn alerts for every ratio SLO; and
- `grafana/bluetusk-v1.json`: a Grafana dashboard covering Provider, Streams,
  Sync, Live, Continuous Graph and Control Plane.

Set `BLUETUSK_TRACES_ENDPOINT` to the TLS endpoint of the organisation's trace
backend before starting the Collector. Keep ports 4317, 4318, 8889 and 13133 on
an internal observability network or protect them with the platform's
authentication and network policies. Do not expose unauthenticated OTLP ingest
to the public Internet.

The alert expressions use OpenTelemetry's conventional Prometheus translation:
dots become underscores, counters receive `_total`, and duration units receive
`_seconds` or `_milliseconds`. Verify the translated names in the target
Collector/Prometheus versions before production activation. A translation
change is a monitored configuration migration, not a reason to disable alerts.

Run the repository verifiers before deploying the pack:

```powershell
pwsh -File eng/verify-telemetry-contract.ps1
pwsh -File eng/verify-v1-production-readiness.ps1 -Mode Engineering
```

The main build workflow also parses all 20 rules with Prometheus 3.13.1 and
validates the Collector configuration with OpenTelemetry Collector Contrib
0.153.0. Both validation images are digest-pinned in the workflow.

The Collector is vendor-neutral, but its exact image digest, resource limits,
TLS trust, backend credentials, retention and high-availability topology belong
to the deployment repository and must be recorded in the final release review.
