using System.Linq;
using System.Collections.Generic;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace CoopSolver;
internal sealed partial class Advisor
{
    internal sealed record Played(ulong Seat,string Kind,string Id,uint? Target,int Slot,CardModel? Physical,CardCopy? Copy=null)
    {
        internal bool Matches(KernelTeamSearch.Action expected,KernelSession? origin)
        {
            if(expected.Player.NetId!=Seat || expected.Target?.CombatId!=Target)return false;
            // Matching inputs can propose a suffix; the new root must replay it before confirmation.
            if(Kind=="end")return expected.EndTurn;
            if(Kind=="potion")return expected.Potion is {} bottle && bottle.Id.Entry==Id && origin!=null && CapturedBelt.Slot(origin,bottle)==Slot;
            if(expected.Card?.Id.Entry!=Id)return false;
            if(Copy!=null)return expected.CardStateKey==Copy.Key;
            // When both physical copies exist in this hand, their different state can matter.
            if(Physical!=null && origin!=null && origin.Hand(expected.Player).Contains(Physical) &&
                origin.Hand(expected.Player).Contains(expected.Card!))return origin.CardStateKey(Physical)==origin.CardStateKey(expected.Card!);
            return origin!=null && origin.Hand(expected.Player).Where(c=>c.Id.Entry==Id).Select(origin.CardStateKey).Distinct().SequenceEqual(new[]{expected.CardStateKey});
        }
    }
    private readonly List<Played> completedInputs=new();
    private sealed record PresentedRoute(HudPlan Plan,KernelSession Origin,HudExecuted Progress);
    private PresentedRoute? presentedRoute;

    private void ObservePresentedStep(Played input)
    {
        if(input.Kind is not ("card" or "potion"))return;
        var advisor=sharedOpening?this:liveAdvisor!;
        if(advisor.root is not {} origin)return;
        var potion=ReferenceEquals(advisor.potionRoot,origin)?advisor.displayedPotion:null;
        var chosen=potion?.Result??advisor.savedResult;
        if(chosen?.Hud is not {} plan)return;
        var prior=presentedRoute;
        int[] used=ReferenceEquals(prior?.Plan,plan) && ReferenceEquals(prior.Origin,origin)
            ?prior.Progress.Steps:System.Array.Empty<int>();
        var line=potion?.Line??advisor.shownLine;
        int[]? consumed=null;
        if(line!=null && dependencyGraphs.TryGet(plan,origin,line,origin.TurnNumber,out var proof) &&
            proof is {Entry:{Graph:{Verified:true},Round:{Start:0}}})
            consumed=ConsumeOrder(line,proof,used,new[]{input},origin);
        else if(chosen.Seed?.Steps.ElementAtOrDefault(used.Length) is {} next &&
            next.Seat==input.Seat && next.Id==input.Id && next.Target==input.Target &&
            (input.Kind=="potion"?next.Slot==input.Slot:next.Slot<0 && input.Copy?.Key==next.Key))
            consumed=used.Append(used.Length).ToArray();
        if(consumed!=null)presentedRoute=new(plan,origin,new(origin.TurnNumber,consumed));
    }

