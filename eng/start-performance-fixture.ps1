[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{2,40}$')][string] $RunId,
    # Container ownership label; cleanup removes only containers carrying both this owner and run id.
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner,
    [Parameter(Mandatory)][ValidateSet('native', 'tls', 'constrained-network', 'linux-container-client')][string] $CaptureProfile,
    # New directory beneath repository artifacts for fixture state; it is never part of the evidence.
    [Parameter(Mandatory)][string] $OutputPath,
    [string] $SourceCommit = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
$artifacts = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output))
{ throw 'Fixture state must use a new directory beneath repository artifacts.' }
$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-evidence-plan.json') -Raw | ConvertFrom-Json
$networkProfiles = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-network-profiles.json') -Raw | ConvertFrom-Json
$map = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-variant-map.json') -Raw | ConvertFrom-Json
$definition = $map.profiles.$CaptureProfile
$image = [string]$plan.fixture.postgreSqlImage
if ($image -cnotmatch '^postgres:[^@\s]+@sha256:[0-9a-f]{64}$') { throw 'The PostgreSQL fixture image must be digest pinned.' }
$insideContainer = Test-Path -LiteralPath '/.dockerenv'
$network = "bt-perf-$RunId"
$labels = @('--label', "bluetusk.owner=$Owner", '--label', "bluetusk.run=$RunId")
if ($SourceCommit) { $labels += @('--label', "bluetusk.commit=$SourceCommit") }
$null = New-Item -ItemType Directory -Path $output

function Invoke-Docker
{
    # Arguments only; never echo environment values or secrets.
    $result = & docker @args
    if ($LASTEXITCODE -ne 0) { throw "docker $($args[0]) failed with exit code $LASTEXITCODE." }
    return $result
}
function Get-Published([string] $Container, [string] $Port)
{
    $binding = @(Invoke-Docker port $Container $Port | Where-Object { $_ -match '^127\.0\.0\.1:\d+$' })[0]
    return [int]$binding.Split(':')[1]
}

