function Get-GameCSharpCompiler {
    $compiler = Get-ChildItem 'C:/Program Files/Microsoft Visual Studio/2022/*/MSBuild/Current/Bin/Roslyn/csc.exe' |
        Select-Object -First 1
    if (!$compiler) { throw 'Visual Studio 2022 C# compiler not found.' }
    $compiler.FullName
}

function Compile-GameLibrary(
    [string]$Compiler, [string]$GameData, [string]$Destination,
    [string[]]$Sources, [string[]]$References = @(), [string[]]$Locales = @()
) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $Destination))
    $gameReferences = @(
        'System.Private.CoreLib', 'System.Runtime', 'System.Linq', 'System.Collections',
        'System.Collections.Concurrent', 'System.Console', 'System.Text.Json', 'System.Security.Cryptography',
        'System.IO.Compression', 'System.IO.FileSystem.DriveInfo', 'System.Text.RegularExpressions',
        'System.Reflection.Primitives', 'System.Reflection.Emit.ILGeneration', 'System.Runtime.Loader',
        'System.Threading', 'System.Runtime.InteropServices', 'System.ObjectModel', 'System.Diagnostics.Process',
        'System.ComponentModel.Primitives', 'System.Memory', 'netstandard', 'GodotSharp', 'sts2', '0Harmony'
    )
    $flags = @('/noconfig', '/nostdlib+', '/target:library', '/langversion:preview', '/nullable:enable',
        '/utf8output', "/out:$Destination")
    $flags += $gameReferences | ForEach-Object { "/reference:$GameData/$_.dll" }
    if ($References.Count) { $flags += $References | ForEach-Object { "/reference:$_" } }
    if ($Locales.Count) {
        $flags += $Locales | ForEach-Object { "/resource:$_,CoopSolver.Locales.$([IO.Path]::GetFileName($_))" }
    }
    & $Compiler @flags @Sources
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Destination" }
}
