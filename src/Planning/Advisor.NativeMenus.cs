using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private PlayerChoiceSynchronizer? observedChoiceSync;
    private static Advisor? menuObserver;
    private readonly Dictionary<(ulong Owner,uint Choice),PendingHand> nativeChoices=new();
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
        OnChoiceWaiting(pending);
    }
    private void OnNativeChoiceReceived(Player player,uint id,NetPlayerChoiceResult _)
    {
        if(!nativeChoices.Remove((player.NetId,id),out var pending))return;
        if(!openChoices.ContainsKey(pending.Action) && !nativeChoices.Values.Contains(pending))
        {
            pending.Waiting=false;
            pending.Probe?.SetPaused(true);
        }
        nextHud=nextChoicePaint=0;
    }

    private PendingHand? ChoiceFor(ulong viewer)=>
        nativeChoices.LastOrDefault(p=>p.Key.Owner==viewer).Value??
        WaitingChoices.LastOrDefault(p=>p.Action.OwnerId==viewer)??
        WaitingChoices.LastOrDefault();

    private void ClearNativeChoices()
    {
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
