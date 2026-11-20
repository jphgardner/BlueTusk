[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $OutputPath,
    [ValidateSet(1, 100, 1000)][int] $Changes = 1,
    [ValidateSet('commit-to-delivery', 'catch-up', 'snapshot', 'spill')][string] $Scenario = 'commit-to-delivery',
    [ValidateSet('count', 'postgresql')][string] $Sink = 'count',
    [ValidateRange(1, 1000)][int] $MaxHttpBatchRows = 1000,
    [ValidateRange(0, 10000)][int] $WarmupTransactions = 20,
    [ValidateRange(1, 16000)][int] $MeasurementTransactions = 1000,
    [ValidateRange(0, 49)][int] $Trial = 0,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{2,40}$')][string] $RunId = ('streams-ref-' + [guid]::NewGuid().ToString('N').Substring(0, 12)),
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner = 'streams-reference-probe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'streams-performance/streams-fixture.ps1')
$spill = $Scenario -ceq 'spill'
$sync = $Sink -ceq 'postgresql'
if (($sync -and $Scenario -cne 'commit-to-delivery') -or (-not $sync -and $Changes -eq 100))
{ throw 'The PostgreSQL Sync sink supports the live delivery scenario and 1, 100 or 1000 mutations.' }
$suffix = if ($sync) { 'destination=postgresql' } else { Get-StreamsWorkloadSuffix $Scenario }
$liveDelivery = $Scenario -ceq 'commit-to-delivery' -or $spill
$snapshot = $Scenario -ceq 'snapshot'
if ($spill -and -not $PSBoundParameters.ContainsKey('WarmupTransactions')) { $WarmupTransactions = 2 }
if ($spill -and -not $PSBoundParameters.ContainsKey('MeasurementTransactions')) { $MeasurementTransactions = 8 }
if (-not $liveDelivery -and -not $PSBoundParameters.ContainsKey('WarmupTransactions')) { $WarmupTransactions = 0 }
if ($liveDelivery -eq ($WarmupTransactions -eq 0) -or ($spill -and $WarmupTransactions + $MeasurementTransactions -gt 32))
{ throw 'Live delivery needs warmup. A spill capture supports up to 32 total transactions.' }
$os = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Use a Windows or Linux benchmark host.' }
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedCommit) { throw 'The benchmark checkout differs from the expected commit.' }
$dirty = & git -C $root status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'The benchmark requires a clean tracked worktree.' }
if ($Changes * [long]$MeasurementTransactions -gt 1048576) { throw 'The reference capture supports up to 1,048,576 measured rows.' }

$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-evidence-plan.json') -Raw | ConvertFrom-Json
$family = if ($sync) { 'Sync' } else { 'Streams' }
$image = [string]$plan.families.$family.referenceImage
if ($image -cnotmatch '^quay\.io/debezium/server:3\.6\.1\.Final@sha256:[0-9a-f]{64}$') { throw 'The Debezium reference must be pinned by digest.' }
$output = [IO.Path]::GetFullPath($OutputPath, $root)
if (Test-Path -LiteralPath $output) { throw 'The reference capture requires a new output directory.' }
$null = New-Item -ItemType Directory -Path $output
$fixturePath = "artifacts/performance-fixtures/$RunId"
$fixtureDirectory = Join-Path $root $fixturePath
$reference = "bt-perf-$RunId-debezium"
$configuration = Join-Path $fixtureDirectory 'application.properties'
$manifest = Join-Path $fixtureDirectory 'probe.mf'
$capturePath = Join-Path $output 'reference.json'
$created = $false

function Invoke-Docker
{
    $result = & docker @args
    if ($LASTEXITCODE) { throw "Docker $($args[0]) failed with exit code $LASTEXITCODE." }
    return $result
}