    private HudExecuted? PresentedSteps(HudPlan? plan)=>presentedRoute is {} shown &&
        ReferenceEquals(shown.Plan,plan)?shown.Progress:null;
    // AfterFinished can remove the choice prompt before the executor publishes AfterActionExecuted.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayCardAction,Played> startedCards=new();
    private Played CapturePlay(PlayCardAction play)
    {
        var card=play.NetCombatCard.ToCardModelOrNull() ?? AccessTools.Field(typeof(PlayCardAction),"_card")?.GetValue(play) as CardModel;
        CardCopy? copy=null;
        if(card?.Owner?.PlayerCombatState?.Hand.Cards is {} hand && hand.Contains(card))
        {
            string key=KernelSession.LiveCardStateKey(card);
            int occurrence=hand.TakeWhile(c=>!ReferenceEquals(c,card)).Count(c=>KernelSession.LiveCardStateKey(c)==key);
            copy=new(key,occurrence);
        }
        return new(play.OwnerId,"card",play.CardModelId.Entry,play.TargetId,-1,card,copy);
    }
    private readonly Dictionary<UsePotionAction,(string Id,uint? Target)> startedPotions=new();
    private static uint? NativePotionTarget(UsePotionAction action,PotionModel? potion)=>
        action.TargetId??(potion?.TargetType.IsSingleTarget()==true?potion.Owner.Creature.CombatId:null);
    private readonly Dictionary<ulong,int> endActionRounds=new();
    private sealed record ReadyCheckpoint(KernelSession Root,OpeningResult Result,InteractiveSearch.Line? Line,SimulationMeter? Meter);
    private readonly Dictionary<ulong,ReadyCheckpoint> beforeReady=new();
    private ActionExecutor? observedExecutor;
    private void WatchNativeInput(CombatState? combat)
    {
        var executor=combat==null ? null : RunManager.Instance.ActionExecutor;
        if(ReferenceEquals(observedExecutor,executor))return;
        if(observedExecutor!=null)
        {
            observedExecutor.BeforeActionExecuted-=OnNativeBegin;
            observedExecutor.AfterActionExecuted-=OnNativeAction;
        }
        if(observedChoiceSync!=null)observedChoiceSync.PlayerChoiceReceived-=OnNativeChoiceReceived;
        observedChoiceSync=combat==null?null:RunManager.Instance.PlayerChoiceSynchronizer;
        if(observedChoiceSync!=null)observedChoiceSync.PlayerChoiceReceived+=OnNativeChoiceReceived;
        ResetNativeChoices();startedPotions.Clear();observedExecutor=executor;liveAdvisor!.completedInputs.Clear();
        if(executor!=null)
        {
            executor.BeforeActionExecuted+=OnNativeBegin;
            executor.AfterActionExecuted+=OnNativeAction;
        }
    }
    private void ResetNativeChoices()
    {
        foreach(var (action,pending) in openChoices)
        {
            action.BeforePausedForPlayerChoice-=OnChoiceWaiting;
            action.AfterFinished-=FinishChoice;
            action.BeforeCancelled-=FinishChoice;
            pending.Probe?.Cancel();
        }
        openChoices.Clear();
        ClearNativeChoices();
        SetPinned("");
    }
    private sealed class PendingHand(GameAction action,KernelSession? root,Move? move,KernelChoice[] choices)
    {
        internal GameAction Action=action;
        internal KernelSession? Root=root;
        internal Move? Move=move;
        internal KernelChoice[] Choices=choices;
        internal readonly SimulationMeter MenuMeter=new();
        internal HandChoiceProbe? Probe;
        internal InteractiveSearch.Line? LastMenuLine;
        internal PotionRoute<DisplayedPotion>[] PotionRows=System.Array.Empty<PotionRoute<DisplayedPotion>>();
        internal object? LastOptions;
        internal SeedRoute? Incumbent;
        internal BattleExchange? Battle;
        internal BattleExchange.Position? Before;
        internal void StartProbe(SearchMemoryBudget memory,string? directory,FightEvaluation evaluation)
        {
            if(Probe==null && Root!=null && Move!=null)
                Probe=new(Root,Move,MenuMeter,memory,Battle,Before,directory,Incumbent,evaluation);
        }
        internal InteractiveSearch.Line? Rendered;
        internal HudPlan? Display;
        internal bool Waiting;
        internal PotionModel? Potion;
        internal readonly List<(ulong Owner,uint Id)> ObservedMenus=new();
        internal KernelChoice[] ActiveRecommendations=System.Array.Empty<KernelChoice>();
    }
    private readonly Dictionary<GameAction,PendingHand> openChoices=new();
    private IEnumerable<PendingHand> PendingChoices=>
        openChoices.Values.Concat(nativeChoices.Values.Where(pending=>pending.Potion!=null)).Concat(potionHooks.Values)
            .Concat(turnStartPending==null?System.Array.Empty<PendingHand>():new[]{turnStartPending}).Distinct();
    private IEnumerable<PendingHand> WaitingChoices=>PendingChoices.Where(s=>s.Waiting);
    private bool SelectingHand=>WaitingChoices.Any() || NPlayerHand.Instance?.IsInCardSelection==true;
    private void RecordInputGroup(TapeGroup group)
    {
        exchange?.Append(group);
        tapeInputs.Clear();
    }
    private void OnNativeBegin(GameAction action)
    {
        if(action is GenericHookGameAction hook && hook.ChoiceContext?.Source is PotionModel potion &&
            nativeChoices.Values.FirstOrDefault(pending=>ReferenceEquals(pending.Potion,potion)) is {} queued &&
            potionHooks.TryAdd(hook,queued))
        {
            hook.AfterFinished+=FinishPotionHook;
            hook.BeforeCancelled+=FinishPotionHook;
        }
        if(action is EndPlayerTurnAction end && hudRoom!=null)
        {
            endActionRounds[end.OwnerId]=hudRoom.RoundNumber;
            nextHud=0;share?.RefreshGraph();
        }
        else if(action is UndoEndPlayerTurnAction undo)
        {endActionRounds.Remove(undo.OwnerId);nextHud=0;share?.RefreshGraph();}

        // This observer runs inside the game's ActionExecutor. An optional
        // preview failing must not interrupt the actual multiplayer action.
        if(playback!=null && action is PlayCardAction or UsePotionAction or EndPlayerTurnAction &&
            !ReferenceEquals(playback.Pending,action))StopPlayback("自动打牌已停止：检测到其他操作");
        try{CaptureNativeBegin(action);}
        catch(System.Exception e) when(e is not System.OutOfMemoryException)
        {Godot.GD.PrintErr("[CoopSolver] Pre-action observation: "+e);}
    }
    private void CaptureNativeBegin(GameAction action)
    {
        if(action is not (PlayCardAction or UsePotionAction or EndPlayerTurnAction) || openChoices.ContainsKey(action))return;
        var room=CombatManager.Instance.DebugOnlyGetState();
        if(action is EndPlayerTurnAction && (room==null || room.Players.Any(p=>p.NetId!=action.OwnerId &&
            p.Creature.IsAlive && !CombatManager.Instance.IsPlayerReadyToEndTurn(p))))return;
        var played=action is PlayCardAction play?startedCards.GetValue(play,CapturePlay):null;
        var view=sharedOpening?this:liveAdvisor!;
        var previous=view.displayedPotion?.Line??view.shownLine;
        var planned=previous?.Steps.FirstOrDefault();
        bool same=false;
        var source=sharedOpening?root:(showLive || liveAdvisor?.running==true) && liveAdvisor!.completedInputs.Count==0 ? liveAdvisor.root : null;
        if(searchEnabled && !WaitingChoices.Any() && room is {} priorRoom &&
            (source==null || BoardStamp(priorRoom)!=view.stamp))
            source=CaptureStable(priorRoom);
        Move? move=null;
        if(source!=null)
        {
            int seat=source.Party.ToList().FindIndex(p=>p.NetId==action.OwnerId);
            if(seat>=0 && action is PlayCardAction card)
            {
                var physical=played?.Physical;
                var hand=source.Hand(source.Party[seat]);
                int slot=physical==null ? -1 : hand.ToList().IndexOf(physical);
                if(slot<0 && hand.Count(c=>c.Id==card.CardModelId)==1)slot=hand.ToList().FindIndex(c=>c.Id==card.CardModelId);
                if(slot>=0)move=new(seat,MoveKind.Card,slot,source.CardStateKey(hand[slot]),card.TargetId);
                same=slot>=0 && planned!=null && played?.Matches(planned,source)==true &&
                    planned.CardStateKey==move!.Key;
            }
            else if(seat>=0 && action is UsePotionAction use && CapturedBelt.At(source,source.Party[seat],checked((int)use.PotionIndex)) is {} potion && (KernelSession.CanModelPotion(potion) || KernelSession.SupportsPotionSelection(potion)))
            {
                var target=NativePotionTarget(use,potion);
                move=new(seat,MoveKind.Potion,(int)use.PotionIndex,potion.Id.Entry,target);
                same=planned?.Player.NetId==action.OwnerId && planned.Potion==potion && planned.Target?.CombatId==target;
            }
            else if(seat>=0 && action is EndPlayerTurnAction)
            {
                move=new(seat,MoveKind.End,-1,"",null);
                same=planned?.EndTurn==true && planned.Player.NetId==action.OwnerId;
            }
        }
        PotionModel? nativePotion=null;
        if(action is UsePotionAction bottleAction)
        {
            var native=CombatManager.Instance.DebugOnlyGetState()?.Players.FirstOrDefault(p=>p.NetId==bottleAction.OwnerId);
            nativePotion=native?.GetPotionAtSlotIndex(checked((int)bottleAction.PotionIndex));
            var id=move?.Key??nativePotion?.Id.Entry;
            if(id!=null)startedPotions[bottleAction]=(id,move?.Target??NativePotionTarget(bottleAction,nativePotion));
        }
        var pending=new PendingHand(action,source,move,same && ReferenceEquals(source,view.root)?HandChoiceProbe.Choices(planned):System.Array.Empty<KernelChoice>());
        pending.Potion=nativePotion;
        pending.Battle=exchange;
        pending.Incumbent=view.displayedPotion?.Result.Seed??view.savedResult?.Seed;
        if(same && ReferenceEquals(source,view.root) && previous?.Ending is {Verified:true} &&
            (view.displayedPotion?.Result.Hud??view.savedResult?.Hud) is {} current && pending.Choices.Length>0 &&
            (move?.Kind!=MoveKind.Card || ReferenceEquals(played?.Physical,planned?.Card) ||
             previous.Recipes.FirstOrDefault()?.Move.Slot==move.Slot))
        {pending.Rendered=previous;pending.Display=current;}
        if(tapeInputs.Count!=0 && exchange!=null && source!=null && !SelectingHand &&
            CombatManager.Instance.DebugOnlyGetState() is {} before)
        {
            var group=SlCache.CaptureGroup(before,tapeInputs.ToArray());
            if(group.Witness==SlCache.Witness(source))RecordInputGroup(group);
        }
        if(action is EndPlayerTurnAction)
        {
            if(System.Environment.GetEnvironmentVariable("STS2_TRANSFIGURE")=="1")
                Godot.GD.Print($"[ChoiceCheck] end capture round={room?.RoundNumber} seat={action.OwnerId} source={source?.TurnNumber} move={move?.Kind}");
            if(source!=null && move!=null && room!=null)
            {
                turnStartPending?.Probe?.Cancel();turnStartPending=pending;turnStartRoom=room;
            }
            return;
        }
        openChoices.Add(action,pending);
        action.BeforePausedForPlayerChoice+=OnChoiceWaiting;
        action.AfterFinished+=FinishChoice;
        action.BeforeCancelled+=FinishChoice;
    }
    private void OnChoiceWaiting(GameAction action)
    {
        if(!openChoices.TryGetValue(action,out var s))return;
        OnChoiceWaiting(s);
    }
    private void OnChoiceWaiting(PendingHand s)
    {
        if(s.Waiting)return;
        s.Waiting=true;nextChoicePaint=0;nextHud=0;
        // Native choice callbacks only record lifecycle. Formatting, archived
        // route reconstruction and starting a new probe happen on the next tick.
        s.ActiveRecommendations=s.Rendered?.Ending?.Verified==true?s.Choices:System.Array.Empty<KernelChoice>();
    }
    private long nextChoicePaint;
    private void RefreshChoice()
    {
        if(System.Threading.Volatile.Read(ref restarting)!=0 || !WaitingChoices.Any() ||
            System.Environment.TickCount64<nextChoicePaint)return;
        nextChoicePaint=System.Environment.TickCount64+100;
        var messages=new List<string>();
        foreach(var s in WaitingChoices)
        {
            bool wantProbe=searchEnabled && SearchCapacity.OpeningPercent<100 && !manuallyPaused;
            if(wantProbe && s.Probe==null && s.Root!=null && s.Move!=null)
            {
                s.Before=s.Battle?.Current(SlCache.Witness(s.Root));
                s.StartProbe(Memory,SeedDirectory,evaluation);
            }
            s.Probe?.SetPaused(!wantProbe);
            var answer=s.Probe?.Latest;
            var menuSide=sharedOpening?this:liveAdvisor!;
            var offers=s.Probe?.Options??System.Array.Empty<PotionRoute<InteractiveSearch.Line>>();
            if(s.Root!=null && (!ReferenceEquals(s.LastOptions,offers) || !ReferenceEquals(s.LastMenuLine,s.Rendered)) &&
                CombatManager.Instance.DebugOnlyGetState() is {} choiceRoom)
            {
                var previousRows=s.PotionRows;
                var front=new PotionFront<DisplayedPotion>();
                if(s.Rendered is {Ending:{Verified:true}} existingLine && s.Display is {} existingHud)
                {
                    var prior=new OpeningResult(existingLine.Ending,existingLine.Steps.Length,existingLine.Score,existingHud.Steps,existingHud.Result,
                        existingLine.Risks,existingHud,SeedRoute.FromLine(s.Root,existingLine),ResourceScoreVersion:4);
                    front.Offer(PotionCost.From(s.Root,existingLine),new(prior,existingLine),CompareDisplay);
                }
                foreach(var row in offers.Select(o=>{
                    var existing=previousRows.FirstOrDefault(r=>ReferenceEquals(r.Value.Line,o.Value));
                    if(existing!=null)return existing;
                    var v=o.Value;
                    var text=FormatSteps(choiceRoom,v.Steps,s.Root.TurnNumber,s.Root,menuSide.targetNames);
                    var outcome=v.Ending!.Victory?"模拟胜利":"全队倒下";
                    var model=menuSide.MakeHud(v,outcome+"\n"+text,$"{outcome} · 当前选牌 · 发现时搜索 {Compact(v.FoundAt)} 次",s.Root);
                    return new PotionRoute<DisplayedPotion>(o.Cost,new(new OpeningResult(v.Ending,v.Steps.Length,v.Score,model.Steps,model.Result,v.Risks,model,
                        SeedRoute.FromLine(s.Root,v),ResourceScoreVersion:4),v));
                }))front.Offer(row.Cost,row.Value,CompareDisplay);
                s.PotionRows=front.Rows;s.LastOptions=offers;s.LastMenuLine=s.Rendered;
            }
            var preferred=livePotion.Pick(MenuWithPrefix(s.PotionRows));
            if(preferred?.Value.Line is {} chosen &&
                (answer?.Route?.Ending is not {Verified:true} incoming || chosen.Ending!.Victory && !incoming.Victory ||
                 chosen.Ending!.Victory==incoming.Victory && (livePotion.Manual!=null || AnytimeSearch.CompareLines(chosen,answer.Route)>=0)) && HandChoiceProbe.FirstChoices(chosen) is {Length:>0} picks)
            {
                answer=new(picks.ToArray(),chosen);
                s.Rendered=chosen;s.Display=preferred.Value.Result.Hud;
            }
            if(s.Rendered is {Ending:{Victory:true,Verified:true} old} kept &&
                (answer?.Route?.Ending is not {} candidate || AnytimeSearch.ComparePlans(candidate,answer.Route.Steps.Length,
                    old,kept.Steps.Length,answer.Route.Rounds,kept.Rounds)<0))
                answer=new(HandChoiceProbe.FirstChoices(kept),kept);
            var choices=answer?.Route?.Ending?.Verified==true?answer.Choices:s.Rendered?.Ending?.Verified==true?s.Choices:System.Array.Empty<KernelChoice>();
            s.ActiveRecommendations=choices;
            var players=CombatManager.Instance.DebugOnlyGetState()?.Players;
            string left=players?.FirstOrDefault(p=>p.NetId==s.Action.OwnerId)?.Character.Title.GetFormattedText()+" · ";
            if(choices.Length==0)continue;
            bool valid=true;
            // Subsequent dependent menus can describe a different hand.
            var checkedOwners=new HashSet<ulong>();
            foreach(var c in choices)
            {
                if(checkedOwners.Add(c.Owner) && !MatchesVisibleHand(c))
                {
                    valid=false;s.Display=null;s.Rendered=null;continue;
                }
                string verb=c.Effect switch {"Discard"=>"弃掉","Exhaust"=>"消耗","MoveToHand"=>"取回","Modify" when (s.Action as PlayCardAction)?.CardModelId.Entry=="TRANSFIGURE"=>"添加重放",_=>"选择"};
                int seat=CombatManager.Instance.DebugOnlyGetState()?.Players.ToList().FindIndex(p=>p.NetId==c.Owner)??-1;
                string actor=c.Owner==s.Action.OwnerId?"":seat>=0?$"P{seat+1} ":left;
                messages.Add(answer?.Route!=null || s.Choices.Length>0?actor+(c.Cards.Count==0?"跳过选择":verb+" "+ChoiceNames(c,live:true)):actor+"本回合建议"+verb+" "+ChoiceNames(c,live:true));
            }
            var displaySide=sharedOpening?this:liveAdvisor!;
            if(valid && answer?.Route is {Ending:{Verified:true}} line && !ReferenceEquals(line,s.Rendered) && s.Root!=null &&
                CombatManager.Instance.DebugOnlyGetState() is {} room)
            {
                var text=FormatSteps(room,line.Steps,s.Root.TurnNumber,s.Root,displaySide.targetNames);
                var label=line.Ending==null?"胜负未定":line.Ending.Victory?"模拟胜利":"全队倒下";
                s.Display=displaySide.MakeHud(line,label+"\n"+text,$"{label} · 当前选牌 · 发现时搜索 {Compact(line.FoundAt)} 次",s.Root);
                s.Rendered=line;nextHud=0;
            }
        }
        SetPinned(string.Join("\n",messages));
    }
    private void FinishChoice(GameAction action)
    {
        if(!openChoices.Remove(action,out var s))return;
        action.BeforePausedForPlayerChoice-=OnChoiceWaiting;action.AfterFinished-=FinishChoice;action.BeforeCancelled-=FinishChoice;
        // Cross-player potion choices can outlive the throwing player's action.
        bool branched=s.Potion!=null && action.State==GameActionState.Finished;
        foreach(var key in nativeChoices.Where(p=>ReferenceEquals(p.Value,s) && (!branched || p.Key.Owner==action.OwnerId))
            .Select(p=>p.Key).ToArray())nativeChoices.Remove(key);
        if(!nativeChoices.Values.Contains(s))s.Probe?.Cancel();
        nextChoicePaint=0;
        nextHud=0;share?.RefreshGraph();
        if(!WaitingChoices.Any())SetPinned("");
    }
    private void OnNativeAction(GameAction action)
    {
        if(action.State.ToString() is "GatheringPlayerChoice" or "ReadyToResumeExecuting")return;
        openChoices.TryGetValue(action,out var input);
        FinishChoice(action);
        if(action.Exception!=null)return;
        Played? observed=action switch
        {
            PlayCardAction c=>startedCards.TryGetValue(c,out var before)?before:new(c.OwnerId,"card",c.CardModelId.Entry,c.TargetId,-1,null),
            UsePotionAction p=>startedPotions.TryGetValue(p,out var bottle)?
                new(p.OwnerId,"potion",bottle.Id,bottle.Target,checked((int)p.PotionIndex),null):
                new(p.OwnerId,"potion",input?.Move?.Key??"",input?.Move?.Target??p.TargetId,checked((int)p.PotionIndex),null),
            EndPlayerTurnAction end=>new(end.OwnerId,"end","",null,-1,null),
            UndoEndPlayerTurnAction undo=>new(undo.OwnerId,"undo","",null,-1,null),
            _=>null
        };
        if(action is UsePotionAction finishedPotion)startedPotions.Remove(finishedPotion);
        if(observed!=null)
        {
            try{ObservePresentedStep(observed);}
            catch(System.Exception e) when(e is not System.OutOfMemoryException)
            {Godot.GD.PrintErr("[CoopSolver] Route display observation: "+e);}
            nextHud=0;share?.RefreshGraph();
            if(sharedOpening || showLive || liveAdvisor?.running==true || manuallyPaused && SearchCapacity.OpeningPercent<100)liveAdvisor!.completedInputs.Add(observed);
            if(exchange!=null)
            {
                int round=observed.Kind=="end"?endActionRounds.GetValueOrDefault(observed.Seat):
                    observed.Kind=="undo"?hudRoom?.RoundNumber??0:0;
                tapeInputs.Add(new(observed.Seat,observed.Kind,observed.Id,observed.Slot,observed.Target,observed.Copy,round));
            }
        }
    }
}
