Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-DotNetCompatibilityServiceReady
{
    param([hashtable] $Fixture, [int] $Port)

    $socket = [Net.Sockets.TcpClient]::new()
    try { $socket.Connect('127.0.0.1', $Port) }
    catch { return $false }
    finally { $socket.Dispose() }

    switch ($Fixture.kind)
    {
        'redis' { return (& docker exec $Fixture.container redis-cli ping) -ceq 'PONG' }
        'nats' {
            & docker exec $Fixture.container wget -q -T 2 -O /dev/null 'http://127.0.0.1:8222/healthz?js-enabled-only=true' 2>$null
            return $LASTEXITCODE -eq 0
        }
        'opensearch' {
            try
            {
                $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/_cluster/health?wait_for_status=yellow&timeout=1s" -TimeoutSec 3
                return !$health.timed_out -and $health.status -in @('yellow', 'green')
            }
            catch { return $false }
        }
        'kafka' {
            $logs = & docker logs --tail 150 $Fixture.container 2>&1
            return $LASTEXITCODE -eq 0 -and ($logs -join [Environment]::NewLine).Contains('Kafka Server started')
        }
    }
}

function Get-DotNetCompatibilityServiceEnvironment
{
    param([string] $Kind, [string] $Address)

    switch ($Kind)
    {
        'redis' { return @{ BLUETUSK_TEST_REDIS_CONNECTION_STRING = "$Address,abortConnect=false" } }
        'nats' { return @{ BLUETUSK_NATS_URL = "nats://$Address" } }
        'opensearch' {
            return @{
                BLUETUSK_OPENSEARCH_URL = "http://$Address"
                BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT = "http://$Address"
            }
        }
        'kafka' { return @{ BLUETUSK_KAFKA_BOOTSTRAP_SERVERS = $Address } }
    }
}

function Start-DotNetCompatibilityService
{
    param(
        [Parameter(Mandatory)] [object] $Contract,
        [Parameter(Mandatory)] [string] $WorkDirectory,
        [string] $Network
    )

    $kind = [string] $Contract.kind
    $image = [string] $Contract.image
    if ($image -cnotmatch '^[a-z0-9][a-z0-9./:_-]*@sha256:[0-9a-f]{64}$')
    { throw "The $kind compatibility image must be pinned by its SHA256 digest." }

    $settings = @()
    $serverArguments = @()
    $bind = '127.0.0.1:'
    $networkArguments = if ($Network) { @('--network', $Network, '--network-alias', $kind) } else { @() }
    switch -CaseSensitive ($kind)
    {
        'redis' { $port = 6379 }
        'nats' {
            $port = 4222
            $serverArguments = @('-js', '-m', '8222')
        }
        'opensearch' {
            $port = 9200
            $settings = @(
                '--env', 'discovery.type=single-node',
                '--env', 'DISABLE_SECURITY_PLUGIN=true',
                '--env', 'DISABLE_INSTALL_DEMO_CONFIG=true',
                '--env', 'OPENSEARCH_JAVA_OPTS=-Xms1g -Xmx1g'
            )
        }
        'kafka' {
            $port = 9092
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            try { $clientPort = $listener.LocalEndpoint.Port }
            finally { $listener.Stop() }
            $bind += $clientPort
            # The transaction coordinator reaches the broker through its internal listener.
            # Host clients use the separate external listener on the published port.
            $internalAddress = if ($Network) { 'kafka:29092' } else { '127.0.0.1:9094' }
            $internalBind = if ($Network) { ':29092' } else { $internalAddress }
            $settings = @(
                '--env', 'KAFKA_NODE_ID=1',
                '--env', 'KAFKA_PROCESS_ROLES=broker,controller',
                '--env', "KAFKA_LISTENERS=EXTERNAL://:9092,INTERNAL://$internalBind,CONTROLLER://127.0.0.1:9093",
                '--env', "KAFKA_ADVERTISED_LISTENERS=EXTERNAL://127.0.0.1:$clientPort,INTERNAL://$internalAddress",
                '--env', 'KAFKA_INTER_BROKER_LISTENER_NAME=INTERNAL',
                '--env', 'KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER',
                '--env', 'KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,EXTERNAL:PLAINTEXT,INTERNAL:PLAINTEXT',
                '--env', 'KAFKA_CONTROLLER_QUORUM_VOTERS=1@127.0.0.1:9093',
                '--env', 'KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1',
                '--env', 'KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1',
                '--env', 'KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1',
                '--env', 'KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0',
                '--env', 'KAFKA_AUTO_CREATE_TOPICS_ENABLE=false'
            )
        }
        default { throw "Unknown compatibility service '$kind'." }
    }

    $null = New-Item -ItemType Directory -Path $WorkDirectory -Force
    $dockerLog = Join-Path $WorkDirectory 'docker.log'
    $containerName = "bluetusk-dotnet-$kind-$([guid]::NewGuid().ToString('N'))"
    $fixture = @{
        kind = $kind
        container = $null
        identity = $image
        serverLog = Join-Path $WorkDirectory 'server.log'
        environment = @{}
        containerEnvironment = @{}
    }
    try
    {
        $container = @(& docker create --name $containerName --publish "$($bind):$port" @networkArguments @settings $image @serverArguments 2> $dockerLog)
        if ($LASTEXITCODE -ne 0 -or $container.Count -ne 1 -or $container[0] -cnotmatch '^[0-9a-f]{64}$')
        { throw "Creating the $kind compatibility service failed. See '$dockerLog'." }
        $fixture.container = $container[0]

        & docker start $fixture.container *>> $dockerLog
        if ($LASTEXITCODE -ne 0) { throw "Starting the $kind compatibility service failed. See '$dockerLog'." }
        $mapping = @(& docker port $fixture.container "$port/tcp" 2>> $dockerLog)
        if ($LASTEXITCODE -ne 0 -or $mapping.Count -ne 1 -or $mapping[0] -cnotmatch '^127\.0\.0\.1:(\d+)$')
        { throw "The $kind compatibility service did not publish a loopback port. See '$dockerLog'." }
        $clientPort = [int] $Matches[1]
        $address = "127.0.0.1:$clientPort"

        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        while (-not (Test-DotNetCompatibilityServiceReady $fixture $clientPort))
        {
            if ([DateTime]::UtcNow -ge $deadline)
            {
                $serverLog = Join-Path $WorkDirectory 'server.log'
                & docker logs $fixture.container *> $serverLog
                throw "The $kind compatibility service did not become ready. See '$serverLog'."
            }
            Start-Sleep -Milliseconds 500
        }

        $fixture.environment = Get-DotNetCompatibilityServiceEnvironment $kind $address
        if ($Network)
        {
            $containerAddress = if ($kind -ceq 'kafka') { $internalAddress } else { "$($kind):$port" }
            $fixture.containerEnvironment = Get-DotNetCompatibilityServiceEnvironment $kind $containerAddress
        }
        return $fixture
    }
    catch
    {
        Stop-DotNetCompatibilityService $fixture
        throw
    }
}

function Stop-DotNetCompatibilityService
{
    param([hashtable] $Fixture, [switch] $RetainServerLog)

    if ($Fixture -and $Fixture.container)
    {
        if ($RetainServerLog) { & docker logs $Fixture.container *> $Fixture.serverLog }
        & docker rm --force --volumes $Fixture.container *> $null
    }
}

Export-ModuleMember -Function Start-DotNetCompatibilityService, Stop-DotNetCompatibilityService
