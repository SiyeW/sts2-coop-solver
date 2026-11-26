using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Orbs;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopSolver;

// Record the origin of indirect effects during route replay without changing
// the simulator's damage dealer or the timing of its effects.
internal static class DamageAttribution
{
    private const string OrbSource="充能球";
    internal sealed record Credit(ulong Seat,string Source,TurnKillEffect Effect=TurnKillEffect.None);
    private static readonly ConditionalWeakTable<CombatPredictionDamageReceivedEntry,Credit> credits=new();
    private static readonly ConditionalWeakTable<CombatPredictionSimulator,Dictionary<Creature,ulong?>> doomKills=new();
    internal static void Install()
    {
        var hook=AccessTools.Method(typeof(AfterCardPlayedMirrors),"HandleHauntPower");
        var harmony=new Harmony("coopsolver.damage-credit");
        harmony.Patch(hook,prefix:new HarmonyMethod(typeof(DamageAttribution),nameof(Before)),
            postfix:new HarmonyMethod(typeof(DamageAttribution),nameof(After)));
        foreach(string method in new[]{"InvokePassive","InvokeEvoke"})
            harmony.Patch(AccessTools.Method(typeof(OrbMirrors),method),
                prefix:new HarmonyMethod(typeof(DamageAttribution),nameof(BeforeOrb)),
                postfix:new HarmonyMethod(typeof(DamageAttribution),nameof(AfterOrb)));
        harmony.Patch(AccessTools.Method(typeof(OrbLifecycleSupport),nameof(OrbLifecycleSupport.TriggerBeforeTurnEnd)),
            prefix:new HarmonyMethod(typeof(DamageAttribution),nameof(BeforeOrb)),
            postfix:new HarmonyMethod(typeof(DamageAttribution),nameof(AfterEndOrbs)));
        harmony.Patch(AccessTools.Method(typeof(CorePowerSupport),"TriggerPoison"),
            prefix:new HarmonyMethod(typeof(DamageAttribution),nameof(BeforePoison)),
            postfix:new HarmonyMethod(typeof(DamageAttribution),nameof(AfterPoison)));
        harmony.Patch(AccessTools.Method(typeof(EndTurnPowerSupport),nameof(EndTurnPowerSupport.TriggerEnemyDoom)),
            prefix:new HarmonyMethod(typeof(DamageAttribution),nameof(BeforeDoom)),
            postfix:new HarmonyMethod(typeof(DamageAttribution),nameof(AfterDoom)));
    }
    internal static Credit? Get(CombatPredictionDamageReceivedEntry hit)=>credits.TryGetValue(hit,out var c)?c:null;
    internal static int Amount(CombatPredictionDamageReceivedEntry hit)=>hit.Result.BlockedDamage+hit.Result.UnblockedDamage;
    internal static ulong? Seat(CombatPredictionDamageReceivedEntry hit,Credit? credit)=>
        credit?.Seat??(hit.Dealer?.Player??hit.Dealer?.PetOwner??hit.CardSource?.Original?.Owner)?.NetId;
    private static void Before(AfterCardPlayedMirrorContext __1,out int __state)
        =>__state=ReplayCoverage.IsTracking(__1.Simulator)?__1.Simulator.History.Count():-1;
    private static void After(HauntPower __0,AfterCardPlayedMirrorContext __1,int __state)
    {
        if(__state<0 || __0.Owner.Player is not {} player)return;
        CreditHits(__1.Simulator,__state,player.NetId,"纠缠");
    }
    private static void BeforeOrb(CombatPredictionSimulator __0,out int __state)
        =>__state=ReplayCoverage.IsTracking(__0)?__0.History.Count():-1;
    private static void AfterOrb(CombatPredictionSimulator __0,OrbModel __1,int __state)
    {
        if(__state>=0)CreditHits(__0,__state,__1.Owner.NetId,OrbSource);
    }

    private static void AfterEndOrbs(CombatPredictionSimulator __0,Player __2,int __state)
    {
        if(__state<0)return;
        foreach(var entry in __0.History.EntriesFrom(__state))
        {
            if(entry is not CombatPredictionDamageReceivedEntry {Result.WasTargetKilled:true} hit)continue;
            var credit=Get(hit);
            if(credit?.Source!=OrbSource && hit.Source.Kind!=CombatDamageSourceKind.Orb)continue;
            credits.Remove(hit);
            credits.Add(hit,new(credit?.Seat??__2.NetId,OrbSource,TurnKillEffect.Orb));
        }
    }
    private static void CreditHits(CombatPredictionSimulator sim,int start,ulong seat,string source)
    {
        var history=sim.History;
        for(int i=start,n=history.Count();i<n;i++)
            if(history[i] is CombatPredictionDamageReceivedEntry hit && hit.Dealer?.Player==null &&
                hit.Dealer?.PetOwner==null && hit.CardSource?.Original?.Owner==null && Amount(hit)>0)
                credits.GetValue(hit,_=>new(seat,source));
    }

    private static ulong? Applier(PowerModel? power)=>power?.Applier?.Player?.NetId??power?.Applier?.PetOwner?.NetId;

    private sealed record PoisonTick(int Begin,Dictionary<Creature,ulong> Appliers);
    private static void BeforePoison(CombatPredictionSimulator __0,SimulatedCombatState __1,
        IEnumerable<Creature> __2,out PoisonTick? __state)
    {
        __state=null;
        if(!ReplayCoverage.IsTracking(__0))return;
        var appliers=new Dictionary<Creature,ulong>();
        foreach(var creature in __2)
            if(Applier(__1.GetPower<PoisonPower>(creature)) is ulong applier)appliers[creature]=applier;
        if(appliers.Count>0)__state=new(__0.History.Count(),appliers);
    }

    private static void AfterPoison(CombatPredictionSimulator __0,PoisonTick? __state)
    {
        if(__state==null)return;
        foreach(var entry in __0.History.EntriesFrom(__state.Begin))
            if(entry is CombatPredictionDamageReceivedEntry hit && hit.Source.Kind==CombatDamageSourceKind.Poison &&
                __state.Appliers.TryGetValue(hit.Receiver,out ulong seat))
                credits.GetValue(hit,_=>new(seat,"中毒",TurnKillEffect.Poison));
    }

    private static void BeforeDoom(CombatPredictionSimulator __0,SimulatedCombatState __1,
        IEnumerable<Creature> __2,out Dictionary<Creature,ulong?>? __state)
    {
        __state=null;
        if(!ReplayCoverage.IsTracking(__0))return;
        foreach(var creature in __2)
        {
            var unit=__0.State.GetCreature(creature);
            if(unit.IsAlive && __1.GetAmount<DoomPower>(creature)>=unit.CurrentHp)
                (__state??=new())[creature]=Applier(__1.GetPower<DoomPower>(creature));
        }
    }

    private static void AfterDoom(CombatPredictionSimulator __0,Dictionary<Creature,ulong?>? __state)
    {
        if(__state==null)return;
        foreach(var (creature,seat) in __state)
            if(__0.State.GetCreature(creature).IsDead)
                doomKills.GetOrCreateValue(__0)[creature]=seat;
    }

    internal static Dictionary<Creature,ulong?>? TakeDoomKills(CombatPredictionSimulator sim)
    {
        if(!doomKills.TryGetValue(sim,out var result))return null;
        doomKills.Remove(sim);
        return result;
    }
}
