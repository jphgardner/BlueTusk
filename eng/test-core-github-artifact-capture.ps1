[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-github-artifact-capture.psm1') -Force
$commit = 'a' * 40
$id = 67890L
$runId = 12345L
$expectedArtifactName = 'core-payload-example'
$url = "https://api.github.com/repos/jphgardner/BlueTusk/actions/artifacts/$id"
$fixture = [ordered]@{
    run=@{id=$runId; head_sha=$commit; repository=@{id=42; full_name='jphgardner/BlueTusk'}
        head_repository=@{id=42; full_name='jphgardner/BlueTusk'}
        run_started_at='2026-01-01T00:00:01Z'; updated_at='2026-01-01T00:00:10Z'}
    artifact=@{id=$id; name=$expectedArtifactName; size_in_bytes=123; digest=('sha256:' + ('b' * 64)); expired=$false
        url=$url; archive_download_url="$url/zip"; created_at='2026-01-01T00:00:04Z'
        updated_at='2026-01-01T00:00:05Z'; expires_at=[DateTimeOffset]::UtcNow.AddDays(30).ToString('yyyy-MM-ddTHH:mm:ssZ')
        workflow_run=@{id=$runId; repository_id=42; head_repository_id=42; head_sha=$commit}}
}
$json = $fixture | ConvertTo-Json -Depth 12
$rejected = 0
function Fresh { return $json | ConvertFrom-Json -Depth 12 -DateKind String }
function Verify([object] $Fixture)
{
    return ConvertTo-CoreGithubArtifactMetadata -Artifact $Fixture.artifact -Run $Fixture.run `
        -ArtifactId $id -ArtifactName $script:expectedArtifactName -RunId $runId -ExpectedCommit $commit
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
$metadata = Verify (Fresh)
if ($metadata.artifactId -ne $id -or $metadata.sourceCommit -cne $commit -or $metadata.bytes -ne 123)
{ throw 'Synthetic artifact mapping lost its requested identity.' }
if (@($metadata.PSObject.Properties.Name | Where-Object { $_ -like '*Validated*' -or $_ -like '*Approved*' }).Count)
{ throw 'Synthetic artifact metadata must not claim live identity or qualification.' }
Reject 'missing-digest' { param($f) $f.artifact.PSObject.Properties.Remove('digest') } 'missing'
Reject 'changed-id' { param($f) $f.artifact.id++ } 'ID or name'
Reject 'changed-name' { param($f) $f.artifact.name='another' } 'ID or name'
Reject 'string-id' { param($f) $f.artifact.id='67890' } 'JSON integer'
Reject 'fractional-size' { param($f) $f.artifact.size_in_bytes=1.5 } 'JSON integer'
Reject 'zero-size' { param($f) $f.artifact.size_in_bytes=0 } 'positive JSON integer'
Reject 'oversize' { param($f) $f.artifact.size_in_bytes=2GB + 1L } 'two-GiB'
Reject 'absent-digest' { param($f) $f.artifact.digest=$null } 'SHA-256 digest'
Reject 'other-digest-kind' { param($f) $f.artifact.digest='md5:' + ('b' * 32) } 'SHA-256 digest'
Reject 'changed-api-repository' { param($f) $f.artifact.url=$f.artifact.url.Replace('jphgardner','another') } 'API URLs'
Reject 'changed-download-url' { param($f) $f.artifact.archive_download_url='https://example.com/archive.zip' } 'API URLs'
Reject 'expired' { param($f) $f.artifact.expired=$true } 'expired'
Reject 'string-expired' { param($f) $f.artifact.expired='false' } 'expired'
Reject 'changed-run' { param($f) $f.artifact.workflow_run.id++ } 'another repository'
Reject 'changed-source' { param($f) $f.artifact.workflow_run.head_sha='c' * 40 } 'another repository'
Reject 'changed-repository-id' { param($f) $f.artifact.workflow_run.repository_id++ } 'another repository'
Reject 'changed-head-repository-id' { param($f) $f.artifact.workflow_run.head_repository_id++ } 'another repository'
Reject 'fork' { param($f) $f.run.head_repository.full_name='another/BlueTusk' } 'another repository'
Reject 'previous-attempt' { param($f) $f.artifact.created_at='2026-01-01T00:00:00Z' } 'outside the selected attempt'
Reject 'later-update' { param($f) $f.artifact.updated_at='2026-01-01T00:00:11Z' } 'outside the selected attempt'
Reject 'inverted-lifecycle' { param($f) $f.artifact.updated_at='2026-01-01T00:00:03Z' } 'outside the selected attempt'
Reject 'actual-expiry' { param($f) $f.artifact.expires_at='2026-01-01T00:00:06Z' } 'expired'
Reject 'offset-time' { param($f) $f.artifact.created_at='2026-01-01T00:00:04+00:00' } 'explicit UTC'

$root = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-artifact-capture-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
function New-Zip([string[]] $Names, [int] $Attributes = 0)
{
    $path = Join-Path $root ([guid]::NewGuid().ToString('N') + '.zip')
    $file = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite)
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $false)
    try
    {
        foreach ($entryName in $Names)
        {
            $entry = $zip.CreateEntry($entryName)
            $entry.ExternalAttributes = $Attributes
            if (-not $entryName.EndsWith('/'))
            {
                $content = $entry.Open()
                try { $bytes = [Text.Encoding]::UTF8.GetBytes('retained actual member bytes'); $content.Write($bytes, 0, $bytes.Length) }
                finally { $content.Dispose() }
            }
        }
    }
    finally { $zip.Dispose(); $file.Dispose() }
    $value = Fresh
    $value.artifact.size_in_bytes = (Get-Item -LiteralPath $path).Length
    $value.artifact.digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    return [pscustomobject]@{Path=$path; Metadata=(Verify $value)}
}
function Reject-Zip([string] $Name, [object] $Zip, [string] $Guard)
{
    $errorText = $null
    try { $null = Get-CoreGithubArtifactArchive -Metadata $Zip.Metadata -ArchivePath $Zip.Path }
    catch { $errorText = $_.Exception.Message }
    if ($null -eq $errorText -or $errorText -notmatch $Guard)
    { throw "Synthetic ZIP '$Name' did not fail at '$Guard': $errorText" }
    $script:rejected++
}
try
{
    $valid = New-Zip @('payload/', 'payload/report.json', 'payload/raw/log.txt')
    $report = Get-CoreGithubArtifactArchive -Metadata $valid.Metadata -ArchivePath $valid.Path
    $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('retained actual member bytes'))).ToLowerInvariant()
    if ($report.members.Count -ne 2 -or $report.uncompressedBytes -ne 56 -or
        $report.members[0].sha256 -cne $expectedHash -or $report.sha256 -cne $valid.Metadata.sha256)
    { throw 'Synthetic ZIP inventory did not retain exact delivered member bytes.' }
    $wrongSize = New-Zip @('report.json'); $wrongSize.Metadata.bytes++
    Reject-Zip 'wrong-size' $wrongSize 'byte count differs'
    $wrongHash = New-Zip @('report.json'); $wrongHash.Metadata.sha256='d' * 64
    Reject-Zip 'wrong-hash' $wrongHash 'SHA-256 differs'
    foreach ($entryName in @('../report.json', '/report.json', 'C:/report.json', 'raw\report.json', 'raw//report.json', 'raw/./report.json', 'NUL.txt', 'raw/report.'))
    { Reject-Zip $entryName (New-Zip @($entryName)) 'unsafe member path' }
    Reject-Zip 'duplicate-file' (New-Zip @('report.json','report.json')) 'duplicated'
    Reject-Zip 'case-file' (New-Zip @('Report.json','report.json')) 'case ambiguous'
    Reject-Zip 'case-parent' (New-Zip @('Raw/a.json','raw/b.json')) 'case ambiguous'
    Reject-Zip 'collision-parent-first' (New-Zip @('raw','raw/report.json')) 'collision'
    Reject-Zip 'collision-child-first' (New-Zip @('raw/report.json','raw')) 'collision'
    Reject-Zip 'symlink' (New-Zip @('report.json') -Attributes -1610612736) 'link, special file'
    Reject-Zip 'reparse' (New-Zip @('report.json') -Attributes 0x400) 'link, special file'
    Reject-Zip 'no-files' (New-Zip @('raw/')) 'no payload files'
}
finally
{
    # Delete only this invocation's verified fresh temporary directory.
    $resolved = [IO.Path]::GetFullPath($root)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolved -Leaf) -cnotmatch '^bluetusk-artifact-capture-[0-9a-f]{32}$')
    { throw 'Refusing to remove an unverified temporary capture path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output ([pscustomobject]@{Stage='CoreGithubArtifactCaptureSyntheticGuards'; Rejected=$rejected
    LiveRunMetadataValidated=$false; ArtifactDeliveryIdentityValidated=$false
    ExecutionAuthenticityValidated=$false; AllPayloadsValidated=$false; ReleaseApproved=$false})
