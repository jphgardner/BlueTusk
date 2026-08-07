[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [string] $InventoryPath,
    [switch] $SkipSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot))
{
    $RepositoryRoot = Split-Path $PSScriptRoot -Parent
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if ([string]::IsNullOrWhiteSpace($InventoryPath))
{
    $InventoryPath = Join-Path $PSScriptRoot 'test-credential-inventory.json'
}
$InventoryPath = (Resolve-Path -LiteralPath $InventoryPath).Path

$inventory = Get-Content -LiteralPath $InventoryPath -Raw | ConvertFrom-Json
if ([int]$inventory.schemaVersion -ne 1)
{
    throw "Expected test-credential inventory schema 1; found '$($inventory.schemaVersion)'."
}

$credentials = @($inventory.credentials)
if ($credentials.Count -lt 1)
{
    throw 'The test-credential inventory is empty.'
}
$credentialIds = @($credentials | ForEach-Object { [string]$_.id })
$credentialHashes = @($credentials | ForEach-Object { ([string]$_.sha256).ToLowerInvariant() })
if (@($credentialIds | Group-Object | Where-Object Count -ne 1).Count -ne 0 -or
    @($credentialHashes | Group-Object | Where-Object Count -ne 1).Count -ne 0)
{
    throw 'Test-credential IDs and fingerprints must be unique.'
}

$ruleIndex = @{}
foreach ($credential in $credentials)
{
    $credentialId = [string]$credential.id
    $fingerprint = ([string]$credential.sha256).ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($credentialId) -or
        $fingerprint -notmatch '^[0-9a-f]{64}$' -or
        [string]$credential.classification -ne 'disposable-local-test-credential' -or
        $credential.productionUseForbidden -ne $true -or
        $credential.rotationRequired -ne $false -or
        [string]$credential.externalScannerDisposition -ne
            'false-positive-test-credential' -or
        [string]::IsNullOrWhiteSpace([string]$credential.rationale))
    {
        throw "Test credential '$credentialId' has an incomplete or unsafe classification."
    }

    $occurrences = @($credential.occurrences)
    if ($occurrences.Count -lt 1)
    {
        throw "Test credential '$credentialId' has no allowed occurrences."
    }
    foreach ($occurrence in $occurrences)
    {
        $path = ([string]$occurrence.path).Replace('\', '/')
        $key = "$credentialId|$path"
        if ($ruleIndex.ContainsKey($key) -or
            [IO.Path]::IsPathRooted($path) -or
            $path.Contains('../', [StringComparison]::Ordinal) -or
            [int]$occurrence.expectedCount -lt 1 -or
            [string]::IsNullOrWhiteSpace([string]$occurrence.requiredLinePattern))
        {
            throw "Test credential '$credentialId' has an invalid or duplicate rule for '$path'."
        }
        try
        {
            $null = [regex]::new(
                [string]$occurrence.requiredLinePattern,
                [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        }
        catch
        {
            throw "Test credential '$credentialId' has an invalid line pattern for '$path'."
        }
        $ruleIndex[$key] = @{
            CredentialId = $credentialId
            Fingerprint = $fingerprint
            Path = $path
            ExpectedCount = [int]$occurrence.expectedCount
            RequiredLinePattern = [string]$occurrence.requiredLinePattern
            ActualCount = 0
        }
    }
}

$extensions = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @($inventory.scannedExtensions | ForEach-Object { [string]$_ }))
{
    if ($extension -notmatch '^\.[a-z0-9]+$' -or -not $extensions.Add($extension))
    {
        throw "Invalid or duplicate scanned extension '$extension'."
    }
}

$scopeFiles = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($scopeRoot in @($inventory.scopeRoots | ForEach-Object { [string]$_ }))
{
    if ([IO.Path]::IsPathRooted($scopeRoot) -or
        $scopeRoot.Contains('..', [StringComparison]::Ordinal))
    {
        throw "Credential scan root '$scopeRoot' is unsafe."
    }
    $resolvedScope = (Resolve-Path -LiteralPath (
        Join-Path $RepositoryRoot $scopeRoot)).Path
    foreach ($file in Get-ChildItem -LiteralPath $resolvedScope -Recurse -File)
    {
        if ($extensions.Contains($file.Extension))
        {
            $scopeFiles.Add($file)
        }
    }
}

$patterns = @(
    [regex]::new(
        '(?i)(?<!Unencrypted )\bpassword\s*=\s*(?<value>[^;\s"''\\]+)',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant),
    [regex]::new(
        '(?i)\bPOSTGRES_PASSWORD\s*:\s*(?<value>[^\s#]+)',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant),
    [regex]::new(
        '(?i)\baddprinc\s+-pw\s+(?<value>[^\s"'']+)',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant),
    [regex]::new(
        '(?i)\bkdb5_util\s+create\b.*\s-P\s+(?<value>[^\s"'']+)',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant),
    [regex]::new(
        '(?i)printf\s+''%s\\n''\s+''(?<value>[^'']+)''\s+\|\s+kinit\b',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
)

$detections = 0
$failures = [Collections.Generic.List[string]]::new()
foreach ($file in $scopeFiles)
{
    $relativePath = [IO.Path]::GetRelativePath(
        $RepositoryRoot,
        $file.FullName).Replace('\', '/')
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($file.FullName))
    {
        $lineNumber++
        foreach ($pattern in $patterns)
        {
            foreach ($match in $pattern.Matches($line))
            {
                $value = [string]$match.Groups['value'].Value
                if ([string]::IsNullOrWhiteSpace($value) -or
                    $value.StartsWith('$', [StringComparison]::Ordinal))
                {
                    continue
                }
                $detections++
                $valueBytes = [Text.Encoding]::UTF8.GetBytes($value)
                try
                {
                    $fingerprint = [Convert]::ToHexString(
                        [Security.Cryptography.SHA256]::HashData($valueBytes)
                    ).ToLowerInvariant()
                }
                finally
                {
                    [Security.Cryptography.CryptographicOperations]::ZeroMemory($valueBytes)
                }

                $credential = @($credentials | Where-Object {
                    [string]::Equals(
                        [string]$_.sha256,
                        $fingerprint,
                        [StringComparison]::OrdinalIgnoreCase)
                })
                if ($credential.Count -ne 1)
                {
                    $failures.Add(
                        "$relativePath`:$lineNumber has an unregistered literal " +
                        "credential fingerprint '$($fingerprint.Substring(0, 12))…'.")
                    continue
                }

                $credentialId = [string]$credential[0].id
                $ruleKey = "$credentialId|$relativePath"
                if (-not $ruleIndex.ContainsKey($ruleKey))
                {
                    $failures.Add(
                        "$relativePath`:$lineNumber uses registered test credential " +
                        "'$credentialId' outside its allowed files.")
                    continue
                }
                $rule = $ruleIndex[$ruleKey]
                if ($line -notmatch $rule.RequiredLinePattern)
                {
                    $failures.Add(
                        "$relativePath`:$lineNumber violates the local-only context for " +
                        "test credential '$credentialId'.")
                    continue
                }
                $rule.ActualCount++
            }
        }
    }
}

foreach ($rule in $ruleIndex.Values)
{
    if ([int]$rule.ActualCount -ne [int]$rule.ExpectedCount)
    {
        $failures.Add(
            "Test credential '$($rule.CredentialId)' has $($rule.ActualCount) accepted " +
            "occurrence(s) in '$($rule.Path)'; expected $($rule.ExpectedCount).")
    }
}

foreach ($forbiddenPath in @($inventory.forbiddenWorkflowPaths | ForEach-Object {
            [string]$_
        }))
{
    if (@($ruleIndex.Values | Where-Object {
                [string]$_.Path -eq $forbiddenPath
            }).Count -ne 0)
    {
        $failures.Add(
            "Candidate or publication workflow '$forbiddenPath' allows a literal credential.")
    }
}

if ($failures.Count -ne 0)
{
    throw (
        "Intentional test-credential boundary failed:`n - " +
        ($failures -join "`n - "))
}

if (-not $SkipSelfTest)
{
    & (Join-Path $PSScriptRoot 'test-test-credential-inventory-verifier.ps1')
}

Write-Output (
    "Verified $detections intentional literal test-credential occurrence(s) across " +
    "$($scopeFiles.Count) automation/infrastructure file(s); production and protected " +
    "release workflows remain literal-credential free.")
