function Wait-SyncNats([string] $Name)
{
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(40)
    do
    {
        $null = & docker exec $Name wget -q -O /dev/null 'http://127.0.0.1:8222/healthz?js-enabled-only=true' 2>$null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    & docker logs --tail 14 $Name
    throw 'The isolated NATS JetStream destination did not become ready.'
}

function Get-SyncNatsEndpoint([string] $ContainerId)
{
    if (Test-Path -LiteralPath '/.dockerenv') { return 'nats://nats:4222' }
    $binding = & docker port $ContainerId '4222/tcp'
    if ($LASTEXITCODE -or $binding -cnotmatch '^127\.0\.0\.1:[0-9]+$') { throw 'The NATS fixture must bind only to loopback.' }
    return "nats://$binding"
}

function New-SyncNatsFixture([string] $Network, [string] $FixtureRun, [string] $FixtureOwner, [string] $Commit, [string] $Image, [string] $FixtureDirectory)
{
    if ($Image -cnotmatch '^nats:2\.14-alpine@sha256:[0-9a-f]{64}$') { throw 'The NATS image must be digest pinned.' }
    $name = "bt-perf-$FixtureRun-nats"
    $config = Join-Path $FixtureDirectory 'nats.conf'
    @'
port: 4222
http: 8222
jetstream {
  store_dir: /tmp/bluetusk-sync-jetstream
  sync_interval: always
}
'@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM
    $id = & docker create --name $name --network $Network --network-alias nats --publish '127.0.0.1::4222' `
        --label "bluetusk.owner=$FixtureOwner" --label "bluetusk.run=$FixtureRun" --label "bluetusk.commit=$Commit" `
        $Image -c /tmp/bluetusk-sync-nats.conf
    if ($LASTEXITCODE -ne 0 -or $id -cnotmatch '^[0-9a-f]{64}$') { throw 'Creating the isolated NATS destination failed.' }
    & docker cp $config "${id}:/tmp/bluetusk-sync-nats.conf"
    if ($LASTEXITCODE) { throw 'Copying the pinned JetStream configuration failed.' }
    $null = & docker start $id
    if ($LASTEXITCODE) { throw 'Starting the isolated NATS destination failed.' }
    Wait-SyncNats $name
    $pin = & docker inspect --format '{{.Config.Image}}' $id
    if ($LASTEXITCODE -or $pin -cne $Image) { throw 'The running NATS destination does not match its pinned image.' }
    [pscustomobject]@{ Id = $id; Name = $name; Endpoint = (Get-SyncNatsEndpoint $id); Image = $Image }
}

function Assert-SyncNatsDelivery([string] $Source, [object] $Nats, [int] $Changes, [int] $Transactions, [string] $DotNet, [string] $Assembly, [string] $Role)
{
    $sourceTransactions = @(Get-SyncSourceTransactions $Source $Changes $Transactions | ForEach-Object { "$($_.Id):$($_.CommitMicros)" })
    $null = & docker kill --signal KILL $Nats.Id
    if ($LASTEXITCODE) { throw 'Stopping the isolated NATS process without a normal shutdown failed.' }
    $null = & docker start $Nats.Id
    if ($LASTEXITCODE) { throw 'NATS did not restart from its own JetStream data.' }
    Wait-SyncNats $Nats.Name

    $previous = $env:BLUETUSK_BENCHMARK_NATS_ENDPOINT
    try
    {
        $env:BLUETUSK_BENCHMARK_NATS_ENDPOINT = Get-SyncNatsEndpoint $Nats.Id
        $proof = & $DotNet $Assembly --sync-nats-verify $Changes ($sourceTransactions -join ',') $Role
        if ($LASTEXITCODE -ne 0) { throw 'Recovered NATS evidence failed transaction-envelope verification.' }
        return ($proof | ConvertFrom-Json)
    }
    finally { $env:BLUETUSK_BENCHMARK_NATS_ENDPOINT = $previous }
}