$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
$envFile = Join-Path $output 'postgres.env.secret'
[IO.File]::WriteAllText($envFile, "POSTGRES_PASSWORD=$password`nPOSTGRES_DB=bluetusk_benchmark`n")
$containers = [Collections.Generic.List[object]]::new()
try
{
    $null = Invoke-Docker network create @labels $network
    if ($insideContainer)
    {
        # Linux runner in a container: join the fixture network and use container names, not host ports.
        $null = Invoke-Docker network connect $network ([Net.Dns]::GetHostName())
    }
    $postgres = "bt-perf-$RunId-postgres"
    $serverArgs = @('-c', "max_connections=$($plan.fixture.maxConnections)")
    # create + cp + start: no host bind mounts, so a containerized Linux runner works through the Docker socket.
    $runArgs = @('create', '--name', $postgres, '--network', $network, '--network-alias', 'postgres') + $labels +
        @('--shm-size', $plan.fixture.sharedMemory, '--publish', '127.0.0.1::5432', '--env-file', $envFile)
    $tls = $definition.tls -eq $true
    if ($tls)
    {
        $tlsRelative = [IO.Path]::GetRelativePath($root, (Join-Path $output 'tls'))
        & (Join-Path $PSScriptRoot 'new-benchmark-tls-fixture.ps1') -OutputPath $tlsRelative -DnsName @('localhost', 'postgres') | Out-Null
        $runArgs += @('--entrypoint', 'sh', $image, '-c',
            ('cp /bt-tls/server.key /var/lib/postgresql/bt-server.key && chown postgres:postgres /var/lib/postgresql/bt-server.key && ' +
             'chmod 0600 /var/lib/postgresql/bt-server.key && exec docker-entrypoint.sh postgres ' + ($serverArgs -join ' ') +
             ' -c ssl=on -c ssl_cert_file=/bt-tls/server.pem -c ssl_key_file=/var/lib/postgresql/bt-server.key'))
    }
    else
    {
        $runArgs += @($image) + $serverArgs
    }
    $null = Invoke-Docker @runArgs
    if ($tls) { $null = Invoke-Docker cp "$(Join-Path $output 'tls')/." "${postgres}:/bt-tls" }
    $null = Invoke-Docker start $postgres
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do
    {
        Start-Sleep -Seconds 1
        & docker exec $postgres pg_isready -U postgres -d bluetusk_benchmark *> $null
    } while ($LASTEXITCODE -ne 0 -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL fixture did not become ready.' }
    Start-Sleep -Seconds 2
    $maxConnections = [int]((Invoke-Docker exec $postgres psql -U postgres -d bluetusk_benchmark -tAc 'show max_connections') | Select-Object -First 1).Trim()
    if ($maxConnections -lt [int]$plan.fixture.maxConnections) { throw "PostgreSQL started with max_connections=$maxConnections." }
    $identity = (Invoke-Docker inspect --format '{{.Id}}|{{.Config.Image}}|{{.Image}}' $postgres).Trim().Split('|')
    if ($identity[1] -cne $image) { throw 'The running PostgreSQL container is not the pinned image.' }
    $containers.Add([ordered]@{ name = $postgres; containerId = $identity[0]; imageReference = $identity[1]; imageId = $identity[2] })

    $images = [Collections.Generic.List[string]]::new()
    $images.Add($image)
    $serverHost = if ($insideContainer) { 'postgres' } else { '127.0.0.1' }
    $serverPort = if ($insideContainer) { 5432 } else { Get-Published $postgres '5432/tcp' }
    $networkProfileId = $null
    if ($null -ne $definition.networkProfile)
    {
        $name = [string]$definition.networkProfile
        $profileDefinition = $networkProfiles.profiles.$name
        $toxiproxyImage = [string]$networkProfiles.toxiproxyImage
        if ($null -eq $profileDefinition -or $toxiproxyImage -cnotmatch '@sha256:[0-9a-f]{64}$') { throw "Network profile '$name' is undefined or unpinned." }
        $toxiproxy = "bt-perf-$RunId-toxiproxy"
        $null = Invoke-Docker run --detach --name $toxiproxy --network $network --network-alias toxiproxy @labels `
            --publish 127.0.0.1::8474 --publish 127.0.0.1::6432 $toxiproxyImage
        $toxIdentity = (Invoke-Docker inspect --format '{{.Id}}|{{.Config.Image}}|{{.Image}}' $toxiproxy).Trim().Split('|')
        if ($toxIdentity[1] -cne $toxiproxyImage) { throw 'The running Toxiproxy container is not the pinned image.' }
        $containers.Add([ordered]@{ name = $toxiproxy; containerId = $toxIdentity[0]; imageReference = $toxIdentity[1]; imageId = $toxIdentity[2] })
        $images.Add($toxiproxyImage)
        $api = if ($insideContainer) { 'http://toxiproxy:8474' } else { "http://127.0.0.1:$(Get-Published $toxiproxy '8474/tcp')" }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
        while ($true)
        {
            try { $null = Invoke-RestMethod -Uri "$api/version" -TimeoutSec 2; break }
            catch { if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Toxiproxy did not become ready.' }; Start-Sleep -Milliseconds 500 }
        }
        $null = Invoke-RestMethod -Method Post -Uri "$api/proxies" -ContentType 'application/json' -Body (
            @{ name = 'postgres'; listen = '0.0.0.0:6432'; upstream = 'postgres:5432'; enabled = $true } | ConvertTo-Json)
        foreach ($toxic in $profileDefinition.toxics)
        {
            $null = Invoke-RestMethod -Method Post -Uri "$api/proxies/postgres/toxics" -ContentType 'application/json' -Body ($toxic | ConvertTo-Json -Depth 4)
        }
        # Retain the configuration Toxiproxy reports, not merely the requested one.
        $proxy = Invoke-RestMethod -Uri "$api/proxies/postgres"
        $observed = [ordered]@{
            profile = $name
            toxiproxyImage = $toxiproxyImage
            proxy = [ordered]@{ listen = $proxy.listen; upstream = $proxy.upstream; enabled = $proxy.enabled }
            toxics = @($proxy.toxics | Sort-Object name | ForEach-Object {
                [ordered]@{ name = $_.name; type = $_.type; stream = $_.stream; toxicity = $_.toxicity; attributes = $_.attributes } })
        }
        $requested = @($profileDefinition.toxics | Sort-Object name | ForEach-Object { $_ | ConvertTo-Json -Depth 4 -Compress })
        $actual = @($observed.toxics | ForEach-Object { $_ | ConvertTo-Json -Depth 4 -Compress })
        if (($requested -join "`n") -cne ($actual -join "`n") -or -not $proxy.enabled)
        { throw 'Toxiproxy does not report the requested network profile.' }
        $networkPath = Join-Path $output 'network-profile.json'
        [IO.File]::WriteAllText($networkPath, ($observed | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        $networkProfileId = "$name@sha256:$((Get-FileHash -LiteralPath $networkPath -Algorithm SHA256).Hash.ToLowerInvariant())"
        $serverHost = if ($insideContainer) { 'toxiproxy' } else { '127.0.0.1' }
        $serverPort = if ($insideContainer) { 6432 } else { Get-Published $toxiproxy '6432/tcp' }
    }
    if ($CaptureProfile -eq 'linux-container-client') { $images.Add([string]$plan.fixture.linuxClientImage) }

    $security = if ($tls)
    { "SSL Mode=VerifyFull;Root Certificate=$(Join-Path $output 'tls/ca.pem')" }
    else { 'SSL Mode=Disable' }
    $connection = "Host=$serverHost;Port=$serverPort;Database=bluetusk_benchmark;Username=postgres;Password=$password;$security"
    # Sibling containers (the proposed Linux-container client) reach the server by its network alias.
    $containerHost = if ($null -ne $networkProfileId) { 'toxiproxy' } else { 'postgres' }
    $containerPort = if ($null -ne $networkProfileId) { 6432 } else { 5432 }
    $containerConnection = "Host=$containerHost;Port=$containerPort;Database=bluetusk_benchmark;Username=postgres;Password=$password;SSL Mode=Disable"
    [IO.File]::WriteAllText((Join-Path $output 'connection.secret'), $connection)
    [IO.File]::WriteAllText((Join-Path $output 'container-connection.secret'), $containerConnection)
    $fixture = [ordered]@{
        schemaVersion = 1
        runId = $RunId
        owner = $Owner
        profile = $CaptureProfile
        network = $network
        insideContainer = $insideContainer
        postgreSqlImage = $image
        maxConnections = $maxConnections
        tls = $tls
        networkProfile = $networkProfileId
        containerImageDigests = @($images | Sort-Object -Unique -CaseSensitive)
        containers = $containers.ToArray()
        topology = $(if ($CaptureProfile -eq 'linux-container-client') { 'Linux .NET client container on the fixture Docker network (proposal)' }
            elseif ($insideContainer) { 'Linux runner container attached to the fixture Docker network' }
            else { 'host client process through a 127.0.0.1 published Docker port' }) +
            $(if ($null -ne $networkProfileId) { ' via Toxiproxy' } else { '' })
    }
    [IO.File]::WriteAllText((Join-Path $output 'fixture.json'), ($fixture | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Output "Started '$CaptureProfile' fixture $RunId ($($containers.Count) container(s), max_connections=$maxConnections). Connection secrets stay in the fixture directory."
}
catch
{
    & (Join-Path $PSScriptRoot 'stop-performance-fixture.ps1') -RunId $RunId -Owner $Owner
    throw
}
finally
{
    [IO.File]::Delete($envFile)
}
