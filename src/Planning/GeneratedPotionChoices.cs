using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

// The kernel exposes the native potion lifecycle and generated menus,
// but its scripted UsePotion leaves these menus unmodeled by default.
internal static class GeneratedPotionChoices
{
    private delegate bool Finish(KernelSession session,PotionModel potion,out string boundary);
    private static readonly Finish FinishEvidence=AccessTools.Method(typeof(KernelSession),"FinishPotionEvidence").CreateDelegate<Finish>();
    private static readonly Func<KernelSession,PotionModel,PotionModel?> FindPotion=
        AccessTools.Method(typeof(KernelSession),"FindCapturedPotion").CreateDelegate<Func<KernelSession,PotionModel,PotionModel?>>();
    private static readonly Func<KernelSession,PotionModel,int> FindSlot=
        AccessTools.Method(typeof(KernelSession),"SimulatedPotionSlot").CreateDelegate<Func<KernelSession,PotionModel,int>>();
    private static readonly Func<KernelSession,Player,TurnStartChoiceCursor> Cursor=
        AccessTools.Method(typeof(KernelSession),"ChoiceCursor").CreateDelegate<Func<KernelSession,Player,TurnStartChoiceCursor>>();
    private static readonly AccessTools.FieldRef<KernelSession,HashSet<uint>> Deaths=
        AccessTools.FieldRefAccess<KernelSession,HashSet<uint>>("processedDeaths");
    private static readonly AccessTools.FieldRef<KernelSession,string?> Failure=
        AccessTools.FieldRefAccess<KernelSession,string?>("failedBoundary");

    internal static bool Supports(PotionModel potion)=>PotionChoiceSupport.GeneratesCardChoice(potion);
    internal static void Install()
    {
        var hook=AccessTools.Method(typeof(KernelSession),"UsePotion",new[]{
            typeof(PotionModel),typeof(Creature),typeof(IReadOnlyList<KernelChoice>),typeof(string).MakeByRefType()});
        new Harmony("coopsolver.generated-potion-choices").Patch(hook,prefix:new HarmonyMethod(typeof(GeneratedPotionChoices),nameof(Use)));
    }

    // Ordinary planning still does not introduce these potions on its own.
    // Enter only when replaying a recorded receipt or solving an observed menu.
    private static bool Use(KernelSession __instance,PotionModel __0,Creature? __1,
        IReadOnlyList<KernelChoice>? __2,ref string __3,ref bool __result)
    {
        if(!Supports(__0) || __2 is not {Count:>0} && !ExactMenus.Observes(__instance))return true;
        __result=Complete(__instance,__0,__1,__2,out __3);
        return false;
    }

    private static bool Complete(KernelSession root,PotionModel potion,Creature? target,
        IReadOnlyList<KernelChoice>? choices,out string reason)
    {
        var owner=potion.Owner;
        if(!root.CanFork || !root.CanAct(owner) || !ReferenceEquals(target,owner.Creature) ||
            FindPotion(root,potion) is not {} native)
        {reason="药水或目标已变化";return false;}
        int slot=FindSlot(root,native);
        if(slot<0){reason="药水槽已变化";return false;}
        var sim=root.Simulator;
        var combat=(SimulatedCombatState)sim.State.CombatState;
        int history=sim.History.Entries.Count;
        Failure(root)="incomplete-action";
        try
        {
            combat.BeginActionChoices(Cursor(root,owner));
            try
            {
                if(!PotionExecutionSupport.Prepare(sim,combat,native,slot,target))
                {reason=Failure(root)="药水选牌前未结算";return false;}
                var spec=PotionChoiceSupport.GetSpec(sim,native);
                var request=new TurnStartChoiceRequest(potion.Id.Entry,spec.Effect,spec.SourcePile,spec.MaxCount,spec,MenuSource:potion.Id.Entry);
                PlanCardChoice? selected=ExactMenus.ChooseGenerated(root,request,owner);
                if(selected==null && choices?.FirstOrDefault() is {} receipt && receipt.Effect==spec.Effect.ToString())
                {
                    var cards=receipt.ResolveMenu(owner.NetId,spec.Options.Select(c=>c.Preview).ToArray(),spec.MinCount,spec.MaxCount,potion.Id.Entry);
                    if(cards!=null)
                        selected=new(spec.Effect,spec.SourcePile,receipt.Cards.Select(c=>new PlanCardToken(
                            c.Id,c.Upgrade,c.StateKey,c.Occurrence,c.Occurrence,c.Id)).ToArray(),potion.Id.Entry);
                }
                if(selected==null || !PotionExecutionSupport.Complete(sim,combat,native,target,selected,history,Deaths(root)) ||
                    !CombatBeamSolver.SettleReplayActionBoundary(sim,combat))
                {reason=Failure(root)="药水选牌或结算与记录不一致";return false;}
            }
            finally{combat.EndActionChoices();}
            sim.CheckWinCondition(combat.GetPlayerTurnNumber(owner));
            return FinishEvidence(root,potion,out reason);
        }
        catch(Exception error) when(error is InvalidOperationException or ArgumentException or NotSupportedException)
        {reason=Failure(root)="药水选牌："+error.GetBaseException().Message;return false;}
    }
}
