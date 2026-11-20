function Wait-SyncKafka([string] $Name)
{
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(65)
    do
    {
        $null = & docker exec $Name timeout 6 /opt/kafka/bin/kafka-topics.sh --bootstrap-server 127.0.0.1:9092 --list 2>$null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 300
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    & docker logs --tail 12 $Name
    throw 'The isolated Kafka broker did not become available.'
}

function New-SyncKafkaFixture([string] $Network, [string] $FixtureRun, [string] $FixtureOwner, [string] $Commit, [string] $Image)
{
    if ($Image -cnotmatch '^apache/kafka:4\.1\.1@sha256:[0-9a-f]{64}$') { throw 'The Kafka image must be digest pinned.' }
    # Kafka advertises the external port in its metadata. Choose it before starting the broker.
    $loopback = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $loopback.Start(); $port = $loopback.LocalEndpoint.Port }
    finally { $loopback.Stop() }
    $name = "bt-perf-$FixtureRun-kafka"
    $id = & docker run --detach --name $name --network $Network --network-alias kafka --publish "127.0.0.1:${port}:9094" `
        --label "bluetusk.owner=$FixtureOwner" --label "bluetusk.run=$FixtureRun" --label "bluetusk.commit=$Commit" `
        --env KAFKA_NODE_ID=1 --env KAFKA_PROCESS_ROLES=broker,controller `
        --env KAFKA_LISTENERS=INTERNAL://:9092,CONTROLLER://:9093,HOST://:9094 `
        --env "KAFKA_ADVERTISED_LISTENERS=INTERNAL://kafka:9092,HOST://127.0.0.1:$port" `
        --env KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER `
        --env KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=INTERNAL:PLAINTEXT,CONTROLLER:PLAINTEXT,HOST:PLAINTEXT `
        --env KAFKA_INTER_BROKER_LISTENER_NAME=INTERNAL --env KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093 `
        --env KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 --env KAFKA_OFFSETS_TOPIC_NUM_PARTITIONS=1 `
        --env KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1 --env KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1 `
        --env KAFKA_TRANSACTION_STATE_LOG_NUM_PARTITIONS=1 --env KAFKA_AUTO_CREATE_TOPICS_ENABLE=false `
        --env KAFKA_LOG_FLUSH_INTERVAL_MESSAGES=1 $Image
    if ($LASTEXITCODE -ne 0 -or $id -cnotmatch '^[0-9a-f]{64}$') { throw 'Starting the isolated Kafka destination failed.' }
    Wait-SyncKafka $name
    $pin = & docker inspect --format '{{.Config.Image}}' $id
    $binding = & docker port $id '9094/tcp'
    if ($LASTEXITCODE -or $pin -cne $Image -or $binding -cne "127.0.0.1:$port")
    { throw 'The running Kafka destination differs from its image or loopback port contract.' }
    $endpoint = if (Test-Path -LiteralPath '/.dockerenv') { 'kafka:9092' } else { $binding }
    [pscustomobject]@{ Id = $id; Name = $name; Endpoint = $endpoint; Image = $Image }
}

function Assert-SyncKafkaDelivery([string] $Source, [object] $Kafka, [int] $Changes, [int] $Transactions,
    [string] $DotNet, [string] $Assembly, [string] $Role)
{
    $sourceTransactions = @(Get-SyncSourceTransactions $Source $Changes $Transactions | ForEach-Object { "$($_.Id):$($_.CommitMicros)" })
    $null = & docker kill --signal KILL $Kafka.Id
    if ($LASTEXITCODE) { throw 'Stopping the isolated Kafka broker without a clean shutdown failed.' }
    $null = & docker start $Kafka.Id
    if ($LASTEXITCODE) { throw 'Kafka did not restart from its transaction log.' }
    Wait-SyncKafka $Kafka.Name

    $previous = $env:BLUETUSK_BENCHMARK_KAFKA_ENDPOINT
    try
    {
        $env:BLUETUSK_BENCHMARK_KAFKA_ENDPOINT = $Kafka.Endpoint
        $proof = & $DotNet $Assembly --sync-kafka-verify $Changes ($sourceTransactions -join ',') $Role
        if ($LASTEXITCODE -ne 0) { throw 'Recovered Kafka evidence failed committed-envelope and checkpoint verification.' }
        return ($proof | ConvertFrom-Json)
    }
    finally { $env:BLUETUSK_BENCHMARK_KAFKA_ENDPOINT = $previous }
}
