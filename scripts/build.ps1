param(
    [Parameter(Mandatory)][string]$KernelDll,
    [Parameter(Mandatory)][string]$GameData,
    [Parameter(Mandatory)][string]$RitsuLibReference,
    [string]$BuildDir
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$BuildDir) { $BuildDir = Join-Path $project 'work/build' }
$data = (Resolve-Path -LiteralPath $GameData).Path
$BuildDir = (New-Item -ItemType Directory -Force $BuildDir).FullName
$KernelDll = (Resolve-Path -LiteralPath $KernelDll).Path
$release = Join-Path $project 'third-party/CoopBots'
$csc = Get-ChildItem 'C:/Program Files/Microsoft Visual Studio/2022/*/MSBuild/Current/Bin/Roslyn/csc.exe' | Select-Object -First 1
if (!$csc) {throw 'Visual Studio 2022 C# compiler not found'}
function Build-Library($Name,$SourceDir,$References,$AdditionalSources=@()) {
    $out = Join-Path $BuildDir $Name
    [void][IO.Directory]::CreateDirectory($out)
    $flags = @('/noconfig','/nostdlib+','/target:library','/langversion:preview','/nullable:enable','/utf8output',"/out:$out/$Name.dll")
    $refs = @('System.Private.CoreLib','System.Runtime','System.Linq','System.Collections','System.Collections.Concurrent','System.Console','System.Text.Json','System.Security.Cryptography','System.IO.Compression','System.IO.FileSystem.DriveInfo','System.Text.RegularExpressions','System.Reflection.Primitives','System.Reflection.Emit.ILGeneration','System.Runtime.Loader','System.Threading','System.Runtime.InteropServices','System.ObjectModel','System.Diagnostics.Process','System.ComponentModel.Primitives','System.Memory','netstandard','GodotSharp','sts2','0Harmony')
    foreach($r in $refs) { $flags += "/reference:$data/$r.dll" }
    foreach($r in $References) { $flags += "/reference:$r" }
    if($Name -eq 'CoopSolver.Planning') {
        foreach($locale in Get-ChildItem -LiteralPath "$SourceDir/locales" -Filter '*.json' -File) {
            $flags += "/resource:$($locale.FullName),CoopSolver.Locales.$($locale.Name)"
        }
    }
    $sources = @(Get-ChildItem -LiteralPath $SourceDir -Filter '*.cs' | ForEach-Object FullName) + $AdditionalSources
    & $csc.FullName @flags @sources
    if($LASTEXITCODE -ne 0){throw "Build failed for $Name"}
    if(Test-Path -LiteralPath "$SourceDir/$Name.json") {Copy-Item -LiteralPath "$SourceDir/$Name.json" -Destination $out -Force}
}
Build-Library 'CoopSolver' "$project/src" @()
. "$PSScriptRoot/kernel-reference.ps1"
$kernelReference = Join-Path $BuildDir 'references/CoopBots.Kernel.dll'
New-KernelReference $KernelDll $data $kernelReference
$ritsu = (Resolve-Path -LiteralPath $RitsuLibReference).Path
Build-Library 'CoopSolver.Planning' "$project/src/Planning" @($kernelReference,$ritsu)
$out = "$BuildDir/CoopSolver"
Copy-Item -LiteralPath "$BuildDir/CoopSolver.Planning/CoopSolver.Planning.dll" -Destination $out -Force
Copy-Item -LiteralPath $KernelDll -Destination "$out/CoopBots.Kernel.dll" -Force
Copy-Item -LiteralPath "$project/LICENSE" -Destination "$out/CoopSolver-LICENSE.txt" -Force
foreach($name in @('LICENSE','THIRD_PARTY_NOTICES.md','licenses')) {
    Copy-Item -LiteralPath "$release/$name" -Destination $out -Recurse -Force
}
Copy-Item -LiteralPath "$project/vendor/notices/combat-solver-fool" -Destination "$out/licenses" -Recurse -Force
