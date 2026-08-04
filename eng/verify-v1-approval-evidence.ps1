[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidencePath,

    [Parameter(Mandatory)]
    [string] $ExpectedGateId,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit,

    [DateTimeOffset] $NotBeforeUtc = [DateTimeOffset]::MinValue
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RequiredProperty
{
    param(
        [Parameter(Mandatory)]
        [object] $InputObject,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Context
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property)
    {
        throw "$Context is missing required property '$Name'."
    }

    $value = $property.Value
    if ($value -is [Collections.IList])
    {
        return ,$value
    }

    return $value
}

function ConvertTo-Number
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Context,

        [switch] $Integer
    )

    if ($Value -is [bool] -or
        $Value -is [string] -or
        $Value -isnot [ValueType])
    {
        throw "$Context must be a JSON number."
    }

    $number = [decimal]$Value
    if ($Integer -and $number -ne [Math]::Truncate($number))
    {
        throw "$Context must be an integer."
    }

    return $number
}

function Assert-HttpsUri
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Context
    )

    if ($Value -isnot [string])
    {
        throw "$Context must be an absolute HTTPS URI."
    }

    $uri = [Uri]::new('https://invalid.example')
    if (-not [Uri]::TryCreate(
            [string]$Value,
            [UriKind]::Absolute,
            [ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttps -or
        [string]::IsNullOrWhiteSpace($uri.Host))
    {
        throw "$Context must be an absolute HTTPS URI."
    }
}

function ConvertTo-UtcDateTime
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Context
    )

    if ($Value -is [DateTimeOffset])
    {
        $parsed = [DateTimeOffset]$Value
        if ($parsed.Offset -ne [TimeSpan]::Zero)
        {
            throw "$Context must be an ISO 8601 UTC timestamp with a Z offset."
        }

        return $parsed
    }
    if ($Value -is [DateTime])
    {
        $dateTime = [DateTime]$Value
        if ($dateTime.Kind -ne [DateTimeKind]::Utc)
        {
            throw "$Context must be an ISO 8601 UTC timestamp with a Z offset."
        }

        return [DateTimeOffset]::new($dateTime)
    }
    if ($Value -isnot [string])
    {
        throw "$Context must be an ISO 8601 UTC timestamp."
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero)
    {
        throw "$Context must be an ISO 8601 UTC timestamp with a Z offset."
    }

    return $parsed
}

$contractPath = Join-Path $PSScriptRoot 'v1-approval-evidence-contract.json'
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
if ([int]$contract.schemaVersion -ne 1 -or
    [int]$contract.approvalSchemaVersion -ne 2)
{
    throw 'The V1 approval-evidence contract has an unsupported schema.'
}

$gateMatches = @($contract.gates | Where-Object {
    [string]$_.id -eq $ExpectedGateId
})
if ($gateMatches.Count -ne 1)
{
    throw "Approval gate '$ExpectedGateId' is not uniquely defined by the contract."
}
$gate = $gateMatches[0]

$evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
$schemaVersion = Get-RequiredProperty $evidence 'schemaVersion' 'Approval evidence'
$gateId = Get-RequiredProperty $evidence 'gateId' 'Approval evidence'
$candidateCommit = Get-RequiredProperty $evidence 'candidateCommit' 'Approval evidence'
$outcome = Get-RequiredProperty $evidence 'outcome' 'Approval evidence'
$approvedBy = Get-RequiredProperty $evidence 'approvedBy' 'Approval evidence'
$approvedUtcText = Get-RequiredProperty $evidence 'approvedUtc' 'Approval evidence'
$summary = Get-RequiredProperty $evidence 'summary' 'Approval evidence'
$blockingFindings = Get-RequiredProperty $evidence 'blockingFindings' 'Approval evidence'
$referencesValue = Get-RequiredProperty $evidence 'references' 'Approval evidence'
$references = @($referencesValue | ForEach-Object { $_ })
$details = Get-RequiredProperty $evidence 'details' 'Approval evidence'

