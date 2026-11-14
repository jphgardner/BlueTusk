[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$verifier = Join-Path $PSScriptRoot 'verify-dotnet-compatibility.ps1'
$contractText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dotnet-compatibility.json') -Raw
$fixtureRoot = Join-Path $RepositoryRoot ('artifacts/dotnet-compatibility/contract-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$baseline = & $verifier -RepositoryRoot $RepositoryRoot -PassThru
if ($baseline.frameworks[0] -cne 'net5.0' -or $baseline.coveredRuntimePackages -lt 3)
{
    throw 'The baseline compatibility contract lost its executable foundation coverage.'
}

function Assert-Rejected
{
    param([string] $Name, [scriptblock] $Mutate, [string] $ExpectedMessage)
    $fixture = $contractText | ConvertFrom-Json
    & $Mutate $fixture
    $path = Join-Path $fixtureRoot "$Name.json"
    $fixture | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
    $rejected = $false
    try { & $verifier -RepositoryRoot $RepositoryRoot -ContractPath $path | Out-Null }
    catch
    {
        if ($_.Exception.Message -notmatch $ExpectedMessage) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Compatibility verifier accepted '$Name'." }
}

Assert-Rejected -Name omitted-package -ExpectedMessage 'Every product-family package' -Mutate {
    param($fixture)
    $fixture.packages = @($fixture.packages | Select-Object -Skip 1)
}
Assert-Rejected -Name duplicate-package -ExpectedMessage 'Every product-family package' -Mutate {
    param($fixture)
    $fixture.packages = @($fixture.packages) + @($fixture.packages[0])
}
Assert-Rejected -Name missing-runtime-major -ExpectedMessage 'every major version' -Mutate {
    param($fixture)
    $fixture.frameworks = @($fixture.frameworks | Where-Object { $_ -cne 'net6.0' })
}
Assert-Rejected -Name wrong-runtime-patch-major -ExpectedMessage 'same .NET major' -Mutate {
    param($fixture)
    $fixture.runtimeVersions.'net5.0' = '11.0.0'
}
Assert-Rejected -Name missing-executable-tests -ExpectedMessage 'needs unique executable test projects' -Mutate {
    param($fixture)
    ($fixture.packages | Where-Object project -CEQ 'src/BlueTusk.Protocol/BlueTusk.Protocol.csproj').testProjects = @()
}
Assert-Rejected -Name relabel-runtime-as-template -ExpectedMessage 'must be classified' -Mutate {
    param($fixture)
    ($fixture.packages | Where-Object project -CEQ 'src/BlueTusk.Protocol/BlueTusk.Protocol.csproj').kind = 'template'
}
Assert-Rejected -Name undocumented-minimum -ExpectedMessage 'explicit upstream constraint' -Mutate {
    param($fixture)
    ($fixture.packages | Where-Object project -CEQ 'src/BlueTusk.Live.Aspire/BlueTusk.Live.Aspire.csproj').constraint = ''
}
Assert-Rejected -Name mutable-legacy-image -ExpectedMessage 'immutable official image digest' -Mutate {
    param($fixture)
    $fixture.legacyLinuxContainers.'net5.0'.image = 'mcr.microsoft.com/dotnet/sdk:5.0'
}
Assert-Rejected -Name mutable-postgresql-image -ExpectedMessage 'immutable Linux image' -Mutate {
    param($fixture)
    $fixture.postgreSqlFixture.linuxImage = 'postgres:18-alpine'
}
Assert-Rejected -Name wrong-minio-platform-archive -ExpectedMessage 'verified distributions' -Mutate {
    param($fixture)
    $fixture.minioFixture.linuxBinary.url = $fixture.minioFixture.windowsBinary.url
}
Assert-Rejected -Name wrong-cli-command -ExpectedMessage 'Invalid installed CLI smoke mapping' -Mutate {
    param($fixture)
    ($fixture.cliTools | Where-Object packageId -CEQ 'BlueTusk.Streams.Tool').command = 'bluetusk'
}
Assert-Rejected -Name omits-live-postgresql-suite -ExpectedMessage 'live test suites must use the pinned' -Mutate {
    param($fixture)
    $fixture.postgreSqlFixture.testProjects = @($fixture.postgreSqlFixture.testProjects | Where-Object { $_ -cnotmatch 'RuntimeCompatibility' })
}
if (-not $baseline.fullRuntimeCoverage)
{
    $rejected = $false
    try { & $verifier -RepositoryRoot $RepositoryRoot -RequireComplete | Out-Null }
    catch
    {
        if ($_.Exception.Message -notmatch 'Full .NET compatibility is incomplete') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Pending runtime packages incorrectly satisfied the full compatibility gate.' }
}
Write-Output 'Verified .NET compatibility gate rejects omitted packages, missing runtimes, false test coverage, undocumented minimums, mutable legacy images, and premature full-support claims.'
