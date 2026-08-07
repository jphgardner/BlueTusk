[CmdletBinding()]
param(
    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$platformRoot = Join-Path $repositoryRoot 'applications/deploy/platform'
$versions = Get-Content -LiteralPath (Join-Path $platformRoot 'versions.json') -Raw |
    ConvertFrom-Json
if ([int]$versions.schemaVersion -ne 1)
{
    throw 'Unsupported application platform version contract.'
}

$context = (& kubectl config current-context).Trim()
if ($LASTEXITCODE -ne 0 -or $context -ne 'proxmox-homelab')
{
    throw "Application platform operations require context 'proxmox-homelab'; found '$context'."
}
$serverVersion = [version]((& kubectl version -o json | ConvertFrom-Json).serverVersion.gitVersion.TrimStart('v').Split('-')[0])
if ($serverVersion -lt [version]'1.35.0')
{
    throw "Kubernetes 1.35 or later is required; found '$serverVersion'."
}

if (-not $Apply)
{
    Write-Output (
        "Platform preflight passed for $context Kubernetes $serverVersion. " +
        "Apply was not requested; no cluster state changed. Planned pins: " +
        "CloudNativePG $($versions.cloudNativePg.operatorVersion), Keycloak " +
        "$($versions.keycloak.version), kube-prometheus-stack " +
        "$($versions.observability.kubePrometheusStackChart), OpenTelemetry " +
        "$($versions.observability.openTelemetryCollectorChart), Loki " +
        "$($versions.observability.lokiChart).")
    return
}

& kubectl apply -f (Join-Path $platformRoot 'namespaces.yaml')
if ($LASTEXITCODE -ne 0) { throw 'Namespace apply failed.' }

$requiredSecrets = [ordered]@{
    keycloak = [ordered]@{
        'keycloak-db-owner' = @('username', 'password')
        'keycloak-db-runtime' = @('username', 'password')
        'keycloak-bootstrap-admin' = @('username', 'password')
        'order-operations-bff' = @('clientSecret')
        'service-topology-bff' = @('clientSecret')
        'fraud-investigation-bff' = @('clientSecret')
    }
    observability = [ordered]@{ 'grafana-admin' = @('username', 'password') }
}
foreach ($entry in $requiredSecrets.GetEnumerator())
{
    foreach ($secret in $entry.Value.GetEnumerator())
    {
        $secretJson = & kubectl get secret $secret.Key -n $entry.Key -o json
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$secretJson))
        {
            throw "Pre-created Secret '$($entry.Key)/$($secret.Key)' is required before platform apply."
        }
        $keys = @((($secretJson -join "`n") | ConvertFrom-Json).data.PSObject.Properties.Name)
        foreach ($key in $secret.Value)
        {
            if ($key -notin $keys)
            {
                throw "Secret '$($entry.Key)/$($secret.Key)' is missing key '$key'."
            }
        }
    }
}
& helm repo add cnpg ([string]$versions.cloudNativePg.repository) --force-update
& helm repo add prometheus-community https://prometheus-community.github.io/helm-charts --force-update
& helm repo add open-telemetry https://open-telemetry.github.io/opentelemetry-helm-charts --force-update
& helm repo add grafana-community https://grafana-community.github.io/helm-charts --force-update
& helm repo update
if ($LASTEXITCODE -ne 0) { throw 'Pinned Helm repository update failed.' }

& helm upgrade --install cnpg cnpg/cloudnative-pg `
    --namespace cnpg-system `
    --version ([string]$versions.cloudNativePg.chartVersion) `
    --wait --atomic --timeout 10m
if ($LASTEXITCODE -ne 0) { throw 'CloudNativePG installation failed.' }

$keycloakSource = "github.com/keycloak/keycloak-k8s-resources/cluster-wide?ref=$($versions.keycloak.kustomizeCommit)"
& kubectl apply -k $keycloakSource
if ($LASTEXITCODE -ne 0) { throw 'Keycloak operator installation failed.' }

& helm upgrade --install kube-prometheus-stack prometheus-community/kube-prometheus-stack `
    --namespace observability `
    --version ([string]$versions.observability.kubePrometheusStackChart) `
    --values (Join-Path $platformRoot 'observability/kube-prometheus-stack.yaml') `
    --wait --atomic --timeout 20m
if ($LASTEXITCODE -ne 0) { throw 'kube-prometheus-stack installation failed.' }
& helm upgrade --install loki oci://ghcr.io/grafana-community/helm-charts/loki `
    --namespace observability `
    --version ([string]$versions.observability.lokiChart) `
    --values (Join-Path $platformRoot 'observability/loki.yaml') `
    --wait --atomic --timeout 15m
if ($LASTEXITCODE -ne 0) { throw 'Loki installation failed.' }
& helm upgrade --install otel-collector open-telemetry/opentelemetry-collector `
    --namespace observability `
    --version ([string]$versions.observability.openTelemetryCollectorChart) `
    --values (Join-Path $platformRoot 'observability/opentelemetry-collector.yaml') `
    --wait --atomic --timeout 10m
if ($LASTEXITCODE -ne 0) { throw 'OpenTelemetry Collector installation failed.' }
& kubectl apply -f (Join-Path $platformRoot 'observability/bluetusk-observability.yaml')
& kubectl apply -f (Join-Path $platformRoot 'keycloak.yaml')
if ($LASTEXITCODE -ne 0) { throw 'Keycloak instance installation failed.' }
& kubectl wait --for=condition=Ready cluster/keycloak-postgresql `
    --namespace keycloak --timeout=15m
if ($LASTEXITCODE -ne 0) { throw 'Keycloak PostgreSQL cluster did not become ready.' }
& kubectl wait --for=condition=Ready keycloak/keycloak --namespace keycloak --timeout=15m
if ($LASTEXITCODE -ne 0) { throw 'Keycloak did not become ready.' }
& kubectl delete job bluetusk-realm-provisioner --namespace keycloak `
    --ignore-not-found=true
& kubectl apply -f (Join-Path $platformRoot 'keycloak-realm.yaml')
if ($LASTEXITCODE -ne 0) { throw 'Keycloak realm provisioning job could not be created.' }
& kubectl wait --for=condition=Complete job/bluetusk-realm-provisioner `
    --namespace keycloak --timeout=10m
if ($LASTEXITCODE -ne 0) { throw 'Keycloak realm provisioning failed.' }

Write-Output 'Pinned platform operators, observability, and Keycloak resources were applied.'
