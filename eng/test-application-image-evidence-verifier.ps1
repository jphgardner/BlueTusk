[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $repositoryRoot 'artifacts/application-image-evidence-tests'
if (Test-Path -LiteralPath $testRoot)
{
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $testRoot
$commit = '1234567890abcdef1234567890abcdef12345678'
$index = 0
$images = [ordered]@{}
foreach ($application in @('order-operations', 'service-topology', 'fraud-investigation'))
{
    $components = [ordered]@{}
    foreach ($component in @('api', 'worker', 'ui'))
    {
        $index++
        $components[$component] = (
            "ghcr.io/jphgardner/bluetusk-$application@sha256:" +
            $index.ToString('x').PadLeft(64, '0'))
    }
    $images[$application] = $components
}
$valid = [ordered]@{
    schemaVersion = 1
    rcVersion = '1.0.0-rc.1'
    workflow = 'applications-images.yml'
    commit = $commit
    images = $images
}

function Write-Evidence([object]$Evidence, [string]$Name)
{
    $path = Join-Path $testRoot "$Name.json"
    $Evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    return $path
}

function Assert-Rejected([object]$Evidence, [string]$Name)
{
    $path = Write-Evidence $Evidence $Name
    try
    {
        & (Join-Path $PSScriptRoot 'verify-application-image-evidence.ps1') `
            -EvidencePath $path `
            -ExpectedCommit $commit *> $null
    }
    catch
    {
        return
    }
    throw "Mutation '$Name' was accepted."
}

try
{
    $validPath = Write-Evidence $valid 'valid'
    & (Join-Path $PSScriptRoot 'verify-application-image-evidence.ps1') `
        -EvidencePath $validPath `
        -ExpectedCommit $commit *> $null

    $wrongCommit = $valid | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $wrongCommit.commit = 'abcdef1234567890abcdef1234567890abcdef12'
    Assert-Rejected $wrongCommit 'wrong-commit'

    $tagged = $valid | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $tagged.images.'order-operations'.api = 'ghcr.io/jphgardner/bluetusk-order-operations:api-rc.1'
    Assert-Rejected $tagged 'tagged-image'

    $reused = $valid | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $reused.images.'order-operations'.worker = $reused.images.'order-operations'.api
    Assert-Rejected $reused 'reused-digest'

    Write-Output 'Application image evidence verifier passed its positive case and three mutations.'
}
finally
{
    if (Test-Path -LiteralPath $testRoot)
    {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
