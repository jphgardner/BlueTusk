function Wait-SyncOpenSearch([string] $Name)
{
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do
    {
        $response = & docker exec $Name curl --silent --fail --max-time 4 'http://127.0.0.1:9200/_cluster/health?wait_for_status=yellow&timeout=2s' 2>$null
        if ($LASTEXITCODE -eq 0)
        {
            $health = $response | ConvertFrom-Json
            if ($health.timed_out -eq $false -and $health.status -cin @('yellow', 'green')) { return }
        }
        Start-Sleep -Milliseconds 400
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    & docker logs --tail 20 $Name
    throw 'The isolated OpenSearch destination did not become ready.'
}

function Get-SyncOpenSearchEndpoint([string] $ContainerId)
{
    if (Test-Path -LiteralPath '/.dockerenv') { return 'http://opensearch:9200/' }
    $binding = & docker port $ContainerId '9200/tcp'
    if ($LASTEXITCODE -or $binding -cnotmatch '^127\.0\.0\.1:[0-9]+$') { throw 'The OpenSearch fixture must bind only to loopback.' }
    return "http://$binding/"
}

function New-SyncOpenSearchFixture([string] $Network, [string] $FixtureRun, [string] $FixtureOwner, [string] $Commit, [string] $Image)
{
    if ($Image -cnotmatch '^opensearchproject/opensearch:3\.7\.0@sha256:[0-9a-f]{64}$') { throw 'The OpenSearch image must be digest pinned.' }
    $name = "bt-perf-$FixtureRun-opensearch"
    $id = & docker run --detach --name $name --network $Network --network-alias opensearch --publish '127.0.0.1::9200' `
        --label "bluetusk.owner=$FixtureOwner" --label "bluetusk.run=$FixtureRun" --label "bluetusk.commit=$Commit" `
        --env discovery.type=single-node --env DISABLE_SECURITY_PLUGIN=true --env DISABLE_INSTALL_DEMO_CONFIG=true `
        --env 'OPENSEARCH_JAVA_OPTS=-Xms1g -Xmx1g' $Image
    if ($LASTEXITCODE -ne 0 -or $id -cnotmatch '^[0-9a-f]{64}$') { throw 'Starting the isolated OpenSearch destination failed.' }
    Wait-SyncOpenSearch $name
    $pin = & docker inspect --format '{{.Config.Image}}' $id
    if ($LASTEXITCODE -or $pin -cne $Image) { throw 'The running OpenSearch destination does not match its pinned image.' }
    [pscustomobject]@{ Id = $id; Name = $name; Endpoint = (Get-SyncOpenSearchEndpoint $id); Image = $Image }
}

function Assert-SyncOpenSearchDelivery([string] $Source, [object] $Search, [int] $Changes, [int] $Transactions,
    [string] $DotNet, [string] $Assembly, [string] $Role)
{
    $sourceIds = (Get-SyncSourceTransactions $Source $Changes $Transactions).Id
    $null = & docker kill --signal KILL $Search.Id
    if ($LASTEXITCODE) { throw 'Stopping the isolated OpenSearch process without a normal shutdown failed.' }
    $null = & docker start $Search.Id
    if ($LASTEXITCODE) { throw 'OpenSearch did not restart with its own index data.' }
    Wait-SyncOpenSearch $Search.Name

    $previous = $env:BLUETUSK_BENCHMARK_OPENSEARCH_ENDPOINT
    try
    {
        $env:BLUETUSK_BENCHMARK_OPENSEARCH_ENDPOINT = Get-SyncOpenSearchEndpoint $Search.Id
        $proof = & $DotNet $Assembly --sync-opensearch-verify $Changes ($sourceIds -join ',') $Role
        if ($LASTEXITCODE -ne 0) { throw 'Recovered OpenSearch evidence failed document or checkpoint verification.' }
        return ($proof | ConvertFrom-Json)
    }
    finally { $env:BLUETUSK_BENCHMARK_OPENSEARCH_ENDPOINT = $previous }
}
