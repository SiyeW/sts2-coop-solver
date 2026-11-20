# The released simulation library is kept unmodified in the mod. Generate a
# compiler-only reference with access to its typed mirror registration API.
function New-KernelReference($Kernel, $GameData, $Destination) {
    $cecil = [Reflection.Assembly]::LoadFrom("$GameData/0Harmony.dll")
    $reader = [Activator]::CreateInstance($cecil.GetType('Mono.Cecil.ReaderParameters'), $true)
    $resolver = [Activator]::CreateInstance($cecil.GetType('Mono.Cecil.DefaultAssemblyResolver'), $true)
    $resolver.AddSearchDirectory($GameData)
    $resolver.AddSearchDirectory((Split-Path -Parent $Kernel))
    $reader.AssemblyResolver = $resolver
    $read = $cecil.GetType('Mono.Cecil.ModuleDefinition').GetMethod('ReadModule', [type[]]@([string], $reader.GetType()))
    $module = $read.Invoke($null, @($Kernel, $reader))
    try {
        $friend = $module.Assembly.CustomAttributes | Where-Object { $_.AttributeType.Name -eq 'InternalsVisibleToAttribute' } | Select-Object -First 1
        foreach ($name in 'CoopSolver.Planning', 'CaseDriver') {
            $attribute = [Activator]::CreateInstance($cecil.GetType('Mono.Cecil.CustomAttribute'), @($friend.Constructor))
            $argument = [Activator]::CreateInstance($cecil.GetType('Mono.Cecil.CustomAttributeArgument'), @($module.TypeSystem.String, $name))
            $attribute.ConstructorArguments.Add($argument)
            $module.Assembly.CustomAttributes.Add($attribute)
        }
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $Destination))
        $module.Write($Destination)
    }
    finally { $module.Dispose(); $resolver.Dispose() }
}
