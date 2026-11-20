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
