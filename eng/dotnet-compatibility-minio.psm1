Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Start-DotNetCompatibilityMinio
{
    param([object] $Contract, [string] $WorkDirectory)

    New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
    $accessKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(12))
    $secretKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $fixture = @{
        process = $null
        accessKey = $accessKey; secretKey = $secretKey
        identity = $null; version = $Contract.version
    }
    $previousUser = $env:MINIO_ROOT_USER
    $previousPassword = $env:MINIO_ROOT_PASSWORD
    try
    {
        $env:MINIO_ROOT_USER = $accessKey
        $env:MINIO_ROOT_PASSWORD = $secretKey
        $distribution = if ($IsWindows) { $Contract.windowsBinary } else { $Contract.linuxBinary }
        $binary = Join-Path $WorkDirectory $(if ($IsWindows) { 'minio.exe' } else { 'minio' })
        Invoke-WebRequest -Uri $distribution.url -OutFile $binary
        if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ine $distribution.sha256)
        { throw 'MinIO executable did not match its pinned SHA256.' }
        if ($IsLinux) { [IO.File]::SetUnixFileMode($binary, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
        $versionInfo = & $binary --version
        if ($LASTEXITCODE -ne 0 -or ($versionInfo -join "`n") -cnotmatch ('\b' + [regex]::Escape($Contract.version) + '\b'))
        { throw "S3 compatibility requires MinIO $($Contract.version)." }
        $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $socket.Start()
        try { $port = $socket.LocalEndpoint.Port }
        finally { $socket.Stop() }
        $data = Join-Path $WorkDirectory 'data'
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        $start = @{
            FilePath = $binary
            ArgumentList = "server `"$data`" --address 127.0.0.1:$port --console-address 127.0.0.1:0"
            PassThru = $true
            RedirectStandardOutput = Join-Path $WorkDirectory 'server.log'
            RedirectStandardError = Join-Path $WorkDirectory 'server-error.log'
        }
        if ($IsWindows) { $start.WindowStyle = 'Hidden' }
        $fixture.process = Start-Process @start
        $fixture.identity = 'sha256:' + $distribution.sha256
        $fixture.endpoint = 'http://127.0.0.1:' + $port
        for ($attempt = 0; $attempt -lt 30; $attempt++)
        {
            try
            {
                $response = Invoke-WebRequest -Uri ($fixture.endpoint + '/minio/health/ready') -TimeoutSec 2
                if ($response.StatusCode -eq 200) { return $fixture }
            }
            catch { Start-Sleep -Seconds 1 }
        }
        throw 'MinIO compatibility server did not become ready.'
    }
    catch
    {
        Stop-DotNetCompatibilityMinio $fixture
        throw
    }
    finally
    {
        $env:MINIO_ROOT_USER = $previousUser
        $env:MINIO_ROOT_PASSWORD = $previousPassword
    }
}

function Stop-DotNetCompatibilityMinio
{
    param([hashtable] $Fixture)
    if ($Fixture.process -and -not $Fixture.process.HasExited)
    {
        $Fixture.process.Kill($true)
        $Fixture.process.WaitForExit()
    }
}

Export-ModuleMember -Function Start-DotNetCompatibilityMinio, Stop-DotNetCompatibilityMinio
