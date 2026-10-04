[CmdletBinding()]
param()

# Self-test for the 1.1.0 independent-pilot rule in verify-v1.1-release-contract.ps1.
# Mutations are applied to the raw contract text so recorded timestamps keep their
# exact form. Every inconsistent pilot/rehearsal state must fail closed.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-v1.1-release-contract.ps1'
$contractText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'v1.1-release-contract.json') -Raw
$root = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-v11-contract-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null

function Invoke-ContractCase([string] $Name, [string] $Text)
{
    $path = Join-Path $root "$Name.json"
    [IO.File]::WriteAllText($path, $Text, [Text.UTF8Encoding]::new($false))
    $failure = $null
    try { & $verifier -ContractPath $path | Out-Null }
    catch { $failure = $_.Exception.Message }
    return $failure
}

function Edit-Contract([string] $Old, [string] $New)
{
    $normalized = $contractText.Replace("`r`n", "`n")
    $index = $normalized.IndexOf($Old.Replace("`r`n", "`n"), [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Contract fixture anchor not found: $Old" }
    return $normalized.Remove($index, $Old.Replace("`r`n", "`n").Length).Insert($index, $New.Replace("`r`n", "`n"))
}

$waiverPattern = '(?s)  "waivedReleaseGates": \[.*?\n  \],\n'
try
{
    $positive = Invoke-ContractCase 'positive' $contractText
    if ($null -ne $positive) { throw "The unmodified 1.1 release contract was rejected: $positive" }

    $withoutWaiver = [regex]::Replace($contractText.Replace("`r`n", "`n"), $waiverPattern, '')
    if ($withoutWaiver -eq $contractText.Replace("`r`n", "`n")) { throw 'The waiver fixture anchor was not found.' }
    $reinstated = $withoutWaiver.Replace('"rollbackRehearsal": true,', "`"rollbackRehearsal`": true,`n    `"independentPilots`": 2,")

    $cases = [ordered]@{
        'waiver-removed' = $withoutWaiver
        'pilots-and-waiver' = (Edit-Contract '"rollbackRehearsal": true,' "`"rollbackRehearsal`": true,`n    `"independentPilots`": 2,")
        'one-pilot-no-waiver' = $withoutWaiver.Replace('"rollbackRehearsal": true,', "`"rollbackRehearsal`": true,`n    `"independentPilots`": 1,")
        'pilots-reinstated-but-core-approvals-still-waive' = $reinstated
        'zero-pilots-key' = (Edit-Contract '"rollbackRehearsal": true,' "`"rollbackRehearsal`": true,`n    `"independentPilots`": 0,")
        'backup-restore-disabled' = (Edit-Contract '"backupRestoreRehearsal": true,' '"backupRestoreRehearsal": false,')
        'rollback-disabled' = (Edit-Contract '"rollbackRehearsal": true,' '"rollbackRehearsal": false,')
        'rollback-not-retained' = (Edit-Contract "        `"backupRestoreRehearsal`",`n        `"rollbackRehearsal`"`n" "        `"backupRestoreRehearsal`"`n")
        'wrong-delegation-time' = (Edit-Contract '"delegatedAt": "2026-10-04T00:43:03+01:00"' '"delegatedAt": "2026-10-05T00:43:03+01:00"')
        'wrong-delegation-quote' = (Edit-Contract '"delegationQuote": "do what needs to be done"' '"delegationQuote": "drop every gate"')
        'waiver-names-rehearsal' = (Edit-Contract "        `"application-pilot-b`"`n      ],`n      `"scope`"" "        `"application-pilot-b`",`n        `"rollback-rehearsal`"`n      ],`n      `"scope`"")
        'waiver-wrong-scope' = (Edit-Contract '"scope": "1.1.0"' '"scope": "1.2.0"')
    }
    $rejected = 0
    foreach ($case in $cases.GetEnumerator())
    {
        $failure = Invoke-ContractCase $case.Key $case.Value
        if ($null -eq $failure) { throw "Inconsistent 1.1 contract case '$($case.Key)' was accepted." }
        $rejected++
    }
    Write-Output (
        "1.1 release-contract verifier self-test passed: the recorded independent-pilot waiver is accepted and " +
        "$rejected inconsistent pilot, waiver and rehearsal states were rejected.")
}
finally
{
    $absolute = [IO.Path]::GetFullPath($root)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($absolute).StartsWith('bluetusk-v11-contract-', [StringComparison]::Ordinal))
    {
        throw 'Refusing to remove an unexpected contract test directory.'
    }
    [IO.Directory]::Delete($absolute, $true)
}
