using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CoopSolver;

// Use the kernel's potion lifecycle and menu specification for potions whose
// recorded selection is handled by the planner's exact menu cursor.
internal static class ExactPotionChoices
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

    internal static bool Supports(PotionModel potion)=>potion is GamblersBrew or Ashwater or DropletOfPrecognition or LiquidMemories ||
        PotionChoiceSupport.GeneratesCardChoice(potion);
    internal static void Install()
    {
        var hook=AccessTools.Method(typeof(KernelSession),"UsePotion",new[]{
            typeof(PotionModel),typeof(Creature),typeof(IReadOnlyList<KernelChoice>),typeof(string).MakeByRefType()});
        new Harmony("coopsolver.exact-potion-choices").Patch(hook,prefix:new HarmonyMethod(typeof(ExactPotionChoices),nameof(Use)));
    }

    // Do not invent a selection when replaying a potion without a receipt.
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
        var recipient=target?.Player;
        if(!root.CanFork || !root.CanAct(owner) || recipient==null ||
            !root.Party.Contains(recipient) || !potion.IsValidTarget(target) || root.Hp(recipient.Creature)<=0 ||
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
                var spec=Menu(sim,native,recipient);
                var request=new TurnStartChoiceRequest(potion.Id.Entry,spec.Effect,spec.SourcePile,spec.MaxCount,spec,MenuSource:potion.Id.Entry);
                var selected=ExactMenus.ChoosePotion(root,request,recipient);
                var cards=selected!=null?
                    TurnStartChoiceSupport.ResolveTokens(selected,spec.Options,
                        Math.Min(spec.MinCount,spec.Options.Count),Math.Min(spec.MaxCount,spec.Options.Count)):
                    ResolveRecorded(choices,spec,recipient,potion.Id.Entry);
                if(cards==null){reason=Failure(root)="药水选牌与记录不一致";return false;}
                if(!Apply(sim,combat,owner,spec.Effect,cards) ||
                    !PotionExecutionSupport.Complete(sim,combat,native,target,null,history,Deaths(root)) ||
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

    private static CardChoiceSpec Menu(CombatPredictionSimulator simulator,PotionModel potion,Player recipient)
    {
        if(PotionChoiceSupport.GeneratesCardChoice(potion))return PotionChoiceSupport.GetSpec(simulator,potion);
        var (effect,pile,arbitrary)=potion switch
        {
            GamblersBrew=>(PlanChoiceEffect.DiscardAndDraw,PileType.Hand,true),
            Ashwater=>(PlanChoiceEffect.Exhaust,PileType.Hand,true),
            DropletOfPrecognition=>(PlanChoiceEffect.MoveToHand,PileType.Draw,false),
            LiquidMemories=>(PlanChoiceEffect.MoveToHandFreeThisTurn,PileType.Discard,false),
            _=>throw new InvalidOperationException(potion.Id.Entry)
        };
        var cards=simulator.State.GetPlayerCombatState(recipient).GetCardPile(pile)!.Cards;
        return new(effect,pile,arbitrary?0:1,arbitrary?cards.Count:1,cards,cards,0);
    }

    private static IReadOnlyList<PredictedCard>? ResolveRecorded(IReadOnlyList<KernelChoice>? choices,CardChoiceSpec spec,
        Player recipient,string source)
    {
        var receipt=choices?.FirstOrDefault(choice=>choice.Owner==recipient.NetId && choice.Source==source &&
            choice.Pile==spec.SourcePile && choice.Effect==spec.Effect.ToString());
        var selected=receipt?.ResolveMenu(recipient.NetId,spec.Options.Select(c=>c.Preview).ToArray(),
            spec.MinCount,spec.MaxCount,source);
        return selected?.Select(card=>spec.Options.Single(option=>ReferenceEquals(option.Preview,card))).ToArray();
    }

    private static bool Apply(CombatPredictionSimulator simulator,SimulatedCombatState combat,Player creator,
        PlanChoiceEffect effect,IReadOnlyList<PredictedCard> cards)
    {
        if(cards.Count==0)return true;
        switch(effect)
        {
            case PlanChoiceEffect.DiscardAndDraw:
                simulator.DiscardAndDraw(cards,cards.Count);
                break;
            case PlanChoiceEffect.Exhaust:
                foreach(var card in cards)
                {
                    simulator.Exhaust(card);
                    if(!CombatBeamSolver.SettleReplayActionBoundary(simulator,combat))return false;
                }
                break;
            case PlanChoiceEffect.MoveToHandFreeThisTurn:
                foreach(var card in cards)card.SetToFreeThisTurn();
                simulator.AddToPile(cards,PileType.Hand);
                break;
            case PlanChoiceEffect.MoveToHand:
                simulator.AddToPile(cards,PileType.Hand);
                break;
            case PlanChoiceEffect.GenerateToHand:
                var generated=cards.Select(card=>card.Clone()).ToArray();
                foreach(var card in generated)card.SetToFreeThisTurn();
                simulator.AddGeneratedCardsToCombat(generated,PileType.Hand,creator,CardPilePosition.Bottom,CardGenerationResultKind.Random);
                break;
            default:
                throw new InvalidOperationException(effect.ToString());
        }
        return CombatBeamSolver.SettleReplayActionBoundary(simulator,combat);
    }
}
