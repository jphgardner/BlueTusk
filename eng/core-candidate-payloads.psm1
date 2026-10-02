Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')
Import-Module (Join-Path $PSScriptRoot 'core-test-evidence.psm1')

function Get-CorePayloadSnapshot
{
    param([string] $Root)
    $files = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force)
    {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        { throw 'Core payload trees must not contain a symbolic link or junction.' }
        if ($item.PSIsContainer) { continue }
        $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
        if (-not $paths.Add($relative)) { throw 'Core payload paths must not be case-ambiguous.' }
        $files.Add($relative, "$($item.Length):$((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash)")
    }
    return ,$files
}

function Get-CoreCandidatePayloadReport
{
    param([Parameter(Mandatory)][string] $EvidencePath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
        [Parameter(Mandatory)][DateTimeOffset] $CandidateCommitUtc,
        [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk')

    $root = Split-Path (Resolve-Path -LiteralPath $EvidencePath).Path -Parent
    $path = Resolve-CoreEvidenceFile $root ([IO.Path]::GetFileName($EvidencePath))
    # This reader consumes an assembled immutable bundle, never an active capture
    # directory. Include raw descendants in the before/after integrity check.
    $before = Get-CorePayloadSnapshot $root
    $bindings = Get-CoreCandidateBindingReport -EvidencePath $path -ExpectedCommit $ExpectedCommit `
        -CandidateCommitUtc $CandidateCommitUtc -ExpectedRepository $ExpectedRepository
    $evidence = Read-CoreEvidenceJson $path
    $contract = Get-CoreCandidateContract
    $records = @{}
    $files = @{}
    foreach ($record in $evidence.artifacts)
    {
        $records[$record.role] = $record
        $files[$record.role] = Resolve-CoreEvidenceFile $root $record.path
    }

    # Same source SHA is insufficient: endurance must exercise the exact package
    # payload and SBOM provenance verified in this bundle, byte for byte.
    foreach ($role in @('streamsProvenance', 'syncProvenance', 'liveControlPlaneProvenance'))
    {
        if ($records[$role].sha256 -cne $records.packageProvenance.sha256 -or
            $records[$role].bytes -ne $records.packageProvenance.bytes)
        { throw "Core payload '$role' does not match the exact packaged candidate provenance." }
    }

    $verified = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    & (Join-Path $PSScriptRoot 'verify-website-evidence.ps1') `
        -DistributionPath (Split-Path $files.websiteMetrics -Parent) `
        -MetricsPath $files.websiteMetrics -ExpectedCommit $ExpectedCommit | Out-Null
    $null = $verified.Add('websiteMetrics')

    & (Join-Path $PSScriptRoot 'verify-v1-package-evidence.ps1') -ReleaseTrack Core `
        -EvidenceRoot (Split-Path $files.packageManifest -Parent) -ExpectedCommit $ExpectedCommit | Out-Null
    $null = $verified.Add('packageManifest')
    $null = $verified.Add('packageProvenance')

    & (Join-Path $PSScriptRoot 'verify-performance-leadership-evidence.ps1') `
        -EvidencePath $files.performanceManifest -ExpectedCommit $ExpectedCommit -Scope Core | Out-Null
    $null = $verified.Add('performanceManifest')

    foreach ($kind in @('Regression', 'Compatibility'))
    {
        $role = $kind.ToLowerInvariant() + 'Manifest'
        $null = Get-CoreTestManifestReport $files[$role] $ExpectedCommit $kind
        $null = $verified.Add($role)
    }

    $minimums = $contract.minimums
    & (Join-Path $PSScriptRoot 'verify-streams-endurance-report.ps1') -ReleaseTrack Core `
        -ReportPath $files.streamsReport -ExpectedCommit $ExpectedCommit `
        -CandidateProvenancePath $files.streamsProvenance `
        -ExpectedPostgreSqlImage $contract.endurancePostgreSqlImage `
        -RequiredDuration ([TimeSpan]::FromHours($minimums.streamsEnduranceHours)) `
        -MinimumTransactions $minimums.streamsMinimumTransactions | Out-Null
    $null = $verified.Add('streamsReport')
    $null = $verified.Add('streamsProvenance')

    $sync = Read-CoreEvidenceJson $files.syncReport
    $destinations = @($sync.destinationImages)
    if ($destinations.Count -ne 5 -or @($destinations | Where-Object {
            $_ -isnot [string] -or $_ -cnotmatch '^\S+@sha256:[0-9a-f]{64}$'
        }).Count -ne 0)
    { throw 'Core Sync payload must retain exactly five digest-pinned destination images.' }
    # These are declared fixture images. Offline payload verification does not
    # independently certify live fixture or execution identity.
    & (Join-Path $PSScriptRoot 'verify-sync-endurance-report.ps1') -ReleaseTrack Core `
        -ReportPath $files.syncReport -ExpectedCommit $ExpectedCommit `
        -CandidateProvenancePath $files.syncProvenance `
        -ExpectedPostgreSqlImage $contract.endurancePostgreSqlImage -ExpectedDestinationImages $destinations `
        -RequiredDuration ([TimeSpan]::FromHours($minimums.syncEnduranceHours)) `
        -MinimumCycles $minimums.syncMinimumCycles | Out-Null
    $null = $verified.Add('syncReport')
    $null = $verified.Add('syncProvenance')
    $connector = Read-CoreEvidenceJson $files.syncConnectorReport
    $null = Get-CoreTestShardReport $files.syncConnectorReport $ExpectedCommit 'SyncConnectors' $connector.environmentId
    $null = $verified.Add('syncConnectorReport')

    & (Join-Path $PSScriptRoot 'verify-live-control-plane-endurance-report.ps1') `
        -ReportPath $files.liveControlPlaneReport -ExpectedCommit $ExpectedCommit `
        -CandidateProvenancePath $files.liveControlPlaneProvenance `
        -RequiredDuration ([TimeSpan]::FromHours($minimums.liveAndControlPlaneEnduranceHours)) `
        -MinimumCycles $minimums.liveAndControlPlaneMinimumCycles | Out-Null
    $null = $verified.Add('liveControlPlaneReport')
    $null = $verified.Add('liveControlPlaneProvenance')

    & (Join-Path $PSScriptRoot 'verify-endurance-disturbance-evidence.ps1') `
        -EvidencePath $files.disturbanceReport -EvidenceRoot $root -ExpectedCommit $ExpectedCommit `
        -ExpectedPackageManifestSha256 $records.packageManifest.sha256 `
        -ExpectedPackageProvenanceSha256 $records.packageProvenance.sha256 `
        -StreamsReportPath $files.streamsReport -ExpectedStreamsReportSha256 $records.streamsReport.sha256 `
        -SyncReportPath $files.syncReport -ExpectedSyncReportSha256 $records.syncReport.sha256 | Out-Null
    $null = $verified.Add('disturbanceReport')

    if ($verified.Count -ne $contract.requiredArtifactRoles.Count -or
        @(Compare-Object @($verified) @($contract.requiredArtifactRoles) -CaseSensitive).Count -ne 0)
    { throw 'Core payload verification did not cover exactly all fourteen required artifact roles.' }
    $after = Get-CorePayloadSnapshot $root
    if ($before.Count -ne $after.Count) { throw 'Core payload file set changed during verification.' }
    foreach ($key in $before.Keys)
    {
        if (-not $after.ContainsKey($key) -or $after[$key] -cne $before[$key])
        { throw "Core payload '$key' changed during verification." }
    }

    return [pscustomobject]@{
        Stage = 'CoreEvidencePayloads'; CandidateCommit = $ExpectedCommit; ReleaseVersion = '1.1.0'
        ProducerCount = $bindings.ProducerCount; WorkflowCount = $bindings.WorkflowCount
        ArtifactCount = $verified.Count; ApprovalCount = $bindings.ApprovalCount
        ApprovalPayloadsValidated = $true; AllPayloadsValidated = $true
        FixtureIdentityValidated = $false; RemoteIdentityValidated = $false
        ExecutionAuthenticityValidated = $false; ReleaseApproved = $false
        LatestProducerCompletedUtc = $bindings.LatestProducerCompletedUtc
        GitHubRunCount = $bindings.GitHubRunCount; LocalRunCount = $bindings.LocalRunCount
    }
}

Export-ModuleMember -Function Get-CoreCandidatePayloadReport
