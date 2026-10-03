[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('windows', 'linux')][string] $Os,
    # Host-persistent raw evidence store shared by capture and summarize jobs (for example D:\bluetusk-evidence).
    [Parameter(Mandatory)][string] $EvidenceStore,
    # Windows only: directory with an extracted actions/runner release for win-x64.
    [string] $RunnerDirectory = '',
    # Optional: the host measurement lock other agents and workflows honour.
    [string] $HostMeasurementLock = '',
    [string] $Repository = 'https://github.com/jphgardner/BlueTusk',
    [switch] $Once
)

# Starts an EPHEMERAL self-hosted runner for core-performance-evidence.yml and re-registers it after every
# job, so no job inherits another job's workspace. Owner action: create a registration token
# (Settings > Actions > Runners > New runner, or the API) and put it in the environment variable
# BLUETUSK_RUNNER_REGISTRATION_TOKEN for this process only. The token is never printed or written to evidence.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_RUNNER_REGISTRATION_TOKEN))
{ throw 'Set BLUETUSK_RUNNER_REGISTRATION_TOKEN (owner action); it is not stored by this script.' }
$store = [IO.Path]::GetFullPath($EvidenceStore)
$null = New-Item -ItemType Directory -Path $store -Force
$labels = 'bluetusk-benchmark'

do
{
    $name = "bt-bench-$Os-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    if ($Os -eq 'windows')
    {
        if (-not $IsWindows) { throw 'The Windows runner must start on the Windows host.' }
        $runner = (Resolve-Path -LiteralPath $RunnerDirectory).Path
        # Runner-scoped variables: the workflow reads these; nothing secret is placed here.
        $variables = @("BLUETUSK_EVIDENCE_STORE=$store")
        if ($HostMeasurementLock) { $variables += "BLUETUSK_HOST_MEASUREMENT_LOCK=$HostMeasurementLock" }
        [IO.File]::WriteAllLines((Join-Path $runner '.env'), $variables)
        & (Join-Path $runner 'config.cmd') --unattended --ephemeral --replace --url $Repository `
            --token $env:BLUETUSK_RUNNER_REGISTRATION_TOKEN --name $name --labels $labels --work _work
        if ($LASTEXITCODE -ne 0) { throw 'Windows runner registration failed.' }
        & (Join-Path $runner 'run.cmd')
    }
    else
    {
        # Linux runner container on the same physical host. The image is built from
        # eng/runners/linux-benchmark-runner.Dockerfile; the evidence store is bind mounted.
        $image = 'bluetusk/linux-benchmark-runner:local'
        & docker build --file (Join-Path $PSScriptRoot 'linux-benchmark-runner.Dockerfile') --tag $image $PSScriptRoot
        if ($LASTEXITCODE -ne 0) { throw 'Linux runner image build failed.' }
        $envFile = Join-Path ([IO.Path]::GetTempPath()) "$name.env"
        try
        {
            $lines = @("RUNNER_TOKEN=$env:BLUETUSK_RUNNER_REGISTRATION_TOKEN", 'BLUETUSK_EVIDENCE_STORE=/evidence')
            if ($HostMeasurementLock) { $lines += 'BLUETUSK_HOST_MEASUREMENT_LOCK=/host-lock/HOST-MEASUREMENT.lock' }
            [IO.File]::WriteAllLines($envFile, $lines)
            $mounts = @('--volume', '/var/run/docker.sock:/var/run/docker.sock', '--volume', "${store}:/evidence")
            if ($HostMeasurementLock) { $mounts += @('--volume', "$(Split-Path $HostMeasurementLock -Parent):/host-lock") }
            # The runner joins fixture networks itself (start-performance-fixture.ps1 detects /.dockerenv).
            & docker run --rm --name $name --hostname $name --label bluetusk.owner=benchmark-runner --group-add 0 `
                --env-file $envFile @mounts $image bash -c (
                    "./config.sh --unattended --ephemeral --replace --url $Repository --token `"`$RUNNER_TOKEN`" " +
                    "--name $name --labels $labels --work _work && unset RUNNER_TOKEN && ./run.sh")
        }
        finally { if (Test-Path -LiteralPath $envFile) { [IO.File]::Delete($envFile) } }
    }
} while (-not $Once)
