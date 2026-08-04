[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$temporaryBase = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
$temporaryRoot = Join-Path $temporaryBase (
    "bluetusk-test-credential-verifier-$([Guid]::NewGuid().ToString('N'))")
$workflowRoot = Join-Path $temporaryRoot '.github/workflows'
$composeRoot = Join-Path $temporaryRoot 'eng/compose'
$null = New-Item -ItemType Directory -Force -Path $workflowRoot
$null = New-Item -ItemType Directory -Force -Path $composeRoot

try
{
    $credential = 'fixture-password'
    $credentialBytes = [Text.Encoding]::UTF8.GetBytes($credential)
    try
    {
        $fingerprint = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($credentialBytes)
        ).ToLowerInvariant()
    }
    finally
    {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($credentialBytes)
    }
    $workflowPath = Join-Path $workflowRoot 'test.yml'
    $inventoryPath = Join-Path $temporaryRoot 'inventory.json'
    $inventory = [ordered]@{
        schemaVersion = 1
        scopeRoots = @('.github/workflows', 'eng/compose')
        scannedExtensions = @('.yml')
        credentials = @(
            [ordered]@{
                id = 'fixture'
                sha256 = $fingerprint
                classification = 'disposable-local-test-credential'
                productionUseForbidden = $true
                rotationRequired = $false
                externalScannerDisposition = 'false-positive-test-credential'
                rationale = 'Verifier fixture.'
                occurrences = @(
                    [ordered]@{
                        path = '.github/workflows/test.yml'
                        expectedCount = 1
                        requiredLinePattern =
                            'Host=localhost;.*Database=bluetusk_tests'
                    }
                )
            }
        )
        forbiddenWorkflowPaths = @(
            '.github/workflows/release-product-family.yml',
            '.github/workflows/v1-candidate-readiness.yml'
        )
    }
    $inventory | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $inventoryPath -Encoding utf8NoBOM

    "connection: Host=localhost;Password=$credential;Database=bluetusk_tests" |
        Set-Content -LiteralPath $workflowPath -Encoding utf8NoBOM
    & (Join-Path $PSScriptRoot 'verify-test-credential-inventory.ps1') `
        -RepositoryRoot $temporaryRoot `
        -InventoryPath $inventoryPath `
        -SkipSelfTest | Out-Null

    "connection: Host=db.example;Password=$credential;Database=bluetusk_tests" |
        Set-Content -LiteralPath $workflowPath -Encoding utf8NoBOM
    $externalRejected = $false
    try
    {
        & (Join-Path $PSScriptRoot 'verify-test-credential-inventory.ps1') `
            -RepositoryRoot $temporaryRoot `
            -InventoryPath $inventoryPath `
            -SkipSelfTest | Out-Null
    }
    catch
    {
        if ($_.Exception.Message -notmatch 'local-only context')
        {
            throw
        }
        $externalRejected = $true
    }
    if (-not $externalRejected)
    {
        throw 'The credential verifier accepted an external-host literal.'
    }

    'connection: Host=localhost;Password=unregistered;Database=bluetusk_tests' |
        Set-Content -LiteralPath $workflowPath -Encoding utf8NoBOM
    $unknownRejected = $false
    try
    {
        & (Join-Path $PSScriptRoot 'verify-test-credential-inventory.ps1') `
            -RepositoryRoot $temporaryRoot `
            -InventoryPath $inventoryPath `
            -SkipSelfTest | Out-Null
    }
    catch
    {
        if ($_.Exception.Message -notmatch 'unregistered literal')
        {
            throw
        }
        $unknownRejected = $true
    }
    if (-not $unknownRejected)
    {
        throw 'The credential verifier accepted an unknown literal.'
    }

    Write-Output (
        'Test-credential verifier self-test passed: scoped fixture accepted; ' +
        'external-host and unregistered literals rejected.')
}
finally
{
    $resolvedRoot = (Resolve-Path -LiteralPath $temporaryRoot).Path
    $temporaryPrefix = $temporaryBase.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith(
            $temporaryPrefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $resolvedRoot).StartsWith(
            'bluetusk-test-credential-verifier-',
            [StringComparison]::Ordinal))
    {
        throw "Refusing to remove unexpected verifier directory '$resolvedRoot'."
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
