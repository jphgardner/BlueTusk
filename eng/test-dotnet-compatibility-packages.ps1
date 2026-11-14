[CmdletBinding()]
param([string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$verifier = Join-Path $PSScriptRoot 'verify-dotnet-compatibility-packages.ps1'
$contract = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dotnet-compatibility.json') -Raw | ConvertFrom-Json
$root = Join-Path $RepositoryRoot ('artifacts/dotnet-compatibility/package-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$frameworks = @($contract.frameworks)
$toolId = 'BlueTusk.Streams.Tool'
$libraryId = 'BlueTusk.Protocol'

function New-PackageFixture
{
    param([string] $Directory, [string] $Id, [string[]] $Assets, [string[]] $DependencyGroups, [switch] $Tool,
        [string] $WrongConfigFramework, [string] $MissingAssemblyFramework, [string] $WrongAssemblyFramework)

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $Directory "$Id.1.2.0.nupkg"), [IO.Compression.ZipArchiveMode]::Create)
    try
    {
        function Write-TestEntry([string] $Name, [string] $Content)
        {
            $entry = $archive.CreateEntry($Name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write($Content) } finally { $writer.Dispose() }
        }

        $metadata = if ($Tool) { '<packageTypes><packageType name="DotnetTool" /></packageTypes>' }
        else
        {
            $groups = @($DependencyGroups | ForEach-Object { '<group targetFramework="' + $_ + '" />' }) -join ''
            "<dependencies>$groups</dependencies>"
        }
        Write-TestEntry "$Id.nuspec" "<package xmlns=`"http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd`"><metadata><id>$Id</id><version>1.2.0</version>$metadata</metadata></package>"
        foreach ($framework in $Assets)
        {
            $folder = if ($Tool) { "tools/$framework/any" } else { "lib/$framework" }
            if ($framework -cne $MissingAssemblyFramework)
            {
                $assemblyFramework = if ($framework -ceq $WrongAssemblyFramework) { $frameworks[-1] } else { $framework }
                $builder = [Reflection.Emit.PersistedAssemblyBuilder]::new([Reflection.AssemblyName]::new($Id), [object].Assembly)
                $null = $builder.DefineDynamicModule('SyntheticCompatibilityPackage')
                $constructor = [Runtime.Versioning.TargetFrameworkAttribute].GetConstructor([type[]]@([string]))
                $attribute = [Reflection.Emit.CustomAttributeBuilder]::new($constructor, [object[]]@('.NETCoreApp,Version=v' + $assemblyFramework.Substring(3)))
                $builder.SetCustomAttribute($attribute)
                $binary = $archive.CreateEntry("$folder/$Id.dll").Open()
                try { $builder.Save($binary) } finally { $binary.Dispose() }
            }
            Write-TestEntry "$folder/$Id.xml" '<doc />'
            if ($Tool)
            {
                Write-TestEntry "$folder/DotnetToolSettings.xml" "<DotNetCliTool Version=`"1`"><Commands><Command Name=`"bluetusk-streams`" EntryPoint=`"$Id.dll`" Runner=`"dotnet`" /></Commands></DotNetCliTool>"
                $compiled = if ($framework -ceq $WrongConfigFramework) { $frameworks[-1] } else { $framework }
                Write-TestEntry "$folder/$Id.runtimeconfig.json" (@{ runtimeOptions = @{ tfm = $compiled } } | ConvertTo-Json -Compress)
            }
        }
    }
    finally { $archive.Dispose() }
}

function Assert-PackageRejected
{
    param([string] $Directory, [string] $Id, [string] $ExpectedMessage)
    $rejected = $false
    try { & $verifier -PackageDirectory $Directory -PackageId $Id -RepositoryRoot $RepositoryRoot | Out-Null }
    catch
    {
        if ($_.Exception.Message -notmatch $ExpectedMessage) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Compatibility package verification accepted invalid fixture '$Directory'." }
}

$valid = Join-Path $root 'valid'
New-PackageFixture -Directory $valid -Id $libraryId -Assets $frameworks -DependencyGroups $frameworks
New-PackageFixture -Directory $valid -Id $toolId -Assets $frameworks -Tool
$report = & $verifier -PackageDirectory $valid -PackageId @($toolId, $libraryId) -RepositoryRoot $RepositoryRoot -PassThru
if ($report.packages.Count -ne 2 -or $report.allRuntimeAssetsVerified)
{ throw 'The package verifier accepted two fixtures as a complete distribution.' }

$missing = Join-Path $root 'missing-target'
New-PackageFixture -Directory $missing -Id $libraryId -Assets @($frameworks | Select-Object -Skip 1) -DependencyGroups $frameworks
Assert-PackageRejected $missing $libraryId 'lib assets frameworks differ'
$wrongGroups = Join-Path $root 'wrong-groups'
New-PackageFixture -Directory $wrongGroups -Id $libraryId -Assets $frameworks -DependencyGroups @($frameworks | Select-Object -Skip 1)
Assert-PackageRejected $wrongGroups $libraryId 'nuspec dependency groups frameworks differ'
$extra = Join-Path $root 'unexpected-target'
New-PackageFixture -Directory $extra -Id $toolId -Assets @($frameworks + @('net4.0')) -Tool
Assert-PackageRejected $extra $toolId 'tools assets frameworks differ'
$wrongConfig = Join-Path $root 'wrong-runtimeconfig'
New-PackageFixture -Directory $wrongConfig -Id $toolId -Assets $frameworks -Tool -WrongConfigFramework $frameworks[0]
Assert-PackageRejected $wrongConfig $toolId 'wrong runtime configuration'
$missingDll = Join-Path $root 'missing-library-dll'
New-PackageFixture -Directory $missingDll -Id $libraryId -Assets $frameworks -DependencyGroups $frameworks -MissingAssemblyFramework $frameworks[0]
Assert-PackageRejected $missingDll $libraryId 'missing its .* assembly'
$wrongAssembly = Join-Path $root 'wrong-assembly-target'
New-PackageFixture -Directory $wrongAssembly -Id $libraryId -Assets $frameworks -DependencyGroups $frameworks -WrongAssemblyFramework $frameworks[0]
Assert-PackageRejected $wrongAssembly $libraryId 'different compiled target framework'
Assert-PackageRejected $missing $toolId 'Missing selected runtime packages'
Write-Output 'Verified runtime package qualification rejects missing or unexpected assets, wrong nuspec groups, wrong or missing assemblies, wrong CLI runtime configuration, and missing package artifacts.'
