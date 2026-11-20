function Wait-SyncRedis([string] $Name)
{
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(40)
    do
    {
        $pong = & docker exec $Name redis-cli --raw PING 2>$null
        if ($LASTEXITCODE -eq 0 -and $pong -ceq 'PONG') { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The isolated Redis destination did not become ready.'
}

function Get-SyncRedisEndpoint([string] $ContainerId)
{
    if (Test-Path -LiteralPath '/.dockerenv') { return 'redis:6379' }
    $endpoint = & docker port $ContainerId '6379/tcp'
    if ($LASTEXITCODE -or $endpoint -cnotmatch '^127\.0\.0\.1:[0-9]+$') { throw 'The Redis fixture must bind only to loopback.' }
    return $endpoint
}

function New-SyncRedisFixture([string] $Network, [string] $FixtureRun, [string] $FixtureOwner, [string] $Commit, [string] $Image)
{
    if ($Image -cnotmatch '^redis:8-alpine@sha256:[0-9a-f]{64}$') { throw 'The Redis image must be digest pinned.' }
    $name = "bt-perf-$FixtureRun-redis"
    $id = & docker run --detach --name $name --network $Network --network-alias redis --publish '127.0.0.1::6379' `
        --label "bluetusk.owner=$FixtureOwner" --label "bluetusk.run=$FixtureRun" --label "bluetusk.commit=$Commit" `
        $Image redis-server --protected-mode no --appendonly yes --appendfsync always --auto-aof-rewrite-percentage 0 `
        --aof-load-truncated no --save ''
    if ($LASTEXITCODE -ne 0 -or $id -cnotmatch '^[0-9a-f]{64}$') { throw 'Starting the isolated Redis destination failed.' }
    Wait-SyncRedis $name
    $pin = & docker inspect --format '{{.Config.Image}}' $id
    if ($LASTEXITCODE -or $pin -cne $Image) { throw 'The running Redis destination does not match its pinned image.' }
    $persistence = @(& docker exec $id redis-cli --raw CONFIG GET appendonly appendfsync aof-load-truncated)
    if ($LASTEXITCODE -or $persistence.Count -ne 6) { throw 'Reading the Redis persistence contract failed.' }
    $settings = @{}
    for ($index = 0; $index -lt $persistence.Count; $index += 2) { $settings[$persistence[$index]] = $persistence[$index + 1] }
    if ($settings.appendonly -cne 'yes' -or $settings.appendfsync -cne 'always' -or $settings.'aof-load-truncated' -cne 'no')
    { throw 'Redis did not start with strict synchronous append-only persistence.' }

    [pscustomobject]@{ Id = $id; Name = $name; Endpoint = (Get-SyncRedisEndpoint $id); Image = $Image }
}

function Assert-SyncRedisDelivery([string] $Source, [object] $Redis, [int] $Changes, [int] $Transactions, [string] $DotNet, [string] $Assembly)
{
    $commits = @(& docker exec $Source psql -XqAt -v ON_ERROR_STOP=1 -U postgres -d bluetusk_benchmark `
        -c 'SELECT min(id), count(*), xmin::text FROM public.streams_capture GROUP BY xmin::text ORDER BY min(id)')
    if ($LASTEXITCODE -or $commits.Count -ne $Transactions) { throw 'The Redis source transaction history is incomplete.' }
    $sourceIds = for ($index = 0; $index -lt $commits.Count; $index++) {
        $first, $count, $xid = $commits[$index].Split('|')
        if ([long]$first -ne 1 + $index * [long]$Changes -or [long]$count -ne $Changes -or $xid -cnotmatch '^[0-9]+$')
        { throw 'The PostgreSQL source rows differ from the requested Redis benchmark.' }
        $xid
    }
    # Crash only the isolated destination created for this trial. Its own named container
    # retains the append-only files, so verification cannot read the live in-memory state.
    $null = & docker kill --signal KILL $Redis.Id
    if ($LASTEXITCODE) { throw 'Stopping the isolated Redis process without a normal shutdown failed.' }
    $null = & docker start $Redis.Id
    if ($LASTEXITCODE) { throw 'Redis did not restart from its own append-only data.' }
    Wait-SyncRedis $Redis.Name

    $previous = $env:BLUETUSK_BENCHMARK_REDIS_ENDPOINT
    try
    {
        $env:BLUETUSK_BENCHMARK_REDIS_ENDPOINT = Get-SyncRedisEndpoint $Redis.Id
        $proof = & $DotNet $Assembly --sync-redis-verify $Changes ($sourceIds -join ',')
        if ($LASTEXITCODE -ne 0) { throw 'Recovered Redis evidence failed document or checkpoint verification.' }
        return ($proof | ConvertFrom-Json)
    }
    finally { $env:BLUETUSK_BENCHMARK_REDIS_ENDPOINT = $previous }
}
