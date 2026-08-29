[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = '/workspace'
$reportRoot = 'artifacts/endurance/live-control-plane'
if ($env:CANDIDATE_SHA -notmatch '^[0-9a-f]{40}$' -or
    $env:CANDIDATE_VERSION -notmatch '^1\.2\.0-rc\.[1-9][0-9]*$')
{
    throw 'A lowercase full candidate SHA and a 1.2.0 RC version are required.'
}

Set-Location $repositoryRoot
if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git')))
{
    & git init --quiet
}
& git remote get-url origin *> $null
if ($LASTEXITCODE -ne 0)
{
    & git remote add origin https://github.com/jphgardner/BlueTusk.git
}
& git fetch --quiet --depth 1 origin $env:CANDIDATE_SHA
& git checkout --quiet --detach $env:CANDIDATE_SHA
if ($LASTEXITCODE -ne 0 -or (& git rev-parse HEAD).Trim() -ne $env:CANDIDATE_SHA)
{
    throw 'The exact candidate commit could not be checked out.'
}

& ./eng/pack-product-family.ps1 `
    -Family Live `
    -Prerelease `
    -VersionOverride $env:CANDIDATE_VERSION `
    -PrereleaseTrainPath eng/prerelease-train.json `
    -Output "$reportRoot/candidate-packages"
& ./eng/pack-product-family.ps1 `
    -Family ControlPlane `
    -Prerelease `
    -VersionOverride $env:CANDIDATE_VERSION `
    -PrereleaseTrainPath eng/prerelease-train.json `
    -Output "$reportRoot/candidate-packages"
& dotnet restore BlueTusk.slnx --locked-mode
& ./eng/generate-sbom.ps1 `
    -PackageDirectory "$reportRoot/candidate-packages" `
    -OutputDirectory "$reportRoot/candidate-sbom" `
    -Commit $env:CANDIDATE_SHA `
    -NoRestore

& ./eng/run-live-control-plane-endurance.ps1 `
    -Duration '1.00:00:00' `
    -MinimumCycles 100000 `
    -IntervalMilliseconds 250 `
    -ReportPath "$reportRoot/report.json" `
    -CandidateProvenancePath "$reportRoot/candidate-sbom/build-provenance.json" `
    -Configuration Release
& ./eng/verify-live-control-plane-endurance-report.ps1 `
    -ReportPath "$reportRoot/report.json" `
    -RequiredDuration '1.00:00:00' `
    -MinimumCycles 100000 `
    -ExpectedCommit $env:CANDIDATE_SHA `
    -CandidateProvenancePath "$reportRoot/candidate-sbom/build-provenance.json"

[IO.File]::WriteAllText(
    (Join-Path $repositoryRoot "$reportRoot/SUCCESS"),
    "$($env:CANDIDATE_SHA) $($env:CANDIDATE_VERSION)`n",
    [Text.UTF8Encoding]::new($false))
