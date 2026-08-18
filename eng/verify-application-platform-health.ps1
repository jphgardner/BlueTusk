[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int] $MinimumReadyNodes = 2,
    [switch] $RequireApplications
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-KubectlJson
{
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments,
        [Parameter(Mandatory)]
        [string] $Context
    )

    $output = @(& kubectl @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Kubernetes query '$Context' failed: $($output -join [Environment]::NewLine)"
    }

    try
    {
        return (($output -join [Environment]::NewLine) | ConvertFrom-Json)
    }
    catch
    {
        throw "Kubernetes query '$Context' did not return valid JSON: $($_.Exception.Message)"
    }
}

$context = (& kubectl config current-context).Trim()
if ($LASTEXITCODE -ne 0 -or $context -ne 'proxmox-homelab')
{
    throw "Application platform health verification requires context 'proxmox-homelab'; found '$context'."
}

$nodes = Invoke-KubectlJson -Arguments @('get', 'nodes', '-o', 'json') -Context 'nodes'
$readyNodes = @($nodes.items | Where-Object {
        @($_.status.conditions | Where-Object {
                $_.type -eq 'Ready' -and $_.status -eq 'True'
            }).Count -eq 1
    })
if ($readyNodes.Count -lt $MinimumReadyNodes)
{
    throw "Application platform requires at least $MinimumReadyNodes Ready nodes; found $($readyNodes.Count)."
}

foreach ($node in $readyNodes)
{
    $nodeName = [string]$node.metadata.name
    $pressure = @($node.status.conditions | Where-Object {
            $_.type -in @('DiskPressure', 'MemoryPressure', 'PIDPressure', 'NetworkUnavailable') -and
            $_.status -eq 'True'
        } | ForEach-Object { [string]$_.type })
    if ($pressure.Count -ne 0)
    {
        throw "Ready node '$nodeName' reports pressure conditions: $($pressure -join ', ')."
    }

    $assigned = Invoke-KubectlJson `
        -Arguments @(
            'get', 'pods', '--all-namespaces',
            '--field-selector', "spec.nodeName=$nodeName", '-o', 'json') `
        -Context "API pods assigned to $nodeName"
    $expectedPods = @($assigned.items | Where-Object {
            [string]$_.status.phase -notin @('Succeeded', 'Failed')
        })
    $runtimePods = Invoke-KubectlJson `
        -Arguments @('get', '--raw', "/api/v1/nodes/$nodeName/proxy/pods/") `
        -Context "kubelet pods on $nodeName"
    $runtimeUids = @(@($runtimePods.items) | Where-Object { $null -ne $_ } |
        ForEach-Object { [string]$_.metadata.uid })
    $missing = @($expectedPods | Where-Object {
            [string]$_.metadata.uid -notin $runtimeUids
        } | ForEach-Object {
            "$($_.metadata.namespace)/$($_.metadata.name)"
        })
    if ($missing.Count -ne 0)
    {
        throw (
            "Kubelet '$nodeName' is not reconciling $($missing.Count) non-terminal API pod(s): " +
            ($missing -join ', '))
    }
}

$longhornVolumes = Invoke-KubectlJson `
    -Arguments @('get', 'volumes.longhorn.io', '--all-namespaces', '-o', 'json') `
    -Context 'Longhorn volumes'
$unhealthyVolumes = @($longhornVolumes.items | Where-Object {
        [string]$_.status.robustness -ne 'healthy'
    } | ForEach-Object {
        "$($_.metadata.name)=$($_.status.state)/$($_.status.robustness)"
    })
if ($unhealthyVolumes.Count -ne 0)
{
    throw "Longhorn has unhealthy volumes: $($unhealthyVolumes -join ', ')."
}

$databaseClusters = Invoke-KubectlJson `
    -Arguments @('get', 'clusters.postgresql.cnpg.io', '--all-namespaces', '-o', 'json') `
    -Context 'CloudNativePG clusters'
