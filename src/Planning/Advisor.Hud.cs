using System.Collections.Generic;
using System;
using System.Linq;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using Godot;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private long TreeBytes=>System.Threading.Volatile.Read(ref restarting)!=0?0:Memory.Trees.Read(searchEnabled && !Memory.Waiting());
    private CombatHud? hud;
    private RouteShare? share;
    private CombatState? hudRoom;
    private INetGameService? hudNet;
    private HudFrame? hudFrame;
    private readonly RouteGraphBook dependencyGraphs=new();
    private long sentGraphRevision;
    private int? localGraphRound,detachedGraphRound;
    private const ulong LiveGraphWorker=ulong.MaxValue;
    private static int[] GraphRounds(int? first,int? second)=>
        new[]{first,second}.OfType<int>().Where(round=>round>0).Distinct().ToArray();
    private int[] RequestedGraphRounds()=>GraphRounds(localGraphRound,detachedGraphRound);
    private void SelectGraphRound(int? round,bool detached)
    {
        if(detached)detachedGraphRound=round;else localGraphRound=round;
        if(FollowingHost)share?.ChooseGraphs(RequestedGraphRounds());
        nextHud=0;
    }
    private long nextHud;
    private bool guestLocal,resumeGuestSearch=true,broadcastRoutes=true;
    private string? guestPauseReason;
    private void SetBroadcast(bool enabled)
    {
        broadcastRoutes=enabled;
        nextHud=0;
    }
    private string? pauseReason;
    private bool FollowingHost=>hudNet?.Type==NetGameType.Client && !guestLocal;
    private void SetGuestLocal(bool enabled)
    {
        if(guestLocal==enabled && (enabled || !searchEnabled))return;
        if(guestLocal)
        {
            resumeGuestSearch=searchEnabled;
            guestPauseReason=pauseReason;
        }
        guestLocal=enabled;
        if(!enabled){dependencyGraphs.CancelPending();share?.ChooseGraphs(RequestedGraphRounds());}
        hudFrame=null;nextHud=nextCheck=0;requested=switchRequested=false;
        searchEnabled=enabled && resumeGuestSearch;
        manuallyPaused=!searchEnabled;
        pauseReason=enabled?guestPauseReason:null;
        foreach(var slot in PendingChoices)slot.Probe?.SetPaused(!searchEnabled);
        if(enabled)return;
        foreach(var side in new[]{this,liveAdvisor!})
        {
            side.running=false;side.activeTime.Stop();side.engine?.Pump(false);
        }
        InteractiveSearch.Dispatcher.Split(false);
    }
    private string pinnedChoice="";
    private string[] playerNames=Array.Empty<string>();

    private void ToggleResults()
    {
        resultsHidden=!resultsHidden;nextHud=0;
    }
    private void SetPinned(string text)
    {pinnedChoice=text;nextHud=0;}
    internal static string CharacterInk(MegaCrit.Sts2.Core.Models.CharacterModel character)
    {
        var source=character.Id.Entry switch
        {
            "NECROBINDER"=>new Color("#766b92"),
            "REGENT"=>new Color("#ef762c"),
            _=>character.NameColor
        };
        return source.ToHtml(false);
    }
    private HudPlan MakeHud(InteractiveSearch.Line line,string route,string summary,CoopBots.Kernel.KernelSession? choiceOrigin=null)
    {
        int firstRound=choiceOrigin?.TurnNumber??rootRound;
        var party=choiceOrigin?.Party??participants;
        var actors=party.Select((player,index)=>(player.NetId,index)).ToDictionary(item=>item.NetId,item=>item.index);
        var origin=choiceOrigin??root;
        var seats=party.Select((p,i)=>{
            var events=DeathSaves.Events(line.Rounds,p.NetId);
            int restored=events.Sum(e=>e.Hp);
            int start=choiceOrigin?.Hp(p.Creature)??startHp[i];
            var settled=line.Rounds?.Where(r=>r.Loss.Length>i).ToArray();
            int loss=settled is {Length:>0}?settled.Sum(r=>r.Loss[i]):start-line.Hp[i]+restored;
            int recovered=settled?.Sum(r=>r.Recovered?.ElementAtOrDefault(i)??0)??0;
            string playerName=i<playerNames.Length?playerNames[i]:(owner??this).playerNames.ElementAtOrDefault(i)??$"P{i+1}";
            return new HudSeat(p.NetId,playerName,p.Character.Title.GetFormattedText(),
                line.FirstHp==null?null:settled?.FirstOrDefault(r=>r.Round==firstRound)?.Loss[i]??start-line.FirstHp[i]+DeathSaves.Events(line.Rounds,p.NetId,firstRound).Sum(e=>e.Hp),
                loss,line.FirstHp!=null && line.FirstHp[i]<=0,line.Hp[i]<=0,
                line.Steps.Count(a=>a.Player.NetId==p.NetId && a.Potion!=null)+events.Count(e=>e.Potion),
                line.Hp[i],CharacterInk(p.Character),restored,recovered);
        }).ToArray();
        string? Position(Creature target)=>target.CombatId is uint id?targetNames.GetValueOrDefault(id):null;
        var kills=(line.Kills??Array.Empty<RouteKill>()).Select(k=>new HudKill(k.Step,
            Position(k.Enemy)??k.Enemy.Name.ToString(),ReferenceEquals(k.Enemy,line.Steps[k.Step].Target),
            k.CreditedSeat,k.Effect,k.Enemy.CombatId,HudTarget.Of(k.Enemy,position:Position(k.Enemy)))).ToArray();
        var targets=line.Steps.Select((action,step)=>(action.Target,step))
            .Where(item=>item.Target is {Monster:not null,CombatId:not null})
            .ToDictionary(item=>item.step,item=>item.Target!.CombatId!.Value);
        HudPick[]? Picks(System.Collections.Generic.IEnumerable<CoopBots.Kernel.KernelChoice>? list)=>
            list?.Select(choice=>new HudPick(actors.GetValueOrDefault(choice.Owner,-1)+1,ChoiceCards(choice))).ToArray();
        var moves=line.Steps.Select(action=>
        {
            var potion=action.Potion;
            int? slot=potion==null?null:CapturedBelt.DisplaySlot(origin,potion);
            var target=action.Target;
            var transition=action.TurnStartChoices;
            return new HudMove(actors[action.Player.NetId],action.EndTurn?HudMoveKind.EndTurn:action.Card!=null?
                HudMoveKind.Card:potion!=null?HudMoveKind.Potion:HudMoveKind.Choice,
                action.Card is {} card?CardPresentation.Capture(card,action.CardStateKey,CardPresentation.FeeFor(action)):null,
                HudName.From(potion?.Title),slot,target==null?null:HudTarget.Of(target,
                    target.Player is {} teammate?actors.GetValueOrDefault(teammate.NetId,-1)+1:0,Position(target)),
                Picks(action.Choices),transition!=null && transition.ToRound!=transition.FromRound?transition.ToRound:null,
                Picks(transition?.Choices));
        }).ToArray();
        var outcome=line.Ending is {Verified:true} record?(record.Victory?HudOutcome.Win:HudOutcome.Loss):HudOutcome.Unknown;
        var model=new HudPlan(choiceOrigin?.TurnNumber??rootRound,line.EndTurn,seats,route,summary,string.Join("\n",line.Risks),kills,line.Rounds,line.EnemyHp,seats.Sum(s=>(s.ToEnd??0)+(owner?.PastLoss(s.Id)??0)),Ui.Terms(line),Ui.Language,GoalResult.For(line.Ending),targets,moves,outcome);
        if(origin!=null)(owner??this).dependencyGraphs.Track(model,origin,line);
        return model;
    }
    internal sealed class LossUpdates
    {
        private object? position;
        private TerminalRecord? best;
        private int? departedLoss;
        private long revision;
        internal bool Increased {get;private set;}
        internal void Depart(int? loss)=>departedLoss??=loss;
        internal void Reset()
        {
            position=null;best=null;departedLoss=null;
        }
        internal long Observe(object? scope,TerminalRecord? result,int? wholeLoss=null)
        {
            if(!ReferenceEquals(scope,position)){position=scope;best=null;}
            if(scope==null || result is not {Verified:true} || ReferenceEquals(best,result))return revision;
            if(departedLoss is int before && wholeLoss is int after)
            {
                departedLoss=null;
                if(after!=before){Increased=after>before;revision++;}
                best=result;return revision;
            }
            if(best==null){best=result;return revision;}
            if(AnytimeSearch.Compare(result,best)<=0)return revision;
            // Even two defeats can be ordered by enemy HP or by turns survived.
            // Flash only for improved player resources, independent of route length.
            if(result.IrreversibleLoss<best.IrreversibleLoss || result.OutstandingStolenResource<best.OutstandingStolenResource ||
                AnytimeSearch.HealthValue(result)>AnytimeSearch.HealthValue(best) ||
                result.Victory && best.Victory && result.ResourcesConsumed<best.ResourcesConsumed)
            {Increased=false;revision++;}
            best=result;return revision;
        }
    }
    private readonly LossUpdates openingChanges=new(),liveChanges=new();
    private readonly SearchHistory openingHistory=new(),liveHistory=new(),guestOpeningHistory=new(),guestLiveHistory=new();
    private readonly object openingRemoteScope=new();
    private readonly object liveRemoteScope=new();
    private long liveHistoryRevision,remoteHistoryRevision=-1;
    private bool remoteDiverged;
    private void ClearLiveHistory(){liveHistory.Clear();liveHistoryRevision++;}
    private bool WantsHistory=>hud?.WantsHistory==true || window?.Panel.WantsHistory==true;
    private void RefreshHistory()
    {
        if(FollowingHost)share?.ChooseHistory(WantsHistory);
        nextHud=0;
    }
    private static bool IsCurrentLivePosition(Advisor side,CombatState room)=>
        ReferenceEquals(side.board,room) && side.root!=null && side.capturedWitness!=null &&
        !side.waitingForStable && side.completedInputs.Count==0 && side.rootRound==room.RoundNumber &&
        side.initialActionId==RunManager.Instance.ActionQueueSet.NextActionId && side.revision==KernelSession.LiveRevision;

    private readonly List<SearchTreeMemory.Estimate> observedMemory=new(16);
    private void CollectMemorySamples(CombatState room)
    {
        if(System.Threading.Volatile.Read(ref restarting)!=0)return;
        var tree=Memory.Trees;
        tree.ContinueSurvey(searchEnabled && !tree.AtLimit);
        if(root!=null && ReferenceEquals(board,room))
        {
            tree.CopyEstimatesAfter(openingHistory.MemoryRevision(meter),observedMemory);
            if(observedMemory.Count>0)
            {
                long work=meter.Actions+(exchange?.ConnectedWork??0);
                foreach(var observation in observedMemory)
                    openingHistory.RecordMemory(meter,work,observation.Bytes,observation.Revision);
            }
        }
        var side=sharedOpening?this:liveAdvisor;
        if(side!=null && (sharedOpening?root!=null && ReferenceEquals(board,room):IsCurrentLivePosition(side,room)))
        {
            var current=side.meter;
            tree.CopyEstimatesAfter(liveHistory.MemoryRevision(current),observedMemory);
            if(observedMemory.Count>0)
            {
                long work=current.Fresh.Total;
                foreach(var observation in observedMemory)
                    liveHistory.RecordMemory(current,work,observation.Bytes,observation.Revision,continuous:true);
            }
        }
    }

    private HudBest SummaryFor(Advisor side,CombatState room,long now,out TerminalRecord? ending)
    {
        // A captured live root can be valid even without the opening prefix.
        bool current=side.Follow?IsCurrentLivePosition(side,room):ReferenceEquals(side.board,room);
        var best=current?side.savedResult:null;
        if(current && side.Follow && side.capturedWitness!=null && side.capturedWitness==openingWitness &&
            !side.waitingForStable && side.completedInputs.Count==0 && savedResult?.Improves(best)==true)best=savedResult;
        ending=best?.Ending;
        var plan=ending?.Verified==true?best!.Hud:null;
        var line=side.shownLine;
        long? pv=current && ReferenceEquals(best,side.savedResult) && line?.Steps.Length>0 && side.root!=null?
            side.meter.BranchVisits(side.root,line.Steps[0]):null;
        var party=plan?.Party??Array.Empty<HudSeat>();
        if(side.Follow && party.Length!=0)
            party=party.Select((seat,i)=>seat with {ToEnd=seat.ToEnd+PastLoss(seat.Id),
                Potions=seat.Potions+(exchange?.UsedPotions(seat.Id)??0)+DeathSaves.AutomaticBottles(side.livePosition?.PastRevives,seat.Id)}).ToArray();
        long work=current?side.meter.Actions:0;
        if(current && !side.Follow)work+=exchange?.ConnectedWork??0;
        var snapshot=side.engine?.Latest;
        bool sameRoot=current && ReferenceEquals(side.boundRoot,side.root) &&
            (!side.Follow || !side.waitingForStable && side.completedInputs.Count==0) &&
            snapshot!=null && snapshot.Generation==side.generation;
        bool exhausted=sameRoot && snapshot!.Exhausted;
        bool waiting=searchEnabled && !exhausted && side.running && Memory.Waiting();
        bool sameLoss=sameRoot && best is {Ending:{Victory:true,Verified:true} displayed} &&
            snapshot!.Result?.Ending is {Victory:true} evaluated && AnytimeSearch.Compare(displayed,evaluated)==0;
        bool supportedLoss=sameLoss && plan is {Risks.Length:0};
        return new(work,pv,current && side.CurrentWorkerError==null && !exhausted && !waiting?side.meter.Rate(now,side.running):0,
            room.RoundNumber,plan?.End,party,
            best?.Ending is {} end?(end.Victory?"胜":"败"):side.pendingCheckVisible?"核对中":"",
            plan==null?null:System.Text.RegularExpressions.Regex.Match(plan.Result,@"发现时(?:搜索|模拟|计算) ([0-9.]+[kMB]?) 次").Groups[1].Value,
            side.Follow && side.CurrentWorkerError==null && !sharedOpening && searchEnabled && SearchCapacity.OpeningPercent<100 &&
            (!current || side.engine?.Latest?.Generation!=side.generation),Exhausted:exhausted,
            MinimumLossProven:supportedLoss && snapshot!.MinimumLossProven,ZeroLossProven:HasZeroLossWin(ending,plan),
            ProofPercent:sameLoss?snapshot!.ProofPercent:null,MemoryWaiting:waiting,ProofDone:sameLoss?snapshot!.ProofDone:0,ProofTotal:sameLoss?snapshot!.ProofTotal:0,ProofBlocked:sameLoss && snapshot!.ProofBlocked,ProofCoverage:sameLoss?snapshot!.ProofCoverage:null);
    }

    // Display proof for the HP-loss metric. This does not certify the worker's
    // broader score or stop it from looking for better potion/recovery outcomes.
    internal static bool HasZeroLossWin(TerminalRecord? ending,HudPlan? plan)
    {
        if(ending is not {Verified:true,Victory:true} ||
            plan is not {Goal:null,Risks.Length:0,Party.Length:>0,Rounds:{Length:>0} rounds})return false;
        int expected=plan.Start;
        foreach(var round in rounds)
        {
            if(round.Loss.Length==0)continue; // A trailing presentation row may have no settlement.
            if(round.Round!=expected++ || round.Loss.Length!=plan.Party.Length ||
                round.Loss.Any(loss=>loss!=0))return false;
        }
        return expected>plan.Start && expected>plan.End;
    }
    private string? interfaceLanguage;
    private static HudFrame AddWarnings(HudFrame frame,string[] warnings,string? first=null,string? second=null)
    {
        if(warnings.Length==0 && first==null && second==null)return frame;
        var merged=(frame.Warnings??Array.Empty<string>()).Concat(warnings);
        if(first!=null)merged=merged.Append(first);
        if(second!=null)merged=merged.Append(second);
        return frame with {Warnings=merged.Distinct().ToArray()};
    }
    private CombatHud CreateHud(Window? nativeWindow)=>new(tree,()=>requested=true,()=>{showLive=!showLive;switchRequested=true;nextHud=0;},
        ()=>OpenRoute(),ToggleResults,SetGuestLocal,RestartSearch,SelectPotion,TogglePlayback,nativeWindow,SetBroadcast,round=>SelectGraphRound(round,nativeWindow!=null),RefreshHistory);
    private HudFrame WithReadyEnds(HudFrame frame)
    {
        if(hudRoom is not {} room)return frame;
        // The native queue can reopen a turn without a separate Undo action.
        // An older shared frame must not overwrite the locally resolved flag.
        var players=room.Players.Where(CombatManager.Instance.IsPlayerReadyToEndTurn)
            .Select(player=>player.NetId).OrderBy(id=>id).ToArray();
        return frame with {ReadyRound=room.RoundNumber,ReadyEnds=players.Length>0?players:null};
    }

    private bool TickHud()
    {
        string language=Ui.Language;
        if(interfaceLanguage!=language)
        {
            interfaceLanguage=language;
            hud?.Remove();hud=null;hudFrame=null;nextHud=0;
            bool reopen=window?.IsShown==true;
            window?.Remove();window=null;
            if(reopen)OpenRoute(automatic:true);
        }
        var rm=RunManager.Instance;
        var net=rm?.NetService;
        var room=CombatManager.Instance.IsInProgress?CombatManager.Instance.DebugOnlyGetState():null;
        bool eligible=net?.Type switch
        {
            NetGameType.Singleplayer=>room?.Players.Count==1,
            NetGameType.Host or NetGameType.Client=>net.IsConnected && room?.Players.Count>=2,
            _=>false
        };
        if(!eligible)room=null;
        if(!ReferenceEquals(room,hudRoom) || !ReferenceEquals(net,hudNet))
        {
            if(!ReferenceEquals(net,hudNet) && (guestLocal || net?.Type==NetGameType.Client))
                SetGuestLocal(false);
            ResetNativeChoices();dependencyGraphs.Clear();endActionRounds.Clear();beforeReady.Clear();presentedRoute=null;
            openingHistory.Clear();ClearLiveHistory();guestOpeningHistory.Clear();guestLiveHistory.Clear();remoteHistoryRevision=-1;remoteDiverged=false;
            StopPlayback();ClearCaptureWarnings();tickError=null;archiveError=null;liveAdvisor!.archiveError=null;
            openingChanges.Reset();liveChanges.Reset();
            openingPotion.Manual=livePotion.Manual=null;hudRoom=room;hudNet=net;hudFrame=null;pauseReason=null;nextHud=0;hud?.Hide();
            window?.Panel.ResetLiveDisplay();
            string battle=room==null?"":$"{room.RunState.Rng.StringSeed}/{room.RunState.TotalFloor}/{room.Encounter?.Id.Entry}/{string.Join(",",room.Players.Select(p=>p.NetId))}";
            share?.Reset(net,battle);
            if(FollowingHost)share?.ChooseGraphs(RequestedGraphRounds());
        }
        if(room==null || net==null){window?.Panel.Hide();return false;}
        bool guestClient=net.Type==NetGameType.Client;
        bool localSolver=!FollowingHost;
        if(localSolver)CollectMemorySamples(room);
        long now=System.Environment.TickCount64;
        if(net is NetClientGameService guest)share?.Guest(guest,now);
        if(window is {IsShown:false,Panel:{} detachedPanel})detachedPanel.SuspendGraphRequest();
        long graphRevision=dependencyGraphs.Revision;
        if(hudFrame==null || now>=nextHud || graphRevision!=sentGraphRevision)
        {
            if(graphRevision!=sentGraphRevision)share?.RefreshGraph();
            sentGraphRevision=graphRevision;
            nextHud=now+250;
            if(NativeUi || localSolver)CaptureLabels(room);
            var a=Selected;
            if(localSolver)
            {
                var choice=ChoiceFor(net.NetId);
                var liveSide=sharedOpening?this:liveAdvisor!;
                var openingPlan=ReferenceEquals(board,room) && savedResult?.Ending?.Verified==true?savedResult.Hud:null;
                var livePlan=ReferenceEquals(liveSide.board,room) && liveSide.savedResult?.Ending?.Verified==true?liveSide.savedResult.Hud:null;
                if(!sharedOpening && liveSide.capturedWitness==openingWitness && openingWitness!=null &&
                    ReferenceEquals(liveSide.board,observedBoard) && !liveSide.waitingForStable && liveSide.completedInputs.Count==0 &&
                    savedResult?.Improves(liveSide.savedResult)==true)livePlan=openingPlan;
                var opening=SummaryFor(this,room,now,out var openingEnd);
                var liveEnd=openingEnd;
                var realtime=sharedOpening && SearchCapacity.OpeningPercent!=100?opening:SummaryFor(liveAdvisor!,room,now,out liveEnd);
                if(liveSide.routeDiverged)livePlan=null;
                else livePlan??=liveSide.transitionPlan;
                object? liveScope=sharedOpening?root:liveAdvisor!.root;
                opening=opening with {LossRevision=openingChanges.Observe(root,openingEnd)};
                int? liveLoss=choice==null && realtime.Party.Length>0 && realtime.Party.All(seat=>seat.ToEnd.HasValue)?
                    realtime.Party.Sum(seat=>seat.ToEnd!.Value):null;
                realtime=realtime with {LossRevision=liveChanges.Observe(choice??liveScope,
                    choice==null?liveEnd:choice.Rendered?.Ending,liveLoss),LossIncreased=liveChanges.Increased};
                string state=a.waitingForStable?"等待实战结算":a.running?"搜索中":a.savedResult!=null?"已保留路线":"等待搜索";
                if(a.Follow && a.liveMessage.Length!=0)state+=" · "+a.liveMessage;
                var selected=showLive?realtime:opening;
                hudFrame=new(selected.Visits,selected.Pv,room.RoundNumber,state,pinnedChoice,showLive,
                    UseWorker?searchEnabled:a.running,showLive?livePlan:openingPlan,Rate:selected.Rate,ViewPercent:ViewPercent);
                bool openingActive=sharedOpening || SearchCapacity.OpeningPercent>0;
                bool liveActive=!sharedOpening && SearchCapacity.OpeningPercent<100;
                bool allWaiting=(opening.MemoryWaiting || realtime.MemoryWaiting) &&
                    (!openingActive || opening.Exhausted || opening.MemoryWaiting) &&
                    (!liveActive || realtime.Exhausted || realtime.MemoryWaiting);
                hudFrame=resultsHidden?hudFrame with {Hidden=true,Plan=null,Pv=null,State="",Choice="",Round=0,ViewPercent=null,MemoryWaiting=allWaiting,
                    Visits=sharedOpening?opening.Visits:opening.Visits+(exchange?.AdditionalWork(liveAdvisor!.livePosition,liveAdvisor.meter)??realtime.Visits),Rate=sharedOpening?opening.Rate:opening.Rate+realtime.Rate}:
                    hudFrame with {Opening=opening,Realtime=realtime,OpeningShare=SearchCapacity.OpeningPercent};
                if(!searchEnabled && !resultsHidden && pauseReason!=null)hudFrame=hudFrame with {PauseReason=pauseReason};
                hudFrame=hudFrame with {TreeBytes=TreeBytes,SearchError=searchEnabled?(sharedOpening?CurrentWorkerError:a.CurrentWorkerError):null};
                if(System.Threading.Volatile.Read(ref restarting)==0)
                {
                    SyncPotionRoutes();
                    if(!sharedOpening)liveAdvisor!.SyncPotionRoutes();
                }
                if(searchEnabled)
                {
                    string? other=ReferenceEquals(a,this)?liveAdvisor!.CurrentWorkerError:CurrentWorkerError;
                    if(!sharedOpening && other!=null)hudFrame=AddWarnings(hudFrame,new[]{(ReferenceEquals(a,this)?"实战搜索出错：":"开局搜索出错：")+other});
                }
                hudFrame=AddWarnings(hudFrame,captureWarnings,tickError,playbackNotice);
                var raw=resultsHidden?hudFrame:hudFrame with {PotionMenu=PotionMenu(room)};
                bool liveGraphSettled=liveSide.rootRound==room.RoundNumber && !liveSide.waitingForStable &&
                    liveSide.completedInputs.Count==0 && !liveSide.pendingCheckVisible;
                if(root!=null && ReferenceEquals(board,room))
                    openingHistory.Record(meter,opening.Visits,opening,openingEnd?.RemainingEnemyHp);
                if(sharedOpening?root!=null && ReferenceEquals(board,room):IsCurrentLivePosition(liveSide,room))
                    liveHistory.Record(liveSide.meter,liveSide.meter.Fresh.Total,
                        realtime,liveEnd?.RemainingEnemyHp,continuous:true);
                HudFrame ForViewer(ulong viewer,bool live,PotionSelection first,PotionSelection second,bool local,int[]? requestedRounds,bool history)
                {
                    var pending=ChoiceFor(viewer);
                    var frame=raw.Hidden?raw:raw with {Live=live,Plan=live?livePlan:openingPlan};
                    if(!frame.Hidden && pending!=null && frame.Realtime is {} liveRow)
                    {
                        var display=pending.Rendered?.Ending?.Verified==true?pending.Display:null;
                        var party=display?.Party.Select(seat=>seat with {ToEnd=seat.ToEnd+PastLoss(seat.Id),
                            Potions=seat.Potions+(exchange?.UsedPotions(seat.Id)??0)+DeathSaves.AutomaticBottles(liveAdvisor?.livePosition?.PastRevives,seat.Id)}).ToArray()
                            ??Array.Empty<HudSeat>();
                        liveRow=liveRow with {Visits=pending.MenuMeter.Actions,
                            Rate=pending.MenuMeter.Rate(now,searchEnabled && pending.Probe!=null && !Memory.Waiting()),
                            Party=party,Pv=null,Round=room.RoundNumber,End=display?.End,Outcome=display?.Outcome??"",
                            Discovered=display!=null && pending.Rendered is {} line?Compact(line.FoundAt):null,
                            MemoryWaiting=searchEnabled && Memory.Waiting(),Checking=false,Exhausted=false,MinimumLossProven=false,ZeroLossProven=false,
                            ProofPercent=null,ProofDone=0,ProofTotal=0,ProofBlocked=false,ProofCoverage=null};
                        frame=frame with {Realtime=liveRow,Plan=live?display:frame.Plan,State=live?"正在评估当前选牌":frame.State};
                    }
                    frame=PotionView(frame,live,first,second,local,pending);
                    if(live && pending?.Root is {} choiceRoot && frame.Plan is {} menuPlan)
                    {
                        var line=ReferenceEquals(menuPlan,pending.Display)?pending.Rendered:
                            pending.PotionRows.FirstOrDefault(option=>ReferenceEquals(option.Value.Result.Hud,menuPlan))?.Value.Line;
                        if(line is {Ending.Verified:true})dependencyGraphs.Track(menuPlan,choiceRoot,line);
                    }
                    bool diverged=liveSide.routeDiverged && livePlan==null && pending==null;
                    if(diverged && frame.Realtime is {} departed)
                        frame=frame with {Plan=live?null:frame.Plan,Realtime=departed.WithoutRoute()};
                    var rounds=frame.Hidden?Array.Empty<int>():requestedRounds??Array.Empty<int>();
                    var graphs=new HudRoundGraph[rounds.Length];
                    for(int slot=0;slot<rounds.Length;slot++)
                    {
                        int round=rounds[slot];
                        var graph=dependencyGraphs.Request(viewer,frame.Plan,round,out bool waiting,slot,
                            settled:!live || pending!=null || liveGraphSettled);
                        bool restoring=archivedRoute is {} cached && ReferenceEquals(frame.Plan,cached.Saved.Hud);
                        bool updating=waiting || restoring && graph==null;
                        var failure=graph==null && !updating && frame.Plan is {} selectedPlan && selectedPlan.Party.Length>1
                            ?dependencyGraphs.Failure(selectedPlan,round):null;
                        graphs[slot]=new(round,graph,updating,failure?.Reason,failure?.OriginalOrderVerified==true);
                        if(live && liveGraphSettled && pending==null && round==room.RoundNumber &&
                            graph!=null && frame.Plan is {} publishedPlan)
                            liveSide.RememberDisplayedRound(publishedPlan,graph);
                        if(failure!=null)frame=AddWarnings(frame,new[]{failure.OriginalOrderVerified
                            ?$"第{round}回合仅支持原顺序：{failure.Reason}"
                            :$"第{round}回合分列图生成失败：{failure.Reason}"});
                    }
                    for(int slot=rounds.Length;slot<2;slot++)dependencyGraphs.Cancel(viewer,slot);
                    var trace=history && !frame.Hidden?(live?liveHistory:openingHistory).Snapshot(SearchCapacity.MemoryLimitMb):null;
                    return WithReadyEnds(frame with {Graphs=graphs,History=trace,RouteDiverged=diverged,LiveHistoryRevision=liveHistoryRevision,
                        Executed=live?PresentedSteps(frame.Plan):null,LiveSettling=!diverged && !liveGraphSettled});
                }
                hudFrame=ForViewer(net.NetId,showLive,openingPotion,livePotion,true,RequestedGraphRounds(),WantsHistory);
                // Verify the live turn for recognizing equivalent input even in list mode.
                if(net.Type is NetGameType.Host or NetGameType.Client)
                    dependencyGraphs.Request(LiveGraphWorker,showLive?hudFrame.Plan:livePlan,room.RoundNumber,out _,
                        settled:liveGraphSettled);
                if(net is NetHostGameService server)share?.Host(server,hudFrame,now,(viewer,h)=>
                    ForViewer(viewer,showLive,new(){Manual=openingPotion.Manual},new(){Manual=livePotion.Manual},
                        false,h.GraphRounds,h.History),broadcastRoutes,viewer=>{
                            dependencyGraphs.Cancel(viewer);
                            dependencyGraphs.Cancel(viewer,1);
                        });
            }
            else
            {
                hudFrame=share?.Remote??new(0,null,room.RoundNumber,"等待房主共享","",true,false,null);
                hudFrame=WithReadyEnds(AddWarnings(hudFrame,captureWarnings,tickError));
                if(!hudFrame.Hidden && hudFrame.State!="主机已关闭广播")
                {
                    if(hudFrame.Live && (hudFrame.LiveHistoryRevision!=remoteHistoryRevision || hudFrame.RouteDiverged && !remoteDiverged))
                    {
                        remoteHistoryRevision=hudFrame.LiveHistoryRevision;
                        guestLiveHistory.Clear();
                    }
                    if(hudFrame.Live)remoteDiverged=hudFrame.RouteDiverged;
                    var observed=hudFrame.Live?guestLiveHistory:guestOpeningHistory;
                    var row=hudFrame.Live?hudFrame.Realtime:hudFrame.Opening;
                    if(hudFrame.Plan!=null)
                    {
                        row??=new(hudFrame.Visits,null,hudFrame.Rate,hudFrame.Round,hudFrame.Plan.End,hudFrame.Plan.Party,hudFrame.Plan.Outcome);
                        observed.Record(hudFrame.Live?liveRemoteScope:openingRemoteScope,row.Visits,row,
                            hudFrame.Plan.EndEnemyHp,hudFrame.TreeBytes,continuous:hudFrame.Live);
                    }
                    if(WantsHistory && hudFrame.History==null)hudFrame=hudFrame with {History=observed.Snapshot(0)};
                }
            }
            if(localSolver && (archiveError!=null || liveAdvisor!.archiveError!=null))
                hudFrame=AddWarnings(hudFrame,Array.Empty<string>(),archiveError,liveAdvisor!.archiveError);
        }
        if(NativeUi)
        {
            bool solo=net.Type==NetGameType.Singleplayer;
            bool available=solo && canSolve && PlaybackSelection()!=null;
            if(window is {IsShown:true,Panel:{} detached})
            {
                detached.SetPlayback(solo,playback!=null,available);
                detached.Update(hudFrame,net.NetId,localSolver,canSolve || root!=null && SearchCapacity.OpeningPercent>0,room.RoundNumber,guestClient,net.Type==NetGameType.Host?broadcastRoutes:null,targetNames);
            }
            if(NCapstoneContainer.Instance?.CurrentCapstoneScreen is NCapstoneSubmenuStack)
                hud?.HideOverlay();
            else
            {
                hud??=CreateHud(null);
                hud.SetPlayback(solo,playback!=null,available);
                hud.Update(hudFrame,net.NetId,localSolver,canSolve || root!=null && SearchCapacity.OpeningPercent>0,
                    room.RoundNumber,guestClient,net.Type==NetGameType.Host?broadcastRoutes:null,targetNames);
            }
        }
        else if(hud?.Visible==true)hud.Hide();
        return !localSolver && !UseWorker;
    }
}
