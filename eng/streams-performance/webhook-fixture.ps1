function Get-SyncWebhookEnvironment
{
    $values = @{}
    foreach ($name in 'BLUETUSK_BENCHMARK_WEBHOOK_ENDPOINT','BLUETUSK_BENCHMARK_WEBHOOK_KEY')
    { $values[$name] = [Environment]::GetEnvironmentVariable($name) }
    $values
}

function Restore-SyncWebhookEnvironment([hashtable] $Values)
{
    foreach ($entry in $Values.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
}

function Wait-SyncWebhook([string] $Id)
{
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do
    {
        $null = & docker exec $Id curl --silent --fail --max-time 3 http://127.0.0.1:8099/health 2>$null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The isolated webhook receiver did not become ready.'
}

function New-SyncWebhookFixture([string] $Network, [string] $FixtureRun, [string] $FixtureOwner, [string] $Commit, [string] $Image)
{
    if ($Image -cnotmatch '^quay\.io/debezium/server:3\.6\.1\.Final@sha256:[0-9a-f]{64}$')
    { throw 'The webhook fixture runtime must be digest pinned.' }
    $name = "bt-perf-$FixtureRun-webhooks"
    $launch = 'mkdir -p /tmp/sync-webhook-classes && javac -cp "/debezium/lib/*" -d /tmp/sync-webhook-classes /tmp/WebhookCaptureProtocol.java /tmp/WebhookReceiptFixture.java && exec java -cp "/tmp/sync-webhook-classes:/debezium/lib/*" WebhookReceiptFixture'
    $env:BLUETUSK_BENCHMARK_WEBHOOK_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $id = & docker create --name $name --network $Network --network-alias webhooks --publish '127.0.0.1::8099' `
        --label "bluetusk.owner=$FixtureOwner" --label "bluetusk.run=$FixtureRun" --label "bluetusk.commit=$Commit" `
        --env BLUETUSK_BENCHMARK_WEBHOOK_KEY --entrypoint /bin/sh $Image -ec $launch
    if ($LASTEXITCODE -ne 0 -or $id -cnotmatch '^[0-9a-f]{64}$') { throw 'Creating the isolated webhook receiver failed.' }
    foreach ($source in 'WebhookCaptureProtocol.java', 'WebhookReceiptFixture.java')
    {
        $null = & docker cp (Join-Path $PSScriptRoot $source) "${id}:/tmp/$source"
        if ($LASTEXITCODE) { throw 'Could not stage the isolated webhook receiver.' }
    }
    $null = & docker start $id
    if ($LASTEXITCODE) { throw 'Starting the isolated webhook receiver failed.' }
    Wait-SyncWebhook $id
    $pin = & docker inspect --format '{{.Config.Image}}' $id
    $binding = & docker port $id '8099/tcp'
    if ($LASTEXITCODE -or $pin -cne $Image -or $binding -cnotmatch '^127\.0\.0\.1:[0-9]+$')
    { throw 'The webhook fixture differs from its pinned runtime or loopback binding.' }
    $env:BLUETUSK_BENCHMARK_WEBHOOK_ENDPOINT = if (Test-Path -LiteralPath '/.dockerenv') { 'http://webhooks:8099/delivery' }
        else { "http://$binding/delivery" }
    [pscustomobject]@{ Id = $id; Image = $Image }
}

function Assert-SyncWebhookDelivery([string] $Source, [object] $Receiver, [int] $Changes, [int] $Transactions)
{
    $sourceTransactions = @(Get-SyncSourceTransactions $Source $Changes $Transactions | ForEach-Object { "$($_.Id):$($_.CommitMicros)" })
    $null = & docker kill --signal KILL $Receiver.Id
    if ($LASTEXITCODE) { throw 'Stopping the isolated webhook receiver without a normal shutdown failed.' }
    $null = & docker start $Receiver.Id
    if ($LASTEXITCODE) { throw 'The webhook receiver did not restart with its durable receipts.' }
    Wait-SyncWebhook $Receiver.Id
    $proof = & docker exec $Receiver.Id java -cp '/tmp/sync-webhook-classes:/debezium/lib/*' WebhookReceiptFixture `
        verify $Changes ($sourceTransactions -join ',')
    if ($LASTEXITCODE) { throw 'Recovered webhook evidence did not match the complete signed transaction sequence.' }
    $proof | ConvertFrom-Json
}
