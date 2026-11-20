function Resolve-SyncS3Image([string] $Image, [string] $RunId)
{
    if (-not $Image)
    {
        $Image = & (Join-Path $PSScriptRoot '../build-minio-endurance-fixture.ps1') `
            -OutputDirectory "artifacts/minio-sync/$RunId" -ImageTag "bluetusk/benchmark-minio:$RunId"
    }
    if ($Image -cnotmatch '^bluetusk/(benchmark|endurance)-minio:[a-z0-9-]+@sha256:[0-9a-f]{64}$')
    { throw 'The isolated S3 fixture requires the digest-addressed MinIO build.' }
    $binary = & docker run --rm --entrypoint /usr/bin/sha256sum $Image /usr/local/bin/minio
    if ($LASTEXITCODE -or $binary -cnotmatch '^7c5bd8512c6e966455b1d198209358b2d191c77a83ab377c4073281065fb855f\s')
    { throw 'The S3 fixture does not contain the pinned MinIO binary.' }
    $Image
}

function Get-SyncS3Endpoint([string] $Id)
{
    if (Test-Path -LiteralPath '/.dockerenv') { return 'http://s3:9000' }
    $bound = & docker port $Id '9000/tcp'
    if ($LASTEXITCODE -or $bound -cnotmatch '^127\.0\.0\.1:[0-9]+$') { throw 'The S3 fixture must bind only to loopback.' }
    "http://$bound"
}

function Wait-SyncS3([string] $Endpoint)
{
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(40)
    do
    {
        try
        {
            $response = Invoke-WebRequest "$Endpoint/minio/health/ready" -TimeoutSec 2 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { return }
        }
        catch [Net.Http.HttpRequestException] { }
        catch [Threading.Tasks.TaskCanceledException] { }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The isolated S3 destination did not become ready.'
}

function Get-SyncS3Environment
{
    $values = @{}
    foreach ($name in 'BLUETUSK_BENCHMARK_S3_ENDPOINT','BLUETUSK_BENCHMARK_S3_ACCESS_KEY','BLUETUSK_BENCHMARK_S3_SECRET_KEY')
    { $values[$name] = [Environment]::GetEnvironmentVariable($name) }
    $values
}

function Restore-SyncS3Environment([hashtable] $Values)
{
    foreach ($entry in $Values.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
}

function New-SyncS3Fixture([string] $Network, [string] $FixtureRun, [string] $FixtureOwner, [string] $Commit, [string] $Image,
    [string] $DotNet, [string] $Assembly)
{
    $imageReference = Resolve-SyncS3Image $Image $FixtureRun
    $name = "bt-perf-$FixtureRun-s3"
    $access = 'fixture-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    $secret = [Guid]::NewGuid().ToString('N')
    $oldAccess = $env:MINIO_ROOT_USER
    $oldSecret = $env:MINIO_ROOT_PASSWORD
    try
    {
        $env:MINIO_ROOT_USER = $access
        $env:MINIO_ROOT_PASSWORD = $secret
        $id = & docker run --detach --name $name --network $Network --network-alias s3 --publish '127.0.0.1::9000' `
            --label "bluetusk.owner=$FixtureOwner" --label "bluetusk.run=$FixtureRun" --label "bluetusk.commit=$Commit" `
            --env MINIO_ROOT_USER --env MINIO_ROOT_PASSWORD $imageReference server /data
        if ($LASTEXITCODE -ne 0 -or $id -cnotmatch '^[0-9a-f]{64}$') { throw 'Starting the isolated S3 destination failed.' }
    }
    finally
    {
        $env:MINIO_ROOT_USER = $oldAccess
        $env:MINIO_ROOT_PASSWORD = $oldSecret
    }
    $pin = & docker inspect --format '{{.Config.Image}}' $id
    if ($LASTEXITCODE -or $pin -cne $imageReference) { throw 'The running S3 destination does not match its pinned image.' }
    $endpoint = Get-SyncS3Endpoint $id
    Wait-SyncS3 $endpoint
    $env:BLUETUSK_BENCHMARK_S3_ENDPOINT = $endpoint
    $env:BLUETUSK_BENCHMARK_S3_ACCESS_KEY = $access
    $env:BLUETUSK_BENCHMARK_S3_SECRET_KEY = $secret
    $null = & $DotNet $Assembly --sync-s3-create-bucket
    if ($LASTEXITCODE) { throw 'Creating the isolated S3 bucket failed.' }
    [pscustomobject]@{ Id = $id; Image = $imageReference }
}

function Get-SyncS3ReferenceLibraries([string] $Directory)
{
    $lockPath = Join-Path $PSScriptRoot 's3-reference-libraries.json'
    $locked = [IO.File]::ReadAllText($lockPath).Replace("`r`n", "`n")
    $libraries = @($locked | ConvertFrom-Json)
    $null = New-Item -ItemType Directory -Path $Directory -Force
    $files = foreach ($library in $libraries)
    {
        if ($library.path -cnotmatch '^org/(apache/(hadoop|parquet)|locationtech/jts)/[a-z0-9-]+/[0-9.]+/[a-z0-9.-]+\.jar$' -or
            $library.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid S3 reference library in the committed lock.' }
        $jar = Join-Path $Directory ([IO.Path]::GetFileName($library.path))
        if (-not (Test-Path -LiteralPath $jar))
        {
            $temporary = "$jar.$([Guid]::NewGuid().ToString('N')).tmp"
            try
            {
                Invoke-WebRequest "https://repo.maven.apache.org/maven2/$($library.path)" -OutFile $temporary -TimeoutSec 120
                if ((Get-FileHash -LiteralPath $temporary).Hash.ToLowerInvariant() -cne $library.sha256)
                { throw "Invalid SHA-256 for downloaded S3 reference library $([IO.Path]::GetFileName($jar))." }
                try { [IO.File]::Move($temporary, $jar) }
                catch [IO.IOException] { if (-not [IO.File]::Exists($jar)) { throw } }
            }
            finally { [IO.File]::Delete($temporary) }
        }
        if ((Get-FileHash -LiteralPath $jar).Hash.ToLowerInvariant() -cne $library.sha256)
        { throw "Invalid SHA-256 for cached S3 reference library $([IO.Path]::GetFileName($jar))." }
        $jar
    }
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($locked))).ToLowerInvariant()
    [pscustomobject]@{ Files = @($files); LockSha256 = $digest }
}

function Assert-SyncS3Delivery([string] $Source, [object] $Store, [int] $Changes, [int] $Transactions, [string] $DotNet, [string] $Assembly)
{
    $sourceTransactions = @(Get-SyncSourceTransactions $Source $Changes $Transactions | ForEach-Object { "$($_.Id):$($_.CommitMicros)" })
    $null = & docker kill --signal KILL $Store.Id
    if ($LASTEXITCODE) { throw 'Stopping the isolated S3 object store without a clean shutdown failed.' }
    $null = & docker start $Store.Id
    if ($LASTEXITCODE) { throw 'The S3 object store did not restart from the same data.' }
    $env:BLUETUSK_BENCHMARK_S3_ENDPOINT = Get-SyncS3Endpoint $Store.Id
    Wait-SyncS3 $env:BLUETUSK_BENCHMARK_S3_ENDPOINT
    $proof = & $DotNet $Assembly --sync-s3-verify $Changes ($sourceTransactions -join ',')
    if ($LASTEXITCODE) { throw 'Recovered S3 evidence failed Parquet and commit manifest verification.' }
    $proof | ConvertFrom-Json
}
