[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ImageEvidencePath,
    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the checked-out commit.' }
& (Join-Path $PSScriptRoot 'verify-application-image-evidence.ps1') `
    -EvidencePath $ImageEvidencePath `
    -ExpectedCommit $commit
$evidence = Get-Content -LiteralPath $ImageEvidencePath -Raw | ConvertFrom-Json

$context = (& kubectl config current-context).Trim()
if ($LASTEXITCODE -ne 0 -or $context -ne 'proxmox-homelab')
{
    throw "RC deployment requires context 'proxmox-homelab'; found '$context'."
}
$nodes = @(& kubectl get nodes -o json | ConvertFrom-Json).items
$readyNodes = @($nodes | Where-Object {
        @($_.status.conditions | Where-Object {
            $_.type -eq 'Ready' -and $_.status -eq 'True'
        }).Count -eq 1
    }).Count
if ($readyNodes -lt 2)
{
    throw "RC staging requires at least two Ready nodes; found $readyNodes."
}
& (Join-Path $PSScriptRoot 'verify-application-platform-health.ps1') -MinimumReadyNodes 2

foreach ($crd in @('clusters.postgresql.cnpg.io', 'keycloaks.k8s.keycloak.org'))
{
    & kubectl get crd $crd -o name *> $null
    if ($LASTEXITCODE -ne 0) { throw "Required operator CRD '$crd' is not installed." }
}

$applications = @(
    [pscustomobject]@{ Key = 'order-operations'; Release = 'orders'; Namespace = 'bluetusk-orders-rc'; Values = 'order-operations.yaml' },
    [pscustomobject]@{ Key = 'service-topology'; Release = 'topology'; Namespace = 'bluetusk-topology-rc'; Values = 'service-topology.yaml' },
    [pscustomobject]@{ Key = 'fraud-investigation'; Release = 'fraud'; Namespace = 'bluetusk-fraud-rc'; Values = 'fraud-investigation.yaml' }
)
$chart = Join-Path $repositoryRoot 'applications/deploy/charts/bluetusk-application'
$renderRoot = Join-Path $repositoryRoot 'artifacts/deployment-render'
$null = New-Item -ItemType Directory -Path $renderRoot -Force

foreach ($application in $applications)
{
    $images = $evidence.images.PSObject.Properties[$application.Key].Value
    $requiredSecrets = switch ($application.Key)
    {
        'order-operations' { [ordered]@{
                'orders-db-owner' = @('username', 'password')
                'orders-runtime' = @('connectionString', 'migrationConnectionString', 'resumeSigningKey')
                'orders-live-tenant' = @('connectionString')
                'orders-oidc' = @('clientSecret')
            } }
        'service-topology' { [ordered]@{
                'topology-db-owner' = @('username', 'password')
                'topology-runtime' = @('connectionString', 'migrationConnectionString', 'resumeSigningKey')
                'topology-live-tenant' = @('connectionString')
                'topology-oidc' = @('clientSecret')
            } }
        'fraud-investigation' { [ordered]@{
                'fraud-db-owner' = @('username', 'password')
                'fraud-runtime' = @('connectionString', 'migrationConnectionString', 'resumeSigningKey')
                'fraud-live-tenant' = @('connectionString')
                'fraud-oidc' = @('clientSecret')
            } }
    }
    foreach ($secret in $requiredSecrets.GetEnumerator())
    {
        $secretJson = & kubectl get secret $secret.Key -n $application.Namespace -o json
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$secretJson))
        {
            throw "Pre-created Secret '$($application.Namespace)/$($secret.Key)' is required."
        }
        $dataKeys = @((($secretJson -join "`n") | ConvertFrom-Json).data.PSObject.Properties.Name)
        foreach ($key in $secret.Value)
        {
            if ($key -notin $dataKeys)
            {
                throw "Secret '$($application.Namespace)/$($secret.Key)' is missing key '$key'."
            }
        }
    }

    $setArguments = @()
    foreach ($component in @('api', 'worker', 'ui'))
    {
        $reference = [string]$images.PSObject.Properties[$component].Value
        $parts = $reference.Split('@', 2)
        $setArguments += @('--set-string', "images.$component.repository=$($parts[0])")
        $setArguments += @('--set-string', "images.$component.digest=$($parts[1])")
    }
    $valuesPath = Join-Path $repositoryRoot "applications/deploy/environments/rc/$($application.Values)"
    $rendered = & helm template $application.Release $chart `
        --namespace $application.Namespace `
        --values $valuesPath `
        @setArguments
    if ($LASTEXITCODE -ne 0) { throw "Helm render failed for '$($application.Key)'." }
    $renderPath = Join-Path $renderRoot "$($application.Key).yaml"
    [IO.File]::WriteAllLines($renderPath, [string[]]$rendered)
    & kubectl apply --server-side --dry-run=server -f $renderPath *> $null
    if ($LASTEXITCODE -ne 0) { throw "Server-side dry run failed for '$($application.Key)'." }

    if ($Apply)
    {
        # Bootstrap the data-bearing CR before Helm's pre-install migration hook. Ownership
        # metadata lets the same Helm release adopt it without deleting persistent data.
        $databaseRendered = & helm template $application.Release $chart `
            --namespace $application.Namespace `
            --values $valuesPath `
            --show-only templates/database.yaml `
            @setArguments
        if ($LASTEXITCODE -ne 0) { throw "Database render failed for '$($application.Key)'." }
        $databasePath = Join-Path $renderRoot "$($application.Key)-database.yaml"
        [IO.File]::WriteAllLines($databasePath, [string[]]$databaseRendered)
        & kubectl apply --server-side -f $databasePath *> $null
        if ($LASTEXITCODE -ne 0) { throw "Database bootstrap failed for '$($application.Key)'." }
        $clusterName = "$($application.Release)-$($application.Key)-postgresql"
        & kubectl label cluster $clusterName -n $application.Namespace `
            app.kubernetes.io/managed-by=Helm --overwrite *> $null
        & kubectl annotate cluster $clusterName -n $application.Namespace `
            "meta.helm.sh/release-name=$($application.Release)" `
            "meta.helm.sh/release-namespace=$($application.Namespace)" `
            --overwrite *> $null
        if ($LASTEXITCODE -ne 0) { throw "Database ownership binding failed for '$($application.Key)'." }
        & kubectl wait --for=condition=Ready "cluster/$clusterName" `
            -n $application.Namespace --timeout=15m
        if ($LASTEXITCODE -ne 0) { throw "Database did not become ready for '$($application.Key)'." }

        & helm upgrade --install $application.Release $chart `
            --namespace $application.Namespace `
            --values $valuesPath `
            @setArguments `
            --wait --atomic --timeout 15m
        if ($LASTEXITCODE -ne 0) { throw "Atomic Helm deployment failed for '$($application.Key)'." }
        foreach ($component in @('worker', 'api', 'ui'))
        {
            & kubectl rollout status "deployment/$($application.Release)-$($application.Key)-$component" `
                -n $application.Namespace --timeout=10m
            if ($LASTEXITCODE -ne 0) { throw "Rollout failed for '$($application.Key)/$component'." }
        }
    }
}

if ($Apply)
{
    & (Join-Path $PSScriptRoot 'verify-application-platform-health.ps1') `
        -MinimumReadyNodes 2 `
        -RequireApplications
}

$mode = if ($Apply) { 'deployed' } else { 'validated without changing cluster state' }
Write-Output "RC staging $mode for all three applications at commit $commit."
