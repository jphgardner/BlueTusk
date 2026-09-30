[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TrxPath,
    [ValidateSet('Core', 'ContinuousGraphPreview')]
    [string] $ReleaseTrack = 'Core'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$plan = & (Join-Path $PSScriptRoot 'get-application-postgresql-test-plan.ps1') -ReleaseTrack $ReleaseTrack
$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$settings.MaxCharactersInDocument = 10MB
$reader = [Xml.XmlReader]::Create((Resolve-Path -LiteralPath $TrxPath).Path, $settings)
$document = [Xml.XmlDocument]::new()
$document.XmlResolver = $null
try { $document.Load($reader) } finally { $reader.Dispose() }
$namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
$namespaces.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$results = @($document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $namespaces))
$summaries = @($document.SelectNodes('/t:TestRun/t:ResultSummary', $namespaces))
$counters = @($document.SelectNodes('/t:TestRun/t:ResultSummary/t:Counters', $namespaces))
$expected = @($plan.expectedTests)
$observed = @($results | ForEach-Object { [string]$_.testName })
if ($results.Count -ne $expected.Count -or
    @($observed | Select-Object -Unique).Count -ne $expected.Count -or
    @((Compare-Object $expected $observed -CaseSensitive)).Count -ne 0 -or
    @($results | Where-Object { $_.outcome -cne 'Passed' }).Count -ne 0 -or
    $summaries.Count -ne 1 -or $summaries[0].outcome -cne 'Completed' -or
    $counters.Count -ne 1)
{
    throw "The $ReleaseTrack application run must execute exactly its named integration tests without skips, substitutions or failures."
}
foreach ($name in @('total', 'executed', 'passed'))
{
    if ([string]$counters[0].GetAttribute($name) -cne [string]$expected.Count)
    {
        throw "Application TRX counter '$name' does not match the required test count."
    }
}
foreach ($name in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'inProgress', 'pending'))
{
    if ($counters[0].HasAttribute($name) -and $counters[0].GetAttribute($name) -cne '0')
    {
        throw "Application TRX has a nonzero '$name' count."
    }
}
if ($document.SelectNodes('/t:TestRun/t:ResultSummary/t:RunInfos/t:RunInfo[@outcome != "Completed"]', $namespaces).Count -ne 0)
{
    throw 'The application test host recorded a run-level error.'
}
Write-Output "Verified $($expected.Count) named $ReleaseTrack PostgreSQL application test(s), zero skips and failures."