function Wait-Delivery([string] $Expected)
{
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do
    {
        $progress = & docker exec $reference curl --silent --show-error --fail --max-time 3 'http://127.0.0.1:8081/progress' 2>$null
        if ($LASTEXITCODE -eq 0 -and $progress -ceq $Expected) { return }
        if ((Invoke-Docker inspect --format '{{.State.Running}}' $reference) -cne 'true') { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "The Debezium reference did not reach delivery state '$Expected'. See 'debezium.log'."
}

try
{
    & (Join-Path $PSScriptRoot 'start-performance-fixture.ps1') -RunId $RunId -Owner $Owner -CaptureProfile native `
        -OutputPath $fixturePath -SourceCommit $ExpectedCommit -LogicalReplication
    $fixture = Get-Content -LiteralPath (Join-Path $fixtureDirectory 'fixture.json') -Raw | ConvertFrom-Json
    $postgres = $fixture.containers[0].name
    New-StreamsCaptureTable $postgres $spill
    if ($sync) { New-SyncPostgreSqlFixture $postgres $true }
    if ($Scenario -ceq 'catch-up')
    {
        $null = Invoke-Docker exec $postgres psql -X -q -v ON_ERROR_STOP=1 -U postgres -d bluetusk_benchmark `
            -c "SELECT pg_create_logical_replication_slot('streams_capture', 'pgoutput')"
    }
    if (-not $liveDelivery) { Write-StreamsTransactions $postgres $Changes 0 $MeasurementTransactions $spill }

    $connection = [Data.Common.DbConnectionStringBuilder]::new()
    $connection.set_ConnectionString([IO.File]::ReadAllText((Join-Path $fixtureDirectory 'container-connection.secret')))
    $snapshotMode = if ($snapshot) { 'initial' } else { 'no_data' }
    $httpBatchSize = if ($spill) { [Math]::Min($Changes, $MaxHttpBatchRows) } else { $MaxHttpBatchRows }
    $properties = @"
debezium.sink.type=http
debezium.sink.http.url=http://127.0.0.1:8081/events
debezium.sink.http.retries=0
debezium.sink.http.batch.enabled=true
debezium.sink.http.batch.max-size=$httpBatchSize
debezium.format.value=json
debezium.format.value.schemas.enable=false
debezium.source.connector.class=io.debezium.connector.postgresql.PostgresConnector
debezium.source.database.hostname=postgres
debezium.source.database.port=5432
debezium.source.database.user=postgres
debezium.source.database.password=$($connection['Password'])
debezium.source.database.dbname=bluetusk_benchmark
debezium.source.database.sslmode=disable
debezium.source.plugin.name=pgoutput
debezium.source.binary.handling.mode=base64
debezium.source.slot.name=streams_capture
debezium.source.publication.name=streams_capture
debezium.source.publication.autocreate.mode=disabled
debezium.source.topic.prefix=streams_reference
debezium.source.table.include.list=public.streams_capture
debezium.source.snapshot.mode=$snapshotMode
debezium.source.offset.storage=org.apache.kafka.connect.storage.FileOffsetBackingStore
debezium.source.offset.storage.file.filename=/debezium/data/streams-offsets.dat
debezium.source.offset.flush.interval.ms=1000
"@
    [IO.File]::WriteAllText($configuration, $properties)
    [IO.File]::WriteAllText($manifest, "Premain-Class: StreamsHttpProbe`n`n")
    $agentArguments = "$Changes,$WarmupTransactions,$MeasurementTransactions,$Trial,$ExpectedCommit,$($fixture.postgreSqlImage),$os,$Scenario,$Sink"
    $launch = 'mkdir /tmp/streams-probe-classes && javac -cp "/debezium/lib/*" -d /tmp/streams-probe-classes /tmp/StreamsHttpProbe.java /tmp/PostgreSqlCaptureSink.java && jar cfm /tmp/streams-probe.jar /tmp/probe.mf -C /tmp/streams-probe-classes . && exec /debezium/run.sh'
    $null = Invoke-Docker create --name $reference --network $fixture.network --label "bluetusk.owner=$Owner" `
        --label "bluetusk.run=$RunId" --label "bluetusk.commit=$ExpectedCommit" `
        --env "JAVA_OPTS=-javaagent:/tmp/streams-probe.jar=$agentArguments" --entrypoint /bin/sh $image -ec $launch
    $created = $true
    $null = Invoke-Docker cp (Join-Path $PSScriptRoot 'streams-performance/StreamsHttpProbe.java') "${reference}:/tmp/StreamsHttpProbe.java"
    $null = Invoke-Docker cp (Join-Path $PSScriptRoot 'streams-performance/PostgreSqlCaptureSink.java') "${reference}:/tmp/PostgreSqlCaptureSink.java"
    $null = Invoke-Docker cp $manifest "${reference}:/tmp/probe.mf"
    $null = Invoke-Docker cp $configuration "${reference}:/debezium/config/application.properties"
    [IO.File]::Delete($configuration)
    $null = Invoke-Docker start $reference

    if ($liveDelivery)
    {
        Wait-Delivery '0'
        $slotDeadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
        do
        {
            $slot = Invoke-Docker exec $postgres psql -X -qAt -U postgres -d bluetusk_benchmark -c "SELECT active FROM pg_replication_slots WHERE slot_name='streams_capture'"
            if ($slot -ceq 't') { break }
            if ((Invoke-Docker inspect --format '{{.State.Running}}' $reference) -cne 'true') { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTimeOffset]::UtcNow -lt $slotDeadline)
        if ($slot -cne 't') { throw 'The reference did not open the pinned PostgreSQL publication slot.' }

        Write-StreamsTransactions $postgres $Changes 0 $WarmupTransactions $spill
        Wait-Delivery ([string]($Changes * [long]$WarmupTransactions))
        $null = Invoke-Docker exec $reference curl --silent --show-error --fail --max-time 3 --request POST 'http://127.0.0.1:8081/measure'
        Write-StreamsTransactions $postgres $Changes $WarmupTransactions $MeasurementTransactions $spill
    }
    Wait-Delivery 'complete'
    $captured = Join-Path $fixtureDirectory 'reference.json'
    $null = Invoke-Docker cp "${reference}:/tmp/streams-reference.json" $captured

    $capture = Get-Content -LiteralPath $captured -Raw | ConvertFrom-Json
    $warmupRows = $Changes * [long]$WarmupTransactions
    $query = "SELECT min(id), count(*), xmin::text, (extract(epoch FROM pg_xact_commit_timestamp((xmin::text)::xid)) * 1000000)::bigint FROM public.streams_capture WHERE id > $warmupRows GROUP BY xmin::text ORDER BY min(id)"
    $commits = @(Invoke-Docker exec $postgres psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d bluetusk_benchmark -c $query)
    $expectedCommitTimestamps = if ($snapshot) { 0 } else { $commits.Count }
    if ($commits.Count -ne $MeasurementTransactions -or $capture.environment.commitMicrosByTransaction.Count -ne $expectedCommitTimestamps -or
        $capture.measurement.completedOperations -ne $Changes * [long]$MeasurementTransactions -or $capture.sourceCommit -cne $ExpectedCommit -or
        -not $capture.workloadKey.EndsWith("|$suffix", [StringComparison]::Ordinal) -or
        $capture.role -cne 'reference' -or $capture.diagnostic -ne $true -or $capture.synthetic -ne $false)
    { throw 'The reference capture did not match its measured workload.' }
    for ($index = 0; $index -lt $commits.Count; $index++)
    {
        $first, $count, $transactionId, $microseconds = $commits[$index].Split('|')
        if ([long]$first -ne $warmupRows + 1 + $index * [long]$Changes -or [long]$count -ne $Changes)
        { throw "The PostgreSQL fixture differs from the requested transaction size at transaction $index." }
        if (-not $snapshot -and ([long]$transactionId -ne $capture.environment.transactionIds[$index] -or
            [long]$microseconds -ne $capture.environment.commitMicrosByTransaction[$index]))
        { throw "Debezium delivery timestamps differ from PostgreSQL commit history at measured transaction $index." }
    }
    if ($spill) { Assert-StreamsSpillPayload $postgres $warmupRows $MeasurementTransactions $capture }
    $finalHead = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $finalHead -cne $ExpectedCommit) { throw 'The benchmark checkout changed during capture.' }
    $dirty = & git -C $root status --porcelain --untracked-files=no
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'The benchmark tracked worktree changed during capture.' }
    if ($sync)
    {
        $destinationProof = Assert-SyncPostgreSqlDelivery $postgres $Changes ($WarmupTransactions + $MeasurementTransactions) -Reference
        $capture.environment | Add-Member -NotePropertyName destinationProof -NotePropertyValue $destinationProof
        $capture | ConvertTo-Json -Depth 12 -Compress | Set-Content -LiteralPath $capturePath -Encoding utf8NoBOM
    }
    else { [IO.File]::Copy($captured, $capturePath) }
    "Captured $($capture.measurement.completedOperations) Debezium $Scenario rows in '$capturePath'."
}
finally
{
    if (Test-Path -LiteralPath $configuration) { [IO.File]::Delete($configuration) }
    if ($created) { & docker logs $reference *> (Join-Path $output 'debezium.log') }
    & (Join-Path $PSScriptRoot 'stop-performance-fixture.ps1') -RunId $RunId -Owner $Owner
    foreach ($file in 'connection.secret', 'container-connection.secret', 'postgres.env.secret')
    {
        $path = Join-Path $fixtureDirectory $file
        if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
    }
}
