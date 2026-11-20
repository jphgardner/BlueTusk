[CmdletBinding(DefaultParameterSetName = 'Start')]
param(
    [Parameter(Mandatory)] [string] $KubernetesContext,
    [Parameter(Mandatory, ParameterSetName = 'Prepare')] [switch] $Prepare,
    [Parameter(Mandatory, ParameterSetName = 'Start')] [ValidateRange(1, [long]::MaxValue)] [long] $RunId,
    [Parameter(Mandatory, ParameterSetName = 'Start')] [ValidatePattern('^bluetusk-deploy-[0-9a-f]{20}$')] [string] $RunnerLabel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$namespace = 'bluetusk-deploy-runners'
$repository = 'jphgardner/BlueTusk'
$api = "repos/$repository"
$kube = @('--context', $KubernetesContext)
$namespaceFile = Join-Path $PSScriptRoot 'kubernetes-deploy-namespace.yaml'
& kubectl @kube apply -f $namespaceFile
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the deployment runner namespace.' }

$service = & kubectl @kube -n default get service kubernetes -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the in-cluster Kubernetes API service.' }
$slices = & kubectl @kube -n default get endpointslice -l kubernetes.io/service-name=kubernetes -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the API service endpoints.' }
$routes = [Collections.Generic.List[object]]::new()
foreach ($address in $service.spec.clusterIPs) {
    $routes.Add(@{ to = @(@{ipBlock = @{cidr = "$address/$(if ($address.Contains(':')) {128} else {32})"}});
                  ports = @(@{protocol = 'TCP'; port = 443}) })
}
foreach ($slice in $slices.items) {
    $ports = @($slice.ports | ForEach-Object { @{protocol = 'TCP'; port = $_.port} })
    $destinations = @($slice.endpoints.addresses | ForEach-Object {
        @{ipBlock = @{cidr = "$_/$(if ($_.Contains(':')) {128} else {32})"}}
    })
    if ($ports.Count -gt 0 -and $destinations.Count -gt 0) { $routes.Add(@{to = $destinations; ports = $ports}) }
}
@{ apiVersion = 'networking.k8s.io/v1'; kind = 'NetworkPolicy'; metadata = @{name = 'kubernetes-api'; namespace = $namespace};
   spec = @{podSelector = @{}; policyTypes = @('Egress'); egress = $routes.ToArray()} } |
    ConvertTo-Json -Depth 15 -Compress | & kubectl @kube apply -f -
if ($LASTEXITCODE -ne 0) { throw 'Could not configure network access to the Kubernetes API.' }
if ($Prepare) { return }

$run = & gh api "$api/actions/runs/$RunId" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $run.path.Split('@')[0] -cne '.github/workflows/deploy-bluetusk.yml' -or
    $run.head_branch -cne 'main' -or $run.event -cne 'workflow_dispatch') { throw 'Expected the protected main deployment workflow.' }
$jobs = & gh api "$api/actions/runs/$RunId/jobs" | ConvertFrom-Json
$matching = @($jobs.jobs | Where-Object { $_.name -ceq 'deploy' -and $_.status -ceq 'queued' -and $RunnerLabel -cin $_.labels })
if ($LASTEXITCODE -ne 0 -or $matching.Count -ne 1) { throw 'The deploy job must be queued with this label after its environment approval.' }
$request = @{name = $RunnerLabel; runner_group_id = 1; labels = @('self-hosted','Linux','X64',$RunnerLabel); work_folder = '_work'}
$registration = $request | ConvertTo-Json -Compress | & gh api -X POST "$api/actions/runners/generate-jitconfig" --input - | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$registration.encoded_jit_config) { throw 'Deployment runner registration failed.' }
try {
    $template = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'kubernetes-deploy-job.yaml') -Raw).Replace('__RUNNER_NAME__',$RunnerLabel)
    $job = $template | & kubectl @kube create -f - -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the deployment runner job.' }
    @{apiVersion = 'v1'; kind = 'Secret'; metadata = @{name = $RunnerLabel; namespace = $namespace;
        ownerReferences = @(@{apiVersion = 'batch/v1'; kind = 'Job'; name = $RunnerLabel; uid = $job.metadata.uid})};
        immutable = $true; type = 'Opaque'; stringData = @{config = $registration.encoded_jit_config}} |
        ConvertTo-Json -Depth 8 -Compress | & kubectl @kube create -f -
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage the one-job deployment credentials.' }
}
catch {
    & kubectl @kube -n $namespace delete job $RunnerLabel --ignore-not-found --wait=false | Out-Null
    & gh api -X DELETE "$api/actions/runners/$($registration.runner.id)" | Out-Null
    throw
}
