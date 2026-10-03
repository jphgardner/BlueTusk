[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{2,40}$')][string] $RunId,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
# Remove only containers that carry BOTH this owner and this run id; never anyone else's fixtures.
$ids = @(& docker ps --all --quiet --filter "label=bluetusk.owner=$Owner" --filter "label=bluetusk.run=$RunId")
foreach ($id in $ids) { if ($id) { & docker rm --force --volumes $id *> $null } }
$network = "bt-perf-$RunId"
$owned = (& docker network ls --quiet --filter "name=^$network$" --filter "label=bluetusk.owner=$Owner" --filter "label=bluetusk.run=$RunId")
if ($owned)
{
    if (Test-Path -LiteralPath '/.dockerenv') { & docker network disconnect --force $network ([Net.Dns]::GetHostName()) *> $null }
    & docker network rm $network *> $null
}
Write-Output "Removed $(@($ids | Where-Object { $_ }).Count) fixture container(s) for run $RunId (owner $Owner)."
