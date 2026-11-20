[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [string] $DotNetPath = 'dotnet',
    [string] $OutputPath,
    [ValidateRange(3, 50)][int] $Trials = 3,
    [ValidateRange(1, 60)][int] $WarmupSeconds = 5,
    [ValidateRange(1, 60)][int] $MeasurementSeconds = 10,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{2,40}$')][string] $RunId = ('provider-primary-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)),
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner = 'provider-primary-probe',
    [switch] $Qualification
)

& (Join-Path $PSScriptRoot 'capture-core-primary.ps1') -Family Provider @PSBoundParameters
