param(
    [Parameter(Mandatory)][string]$StableBuild,
    [Parameter(Mandatory)][string]$BetaBuild,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$OutputDir) { $OutputDir = Join-Path $project 'work/dist' }
$output = (New-Item -ItemType Directory -Force $OutputDir).FullName
$stable = (Resolve-Path -LiteralPath (Join-Path $StableBuild 'CoopSolver')).Path
$beta = (Resolve-Path -LiteralPath (Join-Path $BetaBuild 'CoopSolver')).Path
$manifest = Get-Content -LiteralPath "$stable/CoopSolver.json" -Raw | ConvertFrom-Json
$betaManifest = Get-Content -LiteralPath "$beta/CoopSolver.json" -Raw | ConvertFrom-Json
if ($manifest.id -ne 'CoopSolver' -or $betaManifest.id -ne $manifest.id -or $betaManifest.version -ne $manifest.version) {
    throw 'Expected stable and beta CoopSolver builds with the same mod version.'
}

$entries = [ordered]@{}
foreach ($file in @('CoopSolver.dll','CoopSolver.json','CoopSolver-LICENSE.txt','LICENSE','THIRD_PARTY_NOTICES.md')) {
    $entries[$file] = Join-Path $stable $file
}
foreach ($root in @($stable,$beta)) {
    foreach ($file in Get-ChildItem -LiteralPath "$root/licenses" -File -Recurse) {
        $name = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
        if (!$entries.Contains($name)) { $entries[$name] = $file.FullName }
    }
}
foreach ($file in @('CoopBots.Kernel.dll','CoopSolver.Planning.dll')) {
    $entries["compat/0.111/$file"] = Join-Path $stable $file
    $entries["compat/0.114/$file"] = Join-Path $beta $file
}

$target = Join-Path $output "CoopSolver-$($manifest.version).zip"
$temporary = "$target.$([guid]::NewGuid().ToString('N')).tmp"
try {
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entry.Value, "CoopSolver/$($entry.Key)")
        }
    } finally {
        $zip.Dispose()
    }
    [IO.File]::Move($temporary, $target, $true)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Get-Item -LiteralPath $target
