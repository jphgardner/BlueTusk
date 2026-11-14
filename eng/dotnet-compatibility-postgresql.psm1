Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Start-DotNetCompatibilityPostgreSql
{
    param(
        [Parameter(Mandatory)] [object] $Contract,
        [Parameter(Mandatory)] [string] $WorkDirectory,
        [Parameter(Mandatory)] [string] $DownloadCache,
        [string] $Extension,
        [switch] $WithPgVector,
        [switch] $UseDocker
    )

    New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $database = 'bluetusk_compatibility'
    $versionParts = $Contract.version.Split('.')
    $expectedVersionNumber = ([int]$versionParts[0] * 10000) + [int]$versionParts[1]
    $fixture = @{
        container = $null
        control = $null
        dataDirectory = $null
        version = $Contract.version
        connection = $null
    }
    try
    {
        if ($UseDocker)
        {
            $image = if ($WithPgVector) { $Contract.pgVector.linuxImage } else { $Contract.linuxImage }
            $fixture.container = 'bluetusk-dotnet-' + [guid]::NewGuid().ToString('N')
            $previousPassword = $env:POSTGRES_PASSWORD
            try
            {
                $env:POSTGRES_PASSWORD = $password
                & docker run --detach --rm --name $fixture.container --publish '127.0.0.1::5432' `
                    --env POSTGRES_PASSWORD --env "POSTGRES_DB=$database" $image | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not start the PostgreSQL compatibility container.' }
            }
            finally { $env:POSTGRES_PASSWORD = $previousPassword }

            $mapping = (& docker port $fixture.container 5432/tcp | Select-Object -First 1)
            if ($LASTEXITCODE -ne 0 -or $mapping -cnotmatch '^127\.0\.0\.1:(\d+)$')
            { throw 'PostgreSQL compatibility container has no loopback port.' }
            $port = [int]$Matches[1]
            $version = $null
            for ($attempt = 0; $attempt -lt 45; $attempt++)
            {
                # The official image starts a temporary Unix socket server for
                # initialization. Checking TCP waits for the final service.
                & docker exec $fixture.container pg_isready -q -h 127.0.0.1 -U postgres -d $database
                if ($LASTEXITCODE -eq 0)
                {
                    $version = (& docker exec $fixture.container psql -X -U postgres -d $database -Atc 'SHOW server_version_num')
                    if ($LASTEXITCODE -eq 0) { break }
                }
                Start-Sleep -Seconds 1
            }
            $fixture.identity = $image
        }
        else
        {
            New-Item -ItemType Directory -Path $DownloadCache -Force | Out-Null
            $archiveName = [IO.Path]::GetFileName(([uri]$Contract.windowsArchive.url).AbsolutePath)
            $archive = Join-Path $DownloadCache $archiveName
            if (-not (Test-Path -LiteralPath $archive))
            {
                $download = Join-Path $WorkDirectory $archiveName
                Invoke-WebRequest -Uri $Contract.windowsArchive.url -OutFile $download
                if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ine $Contract.windowsArchive.sha256)
                { throw 'PostgreSQL Windows archive did not match its pinned SHA256.' }
                Move-Item -LiteralPath $download -Destination $archive -Force
            }
            if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $Contract.windowsArchive.sha256)
            { throw 'PostgreSQL Windows archive did not match its pinned SHA256.' }
            $archiveEntries = @('pgsql/bin', 'pgsql/lib', 'pgsql/share')
            if ($WithPgVector) { $archiveEntries += 'pgsql/include' }
            & tar -xf $archive -C $WorkDirectory @archiveEntries
            if ($LASTEXITCODE -ne 0) { throw 'Could not extract the verified PostgreSQL Windows binaries.' }
            $bin = Join-Path $WorkDirectory 'pgsql/bin'
            if ($WithPgVector)
            {
                Install-DotNetCompatibilityPgVector -Contract $Contract.pgVector -WorkDirectory $WorkDirectory `
                    -PostgreSqlRoot (Join-Path $WorkDirectory 'pgsql')
            }
            if ($Extension)
            {
                Install-DotNetCompatibilityPostgreSqlExtension -Contract $Contract -Extension $Extension -WorkDirectory $WorkDirectory `
                    -PostgreSqlRoot (Join-Path $WorkDirectory 'pgsql') -DownloadCache $DownloadCache
            }
            $fixture.control = Join-Path $bin 'pg_ctl.exe'
            $fixture.dataDirectory = Join-Path $WorkDirectory 'data'
            $passwordFile = Join-Path $WorkDirectory 'password.txt'
            try
            {
                [IO.File]::WriteAllText($passwordFile, $password + [Environment]::NewLine)
                & (Join-Path $bin 'initdb.exe') --pgdata $fixture.dataDirectory --username postgres `
                    --auth scram-sha-256 --pwfile $passwordFile --encoding UTF8 --locale C *> (Join-Path $WorkDirectory 'initdb.log')
                if ($LASTEXITCODE -ne 0) { throw 'Could not initialize PostgreSQL for .NET compatibility.' }
            }
            finally { if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile } }
            if ($Extension -ceq 'timescaledb')
            {
                Add-Content -LiteralPath (Join-Path $fixture.dataDirectory 'postgresql.conf') -Value "shared_preload_libraries = 'timescaledb'" -Encoding ascii
            }

            $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $socket.Start()
            try { $port = $socket.LocalEndpoint.Port }
            finally { $socket.Stop() }
            $serverLog = Join-Path $WorkDirectory 'server.log'
            $startArguments = "start -w -t 45 -D `"$($fixture.dataDirectory)`" -l `"$serverLog`" -o `"-h 127.0.0.1 -p $port`""
            $startup = Start-Process -FilePath $fixture.control -ArgumentList $startArguments -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $WorkDirectory 'startup.log') `
                -RedirectStandardError (Join-Path $WorkDirectory 'startup-error.log')
            # Wait only for pg_ctl. The server must continue in the background.
            $startup.WaitForExit()
            if ($startup.ExitCode -ne 0) { throw 'Could not start PostgreSQL for .NET compatibility.' }

            $previousPassword = $env:PGPASSWORD
            try
            {
                $env:PGPASSWORD = $password
                & (Join-Path $bin 'createdb.exe') -h 127.0.0.1 -p $port -U postgres $database | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not create the PostgreSQL compatibility database.' }
                $version = (& (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d $database -X -Atc 'SHOW server_version_num')
                if ($LASTEXITCODE -ne 0) { throw 'Could not verify the PostgreSQL compatibility server version.' }
            }
            finally { $env:PGPASSWORD = $previousPassword }
            $fixture.identity = 'sha256:' + $Contract.windowsArchive.sha256
        }
        if (([string]$version).Trim() -cne [string]$expectedVersionNumber)
        { throw "PostgreSQL compatibility requires server $($Contract.version)." }
        if ($WithPgVector -or $Extension)
        {
            $extensionName = if ($WithPgVector) { 'vector' } else { $Extension }
            if ($extensionName -cnotin @('vector', 'postgis', 'timescaledb')) { throw 'Unknown PostgreSQL compatibility extension.' }
            $extensionVersion = if ($WithPgVector) { $Contract.pgVector.version } else { $Contract.extensionVersion }
            $installSql = "CREATE EXTENSION IF NOT EXISTS $extensionName"
            $versionSql = "SELECT extversion FROM pg_extension WHERE extname = '$extensionName'"
            if ($UseDocker)
            {
                & docker exec $fixture.container psql -X -U postgres -d $database -v ON_ERROR_STOP=1 -c $installSql | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Could not enable $extensionName in the compatibility database." }
                $actualVersion = (& docker exec $fixture.container psql -X -U postgres -d $database -Atc $versionSql)
                $fixture.extensionIdentity = $image
            }
            else
            {
                $previousPassword = $env:PGPASSWORD
                try
                {
                    $env:PGPASSWORD = $password
                    $psql = Join-Path $bin 'psql.exe'
                    & $psql -h 127.0.0.1 -p $port -U postgres -d $database -X -v ON_ERROR_STOP=1 -c $installSql | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw "Could not enable $extensionName in the compatibility database." }
                    $actualVersion = (& $psql -h 127.0.0.1 -p $port -U postgres -d $database -X -Atc $versionSql)
                }
                finally { $env:PGPASSWORD = $previousPassword }
                $fixture.extensionIdentity = 'sha256:' + $(if ($WithPgVector) { $Contract.pgVector.windowsSource.sha256 }
                    else { $Contract.windowsExtensionArchive.sha256 })
            }
            if ($LASTEXITCODE -ne 0 -or ([string]$actualVersion).Trim() -cne $extensionVersion)
            { throw "PostgreSQL compatibility requires $extensionName $extensionVersion." }
            $fixture.extensionVersion = $extensionVersion
        }
        $fixture.connection = "Host=127.0.0.1;Port=$port;Username=postgres;Password=$password;Database=$database;SSL Mode=Disable;Channel Binding=Disable"
        if ($UseDocker) { $fixture.containerConnection = $fixture.connection.Replace("Port=$port;", 'Port=5432;') }
        return $fixture
    }
    catch
    {
        Stop-DotNetCompatibilityPostgreSql $fixture
        throw
    }
}

function Install-DotNetCompatibilityPostgreSqlExtension
{
    param([object] $Contract, [string] $Extension, [string] $WorkDirectory, [string] $PostgreSqlRoot, [string] $DownloadCache)

    $source = $Contract.windowsExtensionArchive
    $name = [IO.Path]::GetFileName(([uri]$source.url).AbsolutePath)
    $archive = Join-Path $DownloadCache "$Extension-$($Contract.extensionVersion)-$name"
    if (-not (Test-Path -LiteralPath $archive))
    {
        $download = Join-Path $WorkDirectory $name
        Invoke-WebRequest -Uri $source.url -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ine $source.sha256)
        { throw "$Extension archive did not match its pinned SHA256." }
        Move-Item -LiteralPath $download -Destination $archive -Force
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $source.sha256)
    { throw "$Extension archive did not match its pinned SHA256." }
    $staging = Join-Path $WorkDirectory $Extension
    Expand-Archive -LiteralPath $archive -DestinationPath $staging
    if ($Extension -ceq 'postgis')
    {
        $roots = @(Get-ChildItem -LiteralPath $staging -Directory -Recurse | Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'lib/postgis-3.dll')) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'share/extension/postgis.control'))
        })
        if ((Test-Path -LiteralPath (Join-Path $staging 'lib/postgis-3.dll')) -and
            (Test-Path -LiteralPath (Join-Path $staging 'share/extension/postgis.control')))
        { $roots += Get-Item -LiteralPath $staging }
        if ($roots.Count -ne 1) { throw 'Could not locate the pinned PostGIS bundle layout.' }
        foreach ($directory in 'bin', 'lib', 'share')
        {
            Get-ChildItem -LiteralPath (Join-Path $roots[0].FullName $directory) |
                Copy-Item -Destination (Join-Path $PostgreSqlRoot $directory) -Recurse -Force
        }
    }
    else
    {
        $files = @(Get-ChildItem -LiteralPath $staging -Recurse -File)
        $dlls = @($files | Where-Object { $_.Name -cmatch '^timescaledb.*\.dll$' })
        $sql = @($files | Where-Object { $_.Name -cmatch '^timescaledb(?:--.+\.sql|\.control)$' })
        $dllNames = @($dlls | ForEach-Object Name)
        $sqlNames = @($sql | ForEach-Object Name)
        if ('timescaledb.dll' -cnotin $dllNames -or 'timescaledb.control' -cnotin $sqlNames -or
            "timescaledb--$($Contract.extensionVersion).sql" -cnotin $sqlNames)
        { throw 'Could not locate the pinned TimescaleDB archive layout.' }
        $dlls | Copy-Item -Destination (Join-Path $PostgreSqlRoot 'lib') -Force
        $sql | Copy-Item -Destination (Join-Path $PostgreSqlRoot 'share/extension') -Force
    }
}

function Install-DotNetCompatibilityPgVector
{
    param([object] $Contract, [string] $WorkDirectory, [string] $PostgreSqlRoot)

    $archive = Join-Path $WorkDirectory 'pgvector-source.tar.gz'
    Invoke-WebRequest -Uri $Contract.windowsSource.url -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $Contract.windowsSource.sha256)
    { throw 'pgvector source did not match its pinned SHA256.' }
    $buildDirectory = Join-Path $WorkDirectory 'pgvector'
    New-Item -ItemType Directory -Path $buildDirectory | Out-Null
    & tar -xzf $archive --strip-components=1 -C $buildDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the verified pgvector source.' }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $visualStudio = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($visualStudio))
    { throw 'The x64 Visual C++ tools are required to build pgvector on Windows.' }
    $developerCommand = Join-Path $visualStudio 'VC/Auxiliary/Build/vcvars64.bat'
    $batch = Join-Path $buildDirectory 'build-pgvector.cmd'
    @('@echo off', "call `"$developerCommand`"", 'if errorlevel 1 exit /b 1',
        "set `"PGROOT=$PostgreSqlRoot`"", 'nmake /nologo /f Makefile.win install') | Set-Content -LiteralPath $batch -Encoding ascii
    Push-Location -LiteralPath $buildDirectory
    try
    {
        & cmd.exe /d /c build-pgvector.cmd *> (Join-Path $WorkDirectory 'pgvector-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Building pgvector failed. See pgvector-build.log in the fixture directory.' }
    }
    finally { Pop-Location }
}

function Stop-DotNetCompatibilityPostgreSql
{
    param([Parameter(Mandatory)] [hashtable] $Fixture)

    if ($Fixture.container)
    {
        & docker stop --timeout 5 $Fixture.container *> $null
    }
    elseif ($Fixture.control -and (Test-Path -LiteralPath (Join-Path $Fixture.dataDirectory 'postmaster.pid')))
    {
        & $Fixture.control stop -w -t 15 -m fast -D $Fixture.dataDirectory *> $null
    }
}

Export-ModuleMember -Function Start-DotNetCompatibilityPostgreSql, Stop-DotNetCompatibilityPostgreSql
