[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),

    [string] $ContractPath = (Join-Path $PSScriptRoot 'telemetry-contract.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$ContractPath = (Resolve-Path -LiteralPath $ContractPath).Path
$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
if ($contract.schemaVersion -ne 1)
{
    throw "Expected telemetry contract schema 1; found '$($contract.schemaVersion)'."
}

$allowedTypes = @(
    'Counter',
    'Histogram',
    'UpDownCounter',
    'ObservableCounter',
    'ObservableGauge',
    'ObservableUpDownCounter')
$cardinalityClasses = @(
    $contract.cardinalityClasses.PSObject.Properties.Name)
$declared = @{}
$meterNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
$allSource = [Text.StringBuilder]::new()

foreach ($sourceFile in Get-ChildItem -LiteralPath (
        Join-Path $RepositoryRoot 'src') -Recurse -Filter '*.cs')
{
    [void]$allSource.AppendLine(
        (Get-Content -LiteralPath $sourceFile.FullName -Raw))
}

foreach ($meter in @($contract.meters))
{
    $meterName = [string]$meter.name
    if ([string]::IsNullOrWhiteSpace($meterName) -or
        -not $meterNames.Add($meterName))
    {
        throw "Telemetry meter names must be non-empty and unique; found '$meterName'."
    }

    $relativeSource = ([string]$meter.sourceFile).Replace('\', '/')
    $sourcePath = Join-Path $RepositoryRoot $relativeSource
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
    {
        throw "Telemetry source '$relativeSource' does not exist."
    }

    $meterSource = Get-Content -LiteralPath $sourcePath -Raw
    if (-not $meterSource.Contains(
            "`"$meterName`"",
            [StringComparison]::Ordinal))
    {
        throw "Telemetry source '$relativeSource' does not declare meter '$meterName'."
    }

    foreach ($instrument in @($meter.instruments))
    {
        $name = [string]$instrument.name
        if ($name -notmatch '^[a-z][a-z0-9_.]*$')
        {
            throw "Telemetry instrument '$name' is not a stable lowercase dotted name."
        }

        if ($declared.ContainsKey($name))
        {
            throw "Telemetry instrument '$name' is declared more than once."
        }

        $type = [string]$instrument.type
        if ($type -notin $allowedTypes)
        {
            throw "Telemetry instrument '$name' uses unsupported type '$type'."
        }

        $unit = [string]$instrument.unit
        if ([string]::IsNullOrWhiteSpace($unit))
        {
            throw "Telemetry instrument '$name' must declare a UCUM or annotation unit."
        }

        $cardinality = [string]$instrument.cardinality
        if ($cardinality -notin $cardinalityClasses)
        {
            throw (
                "Telemetry instrument '$name' uses unknown cardinality class " +
                "'$cardinality'.")
        }

        $tags = @($instrument.tags | ForEach-Object { [string]$_ })
        if ($cardinality -eq 'none' -and $tags.Count -ne 0)
        {
            throw "Telemetry instrument '$name' is cardinality 'none' but declares tags."
        }

        foreach ($tag in $tags)
        {
            if ($tag -notmatch '^[a-z][a-z0-9_.]*$')
            {
                throw "Telemetry tag '$tag' on '$name' is not a stable dotted name."
            }

            if (-not $allSource.ToString().Contains(
                    "`"$tag`"",
                    [StringComparison]::Ordinal))
            {
                throw "Telemetry tag '$tag' on '$name' is not emitted by source code."
            }
        }

        $declared[$name] = [pscustomobject]@{
            Name = $name
            Type = $type
            SourceFile = $relativeSource
        }
    }
}

$pattern = [regex]::new(
    'Create(?<Type>Counter|Histogram|UpDownCounter|ObservableCounter|ObservableGauge|ObservableUpDownCounter)(?:<[^>]+>)?\s*\(\s*"(?<Name>[^"]+)"',
    [Text.RegularExpressions.RegexOptions]::Singleline)
$discovered = @{}
foreach ($sourceFile in Get-ChildItem -LiteralPath (
        Join-Path $RepositoryRoot 'src') -Recurse -Filter '*.cs')
{
    $relativeSource = $sourceFile.FullName.Substring(
        $RepositoryRoot.Length + 1).Replace('\', '/')
    $source = Get-Content -LiteralPath $sourceFile.FullName -Raw
    foreach ($match in $pattern.Matches($source))
    {
        $name = $match.Groups['Name'].Value
        if ($discovered.ContainsKey($name))
        {
            throw "Runtime telemetry instrument '$name' is created more than once."
        }

        $discovered[$name] = [pscustomobject]@{
            Name = $name
            Type = $match.Groups['Type'].Value
            SourceFile = $relativeSource
        }
    }
}

$missingDeclarations = @(
    $discovered.Keys |
        Where-Object { -not $declared.ContainsKey($_) } |
        Sort-Object)
if ($missingDeclarations.Count -ne 0)
{
    throw (
        "Runtime telemetry is missing contract entries: " +
        "$($missingDeclarations -join ', ').")
}

$missingRuntime = @(
    $declared.Keys |
        Where-Object { -not $discovered.ContainsKey($_) } |
        Sort-Object)
if ($missingRuntime.Count -ne 0)
{
    throw (
        "Telemetry contract entries have no runtime instrument: " +
        "$($missingRuntime -join ', ').")
}

foreach ($name in $declared.Keys)
{
    $expected = $declared[$name]
    $actual = $discovered[$name]
    if ($expected.Type -ne $actual.Type)
    {
        throw (
            "Telemetry instrument '$name' is $($actual.Type) in source but " +
            "$($expected.Type) in the contract.")
    }

    if ($expected.SourceFile -ne $actual.SourceFile)
    {
        throw (
            "Telemetry instrument '$name' is implemented by " +
            "'$($actual.SourceFile)', not '$($expected.SourceFile)'.")
    }
}

$minimum = [int]$contract.minimumInstrumentCount
if ($declared.Count -lt $minimum)
{
    throw (
        "Telemetry contract exposes $($declared.Count) instruments; " +
        "the production minimum is $minimum.")
}

Write-Host (
    "Verified $($declared.Count) runtime instruments across " +
    "$($meterNames.Count) product meters with explicit units, tags, and " +
    "cardinality policy.")
