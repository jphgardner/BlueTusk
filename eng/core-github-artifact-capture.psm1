Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')

function ConvertTo-CoreGithubArtifactMetadata
{
    param([Parameter(Mandatory)][object] $Artifact, [Parameter(Mandatory)][object] $Run,
        [Parameter(Mandatory)][long] $ArtifactId, [Parameter(Mandatory)][string] $ArtifactName,
        [Parameter(Mandatory)][long] $RunId,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
        [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk')

    # API-shaped synthetic inputs can test these guards, but cannot prove a live
    # download. The caller must first validate the complete successful attempt.
    function Require([bool] $Condition, [string] $Message)
    { if (-not $Condition) { throw $Message } }
    function Value([object] $Object, [string] $Name)
    {
        Require ($Object -is [pscustomobject]) 'Artifact metadata requires JSON objects.'
        $property = $Object.PSObject.Properties[$Name]
        Require ($null -ne $property) "Artifact metadata is missing '$Name'."
        return ,$property.Value
    }
    function Utc([object] $Text)
    {
        Require ($Text -is [string] -and $Text -cmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') 'Artifact lifecycle requires explicit UTC timestamps.'
        $instant = [DateTimeOffset]::MinValue
        Require ([DateTimeOffset]::TryParse($Text, [ref]$instant)) 'Artifact lifecycle timestamp is invalid.'
        return $instant
    }
    Assert-CoreEvidenceInteger $ArtifactId 'Requested artifact ID'
    Assert-CoreEvidenceInteger $RunId 'Requested artifact run ID'
    $id = Value $Artifact 'id'
    $size = Value $Artifact 'size_in_bytes'
    Assert-CoreEvidenceInteger $id 'GitHub artifact ID'
    Assert-CoreEvidenceInteger $size 'GitHub artifact byte count'
    $actualName = Value $Artifact 'name'
    Require ($id -eq $ArtifactId -and $actualName -is [string] -and $actualName -ceq $ArtifactName -and
        -not [string]::IsNullOrWhiteSpace($ArtifactName)) 'Artifact ID or name differs from the requested delivery.'
    Require ($size -le 2GB) 'Artifact archive exceeds the two-GiB capture limit.'
    $url = "https://api.github.com/repos/$ExpectedRepository/actions/artifacts/$ArtifactId"
    Require ((Value $Artifact 'url') -ceq $url -and (Value $Artifact 'archive_download_url') -ceq "$url/zip") 'Artifact API URLs differ from the expected repository and ID.'
    $expired = Value $Artifact 'expired'
    Require ($expired -is [bool] -and -not $expired) 'Artifact must not be expired.'
    $digest = Value $Artifact 'digest'
    Require ($digest -is [string] -and $digest -cmatch '^sha256:[0-9a-f]{64}$') 'Artifact requires an actual GitHub SHA-256 digest.'
    $binding = Value $Artifact 'workflow_run'
    $bindingId = Value $binding 'id'
    $actualRunId = Value $Run 'id'
    $repositoryId = Value (Value $Run 'repository') 'id'
    $headRepositoryId = Value (Value $Run 'head_repository') 'id'
    $bindingRepositoryId = Value $binding 'repository_id'
    $bindingHeadId = Value $binding 'head_repository_id'
    foreach ($number in @($bindingId, $actualRunId, $repositoryId, $headRepositoryId, $bindingRepositoryId, $bindingHeadId))
    { Assert-CoreEvidenceInteger $number 'Artifact repository/run ID' }
    Require ($bindingId -eq $RunId -and $actualRunId -eq $RunId -and
        $bindingRepositoryId -eq $repositoryId -and $bindingHeadId -eq $headRepositoryId -and
        $repositoryId -eq $headRepositoryId -and
        (Value (Value $Run 'repository') 'full_name') -ceq $ExpectedRepository -and
        (Value (Value $Run 'head_repository') 'full_name') -ceq $ExpectedRepository -and
        (Value $binding 'head_sha') -ceq $ExpectedCommit -and (Value $Run 'head_sha') -ceq $ExpectedCommit) 'Artifact belongs to another repository, workflow run or source.'
    $createdText = Value $Artifact 'created_at'
    $updatedText = Value $Artifact 'updated_at'
    $expiresText = Value $Artifact 'expires_at'
    $created = Utc $createdText
    $updated = Utc $updatedText
    $expires = Utc $expiresText
    Require ($created -ge (Utc (Value $Run 'run_started_at')) -and
        $updated -ge $created -and $updated -le (Utc (Value $Run 'updated_at')) -and
        $expires -gt [DateTimeOffset]::UtcNow) 'Artifact lifecycle is outside the selected attempt or has expired.'
    return [pscustomobject][ordered]@{ artifactId=$ArtifactId; name=$ArtifactName; runId=$RunId
        repositoryId=$repositoryId; headRepositoryId=$headRepositoryId; sourceCommit=$ExpectedCommit
        url=$url; archiveDownloadUrl="$url/zip"; bytes=$size; sha256=$digest.Substring(7)
        createdUtc=$createdText; updatedUtc=$updatedText; expiresUtc=$expiresText }
}

function Get-CoreGithubArtifactArchive
{
    param([Parameter(Mandatory)][object] $Metadata, [Parameter(Mandatory)][string] $ArchivePath)
    # No extraction or qualification occurs here. Inventory every member's actual
    # decompressed bytes so later payload assembly can retain a delivery binding.
    $file = Get-Item -LiteralPath $ArchivePath -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    { throw 'Artifact archive must be a regular file.' }
    $ancestor = $file.Directory
    while ($null -ne $ancestor)
    {
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        { throw 'Artifact archive ancestry contains a symbolic link or junction.' }
        $ancestor = $ancestor.Parent
    }
    $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try
    {
        if ($stream.Length -ne $Metadata.bytes -or $stream.Length -gt 2GB)
        { throw 'Downloaded archive byte count differs from GitHub metadata.' }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        if ($hash -cne $Metadata.sha256) { throw 'Downloaded archive SHA-256 differs from GitHub metadata.' }
        $stream.Position = 0
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try
        {
            if ($zip.Entries.Count -eq 0 -or $zip.Entries.Count -gt 100000) { throw 'Artifact ZIP member count is empty or excessive.' }
            $declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $tree = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
            $spellings = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
            $members = [Collections.Generic.List[object]]::new()
            $total = 0L
            $buffer = [byte[]]::new(65536)
            foreach ($entry in $zip.Entries)
            {
                $name = $entry.FullName
                $directory = $name.EndsWith('/', [StringComparison]::Ordinal)
                $path = $(if ($directory) { $name.Substring(0, $name.Length - 1) } else { $name })
                if ([string]::IsNullOrEmpty($path) -or $path -cmatch '[\\:\x00-\x1f<>"|?*]' -or
                    $path -cne $path.Normalize([Text.NormalizationForm]::FormC) -or $path.Length -gt 1024)
                { throw 'Artifact ZIP contains a nonportable or unsafe member path.' }
                $segments = $path.Split('/')
                foreach ($segment in $segments)
                {
                    if ($segment -in @('', '.', '..') -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or
                        $segment -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)')
                    { throw 'Artifact ZIP contains a nonportable or unsafe member path.' }
                }
                if (-not $declared.Add($path)) { throw 'Artifact ZIP member paths are duplicated or case ambiguous.' }
                $attributes = [uint32]($entry.ExternalAttributes -band 0xffffffffL)
                $kind = ($attributes -shr 16) -band 0xf000
                if (($attributes -band 0x400) -ne 0 -or $kind -notin @(0, 0x8000, 0x4000) -or
                    ($directory -and $kind -eq 0x8000) -or (-not $directory -and $kind -eq 0x4000))
                { throw 'Artifact ZIP contains a link, special file or inconsistent member type.' }
                $parent = ''
                for ($i = 0; $i -lt $segments.Length - 1; $i++)
                {
                    $parent = $(if ($parent) { "$parent/$($segments[$i])" } else { $segments[$i] })
                    if ($spellings.ContainsKey($parent) -and $spellings[$parent] -cne $parent)
                    { throw 'Artifact ZIP directory paths are case ambiguous.' }
                    $spellings[$parent] = $parent
                    if ($tree.ContainsKey($parent) -and $tree[$parent] -ceq 'file')
                    { throw 'Artifact ZIP has a file/directory path collision.' }
                    $tree[$parent] = 'directory'
                }
                if ($spellings.ContainsKey($path) -and $spellings[$path] -cne $path)
                { throw 'Artifact ZIP directory paths are case ambiguous.' }
                $spellings[$path] = $path
                if ($tree.ContainsKey($path) -and ($tree[$path] -ceq 'file' -or -not $directory))
                { throw 'Artifact ZIP has a file/directory path collision.' }
                $tree[$path] = $(if ($directory) { 'directory' } else { 'file' })
                if ($directory)
                {
                    if ($entry.Length -ne 0) { throw 'Artifact ZIP directory contains payload bytes.' }
                    continue
                }
                $total += $entry.Length
                if ($total -gt 8GB) { throw 'Artifact ZIP decompressed bytes exceed the eight-GiB capture limit.' }
                $content = $entry.Open()
                $digest = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                try
                {
                    $actual = 0L
                    while (($count = $content.Read($buffer, 0, $buffer.Length)) -gt 0)
                    {
                        $actual += $count
                        if ($actual -gt $entry.Length) { throw 'Artifact ZIP member exceeds its declared byte count.' }
                        $digest.AppendData($buffer, 0, $count)
                    }
                    if ($actual -ne $entry.Length) { throw 'Artifact ZIP member is truncated.' }
                    $members.Add([pscustomobject][ordered]@{path=$path; bytes=$actual
                        sha256=[Convert]::ToHexString($digest.GetHashAndReset()).ToLowerInvariant()})
                }
                finally { $digest.Dispose(); $content.Dispose() }
            }
            if ($members.Count -eq 0) { throw 'Artifact ZIP contains no payload files.' }
        }
        finally { $zip.Dispose() }
        $stream.Position = 0
        if ($stream.Length -ne $Metadata.bytes -or
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $hash)
        { throw 'Artifact archive changed during inventory.' }
        return [pscustomobject][ordered]@{ path='artifact.zip'; bytes=$stream.Length; sha256=$hash
            uncompressedBytes=$total; members=@($members | Sort-Object path) }
    }
    finally { $stream.Dispose() }
}

Export-ModuleMember -Function ConvertTo-CoreGithubArtifactMetadata, Get-CoreGithubArtifactArchive
