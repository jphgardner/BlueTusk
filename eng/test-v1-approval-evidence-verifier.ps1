[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-v1-approval-evidence.ps1'
$setVerifier = Join-Path $PSScriptRoot 'verify-v1-approval-evidence-set.ps1'
$examplesPath = Join-Path $PSScriptRoot 'v1-approval-evidence.examples.json'
$examples = Get-Content -LiteralPath $examplesPath -Raw | ConvertFrom-Json
$zeroCommit = '0' * 40
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) "bluetusk-v1-approval-self-test-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $temporaryRoot

function Write-Fixture
{
    param(
        [Parameter(Mandatory)]
        [object] $Evidence,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $path = Join-Path $temporaryRoot "$Name.json"
    $Evidence | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $path -Encoding utf8NoBOM
    return $path
}

function Assert-Rejected
{
    param(
        [Parameter(Mandatory)]
        [object] $Evidence,

        [Parameter(Mandatory)]
        [string] $GateId,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $path = Write-Fixture $Evidence $Name
    try
    {
        & $verifier `
            -EvidencePath $path `
            -ExpectedGateId $GateId `
            -ExpectedCommit $zeroCommit *> $null
        throw "Negative approval fixture '$Name' was accepted."
    }
    catch
    {
        if ($_.Exception.Message -eq "Negative approval fixture '$Name' was accepted.")
        {
            throw
        }
    }
}

function Write-ExampleSet
{
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [scriptblock] $Mutate
    )

    $setPath = Join-Path $temporaryRoot $Name
    $null = New-Item -ItemType Directory -Path $setPath
    $copies = @($examples.examples | ForEach-Object {
        $_ | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    })
    if ($null -ne $Mutate)
    {
        & $Mutate $copies
    }
    foreach ($copy in $copies)
    {
        $copy | ConvertTo-Json -Depth 20 |
            Set-Content -LiteralPath (
                Join-Path $setPath "$($copy.gateId).json"
            ) -Encoding utf8NoBOM
    }

    return $setPath
}

function Assert-SetRejected
{
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Mutate,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $setPath = Write-ExampleSet $Name $Mutate
    try
    {
        & $setVerifier `
            -EvidenceDirectory $setPath `
            -ExpectedCommit $zeroCommit `
            -ExpectedWebsiteProductionMetricsSha256 ('0' * 64) *> $null
        throw "Negative approval set '$Name' was accepted."
    }
    catch
    {
        if ($_.Exception.Message -eq "Negative approval set '$Name' was accepted.")
        {
            throw
        }
    }
}

try
{
    $gateIds = @($examples.examples | ForEach-Object { [string]$_.gateId })
    if ($gateIds.Count -ne 10 -or
        @($gateIds | Select-Object -Unique).Count -ne $gateIds.Count)
    {
        throw 'Approval examples must contain ten unique V1 gates.'
    }

    foreach ($example in @($examples.examples))
    {
        $path = Write-Fixture $example ([string]$example.gateId)
        & $verifier `
            -EvidencePath $path `
            -ExpectedGateId ([string]$example.gateId) `
            -ExpectedCommit $zeroCommit *> $null
    }

    $validSet = Write-ExampleSet 'valid-set'
    & $setVerifier `
        -EvidenceDirectory $validSet `
        -ExpectedCommit $zeroCommit `
        -ExpectedWebsiteProductionMetricsSha256 ('0' * 64) *> $null

    $genericPilot = [ordered]@{
        schemaVersion = 3
        gateId = 'application-pilot-a'
        candidateCommit = $zeroCommit
        outcome = 'approved'
        approvedBy = 'pilot-owner'
        approvedUtc = '2026-01-01T00:00:00Z'
        summary = 'A generic narrative approval without measured pilot details must fail.'
        blockingFindings = 0
        references = @('https://evidence.example/generic-pilot')
        details = [ordered]@{}
    }
    Assert-Rejected $genericPilot 'application-pilot-a' 'generic-pilot'

    $badVitals = (
        @($examples.examples | Where-Object {
            [string]$_.gateId -eq 'website-deployment-acceptance'
        })[0] | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    )
    $badVitals.details.lcpP75Milliseconds = 2501
    Assert-Rejected $badVitals 'website-deployment-acceptance' 'bad-vitals'

    $mismatchedRestore = (
        @($examples.examples | Where-Object {
            [string]$_.gateId -eq 'backup-restore-rehearsal'
        })[0] | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    )
    $mismatchedRestore.details.restoredRowCount = 999
    Assert-Rejected $mismatchedRestore 'backup-restore-rehearsal' 'mismatched-restore'

    $unknownDetail = (
        @($examples.examples | Where-Object {
            [string]$_.gateId -eq 'incident-response-game-day'
        })[0] | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    )
    $unknownDetail.details | Add-Member -NotePropertyName unexplainedPass -NotePropertyValue $true
    Assert-Rejected $unknownDetail 'incident-response-game-day' 'unknown-detail'

    $wrongCommit = (
        @($examples.examples | Where-Object {
            [string]$_.gateId -eq 'maintainer-signoff'
        })[0] | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    )
    $wrongCommit.candidateCommit = '1' * 40
    Assert-Rejected $wrongCommit 'maintainer-signoff' 'wrong-commit'

    $prematureTag = (
        @($examples.examples | Where-Object {
            [string]$_.gateId -eq 'maintainer-signoff'
        })[0] | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    )
    $prematureTag.details.releaseTagsCreated = 1
    Assert-Rejected $prematureTag 'maintainer-signoff' 'premature-release-tag'

    $unarmedPolicies = (
        @($examples.examples | Where-Object {
            [string]$_.gateId -eq 'maintainer-signoff'
        })[0] | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    )
    $unarmedPolicies.details.publicationPoliciesArmed = $false
    Assert-Rejected $unarmedPolicies 'maintainer-signoff' 'unarmed-policies'

    Assert-SetRejected -Name 'duplicate-pilot' -Mutate {
        param($copies)
        $pilotA = @($copies | Where-Object {
            [string]$_.gateId -eq 'application-pilot-a'
        })[0]
        $pilotB = @($copies | Where-Object {
            [string]$_.gateId -eq 'application-pilot-b'
        })[0]
        $pilotB.details.applicationName = $pilotA.details.applicationName
    }
    Assert-SetRejected -Name 'wrong-website-hash' -Mutate {
        param($copies)
        $website = @($copies | Where-Object {
            [string]$_.gateId -eq 'website-deployment-acceptance'
        })[0]
        $website.details.productionMetricsSha256 = '1' * 64
    }
    Assert-SetRejected -Name 'premature-independent-review' -Mutate {
        param($copies)
        $security = @($copies | Where-Object {
            [string]$_.gateId -eq 'security-review'
        })[0]
        $security.approvedUtc = '2026-01-02T00:00:00Z'
    }
    Assert-SetRejected -Name 'premature-maintainer-signoff' -Mutate {
        param($copies)
        $review = @($copies | Where-Object {
            [string]$_.gateId -eq 'independent-release-review'
        })[0]
        $review.approvedUtc = '2026-01-02T00:00:00Z'
    }
    Assert-SetRejected -Name 'incomplete-family-coverage' -Mutate {
        param($copies)
        foreach ($pilot in @($copies | Where-Object {
            [string]$_.gateId -in @('application-pilot-a', 'application-pilot-b')
        }))
        {
            $pilot.details.enabledProductFamilies = @(
                $pilot.details.enabledProductFamilies |
                    Where-Object { [string]$_ -ne 'ControlPlane' }
            )
        }
    }
    Assert-SetRejected -Name 'missing-continuous-graph-pilot' -Mutate {
        param($copies)
        foreach ($pilot in @($copies | Where-Object {
            [string]$_.gateId -in @('application-pilot-a', 'application-pilot-b')
        }))
        {
            $pilot.details.enabledProductFamilies = @(
                $pilot.details.enabledProductFamilies |
                    Where-Object { [string]$_ -ne 'ContinuousGraph' }
            )
        }
    }

    $staleSet = Write-ExampleSet 'stale-set'
    try
    {
        & $setVerifier `
            -EvidenceDirectory $staleSet `
            -ExpectedCommit $zeroCommit `
            -ExpectedWebsiteProductionMetricsSha256 ('0' * 64) `
            -NotBeforeUtc ([DateTimeOffset]'2026-01-02T00:00:00Z') *> $null
        throw "Negative approval set 'stale-set' was accepted."
    }
    catch
    {
        if ($_.Exception.Message -eq "Negative approval set 'stale-set' was accepted.")
        {
            throw
        }
    }
}
finally
{
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}

Write-Output (
    'V1 approval-evidence verifier self-test passed: ten positive schemas, one ' +
    'complete set, seven record mutations, six cross-record mutations and one ' +
    'stale-set mutation.')
