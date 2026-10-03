Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')

function ConvertTo-CoreGithubJobLogBinding
{
    param([Parameter(Mandatory)][object] $Job, [Parameter(Mandatory)][string] $LogPath,
        [Parameter(Mandatory)][long] $RunId, [Parameter(Mandatory)][int] $RunAttempt,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
        [Parameter(Mandatory)][object] $ArtifactMetadata,
        [Parameter(Mandatory)][string] $UploadStepName,
        [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk')

    # Pure mapping of retained API-shaped data and logs. Synthetic calls cannot
    # establish live GitHub identity. These observations bind a checkout action
    # and upload in one job; they do not certify fixtures or execution authenticity.
    function Require([bool] $Condition, [string] $Message)
    { if (-not $Condition) { throw $Message } }
    function Value([object] $Object, [string] $Name)
    {
        Require ($Object -is [pscustomobject]) 'Job log metadata must contain JSON objects.'
        $property = $Object.PSObject.Properties[$Name]
        Require ($null -ne $property) "Job log metadata is missing '$Name'."
        return ,$property.Value
    }
    function Utc([object] $Text)
    {
        Require ($Text -is [string] -and $Text -cmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') 'Job lifecycle requires explicit UTC timestamps.'
        $instant = [DateTimeOffset]::MinValue
        Require ([DateTimeOffset]::TryParse($Text, [ref]$instant)) 'Job lifecycle timestamp is invalid.'
        return $instant
    }
    Assert-CoreEvidenceInteger $RunId 'Requested run ID'
    Assert-CoreEvidenceInteger $RunAttempt 'Requested run attempt'
    $jobId = Value $Job 'id'
    Assert-CoreEvidenceInteger $jobId 'GitHub job ID'
    Assert-CoreEvidenceInteger (Value $Job 'run_id') 'GitHub job run ID'
    Assert-CoreEvidenceInteger (Value $Job 'run_attempt') 'GitHub job run attempt'
    Require ((Value $Job 'run_id') -ceq $RunId -and (Value $Job 'run_attempt') -ceq $RunAttempt -and
        (Value $Job 'head_sha') -ceq $ExpectedCommit -and
        (Value $Job 'html_url') -ceq "https://github.com/$ExpectedRepository/actions/runs/$RunId/job/$jobId") 'Job belongs to a different run, attempt, source or repository.'
    Require ((Value $Job 'status') -ceq 'completed' -and (Value $Job 'conclusion') -ceq 'success') 'The selected job must have actually succeeded.'
    $started = Utc (Value $Job 'started_at')
    $completed = Utc (Value $Job 'completed_at')
    Require ($completed -ge $started -and $completed -le [DateTimeOffset]::UtcNow) 'Job lifecycle is inconsistent or in the future.'
    $steps = Value $Job 'steps'
    Require ($steps -is [array] -and $steps.Count -gt 0) 'Complete job step metadata is required.'
    $numbers = [Collections.Generic.HashSet[long]]::new()
    foreach ($step in $steps)
    {
        $number = Value $step 'number'
        Assert-CoreEvidenceInteger $number 'Job step number'
        Require ($numbers.Add($number)) 'Job step numbers are duplicated.'
        $stepStart = Utc (Value $step 'started_at')
        $stepEnd = Utc (Value $step 'completed_at')
        Require ($stepStart -ge $started -and $stepEnd -ge $stepStart -and $stepEnd -le $completed -and
            (Value $step 'status') -ceq 'completed' -and (Value $step 'conclusion') -cin @('success', 'skipped')) 'Job step lifecycle or outcome is invalid.'
    }
    $checkoutName = 'Run actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1'
    $checkouts = @($steps | Where-Object { $_.name -ceq $checkoutName })
    $uploads = @($steps | Where-Object { $_.name -ceq $UploadStepName })
    Require ($checkouts.Count -eq 1 -and $checkouts[0].conclusion -ceq 'success') 'Exactly one successful pinned checkout step is required.'
    Require ($uploads.Count -eq 1 -and $uploads[0].conclusion -ceq 'success') 'Exactly one successful selected upload step is required.'
    $checkout = $checkouts[0]
    $upload = $uploads[0]
    Require ($checkout.number -lt $upload.number -and (Utc $checkout.completed_at) -le (Utc $upload.started_at)) 'Artifact upload must follow the selected checkout.'

    $file = Get-Item -LiteralPath $LogPath -Force
    Require (-not $file.PSIsContainer -and $file.Length -gt 0 -and $file.Length -le 128MB -and
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Job log must be a nonempty regular file within the size limit.'
    $ancestor = $file.Directory
    while ($null -ne $ancestor)
    {
        Require (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Job log ancestry must not traverse a link or junction.'
        $ancestor = $ancestor.Parent
    }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $entries = [Collections.Generic.List[object]]::new()
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName)
    {
        $lineNumber++
        # Actions can emit unprefixed continuation lines for multiline inputs.
        # Retain/hash them but never treat them as checkout/upload observations.
        if ($line -cnotmatch '^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z) (.*)$') { continue }
        $time = [DateTimeOffset]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
        Require ($time -ge $started -and $time -lt $completed.AddSeconds(1)) 'Job log timestamp is outside the selected job.'
        $entries.Add([pscustomobject]@{ Time=$time; Text=$Matches[2]; Line=$lineNumber })
    }
    Require ($entries.Count -gt 0) 'Job log requires timestamped GitHub observations.'
    function StepLines([object] $Step)
    {
        return @($entries | Where-Object { $_.Time -ge (Utc $Step.started_at) -and $_.Time -lt (Utc $Step.completed_at).AddSeconds(1) })
    }
    $checkoutLines = StepLines $checkout
    Require (@($checkoutLines | Where-Object { $_.Text -ceq "##[group]$checkoutName" }).Count -eq 1) 'Pinned checkout action group is missing or ambiguous in its step window.'
    $commands = @($checkoutLines | Where-Object { $_.Text -cmatch '^\[command\](?:/[^\r\n ]*/git|"[A-Za-z]:\\[^"\r\n]*\\git\.exe") log -1 --format=%H$' })
    Require ($commands.Count -eq 1) 'Exactly one checkout git commit observation is required.'
    $result = @($checkoutLines | Where-Object { $_.Line -eq $commands[0].Line + 1 })
    Require ($result.Count -eq 1 -and $result[0].Text -ceq $ExpectedCommit) 'Observed checkout commit differs from the exact candidate.'

    $artifactId = Value $ArtifactMetadata 'artifactId'
    $artifactRun = Value $ArtifactMetadata 'runId'
    $bytes = Value $ArtifactMetadata 'bytes'
    foreach ($number in @($artifactId, $artifactRun, $bytes)) { Assert-CoreEvidenceInteger $number 'Artifact upload binding integer' }
    $name = Value $ArtifactMetadata 'name'
    $digest = Value $ArtifactMetadata 'sha256'
    Require ($artifactRun -eq $RunId -and (Value $ArtifactMetadata 'sourceCommit') -ceq $ExpectedCommit -and
        $name -is [string] -and -not [string]::IsNullOrWhiteSpace($name) -and
        $digest -is [string] -and $digest -cmatch '^[0-9a-f]{64}$') 'Artifact upload binding has a different run/source or malformed identity.'
    $uploadLines = StepLines $upload
    Require (@($uploadLines | Where-Object { $_.Text -ceq '##[group]Run actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a' }).Count -eq 1) 'Pinned upload action group is missing or ambiguous in its step window.'
    $digests = @($uploadLines | Where-Object { $_.Text.StartsWith('SHA256 digest of uploaded artifact is ', [StringComparison]::Ordinal) })
    $finals = @($uploadLines | Where-Object { $_.Text -cmatch '^Artifact .* has been successfully uploaded! Final size is \d+ bytes\. Artifact ID is \d+$' })
    Require ($digests.Count -eq 1 -and $digests[0].Text -ceq "SHA256 digest of uploaded artifact is $digest") 'Upload digest differs from the actual artifact delivery.'
    Require ($finals.Count -eq 1 -and $finals[0].Text -ceq "Artifact $name has been successfully uploaded! Final size is $bytes bytes. Artifact ID is $artifactId") 'Upload ID, name or byte count differs from the actual artifact delivery.'
    Require ($digests[0].Line -lt $finals[0].Line -and $result[0].Line -lt $digests[0].Line) 'Checkout/upload log observations are out of order.'
    foreach ($property in @('createdUtc', 'updatedUtc'))
    {
        $time = Utc (Value $ArtifactMetadata $property)
        Require ($time -ge (Utc $upload.started_at) -and $time -lt (Utc $upload.completed_at).AddSeconds(1)) 'Artifact delivery lifecycle is outside its selected upload step.'
    }
    Require ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $hash -and
        (Get-Item -LiteralPath $file.FullName).Length -eq $file.Length) 'Job log changed during binding.'
    return [pscustomobject][ordered]@{ jobId=$jobId; runId=$RunId; runAttempt=$RunAttempt
        sourceCommit=$ExpectedCommit; observedCheckoutCommit=$result[0].Text
        checkoutStepNumber=$checkout.number; uploadStepNumber=$upload.number; uploadStepName=$UploadStepName
        artifactId=$artifactId; artifactName=$name; artifactSha256=$digest; artifactBytes=$bytes
        logBytes=$file.Length; logSha256=$hash
        checkoutCommandLine=$commands[0].Line; checkoutCommitLine=$result[0].Line
        uploadDigestLine=$digests[0].Line; uploadFinalizationLine=$finals[0].Line }
}

Export-ModuleMember -Function ConvertTo-CoreGithubJobLogBinding
