using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private sealed record AutoStep(SavedStep Input,string Witness,int Loss,string[] Spent);
    private sealed class AutoLine(OpeningResult result)
    {
        internal readonly OpeningResult Result=result;
        internal readonly List<AutoStep> Steps=new();
        internal int Index;
        internal bool Ready;
        internal int RemainingLoss=>Steps.Skip(Index).Sum(step=>step.Loss);
        internal string[] RemainingItems=>Steps.Skip(Index).SelectMany(step=>step.Spent).ToArray();
    }
    private sealed class Playback(CombatState room,int ceiling)
    {
        internal readonly CombatState Room=room;
        internal readonly int Ceiling=ceiling;
        internal AutoLine? Line,Checking;
        internal IEnumerator<bool>? Replay;
        internal GameAction? Pending;
        internal readonly Queue<KernelChoice> Choices=new();
        internal NativeAutoMenu? Menu;
        internal long SentAt;
        internal readonly HashSet<SeedRoute> Tried=new(ReferenceEqualityComparer.Instance);
    }
    private Playback? playback;
    private OpeningResult? playbackCandidate;
    private string? playbackNotice;
    private Advisor LiveSource=>sharedOpening?this:liveAdvisor!;
    private OpeningResult? PlaybackSelection()
    {
        if(!UseWorker || resultsHidden || SelectingHand || hudFrame?.Realtime?.End==null)return null;
        var result=playbackCandidate;
        return result is {Ending.Verified:true,Seed.Terminal:true,Hud.Party.Length:1} && result.Seed.Steps.Length>0?result:null;
    }
    private void StopPlayback(string? reason=null)
    {
        playback?.Replay?.Dispose();playback=null;
        playbackNotice=reason;nextHud=0;
    }
    private void TogglePlayback()
    {
        if(playback!=null){StopPlayback();return;}
        var room=hudRoom;var source=LiveSource;
        if(hudNet?.Type!=NetGameType.Singleplayer || room?.Players.Count!=1 || !canSolve ||
            PlaybackSelection() is not {} result || !ReferenceEquals(source.board,room) || source.root==null ||
            BoardStamp(room)!=source.stamp)return;
        if(!showLive){showLive=true;switchRequested=true;}
        playbackNotice=null;
        var run=new Playback(room,PastLoss(room.Players[0].NetId)+SavedCost(result).Loss);
        playback=run;
        BeginPlaybackCheck(run,result,source.root);
        nextHud=0;
    }
    internal static bool AllowedItems(IEnumerable<string> remaining,IEnumerable<string> proposed)
    {
        var counts=remaining.GroupBy(s=>s).ToDictionary(g=>g.Key,g=>g.Count());
        foreach(var item in proposed)
        {
            if(counts.GetValueOrDefault(item)<=0)return false;
            counts[item]--;
        }
        return true;
    }
    private static string[] RouteItems(OpeningResult r)=>SavedCost(r).Bottles.Select(b=>"p:"+b.Id)
        .Concat(DeathSaves.Events(r.Hud!.Rounds,r.Hud.Party[0].Id).Where(e=>!e.Potion).Select(e=>"r:"+e.Source)).ToArray();
    internal static bool CanReplacePlayback(int paid,int ceiling,int remainingLoss,string[] remaining,
        int newLoss,string[] proposed,bool currentStillValid,bool improvesOutcome)
    {
        if(paid+newLoss>ceiling || !AllowedItems(remaining,proposed))return false;
        return !currentStillValid || newLoss<remainingLoss || newLoss==remainingLoss &&
            (proposed.Length<remaining.Length || improvesOutcome);
    }
    private static bool CompatibleOutcome(OpeningResult prior,OpeningResult next)=>
        next.Ending is {Verified:true} end && (!prior.Ending!.Victory || end.Victory) &&
        end.IrreversibleLoss<=prior.Ending.IrreversibleLoss && end.OutstandingStolenResource<=prior.Ending.OutstandingStolenResource &&
        (prior.Hud?.Goal is not {} goal || next.Hud?.Goal is {} g && g.Kind==goal.Kind && g.CompareLoss(goal)<=0);
    private static IEnumerator<bool> CheckPlayback(AutoLine line,KernelSession root)
    {
        var seed=line.Result.Seed!;
        var state=root.ForkForReplay();var actor=state.Party.Single();
        var combat=(SimulatedCombatState)state.Simulator.State.CombatState;
        foreach(var input in seed.Steps)
        {
            if(state.HasTerminal || input.Seat!=actor.NetId)yield break;
            var witness=SlCache.Witness(state);
            var receipt=DeathSaves.Read(state);
            int hp=state.Hp(actor.Creature),damage=combat.GetCumulativeHpLost(actor.Creature),heal=combat.GetRecoveredHp(actor.Creature);
            KernelTeamSearch.Action? action=null;
            ReplayCoverage.Begin(state);
            try
            {
                if(input.Recipe is {} recipe)
                {
                    if(!ActionFrontier.Replay(state,recipe,out action,out _))yield break;
                }
                else if(input.Bind(state) is not {} bound || !state.ReplayPlanned(bound,int.MaxValue,out _,out action))yield break;
            }
            finally{ReplayCoverage.End(action);}
            if(action==null || action.Player.NetId!=input.Seat)yield break;
            var items=new List<string>();
            if(action.Potion!=null)items.Add("p:"+action.Potion.Id.Entry);
            for(var e=DeathSaves.Read(state);e!=null && !ReferenceEquals(e,receipt);e=e.Previous)
                items.Add((e.Event.Potion?"p:":"r:")+e.Event.Source);
            int lost=Math.Max(combat.GetCumulativeHpLost(actor.Creature)-damage,
                hp-state.Hp(actor.Creature)+Math.Max(0,combat.GetRecoveredHp(actor.Creature)-heal));
            line.Steps.Add(new(SavedStep.From(root,action,input.Recipe),witness,lost,items.ToArray()));
            yield return true;
        }
        var ending=DeathSaves.WithInventory(TerminalRecord.Capture(state,state.Party,true,""),root,state);
        line.Ready=state.HasTerminal && state.HasWon==seed.Won && state.TurnNumber==seed.Turn &&
            seed.Hp.SequenceEqual(new[]{state.Hp(actor.Creature)}) && state.RemainingOpposition()==(seed.EnemyCount,seed.EnemyHp) &&
            state.HeldPotionCount(actor)==seed.Bottles && ending.IrreversibleLoss==line.Result.Ending!.IrreversibleLoss &&
            GoalResult.For(ending)==line.Result.Hud!.Goal && line.RemainingLoss<=SavedCost(line.Result).Loss &&
            AllowedItems(RouteItems(line.Result),line.RemainingItems);
    }
    private static void BeginPlaybackCheck(Playback run,OpeningResult candidate,KernelSession root)
    {
        run.Tried.Add(candidate.Seed!);run.Checking=new(candidate);run.Replay=CheckPlayback(run.Checking,root);
    }
    private void AdvancePlayback()
    {
        if(playback is not {} run)return;
        if(hudNet?.Type!=NetGameType.Singleplayer || !ReferenceEquals(hudRoom,run.Room) || !CombatManager.Instance.IsInProgress)
        {StopPlayback();return;}
        try{AdvancePlayback(run);}
        catch(Exception e){Godot.GD.PrintErr("[CoopSolver] Auto play: "+e);StopPlayback("自动打牌已停止："+e.GetBaseException().Message);}
    }
    private void AdvancePlayback(Playback run)
    {
        var room=run.Room;var actor=room.Players.Single();
        int paid=PastLoss(actor.NetId);
        if(paid>run.Ceiling){StopPlayback("自动打牌已停止：扣血超过起始路线");return;}
        if(run.Pending is {} pending)
        {
            if(pending.Exception!=null || pending.State==GameActionState.Canceled){StopPlayback("自动打牌已停止：动作未完成");return;}
            if(run.Choices.Count>0 || NPlayerHand.Instance?.IsInCardSelection==true || NOverlayStack.Instance?.Peek()!=null)
            {
                var menu=NativeAutoMenu.Current();
                if(menu!=null)
                {
                    if(!ReferenceEquals(run.Menu?.Identity,menu.Identity))
                    {
                        if(run.Choices.Count==0){StopPlayback("自动打牌已停止：选牌与路线不符");return;}
                        menu.Bind(run.Choices.Dequeue(),actor);run.Menu=menu;
                    }
                    if(Environment.TickCount64-run.SentAt>30000){StopPlayback("自动打牌已停止：选牌未完成");return;}
                    run.Menu!.Advance();return;
                }
                if(pending.State==GameActionState.GatheringPlayerChoice && NOverlayStack.Instance?.Peek()!=null)
                {StopPlayback("自动打牌已停止：无法核对当前选择");return;}
                run.Menu=null;
            }
            if(pending.State!=GameActionState.Finished || !canSolve)
            {
                if(!CombatManager.Instance.IsPaused && Environment.TickCount64-run.SentAt>30000)
                    StopPlayback("自动打牌已停止：动作未完成");
                return;
            }
            run.Pending=null;run.Menu=null;run.Choices.Clear();
        }
        if(!canSolve || CombatManager.Instance.IsPaused || CombatManager.Instance.IsEnding ||
            RunManager.Instance.ActionExecutor.CurrentlyRunningAction!=null || !RunManager.Instance.ActionQueueSet.IsEmpty ||
            NOverlayStack.Instance?.Peek()!=null || NPlayerHand.Instance?.IsInCardSelection==true)return;
        if(captureWarnings.Length!=0 || tickError!=null || LiveSource.CurrentWorkerError!=null)
        {StopPlayback("自动打牌已停止：无法核实当前局面");return;}
        if(run.Replay!=null)
        {
            long start=Stopwatch.GetTimestamp();
            do{if(!run.Replay.MoveNext())break;if(Stopwatch.GetElapsedTime(start).TotalMilliseconds>=3)return;}while(true);
            var next=run.Checking!;run.Checking=null;run.Replay.Dispose();run.Replay=null;
            if(next.Ready && next.Steps.Count>0 && paid+next.RemainingLoss<=run.Ceiling &&
                next.Steps[0].Witness==SlCache.Witness(room) &&
                (run.Line==null || AllowedItems(run.Line.RemainingItems,next.RemainingItems)))run.Line=next;
        }
        var current=run.Line;
        if(current==null){StopPlayback("自动打牌已停止：无法复算起始路线");return;}
        var witness=SlCache.Witness(room);
        bool matches=current.Index<current.Steps.Count && witness==current.Steps[current.Index].Witness;
        var source=LiveSource;
        if(source.root!=null && ReferenceEquals(source.board,room) && source.stamp==BoardStamp(room) &&
            SlCache.Witness(source.root)==witness)
        {
            var offers=source.potions.Rows.Select(r=>r.Value.Result).Append(source.savedResult);
            foreach(var route in offers)
            {
                if(route is not {Ending.Verified:true,Seed.Terminal:true,Hud.Party.Length:1} || run.Tried.Contains(route.Seed) ||
                    !CompatibleOutcome(current.Result,route))continue;
                var cost=SavedCost(route);var items=RouteItems(route);var remain=current.RemainingItems;
                if(!CanReplacePlayback(paid,run.Ceiling,current.RemainingLoss,remain,cost.Loss,items,matches,
                    !current.Result.Ending!.Victory && route.Ending.Victory))continue;
                BeginPlaybackCheck(run,route,source.root);return;
            }
        }
        if(!matches || paid+current.RemainingLoss>run.Ceiling)
        {StopPlayback("自动打牌已停止：当前局面无法沿原损失路线继续");return;}
        var input=current.Steps[current.Index].Input;
        var target=room.Creatures.FirstOrDefault(c=>input.Target.HasValue && c.CombatId==input.Target);
        if(input.Target.HasValue && target==null){StopPlayback("自动打牌已停止：目标已变化");return;}
        GameAction move;
        if(input.Id=="end")move=new EndPlayerTurnAction(actor,actor.PlayerCombatState!.TurnNumber);
        else if(input.Slot>=0)
        {
            var potion=actor.GetPotionAtSlotIndex(input.Slot);
            if(potion?.Id.Entry!=input.Id){StopPlayback("自动打牌已停止：药水槽已变化");return;}
            move=new UsePotionAction(potion,target,true);
        }
        else
        {
            var card=actor.PlayerCombatState!.Hand.Cards.Where(c=>c.Id.Entry==input.Id && KernelSession.LiveCardStateKey(c)==input.Key)
                .Skip(input.Occurrence).FirstOrDefault();
            if(card==null){StopPlayback("自动打牌已停止：手牌已变化");return;}
            move=new PlayCardAction(card,target);
        }
        foreach(var pick in (input.Choices??Array.Empty<KernelChoice>()).Concat(input.Turn?.Choices??Array.Empty<KernelChoice>()))run.Choices.Enqueue(pick);
        current.Index++;run.Tried.Clear();run.Pending=move;run.SentAt=Environment.TickCount64;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(move);
    }
}
