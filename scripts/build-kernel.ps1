param(
    [Parameter(Mandatory)][string]$GameData,
    [Parameter(Mandatory)][string]$RitsuLibDir,
    [string]$RitsuLibSharedDir,
    [string]$UpstreamSource,
    [string]$CompatibilityPatch,
    [string]$Dotnet = 'dotnet',
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$UpstreamSource) { $UpstreamSource = Join-Path $project 'vendor/CoopBots' }
if (!$OutputDir) { $OutputDir = Join-Path $project 'work/build/kernel' }
$GameData = (Resolve-Path -LiteralPath $GameData).Path
$RitsuLibDir = (Resolve-Path -LiteralPath $RitsuLibDir).Path
$patches = @('coopbots-planning-state.patch', 'coopbots-route-order.patch') |
    ForEach-Object { Join-Path $project "vendor/patches/$_" }
if ($CompatibilityPatch) { $patches += (Resolve-Path -LiteralPath $CompatibilityPatch).Path }
$revision = git -C $project rev-parse 'HEAD:vendor/CoopBots'
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the pinned CoopBots source revision.' }

$kernelProject = 'src/CoopBots.Kernel/CoopBots.Kernel.csproj'
if (!(Test-Path -LiteralPath (Join-Path $UpstreamSource $kernelProject))) {
    throw 'Initialize vendor/CoopBots or pass -UpstreamSource with the CoopBots source checkout.'
}

$patchHash = ($patches | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash.Substring(0,12) }) -join '-'
$staged = Join-Path $project "work/kernel-source/$($revision.Substring(0,12))-$patchHash"
if (!(Test-Path -LiteralPath (Join-Path $staged '.git'))) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $staged) | Out-Null
    git -c core.longpaths=true -C $UpstreamSource worktree add --detach $staged $revision
    if ($LASTEXITCODE -ne 0) { throw 'Cannot stage the pinned CoopBots source.' }
    git -c core.longpaths=true -C $staged apply @patches
    if ($LASTEXITCODE -ne 0) { throw 'Cannot apply the CoopBots kernel patches.' }
}
if ($CompatibilityPatch) {
    # The compatibility patch may replace lines introduced by the common
    # patches. Reverse the stack against a disposable index in order.
    $reversePatches = $patches.Clone()
    [array]::Reverse($reversePatches)
    $temporaryIndex = Join-Path ([IO.Path]::GetTempPath()) "coopsolver-kernel-$([guid]::NewGuid()).index"
    $previousIndex = $env:GIT_INDEX_FILE
    try {
        $env:GIT_INDEX_FILE = $temporaryIndex
        git -c core.longpaths=true -C $staged read-tree $revision
        if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize the kernel verification index.' }
        git -c core.longpaths=true -C $staged add -A
        if ($LASTEXITCODE -ne 0) { throw 'Cannot snapshot the staged kernel for verification.' }
        foreach ($patch in $reversePatches) {
            git -c core.longpaths=true -C $staged apply --cached --reverse $patch
            if ($LASTEXITCODE -ne 0) { throw "The staged kernel does not contain $patch." }
        }
        git -c core.longpaths=true -C $staged diff --cached --quiet $revision --
        if ($LASTEXITCODE -ne 0) { throw 'The staged kernel contains changes outside the patch stack.' }
    } finally {
        $env:GIT_INDEX_FILE = $previousIndex
        if (Test-Path -LiteralPath $temporaryIndex) { Remove-Item -LiteralPath $temporaryIndex }
    }
} else {
    git -c core.longpaths=true -C $staged apply --reverse --check @patches
    if ($LASTEXITCODE -ne 0) { throw 'The staged kernel is missing the required optimization patch.' }
}

$properties = @("-p:STS2DataDir=$GameData", "-p:RitsuLibDir=$RitsuLibDir", '-p:NuGetAudit=false')
if ($RitsuLibSharedDir) {
    $properties += "-p:RitsuLibSharedDir=$((Resolve-Path -LiteralPath $RitsuLibSharedDir).Path)"
}
& $Dotnet build (Join-Path $staged $kernelProject) -c Release @properties -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw 'The optimized CoopBots kernel did not compile.' }
