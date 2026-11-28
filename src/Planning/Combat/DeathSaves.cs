using System;
using System.Linq;
using System.Runtime.CompilerServices;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Death;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

internal sealed record Revival(ulong Seat,string Source,bool Potion,int Hp);
internal static class DeathSaves
{
    internal sealed record Entry(Entry? Previous,Revival Event,int Hp);
    private static readonly ConditionalWeakTable<SimulatedCombatState,Entry> ledger=new();
    internal static Entry? Read(KernelSession state)=>Read((SimulatedCombatState)state.Simulator.State.CombatState);
    private static Entry? Read(SimulatedCombatState state)=>ledger.TryGetValue(state,out var entry)?entry:null;

    internal static void Install()
    {
        var harmony=new Harmony("coopsolver.death-saves");
        harmony.Patch(AccessTools.Method(typeof(AfterPreventingDeathMirrors),"Invoke"),
            prefix:new HarmonyMethod(typeof(DeathSaves),nameof(Before)),postfix:new HarmonyMethod(typeof(DeathSaves),nameof(After)));
        var map=typeof(SimulatedCombatState).GetInterfaceMap(typeof(ICombatPredictionForkableState));
        harmony.Patch(map.TargetMethods[Array.FindIndex(map.InterfaceMethods,m=>m.Name=="Fork")],
            postfix:new HarmonyMethod(typeof(DeathSaves),nameof(Forked)));
    }
    private static void Forked(SimulatedCombatState __instance,ICombatState __result)
    {
        if(__instance.DeathSaveUseCount!=0 && Read(__instance) is {} entry)
            ledger.Add((SimulatedCombatState)__result,entry);
    }
    private static int Unattributed(SimulatedCombatState s)=>
        s.DeathSaveRelicHpRestored+s.DeathSavePotionHpRestored-(Read(s)?.Hp??0);
    private static void Before(AfterPreventingDeathMirrorContext __1,out int __state)
        =>__state=__1.CombatState is SimulatedCombatState s?Unattributed(s):-1;
    private static void After(AbstractModel __0,AfterPreventingDeathMirrorContext __1,int __state)
    {
        if(__state<0 || __1.CombatState is not SimulatedCombatState state)return;
        int hp=Unattributed(state)-__state;
        if(hp<=0 || __1.Creature.Player is not {} seat)return;
        var previous=Read(state);
        bool potion=__0 is PotionModel;
        ledger.AddOrUpdate(state,new(previous,new(seat.NetId,__0.Id.Entry,potion,hp),(previous?.Hp??0)+hp));
    }
    internal static int Restored(KernelSession state)
    {
        var combat=(SimulatedCombatState)state.Simulator.State.CombatState;
        return combat.DeathSaveRelicHpRestored+combat.DeathSavePotionHpRestored;
    }
    internal static double Cost(KernelSession state)
    {
        var combat=(SimulatedCombatState)state.Simulator.State.CombatState;
        return Math.Max(Restored(state),state.Party.Sum(p=>combat.GetRecoveredHp(p.Creature)));
    }
    internal static double RecoverySince(KernelSession root,KernelSession state)
    {
        double extra=Cost(state)-Cost(root);
        var current=(SimulatedCombatState)state.Simulator.State.CombatState;
        var initial=(SimulatedCombatState)root.Simulator.State.CombatState;
        double hpGain=state.Party.Sum(p=>state.Hp(p.Creature)-root.Hp(p.Creature)+current.GetCumulativeHpLost(p.Creature)-initial.GetCumulativeHpLost(p.Creature));
        return Math.Max(extra,hpGain);
    }
    internal static int UsedBottles(KernelSession state)=>((SimulatedCombatState)state.Simulator.State.CombatState).PotionUses.Count;
    internal static TerminalRecord WithInventory(TerminalRecord end,KernelSession root,KernelSession state)
    {
        double extra=RecoverySince(root,state);
        int returnHp=end is {Victory:true,Verified:true} && root.Party.Count>1?end.PostCombatHp.Count(h=>h<=0):0;
        return GoalResult.Tag(end with {IrreversibleLoss=returnHp>0?0:end.IrreversibleLoss,ResourcesConsumed=UsedBottles(state)-UsedBottles(root),
            ConsumablePreservationPremium=extra-returnHp},GoalResult.Capture(root,state));
    }
    internal static Revival[] Events(HudRoundHp[]? rounds,ulong seat,int? round=null)=>
        rounds?.Where(r=>!round.HasValue || r.Round==round).SelectMany(r=>r.Revives?.Values.SelectMany(v=>v)??Enumerable.Empty<Revival>())
            .Where(e=>e.Seat==seat).ToArray()??Array.Empty<Revival>();
    internal static int AutomaticBottles(Entry? entry,ulong seat)
    {
        int count=0;
        for(;entry!=null;entry=entry.Previous)if(entry.Event.Seat==seat && entry.Event.Potion)count++;
        return count;
    }
    internal static string Name(Revival e)=>e.Potion
        ? ModelDb.GetByIdOrNull<PotionModel>(new ModelId("POTION",e.Source))?.Title.GetFormattedText()??e.Source
        : ModelDb.GetByIdOrNull<RelicModel>(new ModelId("RELIC",e.Source))?.Title.GetFormattedText()??e.Source;
}
