[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),

    [string] $SloPath = (Join-Path $PSScriptRoot 'v1-production-slos.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$SloPath = (Resolve-Path -LiteralPath $SloPath).Path

& (Join-Path $PSScriptRoot 'verify-telemetry-contract.ps1') `
    -RepositoryRoot $RepositoryRoot

$contract = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'telemetry-contract.json') -Raw |
    ConvertFrom-Json
$slos = Get-Content -LiteralPath $SloPath -Raw | ConvertFrom-Json
if ($slos.schemaVersion -ne 1)
{
    throw "Expected production SLO schema 1; found '$($slos.schemaVersion)'."
}

$metrics = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($meter in @($contract.meters))
{
    foreach ($instrument in @($meter.instruments))
    {
        [void]$metrics.Add([string]$instrument.name)
    }
}

$families = @(
    (Get-Content -LiteralPath (
        Join-Path $PSScriptRoot 'product-families.json') -Raw |
        ConvertFrom-Json).families.PSObject.Properties.Name)
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$alerts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$slowBurnAlerts = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
$allowedKinds = @(
    'ratio',
    'tagged-ratio',
    'histogram-percentile',
    'maximum-increase')

foreach ($objective in @($slos.objectives))
{
    $id = [string]$objective.id
    if ($id -notmatch '^[a-z][a-z0-9-]+$' -or -not $ids.Add($id))
    {
        throw "SLO IDs must be unique kebab-case values; found '$id'."
    }

    if ([string]$objective.family -notin $families)
    {
        throw "SLO '$id' references unknown product family '$($objective.family)'."
    }

    $kind = [string]$objective.kind
    if ($kind -notin $allowedKinds)
    {
        throw "SLO '$id' has unsupported indicator kind '$kind'."
    }

    $referencedMetrics = switch ($kind)
    {
        'ratio'
        {
            @(
                [string]$objective.numeratorMetric,
                [string]$objective.failureMetric)
        }
        default
        {
            @([string]$objective.metric)
        }
    }
    foreach ($metric in $referencedMetrics)
    {
        if (-not $metrics.Contains($metric))
        {
            throw "SLO '$id' references undeclared runtime metric '$metric'."
        }
    }

    if ($kind -in @('ratio', 'tagged-ratio'))
    {
        $target = [double]$objective.target
        if ($target -le 0 -or $target -gt 1)
        {
            throw "SLO '$id' ratio target must be greater than zero and at most one."
        }

        $slowBurnAlert = [string]$objective.slowBurnAlert
        if ($slowBurnAlert -notmatch '^BlueTusk[A-Za-z0-9]+SlowBurn$' -or
            -not $slowBurnAlerts.Add($slowBurnAlert))
        {
            throw "Ratio SLO '$id' must declare a unique BlueTusk slow-burn alert."
        }
    }

    if ($kind -eq 'histogram-percentile')
    {
        $percentile = [double]$objective.percentile
        if ($percentile -le 0 -or $percentile -ge 1 -or
            [double]$objective.maximum -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$objective.unit))
        {
            throw "SLO '$id' has an invalid histogram percentile or threshold."
        }
    }

    if ([string]::IsNullOrWhiteSpace([string]$objective.window))
    {
        throw "SLO '$id' must declare a measurement window."
    }

    $alert = [string]$objective.alert
    if ($alert -notmatch '^BlueTusk[A-Za-z0-9]+$' -or -not $alerts.Add($alert))
    {
        throw "SLO alerts must be unique BlueTusk names; found '$alert'."
    }
}

if (@($slos.recoveryObjectives).Count -lt 5)
{
    throw 'The production profile must cover at least five recovery-state objectives.'
}

$rulesPath = Join-Path $RepositoryRoot 'ops/observability/prometheus-rules.yml'
$collectorPath = Join-Path $RepositoryRoot 'ops/observability/otel-collector.yaml'
$dashboardPath = Join-Path $RepositoryRoot (
    'ops/observability/grafana/bluetusk-v1.json')
$runbookPath = Join-Path $RepositoryRoot 'docs/operations/observability.md'
foreach ($path in @($rulesPath, $collectorPath, $dashboardPath, $runbookPath))
{
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Production observability artifact '$path' is missing."
    }
}

$rules = Get-Content -LiteralPath $rulesPath -Raw
foreach ($objective in @($slos.objectives))
{
    if (-not $rules.Contains(
            "alert: $($objective.alert)",
            [StringComparison]::Ordinal) -or
        -not $rules.Contains(
            "slo: $($objective.id)",
            [StringComparison]::Ordinal))
    {
        throw (
            "Prometheus rules do not bind SLO '$($objective.id)' to alert " +
            "'$($objective.alert)'.")
    }

    if ([string]$objective.kind -in @('ratio', 'tagged-ratio') -and
        -not $rules.Contains(
            "alert: $($objective.slowBurnAlert)",
            [StringComparison]::Ordinal))
    {
        throw (
            "Prometheus rules do not contain slow-burn alert " +
            "'$($objective.slowBurnAlert)' for SLO '$($objective.id)'.")
    }
}

$collector = Get-Content -LiteralPath $collectorPath -Raw
foreach ($requiredComponent in @(
        'memory_limiter:',
        'batch:',
        'health_check:',
        'otlp:',
        'prometheus:',
        '${env:BLUETUSK_TRACES_ENDPOINT}'))
{
    if (-not $collector.Contains(
            $requiredComponent,
            [StringComparison]::Ordinal))
    {
        throw "Collector configuration is missing '$requiredComponent'."
    }
}

$dashboard = Get-Content -LiteralPath $dashboardPath -Raw | ConvertFrom-Json
if ([string]$dashboard.uid -ne 'bluetusk-v1-production' -or
    @($dashboard.panels).Count -lt 10)
{
    throw 'The V1 Grafana dashboard must have its stable UID and at least ten panels.'
}

$dashboardSource = Get-Content -LiteralPath $dashboardPath -Raw
foreach ($prefix in @(
        'bluetusk_commands',
        'bluetusk_streams',
        'bluetusk_sync',
        'bluetusk_live',
        'bluetusk_graph',
        'bluetusk_control_plane'))
{
    if (-not $dashboardSource.Contains($prefix, [StringComparison]::Ordinal))
    {
        throw "The V1 Grafana dashboard has no '$prefix' query."
    }
}

Write-Host (
    "Verified $($ids.Count) production SLOs, " +
    "$(@($slos.recoveryObjectives).Count) recovery objectives, " +
    "$($alerts.Count) primary plus $($slowBurnAlerts.Count) slow-burn alerts, " +
    "Collector safety controls, and " +
    "$(@($dashboard.panels).Count) Grafana panels.")