$expectedCommonProperties = @(
    'schemaVersion',
    'gateId',
    'candidateCommit',
    'outcome',
    'approvedBy',
    'approvedUtc',
    'summary',
    'blockingFindings',
    'references',
    'details'
)
$unexpectedCommonProperties = @(
    $evidence.PSObject.Properties.Name |
        Where-Object { $_ -notin $expectedCommonProperties }
)
if ($unexpectedCommonProperties.Count -ne 0)
{
    throw (
        "Approval '$ExpectedGateId' contains unexpected top-level properties: " +
        ($unexpectedCommonProperties -join ', '))
}

if ([int]$schemaVersion -ne [int]$contract.approvalSchemaVersion)
{
    throw (
        "Approval '$ExpectedGateId' must use schema " +
        "$($contract.approvalSchemaVersion).")
}
if ([string]$gateId -ne $ExpectedGateId)
{
    throw "Approval gate '$gateId' does not match expected gate '$ExpectedGateId'."
}
if (-not [string]::Equals(
        [string]$candidateCommit,
        $ExpectedCommit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Approval '$ExpectedGateId' does not match candidate '$ExpectedCommit'."
}
if ([string]$outcome -ne 'approved')
{
    throw "Approval '$ExpectedGateId' outcome must be 'approved'."
}
if ($approvedBy -isnot [string] -or
    [string]::IsNullOrWhiteSpace([string]$approvedBy))
{
    throw "Approval '$ExpectedGateId' must name the accountable approver."
}
if ($summary -isnot [string] -or
    ([string]$summary).Trim().Length -lt [int]$contract.minimumSummaryLength)
{
    throw (
        "Approval '$ExpectedGateId' summary must contain at least " +
        "$($contract.minimumSummaryLength) non-whitespace characters.")
}
if ((ConvertTo-Number $blockingFindings "Approval '$ExpectedGateId' blockingFindings" -Integer) -ne 0)
{
    throw "Approval '$ExpectedGateId' must contain zero blocking findings."
}
if ($references.Count -lt 1)
{
    throw "Approval '$ExpectedGateId' must cite at least one retained evidence record."
}
foreach ($reference in $references)
{
    Assert-HttpsUri $reference "Approval '$ExpectedGateId' reference"
}

$approvedUtc = ConvertTo-UtcDateTime $approvedUtcText "Approval '$ExpectedGateId' approvedUtc"
$now = [DateTimeOffset]::UtcNow
if ($approvedUtc -gt $now)
{
    throw "Approval '$ExpectedGateId' cannot be future-dated."
}
if ($NotBeforeUtc -ne [DateTimeOffset]::MinValue -and
    $approvedUtc -lt $NotBeforeUtc.ToUniversalTime())
{
    throw (
        "Approval '$ExpectedGateId' predates the immutable candidate: " +
        "$approvedUtc is before $($NotBeforeUtc.ToUniversalTime()).")
}

if ($null -eq $details -or $details -is [ValueType] -or $details -is [string])
{
    throw "Approval '$ExpectedGateId' details must be a JSON object."
}

$rules = @($gate.details)
$ruleNames = @($rules | ForEach-Object { [string]$_.name })
$actualDetailNames = @($details.PSObject.Properties.Name)
$missingDetailNames = @($ruleNames | Where-Object { $_ -notin $actualDetailNames })
$unexpectedDetailNames = @($actualDetailNames | Where-Object { $_ -notin $ruleNames })
if ($missingDetailNames.Count -ne 0 -or $unexpectedDetailNames.Count -ne 0)
{
    throw (
        "Approval '$ExpectedGateId' detail schema mismatch. Missing: " +
        "$(if ($missingDetailNames.Count) { $missingDetailNames -join ', ' } else { '<none>' }); " +
        "unexpected: " +
        "$(if ($unexpectedDetailNames.Count) { $unexpectedDetailNames -join ', ' } else { '<none>' }).")
}

foreach ($rule in $rules)
{
    $name = [string]$rule.name
    $context = "Approval '$ExpectedGateId' detail '$name'"
    $value = Get-RequiredProperty $details $name "Approval '$ExpectedGateId' details"
    $type = [string]$rule.type

    switch ($type)
    {
        'string'
        {
            if ($value -isnot [string])
            {
                throw "$context must be a string."
            }
            if ($null -ne $rule.PSObject.Properties['minLength'] -and
                ([string]$value).Trim().Length -lt [int]$rule.minLength)
            {
                throw "$context is shorter than $($rule.minLength) characters."
            }
            if ($null -ne $rule.PSObject.Properties['allowedValues'] -and
                [string]$value -notin @($rule.allowedValues | ForEach-Object { [string]$_ }))
            {
                throw "$context is not an allowed value."
            }
        }
        'boolean'
        {
            if ($value -isnot [bool])
            {
                throw "$context must be a boolean."
            }
        }
        'integer'
        {
            $value = ConvertTo-Number $value $context -Integer
        }
        'number'
        {
            $value = ConvertTo-Number $value $context
        }
        'stringArray'
        {
            if ($value -is [string])
            {
                throw "$context must be an array of non-empty strings."
            }
            $items = @($value | ForEach-Object { $_ })
            if ($null -ne $rule.PSObject.Properties['minimumItems'] -and
                $items.Count -lt [int]$rule.minimumItems)
            {
                throw "$context must contain at least $($rule.minimumItems) items."
            }
            if (@($items | Where-Object {
                $_ -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$_)
            }).Count -ne 0)
            {
                throw "$context must contain only non-empty strings."
            }
            if (@($items | Select-Object -Unique).Count -ne $items.Count)
            {
                throw "$context must not contain duplicate values."
            }
        }
        'httpsUri'
        {
            Assert-HttpsUri $value $context
        }
        'sha256'
        {
            if ($value -isnot [string] -or [string]$value -notmatch '^[0-9a-fA-F]{64}$')
            {
                throw "$context must be a SHA-256 value."
            }
        }
        'utcDateTime'
        {
            $null = ConvertTo-UtcDateTime $value $context
        }
        default
        {
            throw "Approval contract rule '$name' has unsupported type '$type'."
        }
    }

    if ($null -ne $rule.PSObject.Properties['equals'])
    {
        $expected = $rule.equals
        if ($value -ne $expected)
        {
            throw "$context must equal '$expected'."
        }
    }
    if ($null -ne $rule.PSObject.Properties['minimum'] -and
        $value -lt [decimal]$rule.minimum)
    {
        throw "$context must be at least $($rule.minimum)."
    }
    if ($null -ne $rule.PSObject.Properties['maximum'] -and
        $value -gt [decimal]$rule.maximum)
    {
        throw "$context must be no more than $($rule.maximum)."
    }
    if ($null -ne $rule.PSObject.Properties['equalsField'])
    {
        $otherName = [string]$rule.equalsField
        $otherValue = Get-RequiredProperty $details $otherName "Approval '$ExpectedGateId' details"
        if ($value -ne $otherValue)
        {
            throw "$context must equal detail '$otherName'."
        }
    }
    if ($null -ne $rule.PSObject.Properties['maximumField'])
    {
        $otherName = [string]$rule.maximumField
        $otherValue = ConvertTo-Number (
            Get-RequiredProperty $details $otherName "Approval '$ExpectedGateId' details"
        ) "Approval '$ExpectedGateId' detail '$otherName'"
        if ([decimal]$value -gt $otherValue)
        {
            throw "$context must be no more than detail '$otherName'."
        }
    }
}

Write-Output (
    "V1 approval evidence passed: $ExpectedGateId, candidate " +
    "$($ExpectedCommit.ToLowerInvariant()), $($rules.Count) gate-specific measurements.")
