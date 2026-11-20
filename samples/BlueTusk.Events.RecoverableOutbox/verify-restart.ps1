[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RecipeDll
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$recipe = (Get-Item -LiteralPath $RecipeDll).FullName
if (-not $recipe.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Supply the compiled recipe DLL.'
}
if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_OUTBOX_CONNECTION_STRING)) {
    throw 'Set BLUETUSK_OUTBOX_CONNECTION_STRING to an empty disposable database.'
}

function Invoke-Recipe {
    param([string[]] $RecipeArguments, [int] $ExpectedExit = 0)

    $output = & dotnet $recipe @RecipeArguments 2>&1
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "Recipe command '$($RecipeArguments[0])' exited $LASTEXITCODE. Expected $ExpectedExit."
    }
    return $output
}

Invoke-Recipe -RecipeArguments @('init') | Out-Null
$before = [uint64](Invoke-Recipe -RecipeArguments @('checkpoint'))
if ($before -ne 0) { throw 'Expected an empty checkpoint at initialization.' }

Invoke-Recipe -RecipeArguments @('publish', [guid]::NewGuid().ToString('D')) | Out-Null
Invoke-Recipe -RecipeArguments @('consume', '--fail-before-checkpoint') -ExpectedExit 75 | Out-Null
$interrupted = [uint64](Invoke-Recipe -RecipeArguments @('checkpoint'))
if ($interrupted -ne $before) { throw 'Interrupted acknowledgment advanced the durable checkpoint.' }

$replay = Invoke-Recipe -RecipeArguments @('consume')
if ($replay -cne 'Events received 1, effects committed 0.') {
    throw 'The restarted consumer must receive the interrupted event without repeating its effect.'
}
$acknowledged = [uint64](Invoke-Recipe -RecipeArguments @('checkpoint'))
if ($acknowledged -le $interrupted) { throw 'Replay did not advance the durable checkpoint.' }

Invoke-Recipe -RecipeArguments @('publish', [guid]::NewGuid().ToString('D')) | Out-Null
$next = Invoke-Recipe -RecipeArguments @('consume')
if ($next -cne 'Events received 1, effects committed 1.') {
    throw 'The consumer must apply the next event after restart.'
}
$resumed = [uint64](Invoke-Recipe -RecipeArguments @('checkpoint'))
if ($resumed -le $acknowledged) { throw 'The next event did not advance the durable checkpoint.' }

[pscustomobject]@{
    CheckpointAfterInterruption = $interrupted
    CheckpointAfterReplay = $acknowledged
    CheckpointAfterNextEvent = $resumed
}