$unhealthyClusters = @($databaseClusters.items | Where-Object {
        [string]$_.status.phase -ne 'Cluster in healthy state' -or
        [int]$_.status.readyInstances -lt [int]$_.spec.instances
    } | ForEach-Object {
        "$($_.metadata.namespace)/$($_.metadata.name)=$($_.status.phase) " +
        "($($_.status.readyInstances)/$($_.spec.instances))"
    })
if ($unhealthyClusters.Count -ne 0)
{
    throw "CloudNativePG has unhealthy clusters: $($unhealthyClusters -join ', ')."
}

if ($RequireApplications)
{
    $applications = @(
        [pscustomobject]@{ Release = 'orders'; Key = 'order-operations'; Namespace = 'bluetusk-orders-rc' },
        [pscustomobject]@{ Release = 'topology'; Key = 'service-topology'; Namespace = 'bluetusk-topology-rc' },
        [pscustomobject]@{ Release = 'fraud'; Key = 'fraud-investigation'; Namespace = 'bluetusk-fraud-rc' }
    )
    foreach ($application in $applications)
    {
        foreach ($component in @('api', 'worker', 'ui'))
        {
            $name = "$($application.Release)-$($application.Key)-$component"
            $deployment = Invoke-KubectlJson `
                -Arguments @('get', 'deployment', $name, '-n', $application.Namespace, '-o', 'json') `
                -Context "$($application.Namespace)/deployment/$name"
            $desired = [int]$deployment.spec.replicas
            $ready = [int]$deployment.status.readyReplicas
            $available = [int]$deployment.status.availableReplicas
            if ($desired -lt 1 -or $ready -ne $desired -or $available -ne $desired -or
                [long]$deployment.status.observedGeneration -lt [long]$deployment.metadata.generation)
            {
                throw (
                    "Deployment '$($application.Namespace)/$name' is not converged: " +
                    "desired=$desired ready=$ready available=$available generation=" +
                    "$($deployment.metadata.generation)/$($deployment.status.observedGeneration).")
            }
        }

        $pods = Invoke-KubectlJson `
            -Arguments @('get', 'pods', '-n', $application.Namespace, '-o', 'json') `
            -Context "$($application.Namespace) pods"
        $unreadyPods = @($pods.items | Where-Object {
            [string]$_.status.phase -notin @('Succeeded', 'Failed') -and
            (@($_.status.containerStatuses).Count -eq 0 -or
                @($_.status.containerStatuses | Where-Object { $_.ready -ne $true }).Count -ne 0)
        } | ForEach-Object { [string]$_.metadata.name })
        if ($unreadyPods.Count -ne 0)
        {
            throw "Namespace '$($application.Namespace)' has unready pods: $($unreadyPods -join ', ')."
        }

        $jobs = Invoke-KubectlJson `
            -Arguments @('get', 'jobs', '-n', $application.Namespace, '-o', 'json') `
            -Context "$($application.Namespace) jobs"
        $failedJobs = @(@($jobs.items) | Where-Object { $null -ne $_ } | Where-Object {
            @($_.status.conditions | Where-Object {
                    $_.type -eq 'Failed' -and $_.status -eq 'True'
                }).Count -ne 0
        } | ForEach-Object { [string]$_.metadata.name })
        if ($failedJobs.Count -ne 0)
        {
            throw "Namespace '$($application.Namespace)' has failed jobs: $($failedJobs -join ', ')."
        }
    }
}

$applicationSuffix = if ($RequireApplications) { ' and all three RC applications' } else { '' }
Write-Output (
    "Verified $($readyNodes.Count) Ready Kubernetes nodes, kubelet/API reconciliation, " +
    "$(@($longhornVolumes.items).Count) healthy Longhorn volumes, " +
    "$(@($databaseClusters.items).Count) healthy CloudNativePG clusters$applicationSuffix.")
