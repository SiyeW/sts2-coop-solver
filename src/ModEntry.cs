using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Modding;

namespace CoopSolver;

[ModInitializer("Initialize")]
public static class ModEntry
{
    public static void Initialize()
    {
        var assembly = typeof(ModEntry).Assembly;
        var context = AssemblyLoadContext.GetLoadContext(assembly)!;
        if (!context.Assemblies.Any(a => a.GetName().Name == "CoopBots.Kernel"))
            context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "CoopBots.Kernel.dll"));
        var planning=context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "CoopSolver.Planning.dll"));
        planning.GetType("CoopSolver.Advisor",true)!.GetMethod("Attach")!.Invoke(null,null);
    }
}
