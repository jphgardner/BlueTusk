[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Target,

    [Parameter(Mandatory)]
    [string] $FindingDirectory,

    [string] $ArchiveDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$findingRoot = (Resolve-Path -LiteralPath $FindingDirectory).Path
if ([string]::IsNullOrWhiteSpace($ArchiveDirectory)) {
    $ArchiveDirectory = Join-Path $repositoryRoot "artifacts/fuzz/archive/$Target"
}

New-Item -ItemType Directory -Path $ArchiveDirectory -Force | Out-Null
$findings = Get-ChildItem -LiteralPath $findingRoot -Recurse -File |
    Where-Object {
        $_.Name -ne 'README.txt' -and
        ($_.Directory.Name -eq 'crashes' -or $_.Directory.Name -eq 'hangs')
    }

$commit = (git -C $repositoryRoot rev-parse HEAD).Trim()
$records = foreach ($finding in $findings) {
    $bytes = [System.IO.File]::ReadAllBytes($finding.FullName)
    $hash = Convert.ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
    $category = $finding.Directory.Name
    $encodedName = "$category-$hash.b64"
    [System.IO.File]::WriteAllText(
        (Join-Path $ArchiveDirectory $encodedName),
        [Convert]::ToBase64String($bytes) + [Environment]::NewLine)
    [ordered]@{
        target = $Target
        category = $category
        sha256 = $hash
        bytes = $bytes.Length
        encoded_case = $encodedName
        source_name = $finding.Name
    }
}

$metadata = [ordered]@{
    schema_version = 1
    target = $Target
    source_commit = $commit
    archived_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
    runtime = (dotnet --version).Trim()
    findings = @($records)
}
$metadata | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $ArchiveDirectory 'manifest.json') -Encoding utf8

Write-Output "Archived $($findings.Count) fuzz finding(s) to '$ArchiveDirectory'."
