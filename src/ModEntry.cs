using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Godot;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CoopSolver;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    private const string ExternalId = "STS2-RitsuLib";
    private const string ExternalRuntime = ExternalId + ".Runtime";

    public static void Initialize()
    {
        var scene = (SceneTree)Engine.GetMainLoop();
        scene.ProcessFrame += AfterModLoading;

        void AfterModLoading()
        {
            if (ModManager.State != ModManagerState.Initialized)
                return;
            scene.ProcessFrame -= AfterModLoading;
            try
            {
                Start();
            }
            catch (Exception error)
            {
                Log.Error("[CoopSolver] Startup failed: " + error);
            }
        }
    }

    private static void Start()
    {
        var assembly = typeof(ModEntry).Assembly;
        var gameContext = AssemblyLoadContext.GetLoadContext(assembly)!;
        string root = Path.GetDirectoryName(assembly.Location)!;
        string release = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown";
        string series = Version.TryParse(release.TrimStart('v', 'V'), out var version) && version.Major == 0
            ? version.Minor switch { 111 => "0.111", 114 => "0.114", _ => "" }
            : "";
        if (series.Length == 0)
            throw new NotSupportedException("Unsupported game version: " + release);

        string payload = Path.Combine(root, "compat", series);
        if (gameContext.Assemblies.Any(a => a.GetName().Name == "CoopBots.Kernel"))
            throw new InvalidOperationException("A different CoopBots.Kernel is already loaded.");

        var external = gameContext.Assemblies.FirstOrDefault(a => a.GetName().Name == ExternalRuntime);
        bool externalModLoaded = ModManager.GetLoadedMods().Any(mod => mod.manifest?.id == ExternalId);
        bool embedded = !externalModLoaded && external == null &&
            !gameContext.Assemblies.Any(a => a.GetName().Name?.StartsWith(ExternalId, StringComparison.Ordinal) == true);
        AssemblyLoadContext solverContext = gameContext;
        if (embedded)
        {
            solverContext = new EmbeddedLoadContext(gameContext);
        }
        else
        {
            var framework = external?.GetType("STS2RitsuLib.RitsuLibFramework");
            if (!externalModLoaded || external?.GetName().Version < new Version(0, 6, 16, 0) ||
                framework?.GetProperty("IsActive")?.GetValue(null) is not true)
                throw new InvalidOperationException("The active RitsuLib installation is incomplete or too old.");
        }

        string kernelPath = Path.Combine(payload, "CoopBots.Kernel.dll");
        string planningPath = Path.Combine(payload, "CoopSolver.Planning.dll");
        if (!File.Exists(planningPath))
            planningPath = Path.Combine(root, "CoopSolver.Planning.dll");
        if (!File.Exists(kernelPath) || !File.Exists(planningPath))
            throw new FileNotFoundException($"CoopSolver payload for {release} is incomplete: {payload}");

        using var reflectionScope = solverContext.EnterContextualReflection();
        if (embedded)
            EmbeddedRuntime.Start(solverContext, Path.Combine(payload, "embedded"), Path.Combine(root, "embedded"));
        solverContext.LoadFromAssemblyPath(kernelPath);
        var planning = solverContext.LoadFromAssemblyPath(planningPath);
        planning.GetType("CoopSolver.Advisor", true)!.GetMethod("Attach")!.Invoke(null, null);
        Log.Info($"[CoopSolver] RitsuLib provider: {(embedded ? "embedded" : "external")}; game {release}.");
    }
}

internal sealed class EmbeddedLoadContext(AssemblyLoadContext gameContext) : AssemblyLoadContext("CoopSolver embedded")
{
    protected override Assembly? Load(AssemblyName requested)
    {
        // Both sides of a Harmony patch must refer to the same game, engine, and
        // patching types. The bundled RitsuLib assemblies live in this context.
        if (requested.Name is "sts2" or "GodotSharp" or "0Harmony" or "Steamworks.NET" or "SmartFormat")
            return gameContext.LoadFromAssemblyName(requested);
        return null;
    }
}
