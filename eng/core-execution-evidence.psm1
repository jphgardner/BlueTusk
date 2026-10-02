Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')

function Get-CoreProducerIdentifier
{
    param([object] $Record)
    switch -CaseSensitive ($Record.kind)
    {
        'GitHubActions' { return "github:$($Record.run.runId):$($Record.run.runAttempt)" }
        'LocalDocker' { return "local:$($Record.capture.id)" }
        default { throw 'Unknown Core execution kind.' }
    }
}

function Get-CoreExecutionBindingReport
{
    param([Parameter(Mandatory)][object[]] $Records, [Parameter(Mandatory)][string] $EvidenceRoot,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
        [Parameter(Mandatory)][DateTimeOffset] $CandidateCommitUtc,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository)
    $contract = Get-CoreCandidateContract
    $localEligible = @('core-performance-evidence.yml', 'streams-release-endurance.yml',
        'sync-release-endurance.yml', 'live-control-plane-release-endurance.yml')
    if ($contract.localCandidateEvidenceSchemaVersion -ne 5 -or
        @($contract.localEligibleProducers).Count -ne 4 -or
        @(Compare-Object $localEligible @($contract.localEligibleProducers) -CaseSensitive).Count -ne 0)
    { throw 'Core local execution contract changed.' }
    if ($Records.Count -ne $contract.requiredWorkflows.Count)
    { throw 'Core execution evidence must cover exactly the seven required producers.' }
    $identifiers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $remoteRunIds = [Collections.Generic.HashSet[long]]::new()
    $bindings = @{}
    $latest = $CandidateCommitUtc.ToUniversalTime()
    $remote = 0
    $local = 0

    function Read-Utc([object] $Value)
    {
        if ($Value -isnot [string] -or $Value -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$')
        { throw 'Execution timestamps must be explicit ISO 8601 UTC strings.' }
        $result = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($Value, [ref]$result) -or
            $result -lt $CandidateCommitUtc.ToUniversalTime() -or $result -gt [DateTimeOffset]::UtcNow)
        { throw 'Execution timestamps cannot predate the candidate or be in the future.' }
        return $result
    }
    function Read-Artifact([object] $Artifact)
    {
        Assert-CoreEvidenceProperties $Artifact @('path', 'sha256', 'bytes') 'Execution artifact'
        Assert-CoreEvidenceInteger $Artifact.bytes 'Execution artifact bytes'
        if ($Artifact.path -isnot [string] -or $Artifact.sha256 -isnot [string] -or
            $Artifact.sha256 -cnotmatch '^[0-9a-f]{64}$')
        { throw 'Execution artifacts require normalized paths and lowercase SHA-256 digests.' }
        $path = Resolve-CoreEvidenceFile $EvidenceRoot $Artifact.path
        if ((Get-Item -LiteralPath $path).Length -ne $Artifact.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Artifact.sha256)
        { throw 'Execution artifact content differs from its hash or byte-count binding.' }
        return $path
    }

    foreach ($producer in $contract.requiredWorkflows)
    {
        $matches = @($Records | Where-Object producerFile -CEQ $producer)
        if ($matches.Count -ne 1) { throw "Core producer '$producer' must occur exactly once." }
        $record = $matches[0]
        if ($record.kind -ceq 'GitHubActions')
        {
            Assert-CoreEvidenceProperties $record @('kind', 'producerFile', 'run') 'GitHub execution'
            $run = $record.run
            Assert-CoreEvidenceProperties $run @('workflowFile', 'headSha', 'event', 'conclusion',
                'runId', 'runAttempt', 'completedUtc', 'url') 'GitHub run'
            Assert-CoreEvidenceInteger $run.runId 'GitHub run ID'
            Assert-CoreEvidenceInteger $run.runAttempt 'GitHub run attempt'
            if ($run.headSha -isnot [string] -or $run.workflowFile -cne $producer -or $run.headSha -cne $ExpectedCommit -or
                $run.event -cne 'workflow_dispatch' -or $run.conclusion -cne 'success' -or
                $run.url -cne "https://github.com/$ExpectedRepository/actions/runs/$($run.runId)")
            { throw 'GitHub producer must identify its successful manual exact-source repository run.' }
            if (-not $remoteRunIds.Add($run.runId)) { throw 'GitHub producer run ID is duplicated, regardless of attempt.' }
            $completed = Read-Utc $run.completedUtc
            $remote++
            $producerArtifacts = $null
        }
        elseif ($record.kind -ceq 'LocalDocker')
        {
            Assert-CoreEvidenceProperties $record @('kind', 'producerFile', 'capture') 'Local execution'
            if ($producer -cnotin $localEligible)
            { throw 'Local execution cannot replace required build, security or fuzzing workflows.' }
            Assert-CoreEvidenceProperties $record.capture @('id', 'manifest') 'Local capture'
            $id = $record.capture.id
            $guid = [Guid]::Empty
            if ($id -isnot [string] -or $id -cnotmatch '^[0-9a-f-]{36}$' -or
                -not [Guid]::TryParseExact($id, 'D', [ref]$guid) -or $guid -eq [Guid]::Empty)
            { throw 'Local capture identity must be a nonempty lowercase UUID, never a GitHub run ID.' }
            $manifestPath = Read-Artifact $record.capture.manifest
            if ($record.capture.manifest.path -cne "executions/$id/local-run.json")
            { throw 'Local run manifests must use their canonical capture directory.' }
            $manifest = Read-CoreEvidenceJson $manifestPath
            Assert-CoreEvidenceProperties $manifest @('schemaVersion', 'kind', 'captureId', 'producerFile',
                'scope', 'releaseVersion', 'sourceCommit', 'toolSourceCommit', 'sourceTreeDirty',
                'startedUtc', 'completedUtc', 'exitCode', 'environment', 'containerImageDigests',
                'logs', 'artifacts') 'Local run manifest'
            if ($manifest.schemaVersion -isnot [long] -and $manifest.schemaVersion -isnot [int])
            { throw 'Local manifest schema must be an integer.' }
            if ($manifest.schemaVersion -ne 1 -or $manifest.kind -cne 'LocalDocker' -or
                $manifest.captureId -cne $id -or $manifest.producerFile -cne $producer -or
                $manifest.scope -cne 'Core' -or $manifest.releaseVersion -cne '1.1.0' -or
                $manifest.sourceCommit -isnot [string] -or $manifest.sourceCommit -cne $ExpectedCommit -or
                $manifest.toolSourceCommit -isnot [string] -or $manifest.toolSourceCommit -cnotmatch '^[0-9a-f]{40}$' -or
                $manifest.sourceTreeDirty -isnot [bool] -or $manifest.sourceTreeDirty -ne $false -or
                ($manifest.exitCode -isnot [int] -and $manifest.exitCode -isnot [long]) -or $manifest.exitCode -ne 0)
            { throw 'Local producer manifest must bind its actual source, clean tree and successful execution.' }
            $started = Read-Utc $manifest.startedUtc
            $completed = Read-Utc $manifest.completedUtc
            if ($completed -le $started) { throw 'Local completion must follow its recorded start.' }
            Assert-CoreEvidenceProperties $manifest.environment @('hostOs', 'architecture', 'dockerOs') 'Local environment'
            if ($manifest.environment.hostOs -cnotin @('windows', 'linux') -or
                $manifest.environment.architecture -cne 'x64' -or $manifest.environment.dockerOs -cne 'linux')
            { throw 'Local Core captures require a recorded Windows/Linux x64 host and Linux Docker environment.' }
            if ($manifest.containerImageDigests -isnot [array] -or $manifest.containerImageDigests.Count -eq 0 -or
                @($manifest.containerImageDigests | Where-Object { $_ -isnot [string] -or $_ -cnotmatch '^\S+@sha256:[0-9a-f]{64}$' }).Count -ne 0 -or
                @($manifest.containerImageDigests | Sort-Object -Unique).Count -ne $manifest.containerImageDigests.Count)
            { throw 'Local captures require unique immutable container image references.' }
            if ($manifest.logs -isnot [array] -or $manifest.logs.Count -eq 0 -or $manifest.artifacts -isnot [array])
            { throw 'Local captures must retain logs and their produced artifact bindings.' }
            $logPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($log in $manifest.logs)
            {
                $null = Read-Artifact $log
                if (-not $log.path.StartsWith("executions/$id/", [StringComparison]::Ordinal) -or
                    -not $logPaths.Add($log.path))
                { throw 'Local logs must occur once each inside their capture directory.' }
            }
            $roles = @($contract.requiredArtifactRoles | Where-Object {
                $contract.artifactBindings.PSObject.Properties[$_].Value.workflowFile -ceq $producer
            })
            if ($manifest.artifacts.Count -ne $roles.Count)
            { throw 'Local capture must bind exactly its producer artifact roles.' }
            $producerArtifacts = @{}
            foreach ($role in $roles)
            {
                $artifactMatches = @($manifest.artifacts | Where-Object role -CEQ $role)
                if ($artifactMatches.Count -ne 1) { throw 'Local producer roles must occur exactly once.' }
                $artifact = $artifactMatches[0]
                Assert-CoreEvidenceProperties $artifact @('role', 'file') 'Local produced artifact'
                $binding = $contract.artifactBindings.PSObject.Properties[$role].Value
                if ($artifact.file.path -cne $binding.path) { throw 'Local producer artifact path differs from its contract.' }
                $path = Read-Artifact $artifact.file
                $payload = Read-CoreEvidenceJson $path
                $commitProperty = $payload.PSObject.Properties[$binding.commitProperty]
                if ($null -eq $commitProperty -or $commitProperty.Value -isnot [string] -or
                    $commitProperty.Value -cne $ExpectedCommit)
                { throw 'Local producer artifact belongs to another source commit.' }
                $producerArtifacts[$role] = $artifact.file
            }
            $local++
        }
        else { throw 'Unknown Core execution kind.' }
        $identifier = Get-CoreProducerIdentifier $record
        if (-not $identifiers.Add($identifier)) { throw 'Core producer execution identity is duplicated.' }
        $bindings[$producer] = [pscustomobject]@{ Identifier = $identifier; Artifacts = $producerArtifacts }
        if ($completed -gt $latest) { $latest = $completed }
    }
    return [pscustomobject]@{ RunCount = $Records.Count; GitHubRunCount = $remote; LocalRunCount = $local
        LatestCompletedUtc = $latest; Bindings = $bindings; ReleaseApproved = $false }
}

Export-ModuleMember -Function Get-CoreProducerIdentifier, Get-CoreExecutionBindingReport
