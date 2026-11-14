[CmdletBinding()]
param(
    [string[]] $Framework,
    [string[]] $TestProject,
    [string] $DotNetPath = 'dotnet',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/dotnet-compatibility/runs'),
    [switch] $UseLegacyLinuxContainers,
    [switch] $RequireComplete,
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string] $ContractPath = (Join-Path $PSScriptRoot 'dotnet-compatibility.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-test-evidence.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'dotnet-compatibility-postgresql.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'dotnet-compatibility-minio.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'dotnet-compatibility-tool.psm1') -DisableNameChecking
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
$inventory = & (Join-Path $PSScriptRoot 'verify-dotnet-compatibility.ps1') -RepositoryRoot $RepositoryRoot -ContractPath $ContractPath -RequireComplete:$RequireComplete -PassThru
if (-not $Framework -or $Framework.Count -eq 0) { $Framework = @($contract.frameworks) }
if (@($Framework | Sort-Object -Unique).Count -ne $Framework.Count -or
    @($Framework | Where-Object { $_ -cnotin $contract.frameworks }).Count -ne 0)
{
    throw 'Choose unique frameworks listed in the .NET compatibility contract.'
}
if ($TestProject)
{
    if ($RequireComplete) { throw 'Complete runtime validation requires every registered suite.' }
    $registeredTests = @($contract.packages | Where-Object status -CEQ 'covered' | ForEach-Object { $_.testProjects } | Sort-Object -Unique) + @($contract.cliTools.project)
    if (@($TestProject | Sort-Object -Unique).Count -ne $TestProject.Count -or
        @($TestProject | Where-Object { $_ -cnotin $registeredTests }).Count -ne 0)
    {
        throw 'Choose unique test project paths registered in the .NET compatibility contract.'
    }
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$workspacePrefix = $RepositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $OutputRoot.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase))
{
    throw 'Compatibility evidence must be written below the selected repository root.'
}
$DotNetPath = (Get-Command -Name $DotNetPath -CommandType Application -ErrorAction Stop).Source
$runRoot = Join-Path $OutputRoot ([guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $runRoot 'build'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$environmentNames = @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_HOST_PATH', 'DOTNET_ROLL_FORWARD',
    'DOTNET_ROLL_FORWARD_TO_PRERELEASE', 'BLUETUSK_TEST_CONNECTION_STRING', 'BLUETUSK_S3_ENDPOINT',
    'BLUETUSK_S3_ACCESS_KEY', 'BLUETUSK_S3_SECRET_KEY')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
$results = [System.Collections.Generic.List[object]]::new()
$failure = $null
$postgresqlFixtures = @{}
$minio = $null
$sourceCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit for compatibility evidence.' }
$sourceDirty = @(& git -C $RepositoryRoot status --porcelain --untracked-files=normal).Count -ne 0
$sourceFingerprint = Get-CoreTestSourceFingerprint -ExpectedCommit $sourceCommit
$fingerprintPath = Join-Path $runRoot 'source-fingerprint.json'
$sourceFingerprint | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fingerprintPath -Encoding utf8
$sourceUnchanged = $false
$contractSha256 = (Get-FileHash -LiteralPath $ContractPath -Algorithm SHA256).Hash.ToLowerInvariant()
try
{
    $dotnetRoot = Split-Path $DotNetPath -Parent
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetRoot)
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotnetRoot)
    [Environment]::SetEnvironmentVariable('DOTNET_HOST_PATH', $DotNetPath)
    # Permit servicing patches, never a different runtime major. A passing net5
    # test therefore cannot be satisfied by rolling it forward to .NET 11.
    [Environment]::SetEnvironmentVariable('DOTNET_ROLL_FORWARD', 'LatestPatch')
    [Environment]::SetEnvironmentVariable('DOTNET_ROLL_FORWARD_TO_PRERELEASE', '0')
    $sdkVersion = (& $DotNetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -cne $contract.sdkVersion)
    {
        throw "Compatibility validation requires SDK $($contract.sdkVersion); selected '$sdkVersion'."
    }
    $installedRuntimes = @(& $DotNetPath --list-runtimes)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot list installed .NET runtimes.' }
    foreach ($target in $Framework)
    {
        $runtimeVersion = [string] $contract.runtimeVersions.$target
        $runtimeMajorPrefix = $target -replace '^net', ''
        $containerProperty = $contract.legacyLinuxContainers.PSObject.Properties[$target]
        $container = if ($UseLegacyLinuxContainers -and $null -ne $containerProperty) { $containerProperty.Value } else { $null }
        $runtimeListing = if ($null -ne $container)
        {
            @(& docker run --rm $container.image dotnet --list-runtimes)
        }
        else { $installedRuntimes }
        if ($LASTEXITCODE -ne 0 -or
            @($runtimeListing | Where-Object { $_ -cmatch "^Microsoft\.NETCore\.App $([regex]::Escape($runtimeVersion)) \[" }).Count -ne 1 -or
            @($runtimeListing | Where-Object { $_ -cmatch "^Microsoft\.NETCore\.App $([regex]::Escape($runtimeMajorPrefix))\.\d+ \[" }).Count -ne 1)
        {
            throw "Exactly the pinned runtime patch $runtimeVersion is required for '$target'. Use an isolated install-dotnet-compatibility.ps1 directory without other patches of that runtime major."
        }
        $targetMajor = [int]($target -replace '^net|\.0$', '')
        $testProjects = @($contract.packages | Where-Object {
            $_.status -ceq 'covered' -and [int]($_.minimumFramework -replace '^net|\.0$', '') -le $targetMajor
        } | ForEach-Object { $_.testProjects } | Sort-Object -Unique)
        if ($TestProject)
        {
            $testProjects = @($testProjects | Where-Object { $_ -cin $TestProject })
        }
        $toolProjects = @($contract.cliTools | Where-Object {
            $toolPackage = $_.project
            $minimum = ($contract.packages | Where-Object project -CEQ $toolPackage).minimumFramework
            [int]($minimum -replace '^net|\.0$', '') -le $targetMajor -and (-not $TestProject -or $toolPackage -cin $TestProject)
        })
        if ($testProjects.Count -eq 0 -and $toolProjects.Count -eq 0) { throw "No executable compatibility coverage is registered for '$target'." }
        $operatingSystem = if ($null -ne $container -or $IsLinux) { 'linux' } elseif ($IsWindows) { 'windows' } else { 'macos' }
        foreach ($testProject in $testProjects)
        {
            $requiresPgVector = $testProject -cin $contract.postgreSqlFixture.pgVector.testProjects
            $requiresMinio = $testProject -cin $contract.minioFixture.testProjects
            $extensionFixture = $contract.postgreSqlExtensionFixtures | Where-Object { $testProject -cin $_.testProjects }
            $postgresqlFixtureKey = if ($requiresPgVector) { 'vector' }
                elseif ($null -ne $extensionFixture) { $extensionFixture.extension }
                elseif ($testProject -cin $contract.postgreSqlFixture.testProjects) { 'core' }
                else { $null }
            $requiresPostgreSql = $null -ne $postgresqlFixtureKey
            $postgresql = $null
            if ($requiresPostgreSql)
            {
                if (-not $postgresqlFixtures.ContainsKey($postgresqlFixtureKey))
                {
                    $fixtureRoot = Join-Path $RepositoryRoot ('artifacts/dotnet-compatibility/postgresql/' + [guid]::NewGuid().ToString('N'))
                    $fixtureContract = if ($null -ne $extensionFixture) { $extensionFixture } else { $contract.postgreSqlFixture }
                    $extension = if ($null -ne $extensionFixture) { $extensionFixture.extension } else { $null }
                    $postgresqlFixtures[$postgresqlFixtureKey] = Start-DotNetCompatibilityPostgreSql -Contract $fixtureContract `
                        -WorkDirectory $fixtureRoot -DownloadCache (Join-Path $RepositoryRoot 'artifacts/dotnet-downloads') `
                        -WithPgVector:$requiresPgVector -Extension $extension -UseDocker:($IsLinux -or $UseLegacyLinuxContainers)
                }
                $postgresql = $postgresqlFixtures[$postgresqlFixtureKey]
            }
            if ($requiresMinio -and $null -eq $minio)
            {
                $fixtureRoot = Join-Path $RepositoryRoot ('artifacts/dotnet-compatibility/minio/' + [guid]::NewGuid().ToString('N'))
                $minio = Start-DotNetCompatibilityMinio -Contract $contract.minioFixture -WorkDirectory $fixtureRoot
            }
            foreach ($name in @('BLUETUSK_TEST_CONNECTION_STRING', 'BLUETUSK_S3_ENDPOINT', 'BLUETUSK_S3_ACCESS_KEY', 'BLUETUSK_S3_SECRET_KEY'))
            { [Environment]::SetEnvironmentVariable($name, $null) }
            $suite = [IO.Path]::GetFileNameWithoutExtension($testProject)
            $suiteRoot = Join-Path $runRoot "$target/$suite"
            New-Item -ItemType Directory -Path $suiteRoot -Force | Out-Null
            $buildLog = Join-Path $suiteRoot 'build.log'
            $testLog = Join-Path $suiteRoot 'test.log'
            $testPath = Join-Path $RepositoryRoot $testProject
            & $DotNetPath build $testPath -c $Configuration -f $target --artifacts-path $buildRoot "-p:BlueTuskRuntimeTargetFrameworks=$target" -p:CheckEolTargetFramework=false -v:minimal *> $buildLog
            if ($LASTEXITCODE -ne 0) { throw "Building '$suite' for '$target' failed. See '$buildLog'." }
            $targetPath = @(& $DotNetPath msbuild $testPath "-p:ArtifactsPath=$buildRoot" -p:UseArtifactsOutput=true "-p:Configuration=$Configuration" "-p:TargetFramework=$target" "-p:BlueTuskRuntimeTargetFrameworks=$target" -getProperty:TargetPath)
            if ($LASTEXITCODE -ne 0 -or $targetPath.Count -ne 1 -or -not (Test-Path -LiteralPath $targetPath[0] -PathType Leaf))
            {
                throw "Cannot locate isolated build output for '$suite' on '$target'."
            }
            $runtimeConfigPath = [IO.Path]::ChangeExtension($targetPath[0], 'runtimeconfig.json')
            $runtimeConfiguration = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
            if ($runtimeConfiguration.runtimeOptions.tfm -cne $target)
            {
                throw "'$suite' was not compiled for the requested actual framework '$target'."
            }
            $proofRoot = Join-Path $suiteRoot 'proof'
            New-Item -ItemType Directory -Path $proofRoot -Force | Out-Null
            Copy-Item -LiteralPath $targetPath[0], $runtimeConfigPath -Destination $proofRoot
            $proofAssembly = Join-Path $proofRoot "$suite.dll"
            $proofRuntime = Join-Path $proofRoot "$suite.runtimeconfig.json"
            if ($null -ne $container)
            {
                $stage = Join-Path $suiteRoot 'test-output'
                New-Item -ItemType Directory -Path $stage -Force | Out-Null
                $buildOutput = Split-Path $targetPath[0] -Parent
                Get-ChildItem -LiteralPath $buildOutput | Copy-Item -Destination $stage -Recurse -Force
                # SDK 6's VSTest expects this array. SDK 11 omits it from the
                # development config; stage a portable config without changing
                # the original build output or adding machine-specific probes.
                '{"runtimeOptions":{"additionalProbingPaths":[]}}' | Set-Content -LiteralPath (Join-Path $stage "$suite.runtimeconfig.dev.json") -Encoding utf8
                $containerStage = '/repo/' + $stage.Substring($workspacePrefix.Length).Replace('\', '/')
                $containerResults = '/repo/' + $suiteRoot.Substring($workspacePrefix.Length).Replace('\', '/')
                $dockerArguments = @()
                if ($requiresPostgreSql)
                {
                    $env:BLUETUSK_TEST_CONNECTION_STRING = $postgresql.containerConnection
                    $dockerArguments += @('--network', "container:$($postgresql.container)", '--env', 'BLUETUSK_TEST_CONNECTION_STRING')
                }
                if ($requiresMinio)
                {
                    if (-not $IsLinux) { throw 'Run the .NET Linux S3 matrix on a Linux host so it can share the native fixture loopback.' }
                    $env:BLUETUSK_S3_ENDPOINT = $minio.endpoint
                    $env:BLUETUSK_S3_ACCESS_KEY = $minio.accessKey
                    $env:BLUETUSK_S3_SECRET_KEY = $minio.secretKey
                    $dockerArguments += @('--network', 'host', '--env', 'BLUETUSK_S3_ENDPOINT',
                        '--env', 'BLUETUSK_S3_ACCESS_KEY', '--env', 'BLUETUSK_S3_SECRET_KEY')
                }
                & docker run --rm @dockerArguments --mount "type=bind,source=$RepositoryRoot,target=/repo" --workdir /repo --env DOTNET_ROLL_FORWARD=LatestPatch --env DOTNET_ROLL_FORWARD_TO_PRERELEASE=0 $container.image dotnet "/usr/share/dotnet/sdk/$($container.sdkVersion)/vstest.console.dll" "$containerStage/$suite.dll" '/Logger:trx;LogFileName=results.trx' "/ResultsDirectory:$containerResults" *> $testLog
            }
            else
            {
                if ($requiresPostgreSql) { $env:BLUETUSK_TEST_CONNECTION_STRING = $postgresql.connection }
                if ($requiresMinio)
                {
                    $env:BLUETUSK_S3_ENDPOINT = $minio.endpoint
                    $env:BLUETUSK_S3_ACCESS_KEY = $minio.accessKey
                    $env:BLUETUSK_S3_SECRET_KEY = $minio.secretKey
                }
                & $DotNetPath test $testPath -c $Configuration -f $target --artifacts-path $buildRoot --no-build --no-restore "-p:BlueTuskRuntimeTargetFrameworks=$target" -p:CheckEolTargetFramework=false --logger 'trx;LogFileName=results.trx' --results-directory $suiteRoot -v:minimal *> $testLog
            }
            if ($LASTEXITCODE -ne 0) { throw "Testing '$suite' on actual '$target' failed. See '$testLog'." }
            $trxPath = Join-Path $suiteRoot 'results.trx'
            if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { throw "No TRX results were produced for '$suite' on '$target'." }
            [xml] $trx = Get-Content -LiteralPath $trxPath -Raw
            $counters = $trx.TestRun.ResultSummary.Counters
            if ([int]$counters.total -le 0 -or [int]$counters.total -ne [int]$counters.executed -or
                [int]$counters.executed -ne [int]$counters.passed -or [int]$counters.failed -ne 0 -or
                @($trx.TestRun.Results.UnitTestResult | Where-Object outcome -CNE 'Passed').Count -ne 0)
            {
                throw "Compatibility results require nonzero tests, zero failures, and zero skipped tests for '$suite' on '$target'."
            }
            $results.Add([pscustomobject]@{
                framework = $target
                runtimeVersion = $runtimeVersion
                testProject = $testProject
                passed = [int]$counters.passed
                operatingSystem = $operatingSystem
                containerImage = $(if ($null -ne $container) { $container.image } else { $null })
                postgreSqlVersion = $(if ($requiresPostgreSql) { $postgresql.version } else { $null })
                postgreSqlIdentity = $(if ($requiresPostgreSql) { $postgresql.identity } else { $null })
                pgVectorVersion = $(if ($requiresPgVector) { $postgresql.extensionVersion } else { $null })
                pgVectorIdentity = $(if ($requiresPgVector) { $postgresql.extensionIdentity } else { $null })
                postgreSqlExtensionVersion = $(if ($null -ne $extensionFixture) { $postgresql.extensionVersion } else { $null })
                postgreSqlExtensionIdentity = $(if ($null -ne $extensionFixture) { $postgresql.extensionIdentity } else { $null })
                minioVersion = $(if ($requiresMinio) { $minio.version } else { $null })
                minioIdentity = $(if ($requiresMinio) { $minio.identity } else { $null })
                trx = [IO.Path]::GetRelativePath($runRoot, $trxPath).Replace('\', '/')
                trxSha256 = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash.ToLowerInvariant()
                testAssembly = [IO.Path]::GetRelativePath($runRoot, $proofAssembly).Replace('\', '/')
                testAssemblySha256 = (Get-FileHash -LiteralPath $proofAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
                runtimeConfiguration = [IO.Path]::GetRelativePath($runRoot, $proofRuntime).Replace('\', '/')
                runtimeConfigurationSha256 = (Get-FileHash -LiteralPath $proofRuntime -Algorithm SHA256).Hash.ToLowerInvariant()
            })
            Write-Output "$target $suite`: $($counters.passed) passed on runtime $runtimeVersion."
        }
        foreach ($tool in $toolProjects)
        {
            $suiteRoot = Join-Path $runRoot "$target/$($tool.packageId)"
            New-Item -ItemType Directory -Path $suiteRoot -Force | Out-Null
            $proof = Invoke-DotNetCompatibilityTool -Tool $tool -Framework $target -DotNetPath $DotNetPath `
                -RepositoryRoot $RepositoryRoot -SuiteRoot $suiteRoot -BuildRoot $buildRoot -Configuration $Configuration -Container $container
            $result = [ordered]@{
                kind = 'cli'; framework = $target; runtimeVersion = $runtimeVersion
                testProject = $tool.project; passed = 1; operatingSystem = $operatingSystem
                containerImage = $(if ($null -ne $container) { $container.image } else { $null })
                postgreSqlVersion = $null; postgreSqlIdentity = $null
                pgVectorVersion = $null; pgVectorIdentity = $null
                postgreSqlExtensionVersion = $null; postgreSqlExtensionIdentity = $null
                minioVersion = $null; minioIdentity = $null
                exitCode = $proof.exitCode
            }
            foreach ($name in 'helpLog', 'package', 'runtimeConfiguration')
            {
                $file = $proof.$name
                $result[$name] = [IO.Path]::GetRelativePath($runRoot, $file).Replace('\', '/')
                $result[$name + 'Sha256'] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            $results.Add([pscustomobject]$result)
            Write-Output "$target $($tool.command): installed package --help passed on runtime $runtimeVersion."
        }
    }
    $finalFingerprint = Get-CoreTestSourceFingerprint -ExpectedCommit $sourceCommit
    $sourceUnchanged = (ConvertTo-Json -InputObject $sourceFingerprint -Depth 8 -Compress) -ceq
        (ConvertTo-Json -InputObject $finalFingerprint -Depth 8 -Compress)
}
catch
{
    $failure = $_.Exception.Message
}
finally
{
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name]) }
    foreach ($fixture in $postgresqlFixtures.Values) { Stop-DotNetCompatibilityPostgreSql $fixture }
    if ($null -ne $minio) { Stop-DotNetCompatibilityMinio $minio }
    $report = [ordered]@{
        schemaVersion = 2
        sourceCommit = $sourceCommit
        sourceTreeDirty = $sourceDirty
        requestedFrameworks = $Framework
        sdkVersion = $contract.sdkVersion
        inventoryComplete = $inventory.fullRuntimeCoverage
        fullRuntimeCoverage = $false
        contractSha256 = $contractSha256
        sourceFingerprint = 'source-fingerprint.json'
        sourceFingerprintSha256 = (Get-FileHash -LiteralPath $fingerprintPath -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceUnchangedDuringRun = $sourceUnchanged
        coveredRuntimePackages = $inventory.coveredRuntimePackages
        pendingRuntimePackages = $inventory.pendingRuntimePackages
        succeeded = $null -eq $failure
        failure = $failure
        results = @($results)
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runRoot 'report.json') -Encoding utf8
}
if ($null -ne $failure) { throw $failure }
Write-Output "Compatibility evidence: $(Join-Path $runRoot 'report.json'). Inventory complete: $($inventory.fullRuntimeCoverage). Full qualification requires all Windows/Linux framework pairs."
