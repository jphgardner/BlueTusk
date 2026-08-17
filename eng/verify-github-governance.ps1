[CmdletBinding()]
param(
    [ValidateSet('Source', 'Remote')]
    [string] $Mode = 'Source',

    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),

    [string] $Repository,

    [string] $Token
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$configurationPath = Join-Path $PSScriptRoot 'v1-github-governance.json'
$configuration = Get-Content -LiteralPath $configurationPath -Raw |
    ConvertFrom-Json
if ([int]$configuration.schemaVersion -ne 1)
{
    throw "Expected GitHub governance schema 1; found '$($configuration.schemaVersion)'."
}

$Repository = if ([string]::IsNullOrWhiteSpace($Repository))
{
    [string]$configuration.repository
}
else
{
    $Repository
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')
{
    throw "Repository '$Repository' must use owner/name format."
}
if ([string]::IsNullOrWhiteSpace([string]$configuration.protectedBranch))
{
    throw 'The protected branch must be declared.'
}

$repositorySecurity = $configuration.repositorySecurity
if ($repositorySecurity.dependencyGraph -ne $true -or
    [int]$repositorySecurity.minimumDependencyGraphPackages -lt 1 -or
    $repositorySecurity.vulnerabilityAlerts -ne $true -or
    $repositorySecurity.automatedSecurityFixes -ne $true -or
    $repositorySecurity.privateVulnerabilityReporting -ne $true)
{
    throw (
        'The governance contract must require the dependency graph, vulnerability alerts, ' +
        'automated security fixes, and private vulnerability reporting.')
}

$requiredRuleTypes = @(
    $configuration.ruleset.requiredRuleTypes |
        ForEach-Object { [string]$_ }
)
$requiredStatusChecks = @(
    $configuration.ruleset.requiredStatusChecks |
        ForEach-Object { [string]$_ }
)
$missingRequiredRuleTypes = @(
    @('deletion', 'non_fast_forward', 'pull_request', 'required_status_checks') |
        Where-Object { $_ -notin $requiredRuleTypes }
)
if ($requiredRuleTypes.Count -ne ($requiredRuleTypes | Sort-Object -Unique).Count -or
    $missingRequiredRuleTypes.Count -ne 0)
{
    throw 'The governance contract must uniquely require deletion, force-push, PR, and status-check rules.'
}
if ($requiredStatusChecks.Count -lt 1 -or
    $requiredStatusChecks.Count -ne ($requiredStatusChecks | Sort-Object -Unique).Count -or
    @($requiredStatusChecks | Where-Object {
        [string]::IsNullOrWhiteSpace($_)
    }).Count -ne 0)
{
    throw 'Required status-check contexts must be non-empty and unique.'
}

$pullRequest = $configuration.ruleset.pullRequest
if ([int]$pullRequest.minimumApprovals -lt 1 -or
    $pullRequest.dismissStaleReviewsOnPush -ne $true -or
    $pullRequest.requireLastPushApproval -ne $true -or
    $pullRequest.requireReviewThreadResolution -ne $true)
{
    throw 'V1 governance requires an independent fresh approval and resolved review threads.'
}
if ($configuration.ruleset.strictRequiredStatusChecksPolicy -ne $true)
{
    throw 'V1 governance requires status checks against the latest protected-branch state.'
}

$environments = @($configuration.environments)
if ($environments.Count -ne 3 -or
    $environments.Count -ne @(
        $environments.name | Sort-Object -Unique
    ).Count)
{
    throw 'Exactly three uniquely named V1 deployment environments are required.'
}
foreach ($environment in $environments)
{
    if ([int]$environment.minimumConfiguredReviewers -lt 1 -or
        $environment.preventSelfReview -ne $true)
    {
        throw "Environment '$($environment.name)' must require an independent reviewer."
    }

    $requiredSecrets = @(
        $environment.requiredSecrets |
            ForEach-Object { [string]$_ }
    )
    if ($requiredSecrets.Count -lt 1 -or
        $requiredSecrets.Count -ne ($requiredSecrets | Sort-Object -Unique).Count -or
        @($requiredSecrets | Where-Object {
            $_ -notmatch '^[A-Z][A-Z0-9_]*$'
        }).Count -ne 0)
    {
        throw "Environment '$($environment.name)' must declare unique uppercase secret names."
    }

    $branchPolicy = $environment.deploymentBranchPolicy
    if ([bool]$branchPolicy.protectedBranches -eq
        [bool]$branchPolicy.customBranchPolicies)
    {
        throw (
            "Environment '$($environment.name)' must select exactly one " +
            'deployment branch-policy mode.')
    }
    $patterns = @($branchPolicy.requiredPatterns | ForEach-Object { [string]$_ })
    if ($branchPolicy.customBranchPolicies -eq $true -and
        ($patterns.Count -lt 1 -or
         $patterns.Count -ne ($patterns | Sort-Object -Unique).Count))
    {
        throw "Environment '$($environment.name)' must declare unique deployment patterns."
    }
    if ($branchPolicy.customBranchPolicies -ne $true -and $patterns.Count -ne 0)
    {
        throw "Environment '$($environment.name)' cannot mix protected branches and custom patterns."
    }

    $workflowPath = Join-Path $RepositoryRoot ([string]$environment.workflow)
    if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf))
    {
        throw "Governed workflow '$($environment.workflow)' is missing."
    }
    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $environmentPattern = '(?m)^\s*environment:\s*' +
        [regex]::Escape([string]$environment.name) + '\s*$'
    if ($workflow -notmatch $environmentPattern)
    {
        throw (
            "Workflow '$($environment.workflow)' does not bind environment " +
            "'$($environment.name)'.")
    }
    foreach ($secretName in $requiredSecrets)
    {
        $secretPattern = '\$\{\{\s*secrets\.' +
            [regex]::Escape($secretName) + '\s*\}\}'
        if ($workflow -notmatch $secretPattern)
        {
            throw (
                "Workflow '$($environment.workflow)' does not consume required " +
                "environment secret '$secretName'.")
        }
    }
}

