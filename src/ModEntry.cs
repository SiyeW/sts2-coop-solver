using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CoopSolver;

[ModInitializer("Initialize")]
public static class ModEntry
{
    public static void Initialize()
    {
        var assembly = typeof(ModEntry).Assembly;
        var context = AssemblyLoadContext.GetLoadContext(assembly)!;
        string root = Path.GetDirectoryName(assembly.Location)!;
        string compat = Path.Combine(root, "compat");
        string gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown";
        bool universal = Directory.Exists(compat);
        string payload = root;
        if (universal)
        {
            string? variant = Version.TryParse(gameVersion.TrimStart('v', 'V'), out var game) && game.Major == 0
                ? game.Minor switch { 111 => "0.111", 114 => "0.114", _ => null }
                : null;
            if (variant == null)
                throw new NotSupportedException($"CoopSolver supports game versions 0.111.x and 0.114.x. Detected {gameVersion}.");
            payload = Path.Combine(compat, variant);
        }

        string kernelPath = Path.Combine(payload, "CoopBots.Kernel.dll");
        string planningPath = Path.Combine(payload, "CoopSolver.Planning.dll");
        if (!File.Exists(kernelPath) || !File.Exists(planningPath))
            throw new FileNotFoundException($"CoopSolver payload for {gameVersion} is incomplete: {payload}");

        var loadedKernel = context.Assemblies.FirstOrDefault(a => a.GetName().Name == "CoopBots.Kernel");
        if (loadedKernel == null)
            context.LoadFromAssemblyPath(kernelPath);
        else if (universal && !string.Equals(loadedKernel.Location, kernelPath, StringComparison.Ordinal))
            throw new InvalidOperationException($"Another CoopBots.Kernel is already loaded from {loadedKernel.Location}.");

        var planning = context.LoadFromAssemblyPath(planningPath);
        planning.GetType("CoopSolver.Advisor", true)!.GetMethod("Attach")!.Invoke(null, null);
        Log.Info($"[CoopSolver] Game {gameVersion}; loaded {Path.GetRelativePath(root, planningPath)}.");
    }
}
