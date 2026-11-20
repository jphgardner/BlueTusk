Set-StrictMode -Version Latest

function Get-PerformanceProviderVariants
{
    param(
        [Parameter(Mandatory)] $Contract,
        [Parameter(Mandatory)][ValidateSet('windows', 'linux')][string] $Os
    )

    $provider = $Contract.workloads.Provider
    if ($Contract.release -ceq '1.2.0') { return $provider.variantsByEnvironment.$Os }
    return $provider.variants
}

Export-ModuleMember -Function Get-PerformanceProviderVariants
