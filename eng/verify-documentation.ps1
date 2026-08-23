[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
$trackedMarkdown = @(& git -C $RepositoryRoot ls-files -- '*.md')
if ($LASTEXITCODE -ne 0)
{
    throw 'Unable to enumerate tracked Markdown files.'
}
$documentationRoot = Join-Path $RepositoryRoot 'docs'
if (Test-Path -LiteralPath $documentationRoot -PathType Container)
{
    $workspaceDocumentation = @(
        Get-ChildItem -LiteralPath $documentationRoot -Filter '*.md' -File -Recurse |
            ForEach-Object {
                [IO.Path]::GetRelativePath($RepositoryRoot, $_.FullName).Replace('\', '/')
            }
    )
    $trackedMarkdown = @(
        $trackedMarkdown + $workspaceDocumentation |
            Sort-Object -Unique
    )
}

$inlineLinkPattern = [regex]'\[[^\]]*\]\((?<target><[^>]+>|[^)\s]+)(?:\s+(?:"[^"]*"|''[^'']*''))?\)'
$referenceLinkPattern = [regex]'(?m)^\s*\[[^\]]+\]:\s*(?<target><[^>]+>|\S+)'
$failures = [System.Collections.Generic.List[string]]::new()
$checkedLinks = 0

foreach ($trackedPath in $trackedMarkdown)
{
    $sourcePath = Join-Path $RepositoryRoot $trackedPath
    $content = Get-Content $sourcePath -Raw
    $matches = @($inlineLinkPattern.Matches($content)) + @($referenceLinkPattern.Matches($content))

    foreach ($match in $matches)
    {
        $target = $match.Groups['target'].Value.Trim()
        if ($target.StartsWith('<') -and $target.EndsWith('>'))
        {
            $target = $target.Substring(1, $target.Length - 2)
        }

        if ([string]::IsNullOrWhiteSpace($target) -or
            $target.StartsWith('#') -or
            $target -match '^[a-z][a-z0-9+.-]*:')
        {
            continue
        }

        $pathPart = ($target -split '[?#]', 2)[0]
        if ([string]::IsNullOrWhiteSpace($pathPart))
        {
            continue
        }

        try
        {
            $pathPart = [Uri]::UnescapeDataString($pathPart.Replace('\(', '(').Replace('\)', ')'))
        }
        catch
        {
            $line = ($content.Substring(0, $match.Index) -split "`n").Count
            $failures.Add("${trackedPath}:${line}: invalid escaped link target '$target'")
            continue
        }

        $candidate = if ($pathPart.StartsWith('/'))
        {
            Join-Path $RepositoryRoot $pathPart.TrimStart('/')
        }
        else
        {
            Join-Path (Split-Path $sourcePath -Parent) $pathPart
        }

        $checkedLinks++
        if (-not (Test-Path -LiteralPath $candidate))
        {
            $line = ($content.Substring(0, $match.Index) -split "`n").Count
            $failures.Add("${trackedPath}:${line}: missing local link target '$target'")
        }
    }
}

$readinessConfigurationPath = Join-Path $RepositoryRoot 'eng/v1-production-readiness.json'
$websiteProductionGuidePath = Join-Path $RepositoryRoot 'docs/operations/website-production.md'
$readinessConfiguration = Get-Content -LiteralPath $readinessConfigurationPath -Raw |
    ConvertFrom-Json
$requiredWorkflowCount = @($readinessConfiguration.requiredWorkflows).Count
$websiteProductionGuide = Get-Content -LiteralPath $websiteProductionGuidePath -Raw
$workflowCountPattern = (
    'one of the\s+' +
    [regex]::Escape([string]$requiredWorkflowCount) +
    '\s+exact-SHA workflow records')
if ($websiteProductionGuide -notmatch $workflowCountPattern)
{
    $failures.Add(
        'docs/operations/website-production.md does not match the executable ' +
        "$requiredWorkflowCount-workflow V1 evidence contract.")
}

$credentialInventoryPath = Join-Path $RepositoryRoot 'eng/test-credential-inventory.json'
$hardeningProgrammePath = Join-Path $RepositoryRoot 'docs/hardening-programme.md'
$credentialInventory = Get-Content -LiteralPath $credentialInventoryPath -Raw |
    ConvertFrom-Json
[int] $credentialOccurrenceCount = @(
    $credentialInventory.credentials |
        ForEach-Object { $_.occurrences } |
        ForEach-Object { [int]$_.expectedCount } |
        Measure-Object -Sum
).Sum
$hardeningProgramme = Get-Content -LiteralPath $hardeningProgrammePath -Raw
$credentialCountPattern = (
    'fail-closed\s+' +
    [regex]::Escape([string]$credentialOccurrenceCount) +
    '-occurrence intentional test-credential inventory')
if ($hardeningProgramme -notmatch $credentialCountPattern)
{
    $failures.Add(
        'docs/hardening-programme.md does not match the executable ' +
        "$credentialOccurrenceCount-occurrence test-credential inventory.")
}

if ($failures.Count -gt 0)
{
    $failures | ForEach-Object { Write-Error $_ }
    throw "Documentation validation found $($failures.Count) failure(s)."
}

Write-Output (
    "Verified $checkedLinks local links across $($trackedMarkdown.Count) Markdown files; " +
    "release documentation matches $requiredWorkflowCount exact-SHA workflows and " +
    "$credentialOccurrenceCount intentional test-credential occurrences.")
