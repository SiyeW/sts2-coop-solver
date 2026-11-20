param(
    [Parameter(Mandatory)][string]$GameDir,
    [Parameter(Mandatory)][string]$RitsuRuntime
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$data = Join-Path $GameDir 'data_sts2_windows_x86_64'
if (!(Test-Path -LiteralPath "$data/sts2.dll")) { throw "Cannot find the game's assemblies under $data" }
if (!(Test-Path -LiteralPath $RitsuRuntime -PathType Leaf)) { throw "Cannot find RitsuLib runtime at $RitsuRuntime" }
$release = Join-Path $project '.local/coop-release/CoopBots'
if (!(Test-Path -LiteralPath "$release/CoopBots.Kernel.dll")) {
    $cache = Join-Path $project '.local'
    [void][IO.Directory]::CreateDirectory($cache)
    $zip = Join-Path $cache 'CoopBots-v0.40.0.zip'
    $expected = '4a42d21f1e9c65b9a9d94c8faf4a49180842358a6235928a28934d5ab7924a18'
    if (!(Test-Path -LiteralPath $zip) -or (Get-FileHash -LiteralPath $zip).Hash -ne $expected) {
        Invoke-WebRequest 'https://github.com/imbalilang/STS2-Co-op_Bots/releases/download/v0.40.0/CoopBots-v0.40.0.zip' -OutFile $zip
    }
    if ((Get-FileHash -LiteralPath $zip).Hash -ne $expected) { throw 'Unexpected CoopBots release archive' }
    Expand-Archive -LiteralPath $zip -DestinationPath (Split-Path -Parent $release) -Force
}
if ((Get-FileHash -LiteralPath "$release/CoopBots.Kernel.dll").Hash -ne 'a4f0b0c8f05e4f0965a102cb823b2a12dd11157271953f130e8181a59206b81c') { throw 'Unexpected solver kernel' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$csc = if (Test-Path -LiteralPath $vswhere) {
    & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/Roslyn/csc.exe' | Select-Object -First 1
}
if (!$csc) { throw 'Install Visual Studio 2022 Build Tools with the .NET desktop build tools workload.' }
function Build-Library($Name,$SourceDir,$References,$AdditionalSources=@()) {
    $out = Join-Path $project "build/$Name"
    [void][IO.Directory]::CreateDirectory($out)
    $flags = @('/noconfig','/nostdlib+','/target:library','/langversion:preview','/nullable:enable','/utf8output',"/out:$out/$Name.dll")
    $refs = @('System.Private.CoreLib','System.Runtime','System.Linq','System.Collections','System.Collections.Concurrent','System.Console','System.Text.Json','System.Security.Cryptography','System.IO.Compression','System.Text.RegularExpressions','System.Reflection.Primitives','System.Reflection.Emit.ILGeneration','System.Runtime.Loader','System.Threading','System.Runtime.InteropServices','System.ObjectModel','System.Diagnostics.Process','System.ComponentModel.Primitives','System.Memory','netstandard','GodotSharp','sts2','0Harmony')
    foreach($r in $refs) { $flags += "/reference:$data/$r.dll" }
    foreach($r in $References) { $flags += "/reference:$r" }
    $sources = @(Get-ChildItem -LiteralPath $SourceDir -Filter '*.cs' | ForEach-Object FullName) + $AdditionalSources
    &  $csc @flags @sources
    if($LASTEXITCODE -ne 0){throw "Build failed for $Name"}
    if(Test-Path -LiteralPath "$SourceDir/$Name.json") {Copy-Item -LiteralPath "$SourceDir/$Name.json" -Destination $out -Force}
}
Build-Library 'CoopSolver' "$project/src" @()
. "$PSScriptRoot/kernel-reference.ps1"
$kernelReference = "$project/build/references/CoopBots.Kernel.dll"
New-KernelReference "$release/CoopBots.Kernel.dll" $data $kernelReference
Build-Library 'CoopSolver.Planning' "$project/src/Planning" @($kernelReference,$RitsuRuntime)
$out = "$project/build/CoopSolver"
Copy-Item -LiteralPath "$project/build/CoopSolver.Planning/CoopSolver.Planning.dll" -Destination $out -Force
Copy-Item -LiteralPath "$release/CoopBots.Kernel.dll" -Destination $out -Force
Copy-Item -LiteralPath "$project/LICENSE","$project/THIRD_PARTY_NOTICES.md" -Destination $out -Force
$notices = Join-Path $out 'third-party/CoopBots'
[void][IO.Directory]::CreateDirectory($notices)
Copy-Item -Path "$project/third-party/CoopBots/*" -Destination $notices -Recurse -Force
$version = (Get-Content -Raw -LiteralPath "$out/CoopSolver.json" | ConvertFrom-Json).version
$zipOut = Join-Path $project "build/CoopSolver-$version.zip"
Compress-Archive -LiteralPath $out -DestinationPath $zipOut -Force
Write-Output $zipOut
