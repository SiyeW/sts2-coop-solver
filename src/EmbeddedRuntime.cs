using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace CoopSolver;

internal static class EmbeddedRuntime
{
    private const string Prefix = "STS2-RitsuLib.";
    private const string Framework = "STS2RitsuLib.RitsuLibFramework";
    private const string Network = "STS2RitsuLib.Networking.Sidecar.Patches.";
    private const string Lifecycle = "STS2RitsuLib.Lifecycle.Patches.";

    internal static void Start(AssemblyLoadContext context, string payload, string shared)
    {
        context.LoadFromAssemblyPath(Path.Combine(shared, Prefix + "Shared.dll"));
        context.LoadFromAssemblyPath(Path.Combine(shared, Prefix + "Ui.dll"));
        context.LoadFromAssemblyPath(Path.Combine(shared, Prefix + "Settings.dll"));
        var runtime = context.LoadFromAssemblyPath(Path.Combine(payload, Prefix + "Runtime.dll"));

        // Use RitsuLib's patch metadata with a CoopSolver owner. Do not start
        // framework UI, telemetry, update checks, or global content discovery.
        var create = runtime.GetType(Framework, true)!.GetMethod("CreatePatcher")!;
        var patcher = create.Invoke(null, new[]
        {
            (object)"CoopSolver", "embedded-ritsu", "CoopSolver embedded runtime",
            create.GetParameters()[3].DefaultValue!
        })!;
        var register = runtime.GetType("STS2RitsuLib.Patching.Core.ModPatcherExtensions", true)!
            .GetMethods().Single(method => method.Name == "RegisterPatch" && method.IsGenericMethodDefinition);
        foreach (string type in new[]
        {
            Network + "RitsuLibSidecarNetReceivePatch",
            Network + "RitsuLibSidecarNativeTrailerSendPatch",
            Network + "RitsuLibSidecarNativeTrailerSteamSendPatch",
            Network + "RitsuLibSidecarSyncNetBufferPatch",
            Network + "RitsuLibSidecarSyncLocationChangedPatch",
            Network + "RitsuLibSidecarLobbyHelloPatch",
            Network + "RitsuLibSidecarStartRunLobbyHostClientConnectedPatch",
            Network + "RitsuLibSidecarStartRunLobbyHostClientDisconnectedPatch",
            Network + "RitsuLibSidecarPreRunCapabilityGatePatch",
            Lifecycle + "RunEndedLifecyclePatch",
            "STS2RitsuLib.Cards.Patches.DynamicVarTooltipClonePatch"
        })
            register.MakeGenericMethod(runtime.GetType(type, true)!).Invoke(null, new[] { patcher });

        if (patcher.GetType().GetMethod("PatchAll")!.Invoke(patcher, null) is not true)
            throw new InvalidOperationException("The embedded RitsuLib patches could not be installed.");
    }
}
