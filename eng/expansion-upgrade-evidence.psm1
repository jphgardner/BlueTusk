Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')

# Pure policy and phase-evidence rules for the expansion upgrade rehearsals. Synthetic inputs can
# exercise every guard offline; only verify-expansion-release-upgrade.ps1 binds them to real runs.
$script:UpgradeFamilies = @('Events', 'Documents', 'Schema', 'Projections', 'Search', 'Sql', 'Studio', 'Edge', 'Workflows')
$script:UpgradePhases = @('seed', 'upgrade', 'rollback')
# SHA-256 of an empty catalog: the seed phase must start from a schema that does not exist yet.
$script:EmptyCatalogSha256 = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'

function Assert-UpgradeCondition([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}

function Get-ExpansionUpgradeFamily
{
    # Canonical family identity; parameter binding elsewhere is case-insensitive.
    param([Parameter(Mandatory)][string] $Family)
    $match = @($script:UpgradeFamilies | Where-Object { $_ -ceq $Family })
    Assert-UpgradeCondition ($match.Count -eq 1) (
        "Unknown expansion upgrade family '$Family'; Jobs keeps its dedicated gate.")
    return $match[0]
}

function Test-UpgradeInteger([object] $Value)
{
    return ($Value -is [int] -or $Value -is [long]) -and $Value -ge 0
}

function Get-ExpansionUpgradePolicy
{
    # Validates the complete policy (every family) and returns the selected family's entry.
    param([Parameter(Mandatory)][string] $Family,
        [string] $PolicyPath = (Join-Path $PSScriptRoot 'expansion-upgrade-policy.json'),
        [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
    $Family = Get-ExpansionUpgradeFamily $Family
    $policy = Read-CoreEvidenceJson $PolicyPath
    Assert-CoreEvidenceProperties $policy @('schemaVersion', 'postgreSqlImage', 'postgreSqlMajor',
        'timeBudgetRule', 'families') 'Expansion upgrade policy'
    Assert-UpgradeCondition ($policy.schemaVersion -is [long] -or $policy.schemaVersion -is [int]) 'Upgrade policy schema version must be an integer.'
    Assert-UpgradeCondition ($policy.schemaVersion -eq 1) 'Upgrade policy schema version changed.'
    Assert-UpgradeCondition ($policy.postgreSqlImage -is [string] -and
        $policy.postgreSqlImage -cmatch '^postgres:(\d+)[A-Za-z0-9._-]*@sha256:[0-9a-f]{64}$') (
        'The upgrade fixture image must be an official PostgreSQL image pinned by digest.')
    Assert-UpgradeCondition ((Test-UpgradeInteger $policy.postgreSqlMajor) -and
        [int]$Matches[1] -eq $policy.postgreSqlMajor) 'The upgrade fixture major version differs from its pinned image tag.'
    Assert-UpgradeCondition ($policy.timeBudgetRule -is [string] -and $policy.timeBudgetRule.Length -ge 80) (
        'The upgrade policy must state how its time budgets derive from documented guarantees.')
    Assert-CoreEvidenceProperties $policy.families $script:UpgradeFamilies 'Expansion upgrade policy families'
    $manifest = Read-CoreEvidenceJson (Join-Path $RepositoryRoot 'eng/product-families.json')
    foreach ($name in $script:UpgradeFamilies)
    {
        $entry = $policy.families.PSObject.Properties[$name].Value
        Assert-CoreEvidenceProperties $entry @('boundary', 'documentation', 'documentedBehaviour', 'packagedProjects',
            'schemaChange', 'commandTimeoutSeconds', 'phaseWaitSeconds', 'phaseTimeoutSeconds', 'timeRationale',
            'observations') "Expansion upgrade policy for $name"
        Assert-UpgradeCondition ($entry.boundary -is [string] -and $entry.boundary -cmatch '^[a-z0-9]+(?:-[a-z0-9]+){2,}$') (
            "$name upgrade boundary must be a descriptive kebab-case identifier.")
        $documents = @($entry.documentation)
        Assert-UpgradeCondition ($documents.Count -gt 0) "$name upgrade policy must cite product documentation."
        foreach ($document in $documents)
        {
            Assert-UpgradeCondition ($document -is [string] -and $document -cmatch '^docs/[A-Za-z0-9/_.-]+\.md$' -and
                -not $document.Contains('..') -and
                (Test-Path -LiteralPath (Join-Path $RepositoryRoot $document) -PathType Leaf)) (
                "$name upgrade policy cites missing documentation '$document'.")
        }
        Assert-UpgradeCondition ($entry.documentedBehaviour -is [string] -and $entry.documentedBehaviour.Length -ge 80) (
            "$name upgrade policy must state the documented compatibility behaviour it rehearses.")
        $familyPackages = @($manifest.families.PSObject.Properties[$name].Value.packages)
        $packaged = @($entry.packagedProjects)
        Assert-UpgradeCondition ($packaged.Count -gt 0 -and
            $packaged -ccontains "src/BlueTusk.$name/BlueTusk.$name.csproj" -and
            @($packaged | Sort-Object -Unique -CaseSensitive).Count -eq $packaged.Count) (
            "$name upgrade must package its primary project exactly once.")
        foreach ($project in $packaged)
        {
            Assert-UpgradeCondition ($project -is [string] -and $familyPackages -ccontains $project) (
                "$name upgrade packages '$project', which is not one of its family projects.")
        }
        Assert-UpgradeCondition ($entry.schemaChange -is [string] -and $entry.schemaChange -cin @('none', 'upgrade')) (
            "$name upgrade schemaChange must be 'none' or 'upgrade'.")
        Assert-UpgradeCondition ((Test-UpgradeInteger $entry.commandTimeoutSeconds) -and
            $entry.commandTimeoutSeconds -ge 1 -and $entry.commandTimeoutSeconds -le 60) (
            "$name upgrade must state its store's documented command timeout.")
        Assert-CoreEvidenceProperties $entry.phaseWaitSeconds $script:UpgradePhases "$name phase waits"
        Assert-CoreEvidenceProperties $entry.phaseTimeoutSeconds $script:UpgradePhases "$name phase timeouts"
        foreach ($phase in $script:UpgradePhases)
        {
            $wait = $entry.phaseWaitSeconds.$phase
            $timeout = $entry.phaseTimeoutSeconds.$phase
            Assert-UpgradeCondition ((Test-UpgradeInteger $wait) -and $wait -le 300) "$name $phase wait is invalid."
            # The single rule in timeBudgetRule: deliberate documented waits plus ten worst-case
            # command deadlines. Any other value is tuning, not derivation.
            Assert-UpgradeCondition ((Test-UpgradeInteger $timeout) -and
                $timeout -eq $wait + (10 * $entry.commandTimeoutSeconds)) (
                "$name $phase timeout must equal its documented wait plus ten command timeouts.")
        }
        Assert-UpgradeCondition ($entry.timeRationale -is [string] -and $entry.timeRationale.Length -ge 80) (
            "$name upgrade must record its time-budget rationale.")
        Assert-CoreEvidenceProperties $entry.observations $script:UpgradePhases "$name expected observations"
        foreach ($phase in $script:UpgradePhases)
        {
            $expected = @($entry.observations.$phase.PSObject.Properties)
            Assert-UpgradeCondition ($expected.Count -gt 0) "$name $phase must expect observable effects."
            foreach ($property in $expected)
            {
                Assert-UpgradeCondition ($property.Name -cmatch '^[A-Z][A-Za-z0-9]+$' -and
                    (Test-UpgradeInteger $property.Value)) "$name $phase observation '$($property.Name)' is invalid."
            }
        }
    }
    return [pscustomobject][ordered]@{
        Family = $Family
        PostgreSqlImage = [string]$policy.postgreSqlImage
        PostgreSqlMajor = [int]$policy.postgreSqlMajor
        Entry = $policy.families.PSObject.Properties[$Family].Value
    }
}

function Assert-ExpansionUpgradePhases
{
    # Checks the three phase reports and their catalog digests against the family policy:
    # the right family and phase order, exact effect counts, schema continuity between binaries,
    # an expected (or absent) candidate migration, and a rollback that never rewrites the schema.
    param([Parameter(Mandatory)][object] $Policy,
        [Parameter(Mandatory)][hashtable] $Reports,
        [Parameter(Mandatory)][hashtable] $CatalogDigests)
    $family = $Policy.Family
    foreach ($phase in $script:UpgradePhases)
    {
        Assert-UpgradeCondition ($Reports.ContainsKey($phase) -and $CatalogDigests.ContainsKey($phase)) (
            "$family upgrade evidence is missing the $phase phase.")
        $report = $Reports[$phase]
        Assert-CoreEvidenceProperties $report @('Family', 'Phase', 'Passed', 'ServerVersionNum', 'Fingerprints', 'Observations') (
            "$family $phase report")
        Assert-UpgradeCondition ([string]$report.Family -ceq $family -and [string]$report.Phase -ceq $phase -and
            $report.Passed -is [bool] -and $report.Passed) "$family $phase report belongs to another family or phase, or did not pass."
        Assert-UpgradeCondition ((Test-UpgradeInteger $report.ServerVersionNum) -and
            [math]::Floor($report.ServerVersionNum / 10000) -eq $Policy.PostgreSqlMajor) (
            "$family $phase ran against a PostgreSQL major other than the pinned fixture.")
        foreach ($label in @('before', 'after'))
        {
            $recorded = $report.Fingerprints.PSObject.Properties[$label]
            Assert-UpgradeCondition ($null -ne $recorded -and [string]$recorded.Value -cmatch '^[0-9a-f]{64}$' -and
                $CatalogDigests[$phase].ContainsKey($label) -and
                [string]$CatalogDigests[$phase][$label] -ceq [string]$recorded.Value) (
                "$family $phase catalog '$label' is missing or differs from its retained catalog file.")
        }
        $expected = $Policy.Entry.observations.$phase
        $actual = $report.Observations
        $expectedNames = @($expected.PSObject.Properties.Name | Sort-Object -CaseSensitive)
        $actualNames = @($actual.PSObject.Properties.Name | Sort-Object -CaseSensitive)
        Assert-UpgradeCondition (($expectedNames -join '|') -ceq ($actualNames -join '|')) (
            "$family $phase observed effects '$($actualNames -join ',')' differ from the policy set '$($expectedNames -join ',')'.")
        foreach ($name in $expectedNames)
        {
            $value = $actual.$name
            Assert-UpgradeCondition ((Test-UpgradeInteger $value) -and [long]$value -eq [long]$expected.$name) (
                "$family $phase effect '$name' was $value; the policy requires exactly $($expected.$name).")
        }
    }
    $seed = $Reports.seed.Fingerprints
    $upgrade = $Reports.upgrade.Fingerprints
    $rollback = $Reports.rollback.Fingerprints
    Assert-UpgradeCondition ([string]$seed.before -ceq $script:EmptyCatalogSha256 -and
        [string]$seed.after -cne $script:EmptyCatalogSha256) "$family seed did not start from an absent schema and create one."
    Assert-UpgradeCondition ([string]$upgrade.before -ceq [string]$seed.after) (
        "$family candidate did not open the schema the baseline left behind.")
    if ([string]$Policy.Entry.schemaChange -ceq 'none')
    {
        Assert-UpgradeCondition ([string]$upgrade.after -ceq [string]$upgrade.before) (
            "$family candidate changed the durable schema; this boundary declares no migration.")
    }
    else
    {
        Assert-UpgradeCondition ([string]$upgrade.after -cne [string]$upgrade.before) (
            "$family candidate did not apply the migration this boundary declares.")
    }
    Assert-UpgradeCondition ([string]$rollback.before -ceq [string]$upgrade.after -and
        [string]$rollback.after -ceq [string]$rollback.before) (
        "$family rollback binary did not find, or did not leave unchanged, the candidate's schema.")
}

Export-ModuleMember -Function Get-ExpansionUpgradeFamily, Get-ExpansionUpgradePolicy, Assert-ExpansionUpgradePhases
