[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputPath,
    # Extra server names, such as a Docker network alias used by a containerized client.
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{0,62}$')][string[]] $DnsName = @('localhost'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
$allowed = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output))
{
    throw 'TLS test material must use a new directory beneath repository artifacts.'
}
$null = New-Item -ItemType Directory -Path $output
$caKey = [Security.Cryptography.RSA]::Create(2048)
$serverKey = [Security.Cryptography.RSA]::Create(2048)
$ca = $null
$server = $null
try
{
    $hash = [Security.Cryptography.HashAlgorithmName]::SHA256
    $padding = [Security.Cryptography.RSASignaturePadding]::Pkcs1
    $caRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=BlueTusk disposable benchmark CA', $caKey, $hash, $padding)
    $caRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    $caRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign, $true))
    $before = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $after = [DateTimeOffset]::UtcNow.AddDays(2)
    $ca = $caRequest.CreateSelfSigned($before, $after)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=localhost', $serverKey, $hash, $padding)
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    foreach ($name in (@('localhost') + $DnsName | Select-Object -Unique)) { $san.AddDnsName($name) }
    $san.AddIpAddress([Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($san.Build())
    $eku = [Security.Cryptography.OidCollection]::new()
    $null = $eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $true))
    $serial = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $server = $request.Create($ca, $before, $after, $serial)
    $ca.ExportCertificatePem() | Set-Content -LiteralPath (Join-Path $output 'ca.pem') -Encoding utf8NoBOM
    $server.ExportCertificatePem() | Set-Content -LiteralPath (Join-Path $output 'server.pem') -Encoding utf8NoBOM
    $serverKey.ExportPkcs8PrivateKeyPem() | Set-Content -LiteralPath (Join-Path $output 'server.key') -Encoding utf8NoBOM
    Write-Output "Created disposable localhost TLS fixtures in '$output'; no system trust store was changed."
}
finally
{
    if ($null -ne $server) { $server.Dispose() }
    if ($null -ne $ca) { $ca.Dispose() }
    $serverKey.Dispose()
    $caKey.Dispose()
}
