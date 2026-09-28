param(
    [ValidateSet('quick', 'matrix', 'soak', 'faults', 'qualification', 'storage')]
    [string] $Profile = 'quick',
    [ValidateRange(1, 3600)]
    [int] $Seconds = 30,
    [string] $Output
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING)) {
    throw 'BLUETUSK_TEST_CONNECTION_STRING is required; choose a disposable PostgreSQL database.'
}

$taskRepository = Split-Path -Parent $PSScriptRoot
$taskProject = 'benchmarks/BlueTusk.Workflows.LoadHarness/BlueTusk.Workflows.LoadHarness.csproj'
Push-Location -LiteralPath $taskRepository
try {
    & dotnet build $taskProject -c Release -nr:false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Load harness build failed.' }
    $taskArguments = @('run', '--project', $taskProject, '-c', 'Release', '--no-build', '--', $Profile)
    if ($PSBoundParameters.ContainsKey('Seconds')) { $taskArguments += @('--seconds', $Seconds.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    if ($Output) { $taskArguments += @('--output', $Output) }
    & dotnet @taskArguments
    if ($LASTEXITCODE -ne 0) { throw 'Load or recovery verification failed.' }
}
finally {
    Pop-Location
}
