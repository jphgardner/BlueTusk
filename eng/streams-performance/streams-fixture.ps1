$streamsSpillBytes = 4194304

function New-StreamsCaptureTable([string] $Container, [bool] $Spill)
{
    $column = if ($Spill) { ', payload bytea NOT NULL' } else { '' }
    $storage = if ($Spill) { 'ALTER TABLE public.streams_capture ALTER COLUMN payload SET STORAGE EXTERNAL;' } else { '' }
    $sql = "CREATE TABLE public.streams_capture(id bigint PRIMARY KEY$column); $storage CREATE PUBLICATION streams_capture FOR TABLE public.streams_capture WITH (publish = 'insert');"
    & docker exec $Container psql -X -q -v ON_ERROR_STOP=1 -U postgres -d bluetusk_benchmark -c $sql
    if ($LASTEXITCODE) { throw 'Creating the diagnostic source publication failed.' }
}

function Write-StreamsTransactions([string] $Container, [int] $Changes, [int] $Start, [int] $Count, [bool] $Spill)
{
    # psql sends each statement as a separate autocommit. Disable TOAST compression for
    # spill fixtures so the deterministic bytes actually travel through the WAL and sink.
    $baseSize = [int][Math]::Floor($streamsSpillBytes / $Changes)
    $remainder = $streamsSpillBytes % $Changes
    & {
        for ($index = $Start; $index -lt $Start + $Count; $index++)
        {
            $first = $index * [long]$Changes + 1
            $last = $first + $Changes - 1
            if ($Spill)
            {
                "INSERT INTO public.streams_capture SELECT id, substring(decode(repeat(encode(sha256(convert_to(id::text, 'UTF8')), 'hex'), (size + 31) / 32), 'hex') FOR size) FROM generate_series($first, $last) AS id CROSS JOIN LATERAL (SELECT $baseSize + (id - $first < $remainder)::int AS size) AS row_size;"
            }
            else
            {
                "INSERT INTO public.streams_capture SELECT generate_series($first, $last);"
            }
        }
    } | & docker exec -i $Container psql -X -q -v ON_ERROR_STOP=1 -U postgres -d bluetusk_benchmark
    if ($LASTEXITCODE) { throw 'The independent PostgreSQL workload writer failed.' }
}

function Assert-StreamsSpillPayload([string] $Container, [long] $WarmupRows, [int] $Transactions, [object] $Capture)
{
    $query = "SELECT sum(octet_length(payload)), encode(sha256(string_agg(payload, ''::bytea ORDER BY id)), 'hex') FROM public.streams_capture WHERE id > $WarmupRows GROUP BY xmin::text ORDER BY min(id)"
    $payloads = @(& docker exec $Container psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d bluetusk_benchmark -c $query)
    if ($LASTEXITCODE -or $payloads.Count -ne $Transactions -or $Capture.environment.spillPayloadSha256.Count -ne $Transactions)
    { throw 'The spill capture must provide one payload digest per measured transaction.' }
    for ($index = 0; $index -lt $Transactions; $index++)
    {
        $bytes, $digest = $payloads[$index].Split('|')
        if ([long]$bytes -ne $streamsSpillBytes -or $digest -cne $Capture.environment.spillPayloadSha256[$index])
        { throw "Delivery payload differs from the committed 4 MiB fixture at transaction $index." }
    }
}

function Get-StreamsWorkloadSuffix([string] $Scenario)
{
    if ($Scenario -ceq 'spill') { return "spoolBytes=$streamsSpillBytes" }
    return "scenario=$Scenario"
}

