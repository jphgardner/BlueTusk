[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-application-platform-health.ps1'
$global:PlatformHealthTestScenario = 'positive'
$exitCodeVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$hadExitCode = $null -ne $exitCodeVariable
$previousExitCode = if ($hadExitCode) { $exitCodeVariable.Value } else { $null }

function global:kubectl
{
    param([Parameter(ValueFromRemainingArguments)] [object[]] $Remaining)

    $global:LASTEXITCODE = 0
    $argumentsText = @($Remaining | ForEach-Object { [string]$_ })
    $command = $argumentsText -join ' '

    if ($command -eq 'config current-context')
    {
        return 'proxmox-homelab'
    }

    $result = switch -Regex ($command)
    {
        '^get nodes -o json$'
        {
            [ordered]@{
                items = @([ordered]@{
                        metadata = [ordered]@{ name = 'node-a' }
                        status = [ordered]@{
                            conditions = @(
                                [ordered]@{ type = 'Ready'; status = 'True' },
                                [ordered]@{ type = 'DiskPressure'; status = $(if ($global:PlatformHealthTestScenario -eq 'pressure') { 'True' } else { 'False' }) },
                                [ordered]@{ type = 'MemoryPressure'; status = 'False' },
                                [ordered]@{ type = 'PIDPressure'; status = 'False' },
                                [ordered]@{ type = 'NetworkUnavailable'; status = 'False' })
                        }
                    })
            }
            break
        }
        '^get pods --all-namespaces --field-selector spec\.nodeName=node-a -o json$'
        {
            [ordered]@{
                items = @([ordered]@{
                        metadata = [ordered]@{ namespace = 'system'; name = 'controller'; uid = 'pod-1' }
                        status = [ordered]@{ phase = 'Running' }
                    })
            }
            break
        }
        '^get --raw /api/v1/nodes/node-a/proxy/pods/$'
        {
            [ordered]@{
                items = if ($global:PlatformHealthTestScenario -eq 'kubelet-empty') { $null } else {
                    @([ordered]@{ metadata = [ordered]@{ uid = 'pod-1' } })
                }
            }
            break
        }
        '^get volumes\.longhorn\.io --all-namespaces -o json$'
        {
            [ordered]@{
                items = @([ordered]@{
                        metadata = [ordered]@{ name = 'volume-a' }
                        status = [ordered]@{
                            state = 'attached'
                            robustness = $(if ($global:PlatformHealthTestScenario -eq 'unhealthy-volume') { 'degraded' } else { 'healthy' })
                        }
                    })
            }
            break
        }
        '^get clusters\.postgresql\.cnpg\.io --all-namespaces -o json$'
        {
            [ordered]@{
                items = @([ordered]@{
                        metadata = [ordered]@{ namespace = 'database'; name = 'postgresql' }
                        spec = [ordered]@{ instances = 1 }
                        status = [ordered]@{ phase = 'Cluster in healthy state'; readyInstances = 1 }
                    })
            }
            break
        }
        '^get deployment .+ -n bluetusk-.+-rc -o json$'
        {
            [ordered]@{
                metadata = [ordered]@{ generation = 1 }
                spec = [ordered]@{ replicas = 1 }
                status = [ordered]@{ observedGeneration = 1; readyReplicas = 1; availableReplicas = 1 }
            }
            break
        }
        '^get pods -n bluetusk-.+-rc -o json$'
        {
            [ordered]@{
                items = @([ordered]@{
                        metadata = [ordered]@{ name = 'application-pod' }
                        status = [ordered]@{
                            phase = 'Running'
                            containerStatuses = @([ordered]@{ ready = $true })
                        }
                    })
            }
            break
        }
        '^get jobs -n bluetusk-.+-rc -o json$'
        {
            [ordered]@{
                items = if ($global:PlatformHealthTestScenario -eq 'failed-job') {
                    @([ordered]@{
                            metadata = [ordered]@{ name = 'migration' }
                            status = [ordered]@{
                                conditions = @([ordered]@{ type = 'Failed'; status = 'True' })
                            }
                        })
                }
                else { $null }
            }
            break
        }
        default
        {
            $global:LASTEXITCODE = 1
            throw "Unexpected fake kubectl command '$command'."
        }
    }

    $result | ConvertTo-Json -Depth 20 -Compress
}

function Assert-Rejected
{
    param(
        [Parameter(Mandatory)] [string] $Scenario,
        [Parameter(Mandatory)] [string] $Pattern
    )

    $global:PlatformHealthTestScenario = $Scenario
    $failure = $null
    try
    {
        & $verifier -MinimumReadyNodes 1 -RequireApplications *> $null
    }
    catch
    {
        $failure = $_.Exception.Message
    }
    if ([string]::IsNullOrWhiteSpace([string]$failure) -or $failure -notmatch $Pattern)
    {
        throw "Scenario '$Scenario' was not rejected with '$Pattern'. Actual: $failure"
    }
}

try
{
    $global:PlatformHealthTestScenario = 'positive'
    & $verifier -MinimumReadyNodes 1 -RequireApplications *> $null
    Assert-Rejected -Scenario 'pressure' -Pattern 'pressure conditions'
    Assert-Rejected -Scenario 'kubelet-empty' -Pattern 'not reconciling'
    Assert-Rejected -Scenario 'unhealthy-volume' -Pattern 'unhealthy volumes'
    Assert-Rejected -Scenario 'failed-job' -Pattern 'failed jobs'
}
finally
{
    Remove-Item Function:\global:kubectl -ErrorAction SilentlyContinue
    Remove-Variable -Name PlatformHealthTestScenario -Scope Global -ErrorAction SilentlyContinue
    if ($hadExitCode)
    {
        $global:LASTEXITCODE = $previousExitCode
    }
    else
    {
        Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
}

Write-Output 'Application platform health verifier self-test passed one positive and four fail-closed scenarios.'
