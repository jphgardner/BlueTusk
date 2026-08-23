[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$gatePath = Join-Path $PSScriptRoot 'run-v1-performance-gate.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $gatePath,
    [ref] $tokens,
    [ref] $parseErrors)

if ($parseErrors.Count -ne 0)
{
    throw "V1 performance gate has PowerShell parse errors: $($parseErrors -join '; ')"
}

$teeCommands = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Tee-Object'
}, $true))

if ($teeCommands.Count -ne 3)
{
    throw "Expected three Tee-Object calls in the V1 performance gate; found $($teeCommands.Count)."
}

$appendCommands = @($teeCommands | Where-Object {
    @($_.CommandElements | Where-Object {
        $_ -is [Management.Automation.Language.CommandParameterAst]
    } | ForEach-Object ParameterName) -contains 'Append'
})

if ($appendCommands.Count -ne 2)
{
    throw "Expected two append-mode Tee-Object calls; found $($appendCommands.Count)."
}

foreach ($appendCommand in $appendCommands)
{
    $appendParameters = @($appendCommand.CommandElements | Where-Object {
        $_ -is [Management.Automation.Language.CommandParameterAst]
    } | ForEach-Object ParameterName)

    if ($appendParameters -notcontains 'FilePath' -or
        $appendParameters -contains 'LiteralPath')
    {
        throw (
            'Append-mode Tee-Object must use the compatible FilePath parameter set; ' +
            "found: $($appendParameters -join ', ').")
    }
}

$temporaryPath = Join-Path (
    [IO.Path]::GetTempPath()
) "bluetusk-performance-gate-contract-$([Guid]::NewGuid().ToString('N')).log"

try
{
    'paired' | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
    'multiplexing-paired' | Tee-Object -FilePath $temporaryPath -Append | Out-Null
    'benchmark' | Tee-Object -FilePath $temporaryPath -Append | Out-Null
    $content = Get-Content -LiteralPath $temporaryPath
    if (@($content).Count -ne 3 -or
        $content[0] -ne 'paired' -or
        $content[1] -ne 'multiplexing-paired' -or
        $content[2] -ne 'benchmark')
    {
        throw 'Append-mode performance logging did not preserve both phases.'
    }
}
finally
{
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
}

Write-Output (
    'V1 performance gate contract self-test passed: both paired phases and the benchmark phase ' +
    'use valid, append-safe PowerShell parameter sets.')