function New-SyncPostgreSqlFixture([string] $Container, [bool] $Reference)
{
    & docker exec $Container psql -Xq -v ON_ERROR_STOP=1 -U postgres -d postgres -c 'CREATE DATABASE bluetusk_sync_sink' `
        -c 'ALTER DATABASE bluetusk_sync_sink SET synchronous_commit = on'
    if ($LASTEXITCODE) { throw 'Creating the durable Sync destination database failed.' }
    if (-not $Reference) { return }

    $schema = @'
CREATE SCHEMA bluetusk_sync;
CREATE TABLE bluetusk_sync.pipelines (
    pipeline_id text PRIMARY KEY,
    source_fingerprint text NOT NULL DEFAULT 'fixture',
    transform_name text NOT NULL DEFAULT 'sync-capture',
    transform_fingerprint char(64) NOT NULL DEFAULT repeat('0', 64),
    checkpoint_position numeric(20,0), checkpoint_transaction_id bigint,
    store_generation bigint NOT NULL DEFAULT 0, snapshot_epoch uuid,
    snapshot_complete boolean NOT NULL DEFAULT false, updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
INSERT INTO bluetusk_sync.pipelines (pipeline_id) VALUES ('sync_capture');
CREATE TABLE bluetusk_sync.documents (
    pipeline_id text NOT NULL REFERENCES bluetusk_sync.pipelines(pipeline_id) ON DELETE CASCADE,
    collection_name text NOT NULL, document_key text NOT NULL, partition_key text,
    content_type text NOT NULL, content bytea NOT NULL, source_change_id text NOT NULL,
    snapshot_epoch uuid, updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (pipeline_id, collection_name, document_key)
);
'@
    & docker exec $Container psql -Xq -v ON_ERROR_STOP=1 -U postgres -d bluetusk_sync_sink -c $schema
    if ($LASTEXITCODE) { throw 'Creating the native PostgreSQL reference destination failed.' }
}

function Assert-SyncPostgreSqlDelivery([string] $Container, [int] $Changes, [int] $Transactions)
{
    $rows = $Changes * [long]$Transactions
    $sourceXid = & docker exec $Container psql -XqAt -v ON_ERROR_STOP=1 -U postgres -d bluetusk_benchmark `
        -c "SELECT xmin::text FROM public.streams_capture WHERE id = $rows"
    if ($LASTEXITCODE -or $sourceXid -cnotmatch '^[0-9]+$') { throw 'The final source transaction is missing.' }

    $query = @"
WITH documents AS (
    SELECT document_key::bigint AS id, xmin AS destination_xid,
        (collection_name = 'streams_capture' AND content_type = 'application/json' AND
         convert_from(content, 'UTF8')::jsonb = jsonb_build_object('id', document_key::bigint)) AS correct
    FROM bluetusk_sync.documents WHERE pipeline_id = 'sync_capture'
), batches AS (
    SELECT destination_xid, min(id) AS first_id, max(id) AS last_id, count(*) AS rows, bool_and(correct) AS correct
    FROM documents GROUP BY destination_xid
)
SELECT count(*), coalesce(sum(b.rows),0),
       count(*) FILTER (WHERE b.correct AND b.rows = $Changes AND b.last_id - b.first_id = $Changes - 1
                        AND b.first_id BETWEEN 1 AND $rows AND (b.first_id - 1) % $Changes = 0),
       count(*) FILTER (WHERE b.last_id = $rows AND b.destination_xid = p.xmin
                        AND p.checkpoint_transaction_id = $sourceXid AND p.checkpoint_position > 0),
       current_setting('synchronous_commit')
FROM batches b CROSS JOIN bluetusk_sync.pipelines p WHERE p.pipeline_id = 'sync_capture';
"@
    $delivery = & docker exec $Container psql -XqAt -v ON_ERROR_STOP=1 -U postgres -d bluetusk_sync_sink -c $query
    if ($LASTEXITCODE -or $delivery -cne "$Transactions|$rows|$Transactions|1|on")
    { throw 'The destination did not persist every expected transaction and its final checkpoint atomically.' }
    [ordered]@{ transactions = $Transactions; documents = $rows; finalSourceTransactionId = [long]$sourceXid; finalCheckpointSharedTransaction = $true; synchronousCommit = 'on' }
}