$requiredEnvironmentSecretBindings = @(
    $environments |
        ForEach-Object { @($_.requiredSecrets) }
).Count

if ($Mode -eq 'Source')
{
    Write-Output (
        "Verified source governance for $($requiredStatusChecks.Count) required checks, " +
        "$($environments.Count) self-review-protected environments, " +
        "$requiredEnvironmentSecretBindings required secret bindings, protected branch " +
        "'$($configuration.protectedBranch)', and mandatory repository security features. " +
        'Remote repository settings remain a candidate gate.')
    return
}

if ([string]::IsNullOrWhiteSpace($Token))
{
    $Token = if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN))
    {
        $env:GH_TOKEN
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN))
    {
        $env:GITHUB_TOKEN
    }
    else
    {
        $null
    }
}

$headers = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'BlueTusk-v1-governance-gate'
    'X-GitHub-Api-Version' = '2022-11-28'
}
if (-not [string]::IsNullOrWhiteSpace($Token))
{
    $headers.Authorization = "Bearer $Token"
}

function Invoke-GitHubGet
{
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $uri = "https://api.github.com/repos/$Repository/$Path"
    try
    {
        return Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
    }
    catch
    {
        throw "GitHub governance query '$uri' failed: $($_.Exception.Message)"
    }
}

$rulesetsResponse = Invoke-GitHubGet -Path 'rulesets?includes_parents=true&per_page=100'
$rulesets = @($rulesetsResponse)

$dependencyGraph = Invoke-GitHubGet -Path 'dependency-graph/sbom'
if ([string]$dependencyGraph.sbom.spdxVersion -ne 'SPDX-2.3' -or
    @($dependencyGraph.sbom.packages).Count -lt
        [int]$repositorySecurity.minimumDependencyGraphPackages)
{
    throw 'The repository dependency graph is disabled or contains no package evidence.'
}

$null = Invoke-GitHubGet -Path 'vulnerability-alerts'
$automatedSecurityFixes = Invoke-GitHubGet -Path 'automated-security-fixes'
if ($automatedSecurityFixes.enabled -ne $true -or
    $automatedSecurityFixes.paused -eq $true)
{
    throw 'Dependabot automated security fixes are disabled or paused.'
}

$privateVulnerabilityReporting = Invoke-GitHubGet -Path 'private-vulnerability-reporting'
if ($privateVulnerabilityReporting.enabled -ne $true)
{
    throw 'Private vulnerability reporting is disabled.'
}

$rulesetSummary = @($rulesets | Where-Object {
    if ($null -eq $_ -or
        $null -eq $_.PSObject.Properties['name'] -or
        $null -eq $_.PSObject.Properties['enforcement'])
    {
        return $false
    }

    [string]$_.name -eq [string]$configuration.ruleset.name -and
    [string]$_.enforcement -eq [string]$configuration.ruleset.enforcement
})
if ($rulesetSummary.Count -ne 1)
{
    throw (
        "Expected one active '$($configuration.ruleset.name)' ruleset; " +
        "found $($rulesetSummary.Count).")
}
$ruleset = Invoke-GitHubGet -Path "rulesets/$($rulesetSummary[0].id)"
$expectedRef = "refs/heads/$($configuration.protectedBranch)"
if ($expectedRef -notin @($ruleset.conditions.ref_name.include))
{
    throw "Ruleset '$($ruleset.name)' does not include '$expectedRef'."
}
$rules = @($ruleset.rules)
foreach ($type in $requiredRuleTypes)
{
    if (@($rules | Where-Object { [string]$_.type -eq $type }).Count -ne 1)
    {
        throw "Ruleset '$($ruleset.name)' must contain exactly one '$type' rule."
    }
}

