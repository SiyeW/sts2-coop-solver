[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Build')][string]$GameData,
    [Parameter(Mandatory, ParameterSetName = 'Build')][string]$RitsuLibDir,
    [Parameter(Mandatory, ParameterSetName = 'Source')][switch]$StageOnly,
    [ValidateSet('stable', 'beta')][string]$Profile = 'stable',
    [string]$RitsuLibSharedDir,
    [string]$UpstreamSource,
    [string]$CompatibilityPatch,
    [string]$Dotnet = 'dotnet',
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$UpstreamSource) { $UpstreamSource = Join-Path $project 'vendor/CoopBots' }

function Invoke-KernelGit([string]$Repository, [string[]]$CommandArgs) {
    $result = & git -c core.longpaths=true -C $Repository @CommandArgs
    if ($LASTEXITCODE -ne 0) { throw "Git failed in $Repository ($($CommandArgs[0]))." }
    $result
}

$patchDirectory = Join-Path $project 'vendor/patches'
$betaPatch = Join-Path $patchDirectory 'coopbots-beta-0.114.patch'
$extraPatch = $null
if ($CompatibilityPatch) {
    $compatibility = (Resolve-Path -LiteralPath $CompatibilityPatch).Path
    if ($compatibility -eq $betaPatch) {
        if ($PSBoundParameters.ContainsKey('Profile') -and $Profile -ne 'beta') {
            throw 'The beta compatibility patch requires the beta profile.'
        }
        $Profile = 'beta'
    } else {
        $extraPatch = $compatibility
    }
}

$patches = @(Join-Path $patchDirectory 'coopbots-common.patch')
if ($Profile -eq 'beta') { $patches += $betaPatch }
if ($extraPatch) { $patches += $extraPatch }

$kernelProject = 'src/CoopBots.Kernel/CoopBots.Kernel.csproj'
if (!(Test-Path -LiteralPath (Join-Path $UpstreamSource $kernelProject))) {
    throw 'Initialize vendor/CoopBots or pass -UpstreamSource with the CoopBots source checkout.'
}
$UpstreamSource = (Resolve-Path -LiteralPath $UpstreamSource).Path
$revision = Invoke-KernelGit $project @('rev-parse', 'HEAD:vendor/CoopBots')

function Get-KernelTree([string]$Repository, [string]$Base, [string[]]$PatchFiles, [switch]$WorkingTree) {
    $index = Join-Path ([IO.Path]::GetTempPath()) "coopsolver-kernel-$([guid]::NewGuid()).index"
    $previousIndex = $env:GIT_INDEX_FILE
    try {
        $env:GIT_INDEX_FILE = $index
        Invoke-KernelGit $Repository @('read-tree', $Base)
        if ($WorkingTree) {
            Invoke-KernelGit $Repository @('add', '-A')
        } else {
            foreach ($file in $PatchFiles) {
                Invoke-KernelGit $Repository @('apply', '--cached', $file)
            }
        }
        Invoke-KernelGit $Repository @('write-tree')
    } finally {
        $env:GIT_INDEX_FILE = $previousIndex
        if (Test-Path -LiteralPath $index) { Remove-Item -LiteralPath $index }
    }
}

# Assemble the verified source as a Git tree before materializing it. The cache
# key identifies that exact tree, independent of patch formatting or file names.
$expectedTree = Get-KernelTree $UpstreamSource $revision $patches
$staged = Join-Path $project "work/kernel-source/$Profile-$($expectedTree.Substring(0,16))"
if (!(Test-Path -LiteralPath (Join-Path $staged '.git'))) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $staged) | Out-Null
    $commit = Invoke-KernelGit $UpstreamSource @(
        '-c', 'user.name=CoopSolver Build', '-c', 'user.email=build@coopsolver.invalid',
        'commit-tree', $expectedTree, '-p', $revision, '-m', "Materialize CoopSolver kernel ($Profile)"
    )
    Invoke-KernelGit $UpstreamSource @('worktree', 'add', '--detach', $staged, $commit) | Out-Host
}
if ((Get-KernelTree $staged $expectedTree -WorkingTree) -ne $expectedTree) {
    throw "The materialized kernel source contains local changes at $staged."
}

if ($StageOnly) {
    Get-Item -LiteralPath $staged
    return
}

if (!$OutputDir) { $OutputDir = Join-Path $project 'work/build/kernel' }
$GameData = (Resolve-Path -LiteralPath $GameData).Path
$RitsuLibDir = (Resolve-Path -LiteralPath $RitsuLibDir).Path
$properties = @("-p:STS2DataDir=$GameData", "-p:RitsuLibDir=$RitsuLibDir", '-p:NuGetAudit=false')
if ($RitsuLibSharedDir) {
    $properties += "-p:RitsuLibSharedDir=$((Resolve-Path -LiteralPath $RitsuLibSharedDir).Path)"
}
& $Dotnet build (Join-Path $staged $kernelProject) -c Release @properties -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw 'The CoopSolver kernel did not compile.' }
