using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Saves.Runs;
using System.Linq;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

internal static class ProofEvidence
{
    private static readonly ConcurrentDictionary<Type,bool> persistent=new();
    private static bool AccountedRelic(RelicModel relic)
    {
        if(relic.GetType().Assembly!=typeof(RelicModel).Assembly)return false;
        // Periodic counters and fixed map/reward/encounter counters don't spend a
        // choice-dependent one-shot charge. Lizard Tail has its own resource receipt.
        if(RelicCounterCatalog.Identify(relic)!=null || relic is LizardTail or SwordOfStone or LastingCandy or
            WingedBoots or SilverCrucible or Girya or BookOfFiveRings or PaelsWing)return true;
        return !persistent.GetOrAdd(relic.GetType(),type=>{
            for(var t=type;t!=null && t!=typeof(RelicModel);t=t.BaseType)
                if(t.GetMembers(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)
                    .Any(m=>(m is PropertyInfo || m is FieldInfo) && m.IsDefined(typeof(SavedPropertyAttribute),false)))return true;
            return false;
        });
    }

    internal static bool Supported(KernelSession root,KernelSession state)
    {
        if(!state.CanFork || root.ProjectionDifferences.Count!=0 || state.ProjectionDifferences.Count!=0 ||
            state.LastActionHadEnvironmentalRisk && !string.IsNullOrWhiteSpace(state.LastActionRiskDetail))return false;
        var initial=(SimulatedCombatState)root.Simulator.State.CombatState;
        var current=(SimulatedCombatState)state.Simulator.State.CombatState;
        foreach(var player in state.Party)
        {
            // The action frontier binds potions by captured physical identity.
            // A generated replacement cannot silently disappear from a proof.
            if(!state.HasTerminal && state.HeldPotionCount(player)!=root.CapturedPotions(player).Count(state.HoldsPotion))return false;
            var relics=current.RelicsOf(player);
            if(relics.Any(r=>!AccountedRelic(r)) ||
                !initial.RelicsOf(player).Select(r=>(r.Id,r.IsMelted)).SequenceEqual(relics.Select(r=>(r.Id,r.IsMelted))))return false;
        }
        return true;
    }

    internal static bool Apply(KernelSession root,KernelSession state,ActionFrontier? moves,ref TreeStep? step,
        HashSet<string>? risks,out KernelTeamSearch.Action? action)
    {
        if(risks!=null)ReplayCoverage.Begin(state);
        try
        {
            bool ok=moves!=null?moves.Expand(state,out step,out action,out _):
                ActionFrontier.Replay(state,step!,out action,out _);
            if(risks!=null)ReplayCoverage.Collect(state,risks);
            return ok && Supported(root,state);
        }
        finally{if(risks!=null)ReplayCoverage.End();}
    }
}
