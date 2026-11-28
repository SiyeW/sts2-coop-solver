param(
    [string]$KernelDll,
    [string]$GameData,
    [string]$RitsuLibReference,
    [string]$RitsuLibRoot,
    [string]$BuildDir,
    [ValidateSet('stable', 'beta')][string]$Profile,
    [string]$RitsuLibSharedDir,
    [string]$UpstreamSource,
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$GameData) { $GameData = Join-Path $project 'work/local-pair/host/data_sts2_windows_x86_64' }
if (!$BuildDir) { $BuildDir = Join-Path $project 'work/build' }
if (!$UpstreamSource) { $UpstreamSource = Join-Path $project 'vendor/CoopBots' }
$data = (Resolve-Path -LiteralPath $GameData).Path
$gameRoot = Split-Path -Parent $data
$releaseInfo = Join-Path $gameRoot 'release_info.json'
$version = $null
if (Test-Path -LiteralPath $releaseInfo) {
    $version = (Get-Content -LiteralPath $releaseInfo -Raw | ConvertFrom-Json).version
    $detected = switch -Regex ($version) {
        '^[vV]?0\.111\.' { 'stable'; break }
        '^[vV]?0\.114\.' { 'beta'; break }
        default { throw "Unsupported game version: $version" }
    }
    if ($Profile -and $Profile -ne $detected) { throw "The $Profile profile cannot be built against game $version." }
    $Profile = $detected
} elseif (!$Profile) {
    throw 'Pass -Profile when using game data without release_info.json.'
}
$BuildDir = (New-Item -ItemType Directory -Force $BuildDir).FullName
$buildInfo = Join-Path $BuildDir 'build-info.json'
[IO.File]::Delete($buildInfo)

$series = if ($Profile -eq 'beta') { '0.114' } else { '0.111' }
if (!$RitsuLibRoot) {
    $RitsuLibRoot = if ($RitsuLibReference) {
        Join-Path (Split-Path -Parent $RitsuLibReference) '../..'
    } else {
        Join-Path $gameRoot 'mods/STS2-RitsuLib'
    }
}
$RitsuLibRoot = (Resolve-Path -LiteralPath $RitsuLibRoot).Path
$bundledRuntime = Join-Path $RitsuLibRoot "compat/$series.0/STS2-RitsuLib.Runtime.dll"
if (!$RitsuLibReference) { $RitsuLibReference = $bundledRuntime }
$ritsu = (Resolve-Path -LiteralPath $RitsuLibReference).Path
if (!$RitsuLibSharedDir) { $RitsuLibSharedDir = Join-Path $RitsuLibRoot 'shared' }
if ((Get-FileHash -LiteralPath $ritsu).Hash -ne (Get-FileHash -LiteralPath $bundledRuntime).Hash) {
    throw "The RitsuLib reference does not match the bundled runtime for $series."
}
if (!$KernelDll) {
    $ritsuDirectory = Split-Path -Parent $ritsu
    $kernelOutput = Join-Path $BuildDir 'kernel'
    $kernelBuild = @{
        Profile = $Profile; GameData = $data; RitsuLibDir = $ritsuDirectory
        RitsuLibSharedDir = $RitsuLibSharedDir; UpstreamSource = $UpstreamSource
        Dotnet = $Dotnet; OutputDir = $kernelOutput
    }
    & "$PSScriptRoot/build-kernel.ps1" @kernelBuild
    $KernelDll = Join-Path $kernelOutput 'CoopBots.Kernel.dll'
}
$KernelDll = (Resolve-Path -LiteralPath $KernelDll).Path

. "$PSScriptRoot/game-compiler.ps1"
. "$PSScriptRoot/kernel-reference.ps1"
$compiler = Get-GameCSharpCompiler
$kernelReference = Join-Path $BuildDir 'references/CoopBots.Kernel.dll'
New-KernelReference $KernelDll $data $kernelReference

function Build-Library([string]$Name, [string]$SourceDir, [string[]]$References = @(),
    [string[]]$AdditionalSources = @()) {
    $out = Join-Path $BuildDir $Name
    $isPlanner = $Name -eq 'CoopSolver.Planning'
    $sources = @(Get-ChildItem -LiteralPath $SourceDir -File -Filter '*.cs' -Recurse:$isPlanner |
        ForEach-Object FullName) + $AdditionalSources
    $locales = if ($isPlanner) {
        @(Get-ChildItem -LiteralPath "$SourceDir/locales" -File -Filter '*.json' | ForEach-Object FullName)
    }
    Compile-GameLibrary $compiler $data "$out/$Name.dll" $sources $References $locales
    $manifest = Join-Path $SourceDir "$Name.json"
    if (Test-Path -LiteralPath $manifest) { Copy-Item -LiteralPath $manifest -Destination $out -Force }
}

Build-Library 'CoopSolver' "$project/src"
Build-Library 'CoopSolver.Planning' "$project/src/Planning" @($kernelReference, $ritsu)
$out = Join-Path $BuildDir 'CoopSolver'
Copy-Item -LiteralPath "$BuildDir/CoopSolver.Planning/CoopSolver.Planning.dll" -Destination $out -Force
Copy-Item -LiteralPath $KernelDll -Destination "$out/CoopBots.Kernel.dll" -Force

$notices = Join-Path $project 'third-party/CoopBots'
Copy-Item -LiteralPath "$project/LICENSE" -Destination "$out/CoopSolver-LICENSE.txt" -Force
foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'licenses')) {
    Copy-Item -LiteralPath (Join-Path $notices $name) -Destination $out -Recurse -Force
}
Copy-Item -LiteralPath "$project/vendor/notices/combat-solver-fool" -Destination "$out/licenses" -Recurse -Force
Copy-Item -LiteralPath "$project/third-party/RitsuLib/LICENSE" -Destination "$out/licenses/RitsuLib-0.6.16-MIT.txt" -Force
Copy-Item -LiteralPath "$project/THIRD_PARTY_NOTICES.md" -Destination "$out/CoopSolver-THIRD-PARTY-NOTICES.md" -Force
$shared = Join-Path $out 'embedded'
$variantRuntime = Join-Path $out "compat/$series/embedded"
[void][IO.Directory]::CreateDirectory($shared)
[void][IO.Directory]::CreateDirectory($variantRuntime)
foreach ($file in @('STS2-RitsuLib.Shared.dll','STS2-RitsuLib.Ui.dll','STS2-RitsuLib.Settings.dll')) {
    Copy-Item -LiteralPath (Join-Path $RitsuLibSharedDir $file) -Destination $shared -Force
}
Copy-Item -LiteralPath $bundledRuntime -Destination $variantRuntime -Force
foreach ($file in @('CoopBots.Kernel.dll','CoopSolver.Planning.dll')) {
    Copy-Item -LiteralPath "$out/$file" -Destination (Split-Path -Parent $variantRuntime) -Force
}
if (Test-Path -LiteralPath "$project/tests/CaseDriver") {
    Build-Library 'CaseDriver' "$project/tests/CaseDriver" @($kernelReference, "$out/CoopSolver.Planning.dll") @("$project/src/Planning/KernelAccess.cs")
}
if (Test-Path -LiteralPath "$project/tests/GuestReady") {
    Build-Library 'GuestReady' "$project/tests/GuestReady"
}
@{profile=$Profile; gameVersion=$version} | ConvertTo-Json | Set-Content -LiteralPath $buildInfo -Encoding utf8
