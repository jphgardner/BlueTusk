[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-github-job-log-binding.psm1') -Force
$commit = 'a' * 40
$checkout = 'Run actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1'
$uploadAction = 'Run actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a'
$upload = 'Retain raw Core unit regression evidence'
$digest = 'b' * 64
$fixture = [ordered]@{
    job=@{id=54321; run_id=12345; run_attempt=1; head_sha=$commit; status='completed'; conclusion='success'
        html_url='https://github.com/jphgardner/BlueTusk/actions/runs/12345/job/54321'
        started_at='2026-01-01T00:00:01Z'; completed_at='2026-01-01T00:00:09Z'
        steps=@(
            @{name=$checkout; number=2; status='completed'; conclusion='success'; started_at='2026-01-01T00:00:02Z'; completed_at='2026-01-01T00:00:04Z'},
            @{name=$upload; number=4; status='completed'; conclusion='success'; started_at='2026-01-01T00:00:06Z'; completed_at='2026-01-01T00:00:08Z'})}
    artifact=@{artifactId=67890; runId=12345; sourceCommit=$commit; name='core-example'; bytes=123; sha256=$digest
        createdUtc='2026-01-01T00:00:07Z'; updatedUtc='2026-01-01T00:00:07Z'}
}
$json = $fixture | ConvertTo-Json -Depth 12
$text = @"
2026-01-01T00:00:02.1000000Z ##[group]$checkout
2026-01-01T00:00:03.1000000Z [command]/usr/bin/git log -1 --format=%H
2026-01-01T00:00:03.2000000Z $commit
2026-01-01T00:00:06.1000000Z ##[group]$uploadAction
2026-01-01T00:00:07.1000000Z SHA256 digest of uploaded artifact is $digest
2026-01-01T00:00:08.1000000Z Artifact core-example has been successfully uploaded! Final size is 123 bytes. Artifact ID is 67890
"@
$temporary = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-job-log-test-' + [Guid]::NewGuid().ToString('N'))))
$null = New-Item -ItemType Directory -Path $temporary
$log = Join-Path $temporary 'job.log'
$rejections = 0
function Fresh { return $json | ConvertFrom-Json -Depth 12 -DateKind String }
function Verify([object] $Fixture, [string] $Text = $text)
{
    [IO.File]::WriteAllText($log, $Text, [Text.UTF8Encoding]::new($false))
    return ConvertTo-CoreGithubJobLogBinding -Job $Fixture.job -LogPath $log -RunId 12345 -RunAttempt 1 `
        -ExpectedCommit $commit -ArtifactMetadata $Fixture.artifact -UploadStepName $upload
}
function Reject([string] $Name, [scriptblock] $Change, [string] $Guard)
{
    $value = Fresh
    & $Change $value
    $failure = $null
    try { $null = Verify $value } catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notmatch $Guard) { throw "Synthetic '$Name' did not fail at '$Guard': $failure" }
    $script:rejections++
}
function Reject-Log([string] $Name, [string] $Text, [string] $Guard)
{
    $failure = $null
    try { $null = Verify (Fresh) $Text } catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notmatch $Guard) { throw "Synthetic log '$Name' did not fail at '$Guard': $failure" }
    $script:rejections++
}
try
{
    $binding = Verify (Fresh)
    if ($binding.observedCheckoutCommit -cne $commit -or $binding.artifactSha256 -cne $digest -or
        $binding.checkoutCommitLine -ne 3 -or $binding.uploadFinalizationLine -ne 6)
    { throw 'Synthetic job log mapping lost its exact observations.' }
    if (@($binding.PSObject.Properties.Name | Where-Object { $_ -like '*Validated*' -or $_ -like '*Approved*' }).Count)
    { throw 'Pure job log mapping must not claim live identity, authenticity or qualification.' }
    $null = Verify (Fresh) $text.Replace('/usr/bin/git', '"C:\Program Files\Git\bin\git.exe"')
    $null = Verify (Fresh) ($text + "`nUnprefixed multiline action input`n")
    Reject 'other-run' { param($f) $f.job.run_id++ } 'different run'
    Reject 'other-attempt' { param($f) $f.job.run_attempt++ } 'different run'
    Reject 'string-run' { param($f) $f.job.run_id='12345' } 'JSON integer'
    Reject 'string-attempt' { param($f) $f.job.run_attempt='1' } 'JSON integer'
    Reject 'other-job-url' { param($f) $f.job.html_url += '1' } 'different run'
    Reject 'other-source' { param($f) $f.job.head_sha='c' * 40 } 'different run'
    Reject 'failed-job' { param($f) $f.job.conclusion='failure' } 'actually succeeded'
    Reject 'running-job' { param($f) $f.job.status='in_progress' } 'actually succeeded'
    Reject 'missing-steps' { param($f) $f.job.steps=@() } 'step metadata'
    Reject 'duplicate-step' { param($f) $f.job.steps[1].number=2 } 'duplicated'
    Reject 'checkout-unpinned' { param($f) $f.job.steps[0].name='Run actions/checkout@main' } 'pinned checkout'
    Reject 'checkout-skipped' { param($f) $f.job.steps[0].conclusion='skipped' } 'pinned checkout'
    Reject 'upload-skipped' { param($f) $f.job.steps[1].conclusion='skipped' } 'selected upload'
    Reject 'ambiguous-checkout' { param($f) $f.job.steps += ($f.job.steps[0] | ConvertTo-Json | ConvertFrom-Json -DateKind String); $f.job.steps[2].number=3 } 'pinned checkout'
    Reject 'ambiguous-upload' { param($f) $f.job.steps += ($f.job.steps[1] | ConvertTo-Json | ConvertFrom-Json -DateKind String); $f.job.steps[2].number=5 } 'selected upload'
    Reject 'wrong-upload' { param($f) $f.job.steps[1].name='Unrelated artifact' } 'selected upload'
    Reject 'step-outside-job' { param($f) $f.job.steps[1].completed_at='2026-01-01T00:00:10Z' } 'step lifecycle'
    Reject 'upload-before-checkout' { param($f) $f.job.steps[1].number=1 } 'must follow'
    Reject 'non-utc-job' { param($f) $f.job.started_at='2026-01-01T00:00:01+00:00' } 'explicit UTC'
    Reject 'other-artifact-run' { param($f) $f.artifact.runId++ } 'different run/source'
    Reject 'other-artifact-source' { param($f) $f.artifact.sourceCommit='c' * 40 } 'different run/source'
    Reject 'string-artifact-id' { param($f) $f.artifact.artifactId='67890' } 'JSON integer'
    Reject 'other-artifact-id' { param($f) $f.artifact.artifactId++ } 'Upload ID'
    Reject 'other-artifact-name' { param($f) $f.artifact.name='other' } 'Upload ID'
    Reject 'other-artifact-bytes' { param($f) $f.artifact.bytes++ } 'Upload ID'
    Reject 'other-artifact-digest' { param($f) $f.artifact.sha256='c' * 64 } 'Upload digest'
    Reject 'artifact-outside-step' { param($f) $f.artifact.createdUtc='2026-01-01T00:00:05Z' } 'outside its selected upload'
    Reject-Log 'unlabelled-commit' $text.Replace('[command]/usr/bin/git log -1 --format=%H', 'Candidate commit:') 'commit observation'
    Reject-Log 'wrong-commit' $text.Replace("Z $commit", ('Z ' + ('c' * 40))) 'Observed checkout commit'
    Reject-Log 'missing-checkout-group' $text.Replace("##[group]$checkout", 'Checkout') 'checkout action group'
    Reject-Log 'missing-upload-group' $text.Replace("##[group]$uploadAction", 'Upload') 'upload action group'
    Reject-Log 'late-checkout-observation' $text.Replace('00:00:03.', '00:00:05.') 'commit observation'
    Reject-Log 'early-upload-digest' $text.Replace('00:00:07.1000000Z', '00:00:05.1000000Z') 'Upload digest'
    Reject-Log 'duplicate-upload-digest' ($text + "`n2026-01-01T00:00:07.2000000Z SHA256 digest of uploaded artifact is $digest") 'Upload digest'
    Reject-Log 'missing-upload-finalization' $text.Replace(' has been successfully uploaded!', ' upload started!') 'Upload ID'
    Reject-Log 'log-outside-job' $text.Replace('00:00:02.1000000Z', '00:00:00.1000000Z') 'outside the selected job'
    Reject-Log 'missing-checkout-timestamp' $text.Replace('2026-01-01T00:00:02.1000000Z ', '') 'checkout action group'
    Reject-Log 'missing-digest-timestamp' $text.Replace('2026-01-01T00:00:07.1000000Z ', '') 'Upload digest'
    Write-Output "Core GitHub job log mapper passed Linux/Windows observations and $rejections synthetic substitution guards. Synthetic validation only; no live identity or release qualification."
}
finally
{
    $taskTempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $temporary.StartsWith($taskTempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $temporary -Leaf) -cnotmatch '^bluetusk-job-log-test-[0-9a-f]{32}$')
    { throw 'Unexpected test cleanup path.' }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
