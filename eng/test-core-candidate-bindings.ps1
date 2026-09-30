[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1') -Force
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $repositoryRoot ('artifacts/core-candidate-bindings-self-test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$commit = '0' * 40
$commitUtc = [DateTimeOffset]'2025-12-31T23:59:59Z'
$contract = Get-CoreCandidateContract
$builder = Join-Path $PSScriptRoot 'build-core-candidate-envelope.ps1'
$baseRoot = Join-Path $scratch 'positive'
$null = New-Item -ItemType Directory -Path $baseRoot
$rejected = 0

function Write-FixtureJson([string] $Path, [object] $Value)
{
    $null = [IO.Directory]::CreateDirectory((Split-Path $Path -Parent))
    $Value | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Refresh-Binding([object] $Record, [string] $Root)
{
    $path = Join-Path $Root $Record.path
    $Record.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $Record.bytes = (Get-Item -LiteralPath $path).Length
}

function Edit-Payload([object] $Envelope, [string] $Root, [string] $Role, [scriptblock] $Change)
{
    $record = @($Envelope.artifacts | Where-Object role -ceq $Role)[0]
    $path = Join-Path $Root $record.path
    $payload = Read-CoreEvidenceJson $path
    & $Change $payload
    Write-FixtureJson $path $payload
    Refresh-Binding $record $Root
}

function Edit-Approval([object] $Envelope, [string] $Root, [string] $Id, [scriptblock] $Change)
{
    $record = @($Envelope.approvals | Where-Object id -ceq $Id)[0]
    $path = Join-Path $Root $record.path
    $payload = Read-CoreEvidenceJson $path
    & $Change $payload
    Write-FixtureJson $path $payload
    Refresh-Binding $record $Root
}

function Verify-Fixture([string] $Root)
{
    Get-CoreCandidateBindingReport -EvidencePath (Join-Path $Root 'candidate.json') `
        -ExpectedCommit $commit -CandidateCommitUtc $commitUtc
}

function Reject-Fixture([string] $Name, [scriptblock] $Change, [string] $ExpectedError, [string] $FixtureRoot = $baseRoot)
{
    $root = Join-Path $scratch $Name
    $null = New-Item -ItemType Directory -Path $root
    Get-ChildItem -LiteralPath $FixtureRoot -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $root -Recurse }
    $path = Join-Path $root 'candidate.json'
    $envelope = Read-CoreEvidenceJson $path
    & $Change $envelope $root
    Write-FixtureJson $path $envelope
    $failure = $null
    try { Verify-Fixture $root | Out-Null } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or $failure -notmatch $ExpectedError)
    { throw "Fixture '$Name' did not fail at its expected guard '$ExpectedError': $failure" }
    $script:rejected++
}

try
{
    # These are deliberately incomplete synthetic payloads. Passing this stage
    # MUST NOT claim to have verified their benchmark/test/endurance contents.
    foreach ($role in $contract.requiredArtifactRoles)
    {
        $binding = $contract.artifactBindings.PSObject.Properties[$role].Value
        $payload = [ordered]@{ schemaVersion = 1; fixture = 'synthetic binding self-test; NOT release evidence' }
        $payload[$binding.commitProperty] = $commit
        if ($role -eq 'packageManifest') { $payload.releaseTrack = 'Core'; $payload.releaseVersion = '1.2.0' }
        if ($role -eq 'performanceManifest') { $payload.scope = 'Core'; $payload.release = '1.2.0' }
        Write-FixtureJson (Join-Path $baseRoot $binding.path) $payload
    }
    $runs = @(for ($index = 0; $index -lt $contract.requiredWorkflows.Count; $index++)
    {
        [ordered]@{ workflowFile = $contract.requiredWorkflows[$index]; headSha = $commit
            event = 'workflow_dispatch'; conclusion = 'success'; runId = $index + 1; runAttempt = 1
            completedUtc = "2026-01-01T00:00:0$($index + 1)Z"
            url = "https://github.com/jphgardner/BlueTusk/actions/runs/$($index + 1)" }
    })
    Write-FixtureJson (Join-Path $baseRoot 'workflow-runs.json') $runs
    $examples = (Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'v1-approval-evidence.examples.json')).examples
    $metricsPath = Join-Path $baseRoot $contract.artifactBindings.websiteMetrics.path
    $metricsHash = (Get-FileHash -LiteralPath $metricsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    foreach ($record in $examples)
    {
        $record.approvedUtc = '2026-01-01T00:00:10Z'
        if ($record.gateId -eq 'independent-release-review') { $record.approvedUtc = '2026-01-01T00:00:15Z' }
        if ($record.gateId -eq 'maintainer-signoff') { $record.approvedUtc = '2026-01-01T00:00:20Z' }
        if ($record.gateId -like 'application-pilot-*')
        { $record.details.enabledProductFamilies = @($record.details.enabledProductFamilies | Where-Object { $_ -ne 'ContinuousGraph' }) }
        if ($record.gateId -eq 'independent-release-review') { $record.details.packageFamiliesReviewed = 5 }
        if ($record.gateId -eq 'website-deployment-acceptance') { $record.details.productionMetricsSha256 = $metricsHash }
        if ($record.gateId -eq 'maintainer-signoff')
        {
            $record.details.versions = @('Provider 1.2.0', 'Streams 1.2.0', 'Sync 1.2.0', 'Live 1.2.0', 'Control Plane 1.2.0')
            $record.details.publishedPrereleaseTags = 5
            $record.details.publishedPrereleaseFamilies = @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane')
        }
        Write-FixtureJson (Join-Path $baseRoot "approvals/$($record.gateId).json") $record
    }
    $positive = & $builder -EvidenceRoot $baseRoot -ExpectedCommit $commit -CandidateCommitUtc $commitUtc
    if ($positive.Stage -cne 'CoreEvidenceBindings' -or $positive.WorkflowCount -ne 7 -or
        $positive.ArtifactCount -ne 14 -or $positive.ApprovalCount -ne 10 -or
        $positive.ApprovalPayloadsValidated -ne $true -or $positive.AllPayloadsValidated -ne $false -or
        $positive.RemoteIdentityValidated -ne $false -or $positive.ReleaseApproved -ne $false)
    { throw 'Binding-only report weakened coverage or claimed release qualification.' }
    $null = Verify-Fixture $baseRoot
    $hashBefore = (Get-FileHash -LiteralPath (Join-Path $baseRoot 'candidate.json') -Algorithm SHA256).Hash
    $failure = $null
    try { & $builder -EvidenceRoot $baseRoot -ExpectedCommit $commit -CandidateCommitUtc $commitUtc | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($failure -notmatch 'already exists' -or
        (Get-FileHash -LiteralPath (Join-Path $baseRoot 'candidate.json') -Algorithm SHA256).Hash -cne $hashBefore)
    { throw 'Existing evidence was overwritten or the refusal was not enforced.' }
    $rejected++

    $cases = @(
        @{ Name = 'old-schema'; Error = 'envelope'; Change = {param($e,$r) $e.schemaVersion = 3} },
        @{ Name = 'text-schema'; Error = 'envelope'; Change = {param($e,$r) $e.schemaVersion = '4'} },
        @{ Name = 'preview-scope'; Error = 'envelope'; Change = {param($e,$r) $e.scope = 'ContinuousGraphPreview'} },
        @{ Name = 'boolean-scope'; Error = 'envelope'; Change = {param($e,$r) $e.scope = $true} },
        @{ Name = 'old-release'; Error = 'envelope'; Change = {param($e,$r) $e.releaseVersion = '1.1.0'} },
        @{ Name = 'wrong-candidate'; Error = 'envelope'; Change = {param($e,$r) $e.candidateCommit = ('1' * 40)} },
        @{ Name = 'pretend-ready'; Error = 'schema mismatch'; Change = {param($e,$r) $e | Add-Member releaseApproved $true} },
        @{ Name = 'missing-workflow'; Error = 'each required workflow'; Change = {param($e,$r) $e.workflowRuns = @($e.workflowRuns | Select-Object -First 6)} },
        @{ Name = 'graph-workflow'; Error = 'exactly one'; Change = {param($e,$r) $e.workflowRuns[6].workflowFile = 'continuous-graph-release-endurance.yml'} },
        @{ Name = 'wrong-run-commit'; Error = 'successful positive-attempt'; Change = {param($e,$r) $e.workflowRuns[0].headSha = ('1' * 40)} },
        @{ Name = 'duplicate-run-id'; Error = 'duplicated'; Change = {param($e,$r) $e.workflowRuns[1].runId = $e.workflowRuns[0].runId} },
        @{ Name = 'text-run-id'; Error = 'JSON integer'; Change = {param($e,$r) $e.workflowRuns[0].runId = '1'} },
        @{ Name = 'automatic-run'; Error = 'successful positive-attempt'; Change = {param($e,$r) $e.workflowRuns[0].event = 'push'} },
        @{ Name = 'failed-run'; Error = 'successful positive-attempt'; Change = {param($e,$r) $e.workflowRuns[0].conclusion = 'failure'} },
        @{ Name = 'wrong-repository'; Error = 'different repository'; Change = {param($e,$r) $e.workflowRuns[0].url = 'https://github.com/other/repo/actions/runs/1'} },
        @{ Name = 'future-run'; Error = 'future'; Change = {param($e,$r) $e.workflowRuns[0].completedUtc = [DateTimeOffset]::UtcNow.AddDays(1).UtcDateTime.ToString('o')} },
        @{ Name = 'missing-role'; Error = 'fourteen'; Change = {param($e,$r) $e.artifacts = @($e.artifacts | Select-Object -First 13)} },
        @{ Name = 'duplicate-role'; Error = 'exactly once'; Change = {param($e,$r) $e.artifacts[13].role = $e.artifacts[0].role} },
        @{ Name = 'wrong-producer'; Error = 'canonical'; Change = {param($e,$r) $e.artifacts[0].workflowFile = 'security.yml'} },
        @{ Name = 'wrong-producer-run'; Error = 'different producer run'; Change = {param($e,$r) $e.artifacts[0].runId = 2} },
        @{ Name = 'handoff-invented-run'; Error = 'invent'; Change = {param($e,$r) $e.artifacts[13].runId = 1} },
        @{ Name = 'wrong-file-hash'; Error = 'file hash'; Change = {param($e,$r) $e.artifacts[0].sha256 = ('f' * 64)} },
        @{ Name = 'wrong-file-bytes'; Error = 'byte-count'; Change = {param($e,$r) $e.artifacts[0].bytes++} },
        @{ Name = 'fractional-bytes'; Error = 'JSON integer'; Change = {param($e,$r) $e.artifacts[0].bytes = 1.5} },
        @{ Name = 'boolean-bytes'; Error = 'JSON integer'; Change = {param($e,$r) $e.artifacts[0].bytes = $true} },
        @{ Name = 'traversal-path'; Error = 'canonical'; Change = {param($e,$r) $e.artifacts[0].path = '../website/production-metrics.json'} },
        @{ Name = 'case-changed-path'; Error = 'canonical'; Change = {param($e,$r) $e.artifacts[0].path = 'Website/production-metrics.json'} },
        @{ Name = 'stale-payload'; Error = 'exact candidate'; Change = {param($e,$r) Edit-Payload $e $r websiteMetrics {param($p) $p.sourceCommit = ('1' * 40)}} },
        @{ Name = 'boolean-payload-commit'; Error = 'exact candidate'; Change = {param($e,$r) Edit-Payload $e $r websiteMetrics {param($p) $p.sourceCommit = $true}} },
        @{ Name = 'graph-performance'; Error = 'scope/version'; Change = {param($e,$r) Edit-Payload $e $r performanceManifest {param($p) $p.scope = 'ContinuousGraphPreview'}} },
        @{ Name = 'boolean-performance-scope'; Error = 'scope/version'; Change = {param($e,$r) Edit-Payload $e $r performanceManifest {param($p) $p.scope = $true}} },
        @{ Name = 'old-performance-version'; Error = 'scope/version'; Change = {param($e,$r) Edit-Payload $e $r performanceManifest {param($p) $p.release = '1.1.0'}} },
        @{ Name = 'legacy-package-track'; Error = 'scope/version'; Change = {param($e,$r) Edit-Payload $e $r packageManifest {param($p) $p.releaseTrack = 'Legacy'}} },
        @{ Name = 'missing-approval'; Error = 'ten canonical'; Change = {param($e,$r) $e.approvals = @($e.approvals | Select-Object -First 9)} },
        @{ Name = 'duplicate-approval'; Error = 'exactly once'; Change = {param($e,$r) $e.approvals[9].id = $e.approvals[0].id} },
        @{ Name = 'wrong-approval-hash'; Error = 'file hash'; Change = {param($e,$r) $e.approvals[0].sha256 = ('f' * 64)} },
        @{ Name = 'non-independent-review'; Error = 'reviewerIndependent'; Change = {param($e,$r) Edit-Approval $e $r independent-release-review {param($p) $p.details.reviewerIndependent = $false}} },
        @{ Name = 'wrong-website-approval'; Error = 'production-metrics'; Change = {param($e,$r) Edit-Approval $e $r website-deployment-acceptance {param($p) $p.details.productionMetricsSha256 = ('f' * 64)}} },
        @{ Name = 'stale-release-review'; Error = 'before|predate'; Change = {param($e,$r) Edit-Approval $e $r independent-release-review {param($p) $p.approvedUtc = '2026-01-01T00:00:00Z'}} },
        @{ Name = 'extra-approval-file'; Error = 'exactly the ten'; Change = {param($e,$r) Write-FixtureJson (Join-Path $r 'approvals/unbound.json') @{fixture='synthetic'}} }
    )
    foreach ($case in $cases) { Reject-Fixture $case.Name $case.Change $case.Error }

    # A distinct schema preserves local identities instead of inventing GitHub
    # workflow IDs. These fixtures exercise bindings only, never qualification.
    $localRoot = Join-Path $scratch 'local-positive'
    $null = New-Item -ItemType Directory -Path $localRoot
    Get-ChildItem -LiteralPath $baseRoot -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $localRoot -Recurse }
    Remove-Item -LiteralPath (Join-Path $localRoot 'candidate.json')
    $localRecords = @(for ($index = 0; $index -lt $runs.Count; $index++)
    {
        $producer = $runs[$index].workflowFile
        if ($index -lt 3)
        { [ordered]@{ kind = 'GitHubActions'; producerFile = $producer; run = $runs[$index] }; continue }
        $id = '00000000-0000-0000-0000-' + ($index + 1).ToString('000000000000')
        $logPath = "executions/$id/capture.log"
        $null = [IO.Directory]::CreateDirectory((Join-Path $localRoot "executions/$id"))
        'SYNTHETIC reader fixture, not a captured run or release evidence.' |
            Set-Content -LiteralPath (Join-Path $localRoot $logPath) -Encoding utf8NoBOM
        $log = [ordered]@{ path = $logPath; sha256 = ''; bytes = 0 }
        Refresh-Binding $log $localRoot
        $produced = @(foreach ($role in $contract.requiredArtifactRoles)
        {
            $binding = $contract.artifactBindings.PSObject.Properties[$role].Value
            if ($binding.workflowFile -cne $producer) { continue }
            $file = [ordered]@{ path = $binding.path; sha256 = ''; bytes = 0 }
            Refresh-Binding $file $localRoot
            [ordered]@{ role = $role; file = $file }
        })
        $manifest = [ordered]@{ schemaVersion = 1; kind = 'LocalDocker'; captureId = $id; producerFile = $producer
            scope = 'Core'; releaseVersion = '1.2.0'; sourceCommit = $commit; toolSourceCommit = $commit
            sourceTreeDirty = $false; startedUtc = '2026-01-01T00:00:01Z'; completedUtc = "2026-01-01T00:00:0$($index + 1)Z"
            exitCode = 0; environment = @{ hostOs = 'windows'; architecture = 'x64'; dockerOs = 'linux' }
            containerImageDigests = @($contract.endurancePostgreSqlImage); logs = @($log); artifacts = @($produced) }
        $manifestPath = "executions/$id/local-run.json"
        Write-FixtureJson (Join-Path $localRoot $manifestPath) $manifest
        $file = [ordered]@{ path = $manifestPath; sha256 = ''; bytes = 0 }
        Refresh-Binding $file $localRoot
        [ordered]@{ kind = 'LocalDocker'; producerFile = $producer; capture = @{ id = $id; manifest = $file } }
    })
    Write-FixtureJson (Join-Path $localRoot 'producer-runs.json') $localRecords
    $localReport = & $builder -EvidenceRoot $localRoot -ExpectedCommit $commit -CandidateCommitUtc $commitUtc -UseLocalExecution
    if ($localReport.GitHubRunCount -ne 3 -or $localReport.LocalRunCount -ne 4 -or
        $localReport.WorkflowCount -ne 3 -or $localReport.ProducerCount -ne 7 -or $localReport.ReleaseApproved -ne $false -or
        $localReport.AllPayloadsValidated -ne $false -or $localReport.RemoteIdentityValidated -ne $false)
    { throw 'Hybrid bindings must retain distinct identities and cannot claim release qualification.' }
    function Edit-LocalManifest([object] $Envelope, [string] $Root, [scriptblock] $Change)
    {
        $record = $Envelope.producerRuns[3].capture.manifest
        $path = Join-Path $Root $record.path
        $manifest = Read-CoreEvidenceJson $path
        & $Change $manifest
        Write-FixtureJson $path $manifest
        Refresh-Binding $record $Root
    }
    $localCases = @(
        @{ Name = 'local-fake-github-id'; Error = 'schema mismatch'; Change = {param($e,$r) $e.producerRuns[3] | Add-Member runId 1} },
        @{ Name = 'local-fake-github-url'; Error = 'schema mismatch'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m | Add-Member url 'https://github.com/jphgardner/BlueTusk/actions/runs/1'}} },
        @{ Name = 'local-numeric-id'; Error = 'UUID'; Change = {param($e,$r) $e.producerRuns[3].capture.id = 123} },
        @{ Name = 'local-unknown-kind'; Error = 'Unknown'; Change = {param($e,$r) $e.producerRuns[3].kind = 'GitHubLocal'} },
        @{ Name = 'local-required-security'; Error = 'cannot replace'; Change = {param($e,$r) $e.producerRuns[1] = $e.producerRuns[3] | ConvertTo-Json -Depth 20 | ConvertFrom-Json; $e.producerRuns[1].producerFile = 'security.yml'} },
        @{ Name = 'local-failed-run'; Error = 'successful execution'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.exitCode = 1}} },
        @{ Name = 'local-dirty-source'; Error = 'clean tree'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.sourceTreeDirty = $true}} },
        @{ Name = 'local-wrong-source'; Error = 'actual source'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.sourceCommit = ('1' * 40)}} },
        @{ Name = 'local-numeric-source'; Error = 'actual source'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.sourceCommit = 0}} },
        @{ Name = 'local-missing-tool-source'; Error = 'actual source'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.toolSourceCommit = ''}} },
        @{ Name = 'local-zero-duration'; Error = 'follow'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.completedUtc = $m.startedUtc}} },
        @{ Name = 'local-future-completion'; Error = 'future'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.completedUtc = [DateTimeOffset]::UtcNow.AddDays(1).UtcDateTime.ToString('O')}} },
        @{ Name = 'local-unpinned-image'; Error = 'immutable'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.containerImageDigests = @('postgres:18-alpine')}} },
        @{ Name = 'local-without-log'; Error = 'retain logs'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.logs = @()}} },
        @{ Name = 'local-changed-log'; Error = 'artifact content'; Change = {param($e,$r) $m = Read-CoreEvidenceJson (Join-Path $r $e.producerRuns[3].capture.manifest.path); Add-Content -LiteralPath (Join-Path $r $m.logs[0].path) 'tampered'} },
        @{ Name = 'local-changed-manifest'; Error = 'artifact content'; Change = {param($e,$r) Add-Content -LiteralPath (Join-Path $r $e.producerRuns[3].capture.manifest.path) ' '} },
        @{ Name = 'local-without-produced-artifact'; Error = 'exactly its producer'; Change = {param($e,$r) Edit-LocalManifest $e $r {param($m) $m.artifacts = @()}} },
        @{ Name = 'local-wrong-execution-binding'; Error = 'execution identity'; Change = {param($e,$r) $e.artifacts[3].executionId = 'github:1:1'} },
        @{ Name = 'local-stale-producer-artifact'; Error = 'artifact content'; Change = {param($e,$r) Edit-Payload $e $r performanceManifest {param($p) $p | Add-Member changed 'after capture'}} },
        @{ Name = 'local-remote-automatic-run'; Error = 'manual'; Change = {param($e,$r) $e.producerRuns[0].run.event = 'push'} },
        @{ Name = 'local-duplicate-remote-identity'; Error = 'duplicated'; Change = {param($e,$r) $e.producerRuns[1].run.runId = 1; $e.producerRuns[1].run.url = $e.producerRuns[0].run.url} }
    )
    foreach ($case in $localCases) { Reject-Fixture $case.Name $case.Change $case.Error $localRoot }

    $contractCases = @(
        @{ Name = 'ready-status'; Error = 'unqualified-publication'; Change = {param($c) $c.status = 'ready'} },
        @{ Name = 'enabled-publication'; Error = 'disabled-publication'; Change = {param($c) $c.publicationEnabled = $true} },
        @{ Name = 'preview-family'; Error = 'family scope'; Change = {param($c) $c.coreFamilies[4] = 'ContinuousGraph'} },
        @{ Name = 'local-build-substitution'; Error = 'local execution contract'; Change = {param($c) $c.localEligibleProducers[0] = 'build.yml'} },
        @{ Name = 'local-schema-text'; Error = 'local execution contract'; Change = {param($c) $c.localCandidateEvidenceSchemaVersion = '5'} },
        @{ Name = 'shortened-endurance'; Error = 'durations'; Change = {param($c) $c.minimums.streamsEnduranceHours = 1} },
        @{ Name = 'unpinned-endurance-image'; Error = 'digest-pinned'; Change = {param($c) $c.endurancePostgreSqlImage = 'postgres:18-alpine'} }
    )
    foreach ($case in $contractCases)
    {
        $copy = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'v1.2-candidate-readiness.json')
        & $case.Change $copy
        $path = Join-Path $scratch ("contract-$($case.Name).json")
        Write-FixtureJson $path $copy
        $failure = $null
        try { Get-CoreCandidateContract -ConfigurationPath $path | Out-Null }
        catch { $failure = $_.Exception.Message }
        if ($null -eq $failure -or $failure -notmatch $case.Error)
        { throw "Contract '$($case.Name)' did not fail at its expected guard '$($case.Error)': $failure" }
        $rejected++
    }

    $duplicateRoot = Join-Path $scratch 'duplicate-json-name'
    $null = New-Item -ItemType Directory -Path $duplicateRoot
    $path = Join-Path $duplicateRoot 'candidate.json'
    '{"schemaVersion":4,"scope":"Core","Scope":"ContinuousGraphPreview"}' | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    $failure = $null
    try { Verify-Fixture $duplicateRoot | Out-Null } catch { $failure = $_.Exception.Message }
    if ($failure -notmatch 'duplicate or case-ambiguous') { throw "Duplicate JSON key guard failed: $failure" }
    $rejected++

    $link = Join-Path $scratch 'linked-root'
    if ($IsWindows) { $null = New-Item -ItemType Junction -Path $link -Value $baseRoot }
    else { $null = New-Item -ItemType SymbolicLink -Path $link -Value $baseRoot }
    try
    {
        $failure = $null
        try { Verify-Fixture $link | Out-Null } catch { $failure = $_.Exception.Message }
        if ($failure -notmatch 'link or junction') { throw "Evidence root link guard failed: $failure" }
        $rejected++
        # The supplied root itself is ordinary; its linked parent must still fail.
        $failure = $null
        try { Resolve-CoreEvidenceFile (Join-Path $link 'website') 'production-metrics.json' | Out-Null }
        catch { $failure = $_.Exception.Message }
        if ($failure -notmatch 'ancestry') { throw "Evidence root ancestor link guard failed: $failure" }
        $rejected++
    }
    finally { Remove-Item -LiteralPath $link -Force }
    Write-Output "Core candidate binding self-test passed: synthetic remote and hybrid 7-producer/14-artifact/10-approval joins and $rejected rejected mutations. No payload qualification, execution authenticity, workflow execution or publication is certified."
}
finally
{
    $full = [IO.Path]::GetFullPath($scratch)
    $prefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($full).StartsWith('core-candidate-bindings-self-test-', [StringComparison]::Ordinal))
    { throw 'Refusing unexpected binding self-test cleanup target.' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
