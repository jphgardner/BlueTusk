[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]] $ReportPath,
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string] $ContractPath = (Join-Path $PSScriptRoot 'dotnet-compatibility.json'),
    [switch] $RequireComplete,
    [switch] $RequireMatrixComplete,
    [switch] $Historical,
    [switch] $PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'core-test-evidence.psm1') -DisableNameChecking
if ($RequireComplete -and $Historical) { throw 'Historical evidence cannot establish current complete .NET compatibility.' }
$inventory = & (Join-Path $PSScriptRoot 'verify-dotnet-compatibility.ps1') -RepositoryRoot $RepositoryRoot -ContractPath $ContractPath -RequireComplete:$RequireComplete -PassThru
$contract = Read-CoreEvidenceJson $ContractPath
$contractHash = (Get-FileHash -LiteralPath $ContractPath -Algorithm SHA256).Hash.ToLowerInvariant()
$head = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the current compatibility source commit.' }
$currentFingerprint = if (-not $Historical) { Get-CoreTestSourceFingerprint -ExpectedCommit $head } else { $null }
$baselineFingerprint = $null
$pairs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$covered = @($contract.packages | Where-Object status -CEQ 'covered')
$toolMappings = @{}
foreach ($tool in $contract.cliTools) { $toolMappings[$tool.project] = $tool }
foreach ($os in @($contract.requiredOperatingSystems))
{
    foreach ($framework in @($contract.frameworks))
    {
        $major = [int]($framework -replace '^net|\.0$', '')
        foreach ($test in @($covered | Where-Object {
            [int]($_.minimumFramework -replace '^net|\.0$', '') -le $major
        } | ForEach-Object { $_.testProjects } | Sort-Object -Unique))
        {
            [void]$expected.Add("$os|$framework|$test")
        }
        foreach ($package in @($covered | Where-Object {
            [int]($_.minimumFramework -replace '^net|\.0$', '') -le $major -and $toolMappings.ContainsKey($_.project)
        }))
        {
            [void]$expected.Add("$os|$framework|$($package.project)")
        }
    }
}
$executed = 0
foreach ($path in $ReportPath)
{
    $resolvedReport = (Resolve-Path -LiteralPath $path).Path
    $root = Split-Path $resolvedReport -Parent
    $report = Read-CoreEvidenceJson $resolvedReport
    if ($report.schemaVersion -ne 2 -or $report.succeeded -isnot [bool] -or -not $report.succeeded -or
        $report.sourceUnchangedDuringRun -isnot [bool] -or -not $report.sourceUnchangedDuringRun -or
        $report.contractSha256 -cne $contractHash -or $report.sdkVersion -cne $contract.sdkVersion -or
        $report.sourceCommit -cne $head -or $report.sourceTreeDirty -isnot [bool] -or
        @($report.results).Count -eq 0 -or @($report.requestedFrameworks).Count -eq 0 -or
        @($report.requestedFrameworks | Sort-Object -Unique).Count -ne @($report.requestedFrameworks).Count -or
        @($report.requestedFrameworks | Where-Object { $_ -cnotin $contract.frameworks }).Count -ne 0)
    {
        throw 'Compatibility evidence needs a successful stable-source schema-2 run bound to the current contract, SDK, and source commit.'
    }
    $fingerprintPath = Resolve-CoreEvidenceFile $root $report.sourceFingerprint
    if ($report.sourceFingerprintSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        (Get-FileHash -LiteralPath $fingerprintPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $report.sourceFingerprintSha256)
    {
        throw 'Compatibility source fingerprint failed its file hash binding.'
    }
    $fingerprint = Read-CoreEvidenceJson $fingerprintPath
    if ($fingerprint.sourceCommit -cne $head -or $fingerprint.worktreeClean -eq $report.sourceTreeDirty)
    {
        throw 'Compatibility source fingerprint and run source identity differ.'
    }
    $canonicalFingerprint = ConvertTo-Json -InputObject $fingerprint -Depth 8 -Compress
    if ($null -eq $baselineFingerprint) { $baselineFingerprint = $canonicalFingerprint }
    if ($canonicalFingerprint -cne $baselineFingerprint -or
        (-not $Historical -and $canonicalFingerprint -cne (ConvertTo-Json -InputObject $currentFingerprint -Depth 8 -Compress)))
    {
        throw 'Compatibility reports must test the same exact source fingerprint; current qualification also requires the current checkout.'
    }
    foreach ($result in @($report.results))
    {
        $key = "$($result.operatingSystem)|$($result.framework)|$($result.testProject)"
        if (-not $expected.Contains($key) -or -not $pairs.Add($key) -or
            $result.framework -cnotin $report.requestedFrameworks -or
            $result.runtimeVersion -cne $contract.runtimeVersions.($result.framework) -or
            ($result.passed -isnot [int] -and $result.passed -isnot [long]) -or [long]$result.passed -le 0)
        {
            throw 'Compatibility evidence has an unknown, duplicate, unrequested, or empty runtime/test pair.'
        }
        $legacy = $contract.legacyLinuxContainers.PSObject.Properties[$result.framework]
        if ($result.operatingSystem -ceq 'linux' -and $null -ne $legacy)
        {
            if ($result.containerImage -cne $legacy.Value.image) { throw 'Legacy Linux evidence must use its exact contracted SDK image digest.' }
        }
        elseif ($null -ne $result.containerImage) { throw 'Native framework evidence must not claim an unrelated container.' }
        $extensionFixture = $contract.postgreSqlExtensionFixtures | Where-Object { $result.testProject -cin $_.testProjects }
        $requiresPgVector = $result.testProject -cin $contract.postgreSqlFixture.pgVector.testProjects
        if ($result.testProject -cin $contract.postgreSqlFixture.testProjects -or $null -ne $extensionFixture)
        {
            $serverFixture = if ($null -ne $extensionFixture) { $extensionFixture } else { $contract.postgreSqlFixture }
            $expectedPostgreSql = if ($result.operatingSystem -ceq 'windows') { 'sha256:' + $serverFixture.windowsArchive.sha256 }
                elseif ($requiresPgVector) { $serverFixture.pgVector.linuxImage }
                else { $serverFixture.linuxImage }
            if ($result.postgreSqlVersion -cne $serverFixture.version -or $result.postgreSqlIdentity -cne $expectedPostgreSql)
            {
                throw 'Live compatibility evidence requires the pinned PostgreSQL fixture version and distribution.'
            }
        }
        elseif ($null -ne $result.postgreSqlVersion -or $null -ne $result.postgreSqlIdentity)
        {
            throw 'A compatibility suite must be registered before claiming PostgreSQL fixture coverage.'
        }
        if ($null -ne $extensionFixture)
        {
            $expectedExtension = if ($result.operatingSystem -ceq 'linux') { $extensionFixture.linuxImage }
                else { 'sha256:' + $extensionFixture.windowsExtensionArchive.sha256 }
            if ($result.postgreSqlExtensionVersion -cne $extensionFixture.extensionVersion -or
                $result.postgreSqlExtensionIdentity -cne $expectedExtension)
            { throw 'Live compatibility evidence requires the pinned PostgreSQL extension version and distribution.' }
        }
        elseif ($null -ne $result.postgreSqlExtensionVersion -or $null -ne $result.postgreSqlExtensionIdentity)
        { throw 'A compatibility suite must bind to a fixture before claiming PostgreSQL extension coverage.' }
        foreach ($fixture in @(
            @{ name = 'pgVector'; definition = $contract.postgreSqlFixture.pgVector; windowsSha = $contract.postgreSqlFixture.pgVector.windowsSource.sha256; linuxIdentity = $contract.postgreSqlFixture.pgVector.linuxImage },
            @{ name = 'minio'; definition = $contract.minioFixture; windowsSha = $contract.minioFixture.windowsBinary.sha256; linuxIdentity = 'sha256:' + $contract.minioFixture.linuxBinary.sha256 }))
        {
            $version = $result.($fixture.name + 'Version')
            $identity = $result.($fixture.name + 'Identity')
            if ($result.testProject -cin $fixture.definition.testProjects)
            {
                $expectedIdentity = if ($result.operatingSystem -ceq 'linux') { $fixture.linuxIdentity } else { 'sha256:' + $fixture.windowsSha }
                if ($version -cne $fixture.definition.version -or $identity -cne $expectedIdentity)
                { throw "Live compatibility evidence requires the pinned $($fixture.name) fixture version and distribution." }
            }
            elseif ($null -ne $version -or $null -ne $identity)
            { throw "A compatibility suite must be registered before claiming $($fixture.name) fixture coverage." }
        }
        if ($toolMappings.ContainsKey($result.testProject))
        {
            $tool = $toolMappings[$result.testProject]
            if ($result.kind -cne 'cli' -or $result.exitCode -ne 0 -or $result.passed -ne 1)
            { throw 'CLI evidence must report one successful invocation of the installed tool.' }
            foreach ($name in 'package', 'runtimeConfiguration', 'helpLog')
            {
                $bound = Resolve-CoreEvidenceFile $root $result.$name
                $hash = $result.($name + 'Sha256')
                if ($hash -cnotmatch '^[0-9a-f]{64}$' -or
                    (Get-FileHash -LiteralPath $bound -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash)
                { throw "CLI $name evidence failed its file hash binding." }
            }
            $help = Resolve-CoreEvidenceFile $root $result.helpLog
            if (-not (Select-String -LiteralPath $help -Pattern '^Usage:' -CaseSensitive -Quiet))
            { throw 'CLI output is missing the usage heading.' }
            $runtime = Read-CoreEvidenceJson (Resolve-CoreEvidenceFile $root $result.runtimeConfiguration)
            if ($runtime.runtimeOptions.tfm -cne $result.framework) { throw 'CLI evidence selected a different framework.' }
            $packageFile = Resolve-CoreEvidenceFile $root $result.package
            if ([IO.Path]::GetFileName($packageFile) -cnotmatch ('^' + [regex]::Escape($tool.packageId) + '\.[\w.-]+\.nupkg$'))
            { throw 'CLI evidence contains a different package asset.' }
            $zip = [IO.Compression.ZipFile]::OpenRead($packageFile)
            try
            {
                $toolConfig = "tools/$($result.framework)/any/$($tool.packageId).runtimeconfig.json"
                $configEntry = $zip.GetEntry($toolConfig)
                $settings = $zip.GetEntry("tools/$($result.framework)/any/DotnetToolSettings.xml")
                if ($null -eq $configEntry -or $null -eq $settings)
                { throw 'The CLI package has no tool entrypoint for the claimed framework.' }
                $stream = $configEntry.Open()
                try { $packagedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
                finally { $stream.Dispose() }
                if ($packagedHash -cne $result.runtimeConfigurationSha256)
                { throw 'The installed runtime configuration differs from the packaged CLI asset.' }
            }
            finally { $zip.Dispose() }
            $executed += 1
            continue
        }
        foreach ($binding in @(
            @('trx', 'trxSha256'), @('testAssembly', 'testAssemblySha256'),
            @('runtimeConfiguration', 'runtimeConfigurationSha256')))
        {
            $bound = Resolve-CoreEvidenceFile $root $result.($binding[0])
            if ($result.($binding[1]) -cnotmatch '^[0-9a-f]{64}$' -or
                (Get-FileHash -LiteralPath $bound -Algorithm SHA256).Hash.ToLowerInvariant() -cne $result.($binding[1]))
            {
                throw "Compatibility $($binding[0]) failed its file hash binding."
            }
        }
        $suite = [IO.Path]::GetFileNameWithoutExtension($result.testProject)
        $assembly = Resolve-CoreEvidenceFile $root $result.testAssembly
        $runtimePath = Resolve-CoreEvidenceFile $root $result.runtimeConfiguration
        $runtime = Read-CoreEvidenceJson $runtimePath
        if ([IO.Path]::GetFileName($assembly) -cne "$suite.dll" -or
            $runtime.runtimeOptions.tfm -cne $result.framework)
        {
            throw 'Compatibility test assembly or compiled runtime framework differs from the claimed test pair.'
        }
        $trxPath = Resolve-CoreEvidenceFile $root $result.trx
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 100MB
        $reader = [Xml.XmlReader]::Create($trxPath, $settings)
        $trx = [Xml.XmlDocument]::new()
        $trx.XmlResolver = $null
        try { $trx.Load($reader) } finally { $reader.Dispose() }
        $ns = [Xml.XmlNamespaceManager]::new($trx.NameTable)
        $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
        $counters = @($trx.SelectNodes('/t:TestRun/t:ResultSummary/t:Counters', $ns))
        $summaries = @($trx.SelectNodes('/t:TestRun/t:ResultSummary', $ns))
        $results = @($trx.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns))
        $definitions = @($trx.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $ns))
        $entries = @($trx.SelectNodes('/t:TestRun/t:TestEntries/t:TestEntry', $ns))
        if ($counters.Count -ne 1 -or $summaries.Count -ne 1 -or $summaries[0].GetAttribute('outcome') -cne 'Completed' -or
            $results.Count -ne $result.passed -or $definitions.Count -ne $result.passed -or $entries.Count -ne $result.passed -or
            @($results | Where-Object { $_.GetAttribute('outcome') -cne 'Passed' }).Count -ne 0)
        {
            throw 'Compatibility TRX must contain a completed run with nonzero matching passed results, definitions, and entries.'
        }
        foreach ($attribute in $counters[0].Attributes)
        {
            $expectedCount = if ($attribute.Name -cin @('total', 'executed', 'passed')) { [string]$result.passed } else { '0' }
            if ($attribute.Value -cne $expectedCount) { throw 'Compatibility TRX contains skipped, failed, or inconsistent test counts.' }
        }
        foreach ($counter in 'total', 'executed', 'passed')
        {
            if ($counters[0].GetAttribute($counter) -cne [string]$result.passed) { throw 'Compatibility TRX omits required nonzero test counts.' }
        }
        $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($definition in $definitions)
        {
            if (-not $identities.Add($definition.GetAttribute('id')) -or
                [IO.Path]::GetFileName($definition.TestMethod.GetAttribute('codeBase').Replace('\', '/')) -cne "$suite.dll")
            {
                throw 'Compatibility TRX has duplicate test identities or a different test assembly.'
            }
        }
        foreach ($testResult in $results)
        {
            if (-not $identities.Remove($testResult.GetAttribute('testId'))) { throw 'Compatibility TRX results duplicate or omit a declared test identity.' }
        }
        $executed += [int]$result.passed
    }
}
$missing = @($expected | Where-Object { -not $pairs.Contains($_) } | Sort-Object)
if (($RequireMatrixComplete -or $RequireComplete) -and $missing.Count -ne 0)
{
    throw "Compatibility evidence is missing $($missing.Count) required OS/framework/test pairs: $($missing -join ', ')."
}
$summary = [pscustomobject]@{
    schemaVersion = 1
    sourceCommit = $head
    verifiedPairs = $pairs.Count
    requiredPairs = $expected.Count
    missingPairs = $missing
    passedTests = $executed
    inventoryComplete = $inventory.fullRuntimeCoverage
    matrixComplete = $missing.Count -eq 0
    currentSourceValidated = -not $Historical
    fullRuntimeCoverage = $inventory.fullRuntimeCoverage -and $missing.Count -eq 0 -and -not $Historical
}
if ($PassThru) { return $summary }
Write-Output "Verified $($pairs.Count)/$($expected.Count) compatibility pairs and $executed passed tests. Full runtime coverage: $($summary.fullRuntimeCoverage)."
