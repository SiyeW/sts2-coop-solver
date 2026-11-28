using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private PlayerChoiceSynchronizer? observedChoiceSync;
    private static Advisor? menuObserver;
    private readonly Dictionary<(ulong Owner,uint Choice),PendingHand> nativeChoices=new();
    private readonly Dictionary<GenericHookGameAction,PendingHand> potionHooks=new();
    private PendingHand? turnStartPending;
    private CombatState? turnStartRoom;

    private void InstallNativeMenus()
    {
        menuObserver=this;
        var harmony=new Harmony("coopsolver.native-choice-observation");
        harmony.Patch(AccessTools.Method(typeof(PlayerChoiceSynchronizer),nameof(PlayerChoiceSynchronizer.ReserveChoiceId)),
            postfix:new HarmonyMethod(typeof(Advisor),nameof(OnChoiceReserved)));
        harmony.Patch(AccessTools.Method(typeof(PlayerChoiceSynchronizer),nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice)),
            postfix:new HarmonyMethod(typeof(Advisor),nameof(OnRemoteChoiceWaiting)));
    }
    private static void OnChoiceReserved(PlayerChoiceSynchronizer __instance,Player __0,uint __result)
    {
        var advisor=menuObserver;
        if(advisor?.observedChoiceSync==__instance)
            advisor.ObserveNativeChoice(__0,__result);
    }
    private static void OnRemoteChoiceWaiting(PlayerChoiceSynchronizer __instance,Player __0,uint __1,Task<PlayerChoiceResult> __result)
    {
        var advisor=menuObserver;
        if(advisor?.observedChoiceSync==__instance && !__result.IsCompleted)
            advisor.ObserveNativeChoice(__0,__1);
    }

    private void ObserveNativeChoice(Player player,uint id)
    {
        var key=(player.NetId,id);
        if(nativeChoices.ContainsKey(key) || hudRoom==null)return;
        // The enemy/turn-start transition runs outside the EndPlayerTurnAction.
        // Replay it from the frozen board captured before the last player ended.
        PendingHand? pending=null;
        if(observedExecutor?.CurrentlyRunningAction is {} action)
            openChoices.TryGetValue(action,out pending);
        pending??=openChoices.Values.LastOrDefault(s=>s.Waiting && s.Action.OwnerId==player.NetId);
        if(pending==null && ReferenceEquals(turnStartRoom,hudRoom) && turnStartPending?.Root is {} start &&
            hudRoom.RoundNumber>start.TurnNumber)
            pending=turnStartPending;
        if(pending==null)return;
        nativeChoices[key]=pending;
        if(!pending.ObservedMenus.Contains(key))pending.ObservedMenus.Add(key);
        OnChoiceWaiting(pending);
        nextHud=nextChoicePaint=0;share?.RefreshGraph();
    }
    private void OnNativeChoiceReceived(Player player,uint id,NetPlayerChoiceResult _)
    {
        if(!nativeChoices.Remove((player.NetId,id),out var pending))return;
        if(!openChoices.ContainsKey(pending.Action) && !nativeChoices.Values.Contains(pending))
        {
            pending.Waiting=false;
            if(pending.Potion!=null)pending.Probe?.Cancel();else pending.Probe?.SetPaused(true);
        }
        nextHud=nextChoicePaint=0;share?.RefreshGraph();
    }

    private PendingHand? ChoiceFor(ulong viewer)=>
        nativeChoices.LastOrDefault(p=>p.Key.Owner==viewer).Value??
        WaitingChoices.LastOrDefault(p=>p.Action.OwnerId==viewer)??
        WaitingChoices.LastOrDefault();

    private bool MatchesVisibleHand(KernelChoice choice)
    {
        if(choice.Pile!=PileType.Hand || choice.Owner!=RunManager.Instance.NetService.NetId ||
            NPlayerHand.Instance is not {IsInCardSelection:true} hand ||
            hudRoom?.Players.FirstOrDefault(player=>player.NetId==choice.Owner)?.PlayerCombatState is not {} combat)
            return true;
        var filter=AccessTools.Field(typeof(NPlayerHand),"_currentSelectionFilter").GetValue(hand) as Func<CardModel,bool>;
        var options=KernelChoice.MenuTokens(combat.Hand.Cards.Where(card=>filter==null || filter(card)).ToArray());
        return (choice.Options==null || choice.Options.SequenceEqual(options)) &&
            choice.Cards.All(card=>CardPresentation.MenuIndex(choice,card,options)>=0);
    }

    private HudPotionMenu? PotionMenu(CombatState room)
    {
        var prompts=new List<HudPotionPrompt>();
        foreach(var pending in PendingChoices)
        {
            if(pending.Action is not UsePotionAction action || HudName.From(pending.Potion?.Title) is not {} potion)continue;
            var active=pending.ObservedMenus.Where(menu=>nativeChoices.TryGetValue(menu,out var owner) &&
                ReferenceEquals(owner,pending)).ToArray();
            var visible=active.Length>0?active:pending.ObservedMenus.TakeLast(1);
            foreach(var menu in visible)
            {
                int index=pending.ObservedMenus.TakeWhile(opened=>opened!=menu).Count(opened=>opened.Owner==menu.Owner);
                var choice=active.Length>0?pending.ActiveRecommendations
                    .Where(pick=>pick.Owner==menu.Owner).ElementAtOrDefault(index):null;
                var cards=choice!=null && MatchesVisibleHand(choice)?ChoiceCards(choice,live:true):null;
                prompts.Add(new(action.OwnerId,menu.Owner,potion,checked((int)action.PotionIndex),
                    cards,choice?.Effect is "Discard" or "DiscardAndDraw",action.State==GameActionState.Finished));
            }
        }
        if(prompts.Count==0)return null;
        var party=room.Players.Select((player,index)=>new HudSeat(player.NetId,playerNames.ElementAtOrDefault(index)??$"P{index+1}",
            "",null,null,false,false,null,Color:CharacterInk(player.Character))).ToArray();
        return new(room.RoundNumber,party,prompts.ToArray());
    }

    private void FinishPotionHook(GameAction action)
    {
        action.AfterFinished-=FinishPotionHook;
        action.BeforeCancelled-=FinishPotionHook;
        if(action is not GenericHookGameAction hook || !potionHooks.Remove(hook,out var source))return;
        foreach(var (key,pending) in nativeChoices.Where(pair=>pair.Key.Owner==action.OwnerId &&
            ReferenceEquals(pair.Value,source)).ToArray())
        {
            nativeChoices.Remove(key);
            pending.Probe?.Cancel();
        }
        nextHud=nextChoicePaint=0;share?.RefreshGraph();
    }

    private void ClearNativeChoices()
    {
        foreach(var (hook,pending) in potionHooks)
        {
            hook.AfterFinished-=FinishPotionHook;
            hook.BeforeCancelled-=FinishPotionHook;
            pending.Probe?.Cancel();
        }
        potionHooks.Clear();
        foreach(var pending in nativeChoices.Values.Distinct())pending.Probe?.Cancel();
        turnStartPending?.Probe?.Cancel();
        turnStartPending=null;turnStartRoom=null;nativeChoices.Clear();
    }
    private void CloseSettledTurnStart(CombatState room)
    {
        // One seat can enter Play before a teammate's turn-start selector has
        // opened. Keep the capture through that round; the next end input replaces it.
        if(turnStartPending?.Root is {} start && room.RoundNumber>start.TurnNumber+1 && nativeChoices.Count==0)
            ClearNativeChoices();
    }
}
