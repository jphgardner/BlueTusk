[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SampleDll,
    [Parameter(Mandatory)][string] $TestDll,
    [Parameter(Mandatory)][string] $PackageVersion,
    [Parameter(Mandatory)][string] $ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING)) {
    throw 'Set BLUETUSK_TEST_CONNECTION_STRING to the local logical-replication test fixture.'
}

$sample = Get-Item -LiteralPath $SampleDll
$test = Get-Item -LiteralPath $TestDll
if ($sample.Name -cne 'BlueTusk.Projections.Orders.Live.dll' -or
    $test.Name -cne 'BlueTusk.Projections.Tests.dll') {
    throw 'Supply the built Orders application and Projections test DLLs.'
}

$depsFile = [IO.Path]::ChangeExtension($sample.FullName, '.deps.json')
$deps = Get-Content -LiteralPath $depsFile -Raw | ConvertFrom-Json -AsHashtable
$blueTuskPackages = @(
    foreach ($library in $deps.libraries.GetEnumerator()) {
        $name, $version = $library.Key -split '/', 2
        if (($name -ine 'BlueTusk' -and $name -notlike 'BlueTusk.*') -or
            $name -ceq $sample.BaseName) { continue }
        if ($library.Value.type -cne 'package' -or $version -cne $PackageVersion) {
            throw "Expected an exact candidate package for $name."
        }
        $name
    }
)
foreach ($required in 'BlueTusk.Projections.Live', 'BlueTusk.Live.DependencyInjection',
    'BlueTusk.Live.ServerSentEvents') {
    if ($required -notin $blueTuskPackages) {
        throw "The application runtime graph is missing $required."
    }
}

$resultPath = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $resultPath) {
    throw 'Choose a new results directory to preserve the previous runs.'
}
New-Item -ItemType Directory -Path $resultPath | Out-Null
$originalSample = $env:BLUETUSK_ORDERS_SAMPLE_DLL
$env:BLUETUSK_ORDERS_SAMPLE_DLL = $sample.FullName
$runs = [Collections.Generic.List[object]]::new()
try {
    foreach ($number in 1..3) {
        $trxName = "warm-$number.trx"
        $log = Join-Path $resultPath "warm-$number.log"
        $timer = [Diagnostics.Stopwatch]::StartNew()
        & dotnet vstest $test.FullName '/TestCaseFilter:FullyQualifiedName~OrdersLiveSampleTests' `
            "/Logger:trx;LogFileName=$trxName" "/ResultsDirectory:$resultPath" 2>&1 |
            Tee-Object -FilePath $log | Out-Host
        $exitCode = $LASTEXITCODE
        $timer.Stop()
        $trxPath = Join-Path $resultPath $trxName
        $results = @()
        if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
            [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
            $results = @($trx.TestRun.Results.UnitTestResult | Where-Object { $null -ne $_ })
        }
        $passed = $exitCode -eq 0 -and $results.Count -eq 1 -and $results[0].outcome -ceq 'Passed' -and
            $results[0].testName -like '*OrdersLiveSampleTests.DeployedSample*'
        $runs.Add([ordered]@{
            number = $number
            wallSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 3)
            exitCode = $exitCode
            passed = $passed
            outcomes = @($results | ForEach-Object outcome)
            trx = $trxName
        })
    }
}
finally {
    $env:BLUETUSK_ORDERS_SAMPLE_DLL = $originalSample
}

[ordered]@{
    packageVersion = $PackageVersion
    applicationSha256 = (Get-FileHash -LiteralPath $sample.FullName).Hash.ToLowerInvariant()
    testHarnessSha256 = (Get-FileHash -LiteralPath $test.FullName).Hash.ToLowerInvariant()
    packages = @($blueTuskPackages | Sort-Object)
    runs = $runs.ToArray()
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resultPath 'results.json') -Encoding utf8NoBOM

if (@($runs | Where-Object { $_.passed }).Count -ne 3) {
    throw 'One or more of the three warm Orders runs did not pass.'
}
