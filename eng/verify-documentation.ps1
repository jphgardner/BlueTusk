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

if ($failures.Count -gt 0)
{
    $failures | ForEach-Object { Write-Error $_ }
    throw "Documentation validation found $($failures.Count) broken local link(s)."
}

Write-Output "Verified $checkedLinks local links across $($trackedMarkdown.Count) tracked Markdown files."
