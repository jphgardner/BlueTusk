[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-github-run-capture.psm1') -Force
$commit = 'a' * 40
$commitUtc = [DateTimeOffset]'2025-12-31T23:59:59Z'
$runId = 12345L
$jobId = 54321L
$fixture = [ordered]@{
    run=[ordered]@{id=$runId; run_attempt=1; head_sha=$commit; event='workflow_dispatch'; status='completed'
        conclusion='success'; workflow_id=42; path='.github/workflows/build.yml'
        html_url="https://github.com/jphgardner/BlueTusk/actions/runs/$runId"
        repository=@{full_name='jphgardner/BlueTusk'}; head_repository=@{full_name='jphgardner/BlueTusk'}
        created_at='2026-01-01T00:00:00Z'; run_started_at='2026-01-01T00:00:01Z'; updated_at='2026-01-01T00:00:10Z'}
    workflow=[ordered]@{id=42; path='.github/workflows/build.yml'}
    pages=@(@{total_count=1; jobs=@(@{id=$jobId; run_id=$runId; run_attempt=1; head_sha=$commit
        status='completed'; conclusion='success'; started_at='2026-01-01T00:00:02Z'; completed_at='2026-01-01T00:00:09Z'
        html_url="https://github.com/jphgardner/BlueTusk/actions/runs/$runId/job/$jobId"})})
}
$json = $fixture | ConvertTo-Json -Depth 12
$rejected = 0
function Fresh { return $json | ConvertFrom-Json -Depth 12 -DateKind String }
function Verify([object] $Fixture)
{
    return ConvertTo-CoreGithubRunRecord -Run $Fixture.run -Workflow $Fixture.workflow -JobPages $Fixture.pages `
        -WorkflowFile build.yml -RunId $runId -RunAttempt 1 -ExpectedCommit $commit -CandidateCommitUtc $commitUtc
}
function Reject([string] $Name, [scriptblock] $Change, [string] $Guard)
{
    $value = Fresh
    & $Change $value
    $errorText = $null
    try { $null = Verify $value } catch { $errorText = $_.Exception.Message }
    if ($null -eq $errorText -or $errorText -notmatch $Guard)
    { throw "Synthetic '$Name' did not fail at '$Guard': $errorText" }
    $script:rejected++
}
$record = Verify (Fresh)
if ($record.runId -ne $runId -or $record.headSha -cne $commit -or
    $record.completedUtc -cne '2026-01-01T00:00:09Z' -or $record.runAttempt -ne 1 -or
    @($record.PSObject.Properties).Count -ne 8)
{ throw 'Synthetic mapping changed the canonical eight-field workflow record.' }
# A mapped fixture has no live-identity, artifact, execution or release flags.
if (@($record.PSObject.Properties.Name | Where-Object { $_ -like '*Validated*' -or $_ -like '*Approved*' }).Count)
{ throw 'Synthetic API-shaped inputs must not claim live identity or qualification.' }

Reject 'missing-repository' { param($f) $f.run.PSObject.Properties.Remove('repository') } 'missing'
Reject 'run-id-string' { param($f) $f.run.id='12345' } 'JSON integer'
Reject 'run-id-zero' { param($f) $f.run.id=0 } 'positive JSON integer'
Reject 'different-run' { param($f) $f.run.id=67890 } 'ID or attempt'
Reject 'different-attempt' { param($f) $f.run.run_attempt=2 } 'ID or attempt'
Reject 'fractional-attempt' { param($f) $f.run.run_attempt=1.5 } 'JSON integer'
Reject 'repository' { param($f) $f.run.repository.full_name='another/BlueTusk' } 'repository or fork'
Reject 'fork' { param($f) $f.run.head_repository.full_name='another/BlueTusk' } 'repository or fork'
Reject 'different-head' { param($f) $f.run.head_sha='b'*40 } 'exact candidate'
Reject 'push-is-not-manual' { param($f) $f.run.event='push' } 'manual workflow_dispatch'
Reject 'pr-is-not-manual' { param($f) $f.run.event='pull_request' } 'manual workflow_dispatch'
Reject 'queued-run' { param($f) $f.run.status='queued' } 'completed successfully'
Reject 'failed-run' { param($f) $f.run.conclusion='failure' } 'completed successfully'
Reject 'cancelled-run' { param($f) $f.run.conclusion='cancelled' } 'completed successfully'
Reject 'workflow-id-string' { param($f) $f.workflow.id='42' } 'JSON integer'
Reject 'workflow-id-substitution' { param($f) $f.workflow.id=99 } 'workflow ID or path'
Reject 'workflow-path' { param($f) $f.workflow.path='.github/workflows/security.yml' } 'workflow ID or path'
Reject 'run-workflow-path' { param($f) $f.run.path='.github/workflows/security.yml' } 'workflow ID or path'
Reject 'url-repository' { param($f) $f.run.html_url='https://github.com/another/BlueTusk/actions/runs/12345' } 'run URL'
Reject 'url-fragment' { param($f) $f.run.html_url+='?claim=valid' } 'run URL'
Reject 'pre-candidate' { param($f) $f.run.created_at='2025-01-01T00:00:00Z' } 'predates'
Reject 'non-utc' { param($f) $f.run.created_at='2026-01-01T00:00:00+00:00' } 'explicit UTC'
Reject 'future-time' { param($f) $f.run.updated_at='2099-01-01T00:00:00Z' } 'future'
Reject 'run-clock-order' { param($f) $f.run.run_started_at='2025-12-31T23:59:59Z' } 'lifecycle'
Reject 'missing-jobs' { param($f) $f.pages=@() } 'empty'
Reject 'empty-job-page' { param($f) $f.pages[0].jobs=@() } 'empty/malformed'
Reject 'truncated-pages' { param($f) $f.pages[0].total_count=2 } 'complete declared'
Reject 'job-total-string' { param($f) $f.pages[0].total_count='1' } 'JSON integer'
Reject 'inconsistent-page-count' { param($f) $f.pages+=($f.pages[0] | ConvertTo-Json -Depth 8 | ConvertFrom-Json -DateKind String); $f.pages[1].total_count=2 } 'pages disagree'
Reject 'duplicate-job' { param($f) $f.pages[0].total_count=2; $f.pages[0].jobs+=($f.pages[0].jobs[0] | ConvertTo-Json | ConvertFrom-Json -DateKind String) } 'duplicated'
Reject 'job-id-string' { param($f) $f.pages[0].jobs[0].id='54321' } 'JSON integer'
Reject 'job-run-substitution' { param($f) $f.pages[0].jobs[0].run_id=222 } 'another run'
Reject 'job-attempt-substitution' { param($f) $f.pages[0].jobs[0].run_attempt=2 } 'another run'
Reject 'job-source-substitution' { param($f) $f.pages[0].jobs[0].head_sha='b'*40 } 'another run'
Reject 'unfinished-job' { param($f) $f.pages[0].jobs[0].status='in_progress' } 'unfinished or unsuccessful'
foreach ($conclusion in @('failure', 'cancelled', 'timed_out', 'neutral'))
{
    $invalidConclusion = $conclusion
    Reject "job-$conclusion" { param($f) $f.pages[0].jobs[0].conclusion=$invalidConclusion } 'unfinished or unsuccessful'
}
Reject 'job-url-substitution' { param($f) $f.pages[0].jobs[0].html_url='https://github.com/jphgardner/BlueTusk/actions/runs/999/job/54321' } 'job URL'
Reject 'job-completion-missing' { param($f) $f.pages[0].jobs[0].completed_at=$null } 'explicit UTC'
Reject 'job-completed-before-start' { param($f) $f.pages[0].jobs[0].completed_at='2026-01-01T00:00:01Z' } 'lifecycle'
Reject 'job-after-run-completion' { param($f) $f.pages[0].jobs[0].completed_at='2026-01-01T00:00:11Z' } 'lifecycle'
Reject 'only-skipped-jobs' { param($f) $f.pages[0].jobs[0].conclusion='skipped' } 'not only skipped'

$paginated = Fresh
$second = $paginated.pages[0].jobs[0] | ConvertTo-Json | ConvertFrom-Json -DateKind String
$second.id=54322; $second.html_url='https://github.com/jphgardner/BlueTusk/actions/runs/12345/job/54322'
$second.conclusion='skipped'; $second.completed_at='2026-01-01T00:00:10Z'
$paginated.pages[0].total_count=2
$paginated.pages+= [pscustomobject]@{total_count=2; jobs=@($second)}
$record = Verify $paginated
if ($record.completedUtc -cne '2026-01-01T00:00:10Z') { throw 'Last actual job completion was not retained.' }
Write-Output "Core GitHub run mapper rejected $rejected synthetic substitutions and preserved pagination/conditional skips. Synthetic validation only; no live identity or release qualification."
