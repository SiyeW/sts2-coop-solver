using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

internal static class ContinuationHistory
{

    internal static void Install()
    {
        var patch=new Harmony("coopsolver.continuation-history");
        var history=typeof(CombatPredictionHistory);
        foreach(string method in new[]{"SupportsExecutionContinuation","SupportsManualCardChoice"})
            patch.Patch(AccessTools.Method(history,method),transpiler:new HarmonyMethod(typeof(ContinuationHistory),nameof(Qualify)));
        foreach(string method in new[]{"ForkExecutionContinuation","ForkManualCardChoice"})
            patch.Patch(AccessTools.Method(history,method),transpiler:new HarmonyMethod(typeof(ContinuationHistory),nameof(ExtendCopy)));
    }

    private static bool IsAdditionalObservation(CombatPredictionHistoryEntry entry)=>
        entry is CombatPredictionCardAfflictedEntry or CombatPredictionOrbChanneledEntry or CombatPredictionPotionGeneratedEntry;

    // Extend one existing type test in each allowlist. The original active-play,
    // trace ownership and pending draw/generation checks still run.
    private static bool ExecutionObservation(CombatPredictionHistoryEntry entry)=>
        entry is CombatPredictionAutoPlayFromDrawPileEntry || IsAdditionalObservation(entry);

    private static bool ManualObservation(CombatPredictionHistoryEntry entry)=>
        entry is CombatPredictionCardGenerationResolvedEntry || IsAdditionalObservation(entry);

    private static IEnumerable<CodeInstruction> Qualify(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        bool manual=__originalMethod.Name=="SupportsManualCardChoice";
        var anchor=manual?typeof(CombatPredictionCardGenerationResolvedEntry):typeof(CombatPredictionAutoPlayFromDrawPileEntry);
        var code=instructions.ToList();
        var check=code.Single(i=>i.opcode==OpCodes.Isinst && Equals(i.operand,anchor));
        check.opcode=OpCodes.Call;check.operand=AccessTools.Method(typeof(ContinuationHistory),manual?nameof(ManualObservation):nameof(ExecutionObservation));
        return code;
    }

    private static T ObservedModel<T>(T model,PredictionForkContext context) where T:AbstractModel
    {
        if(context.TryRemap(model,out T? child))return child!;
        if(!model.IsMutable)return model;
        child=PredictionUtils.CloneModelForSimulation(model);
        context.Register(model,child);
        return child;
    }

    private static CombatPredictionHistoryEntry Copy(CombatPredictionHistoryEntry entry,PredictionForkContext context,string unsupported)
    {
        if(entry is CombatPredictionOrbChanneledEntry orb)
            return new CombatPredictionOrbChanneledEntry{Orb=ObservedModel(orb.Orb,context)};
        if(entry is CombatPredictionPotionGeneratedEntry potion)
            return new CombatPredictionPotionGeneratedEntry{Potion=ObservedModel(potion.Potion,context)};
        if(entry is not CombatPredictionCardAfflictedEntry e)throw new InvalidOperationException(unsupported+entry.GetType().Name);
        var effect=e.Affliction;
        if(!context.TryRemap(effect,out AfflictionModel? copy))
        {
            var card=effect.HasCard?PredictionUtils.CloneCardStateForSimulation(effect.Card):null;
            if(card!=null && ReferenceEquals(effect.Card.Affliction,effect))copy=card.Affliction!;
            else
            {
                copy=PredictionUtils.CloneModelForSimulation(effect);
                if(card!=null)copy.Card=card;
            }
            context.Register(effect,copy);
        }
        return new CombatPredictionCardAfflictedEntry{Card=e.Card,Affliction=copy!};
    }

    // The pinned kernel's default switch arm throws. Extend only that arm and
    // rejoin its normal Index, Trace and reference-remapping code for every copy.
    private static IEnumerable<CodeInstruction> ExtendCopy(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        var code=instructions.ToList();
        string unsupported=__originalMethod.Name=="ForkManualCardChoice"?"Unqualified manual card continuation history entry: ":"Unqualified execution continuation history entry: ";
        int start=code.FindIndex(i=>i.opcode==OpCodes.Ldstr && Equals(i.operand,unsupported));
        if(start<2)throw new InvalidOperationException("Missing history copy switch");
        int end=code.FindIndex(start,i=>i.opcode==OpCodes.Throw);
        if(!code[start+1].IsLdloc() || !code[start-2].IsStloc() || !code[end+1].IsLdloc())
            throw new InvalidOperationException("Unrecognized history copy switch");
        var entry=new CodeInstruction(code[start+1].opcode,code[start+1].operand);
        entry.labels.AddRange(code[start].labels);
        var store=new CodeInstruction(code[start-2].opcode,code[start-2].operand);
        code.RemoveRange(start,end-start+1);
        code.InsertRange(start,new[]{entry,new CodeInstruction(OpCodes.Ldarg_2),new CodeInstruction(OpCodes.Ldstr,unsupported),CodeInstruction.Call(typeof(ContinuationHistory),nameof(Copy)),store});
        return code;
    }
}
