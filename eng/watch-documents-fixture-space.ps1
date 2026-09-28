param(
    [Parameter(Mandatory)][string]$Container,
    [Parameter(Mandatory)][string]$Fixture,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$StopFile,
    [ValidateRange(30,24000)][int]$MaximumSeconds = 1800
)
$ErrorActionPreference = 'Stop'
$owner = 'bluetusk.documents.load'
$dockerCommand = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$started = [Diagnostics.Stopwatch]::StartNew()
$minimumAvailableKiB = [long]::MaxValue
$maximumUsedKiB = 0L
$outputParent = Split-Path -Path $Output -Parent
$stage = 'startup'
$lastPublishExceptionType = $null
$lastPublishHResult = $null
$sampleIndex = 0
New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
try {
    if (Test-Path -LiteralPath $Output) { throw 'owned-filesystem-sample-directory-exists' }
    New-Item -ItemType Directory -Path $Output | Out-Null
    while (-not (Test-Path -LiteralPath $StopFile -PathType Leaf) -and $started.Elapsed.TotalSeconds -lt $MaximumSeconds) {
        $stage = 'container-label'
        $labelsJson = & $dockerCommand container inspect $Container --format '{{json .Config.Labels}}' 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'owned-container-unavailable' }
        $labels = $labelsJson | ConvertFrom-Json
        if ($labels.'bluetusk.owner' -ne $owner -or $labels.'bluetusk.fixture' -ne $Fixture) { throw 'owned-container-label-mismatch' }
        $stage = 'filesystem-stat'
        $df = @(& $dockerCommand exec $Container df -Pk /var/lib/postgresql/data/pgdata 2>$null)
        if ($LASTEXITCODE -ne 0 -or $df.Count -ne 2) { throw 'owned-filesystem-stat-unavailable' }
        $fields = @($df[-1].Trim() -split '\s+')
        if ($fields.Count -lt 6) { throw 'owned-filesystem-stat-invalid' }
        $capacityKiB = [long]::Parse($fields[1], [Globalization.CultureInfo]::InvariantCulture)
        $usedKiB = [long]::Parse($fields[2], [Globalization.CultureInfo]::InvariantCulture)
        $availableKiB = [long]::Parse($fields[3], [Globalization.CultureInfo]::InvariantCulture)
        if ($capacityKiB -le 0 -or $usedKiB -lt 0 -or $availableKiB -lt 0 -or $usedKiB -gt $capacityKiB -or $availableKiB -gt $capacityKiB) {
            throw 'owned-filesystem-stat-out-of-range'
        }
        if ($capacityKiB -gt 9000000000000000) { throw 'owned-filesystem-stat-out-of-range' }
        $minimumAvailableKiB = [Math]::Min($minimumAvailableKiB, $availableKiB)
        $maximumUsedKiB = [Math]::Max($maximumUsedKiB, $usedKiB)
        $observation = [ordered]@{
            ObservedUtc = [DateTimeOffset]::UtcNow.ToString('o')
            CapacityBytes = [long]($capacityKiB * 1024L)
            UsedBytes = [long]($usedKiB * 1024L)
            AvailableBytes = [long]($availableKiB * 1024L)
            MinimumAvailableBytesObserved = [long]($minimumAvailableKiB * 1024L)
            MaximumUsedBytesObserved = [long]($maximumUsedKiB * 1024L)
        } | ConvertTo-Json -Compress
        if ($sampleIndex -ge 24000) { throw 'owned-filesystem-sample-count-exceeded' }
        $sampleIndex++
        $stage = 'sample-write'
        $temporary = Join-Path $Output ('{0:D6}.{1}.tmp' -f $sampleIndex, $PID)
        $completed = Join-Path $Output ('{0:D6}.json' -f $sampleIndex)
        [IO.File]::WriteAllText($temporary, $observation, [Text.UTF8Encoding]::new($false))
        $published = $false
        $stage = 'sample-publish'
        for ($attempt = 0; $attempt -lt 10 -and -not $published; $attempt++) {
            try {
                [IO.File]::Move($temporary, $completed)
                $published = $true
            }
            catch {
                $underlying = if ($null -ne $_.Exception.InnerException) { $_.Exception.InnerException } else { $_.Exception }
                if ($underlying -isnot [IO.IOException] -and $underlying -isnot [System.UnauthorizedAccessException]) { throw }
                $lastPublishExceptionType = $underlying.GetType().Name
                $lastPublishHResult = $underlying.HResult
                Start-Sleep -Milliseconds 50
            }
        }
        if (-not $published) { throw 'owned-filesystem-observation-atomic-publish-failed' }
        # Keep a bounded recent window. Each observation carries lifetime minimum
        # headroom and maximum use, while the workload report retains the physical
        # time series. The reader always selects the highest completed sample ID.
        if ($sampleIndex -gt 64) {
            [IO.File]::Delete((Join-Path $Output ('{0:D6}.json' -f ($sampleIndex - 64))))
        }
        Start-Sleep -Seconds 1
    }
}
catch {
    $failure = [ordered]@{
        Code = 'owned-filesystem-observer-stopped'
        ObservedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Stage = $stage
        ExceptionType = $_.Exception.GetType().Name
        HResult = $_.Exception.HResult
        LastPublishExceptionType = $lastPublishExceptionType
        LastPublishHResult = $lastPublishHResult
        PublishedSampleCount = $sampleIndex - 1
    } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText("$Output.error.json", $failure, [Text.UTF8Encoding]::new($false))
    exit 1
}