$remotePullRequest = @($rules | Where-Object type -eq 'pull_request')[0].parameters
if ([int]$remotePullRequest.required_approving_review_count -lt
        [int]$pullRequest.minimumApprovals -or
    $remotePullRequest.dismiss_stale_reviews_on_push -ne
        [bool]$pullRequest.dismissStaleReviewsOnPush -or
    $remotePullRequest.require_last_push_approval -ne
        [bool]$pullRequest.requireLastPushApproval -or
    $remotePullRequest.required_review_thread_resolution -ne
        [bool]$pullRequest.requireReviewThreadResolution)
{
    throw "Ruleset '$($ruleset.name)' does not satisfy the V1 pull-request review policy."
}

$remoteStatusRule = @($rules | Where-Object type -eq 'required_status_checks')[0]
if ($remoteStatusRule.parameters.strict_required_status_checks_policy -ne $true)
{
    throw "Ruleset '$($ruleset.name)' does not require checks against the latest branch state."
}
$remoteContexts = @(
    $remoteStatusRule.parameters.required_status_checks |
        ForEach-Object { [string]$_.context }
)
$missingContexts = @($requiredStatusChecks | Where-Object {
    $_ -notin $remoteContexts
})
if ($missingContexts.Count -ne 0)
{
    throw "Ruleset '$($ruleset.name)' is missing required checks: $($missingContexts -join ', ')."
}

$environmentSecretFailures = [Collections.Generic.List[string]]::new()
foreach ($environment in $environments)
{
    $encodedName = [Uri]::EscapeDataString([string]$environment.name)
    $remoteEnvironment = Invoke-GitHubGet -Path "environments/$encodedName"
    $reviewRule = @(
        $remoteEnvironment.protection_rules |
            Where-Object type -eq 'required_reviewers'
    )
    if ($reviewRule.Count -ne 1 -or
        @($reviewRule[0].reviewers).Count -lt
            [int]$environment.minimumConfiguredReviewers -or
        $reviewRule[0].prevent_self_review -ne $true)
    {
        throw "Environment '$($environment.name)' lacks independent required-reviewer protection."
    }

    $expectedPolicy = $environment.deploymentBranchPolicy
    $actualPolicy = $remoteEnvironment.deployment_branch_policy
    if ($null -eq $actualPolicy -or
        $actualPolicy.protected_branches -ne
            [bool]$expectedPolicy.protectedBranches -or
        $actualPolicy.custom_branch_policies -ne
            [bool]$expectedPolicy.customBranchPolicies)
    {
        throw "Environment '$($environment.name)' has the wrong deployment branch policy."
    }

    if ($expectedPolicy.customBranchPolicies -eq $true)
    {
        $remotePolicies = Invoke-GitHubGet -Path (
            "environments/$encodedName/deployment-branch-policies?per_page=100")
        $actualPatterns = @(
            $remotePolicies.branch_policies |
                ForEach-Object { [string]$_.name }
        )
        $missingPatterns = @(
            $expectedPolicy.requiredPatterns |
                Where-Object { [string]$_ -notin $actualPatterns }
        )
        if ($missingPatterns.Count -ne 0)
        {
            throw (
                "Environment '$($environment.name)' is missing deployment patterns: " +
                ($missingPatterns -join ', ') + '.')
        }
    }

    $remoteSecrets = Invoke-GitHubGet -Path (
        "environments/$encodedName/secrets?per_page=100")
    $actualSecretNames = @(
        $remoteSecrets.secrets |
            ForEach-Object { [string]$_.name }
    )
    $missingSecrets = @(
        $environment.requiredSecrets |
            Where-Object { [string]$_ -notin $actualSecretNames }
    )
    if ($missingSecrets.Count -ne 0)
    {
        $environmentSecretFailures.Add(
            "'$($environment.name)': $($missingSecrets -join ', ')")
    }
}

if ($environmentSecretFailures.Count -ne 0)
{
    throw (
        'Protected environments are missing required secrets: ' +
        ($environmentSecretFailures -join '; ') + '.')
}

Write-Output (
    "Verified live GitHub governance for '$Repository': ruleset '$($ruleset.name)', " +
    "$($requiredStatusChecks.Count) required checks, and $($environments.Count) " +
    "self-review-protected deployment environments with $requiredEnvironmentSecretBindings " +
    'required secret bindings, dependency controls, and vulnerability protections enabled.')
