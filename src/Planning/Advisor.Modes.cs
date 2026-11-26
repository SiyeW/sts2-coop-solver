using System.Collections.Generic;
using System;
using System.IO;
using Environment=System.Environment;
using System.Linq;
using CoopBots.Kernel;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Runs;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private CombatState? observedBoard;
    private bool openingChecked,waitingForStable;
    private int rootRound,rotations;
    private long nextStampCheck,nextRotation,finishedNodes,finishedSettled,finishedReplays;
    internal string? ReplayDirectory {get;set;}
    private bool reuseSavedSearch=true,clearSavedRoutesOnWrite;
    private string? SeedDirectory=>reuseSavedSearch?ReplayDirectory:null;
    private string? openingKey,battleKey;
    private LocalRouteRole routeRole;
    private FightEvaluation evaluation=new();
    private string? runKey;
    private double[]? lastWrittenWeights;
    private long nextWeightSave;
    private void SaveLearnedWeights(bool force=false)
    {
        var weights=evaluation.Weights;
        if(runKey==null || battleKey==null || ReferenceEquals(lastWrittenWeights,weights))return;
        long now=Environment.TickCount64;
        if(!force && now<nextWeightSave)return;
        nextWeightSave=now+5000;
        try
        {
            LocalRouteSlots.SaveWeights(LocalRouteSlots.FilePath,routeRole,battleKey,runKey,weights);
            lastWrittenWeights=weights;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {archiveError="本机权重未写入："+e.Message;}
    }
    private LocalBattleRoutes? localRoutes;
    private static readonly System.Reflection.FieldInfo? runStart=HarmonyLib.AccessTools.Field(typeof(RunManager),"_startTime");
    private void OpenLocalSlot(CombatState live)
    {
        routeRole=hudNet?.Type switch {
            MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Host=>LocalRouteRole.Host,
            MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client=>LocalRouteRole.Guest,_=>LocalRouteRole.Solo};
        var party=string.Join(",",live.Players.Select(p=>$"{p.NetId}/{p.Character.Id.Entry}"));
        battleKey=OpeningArchive.Key(live,$"{runStart?.GetValue(RunManager.Instance)}|{party}");
        var started=runStart?.GetValue(RunManager.Instance);
        runKey=started is long time && time>0?$"v1/{time}/{live.RunState.Rng.StringSeed}/{party}":null;
        try
        {
            var slot=LocalRouteSlots.Last(LocalRouteSlots.FilePath,routeRole);
            localRoutes=slot?.Battle==battleKey?slot:null;
            evaluation=FightEvaluation.Restore(slot,runKey);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {evaluation=new();archiveError="本机路线读取失败："+e.Message;}
        lastWrittenWeights=evaluation.Weights;nextWeightSave=0;
    }
    private BattleExchange? exchange;
    private sealed record RetainedOpening(string Key,string Witness,BattleExchange Battle,long Visits);
    private RetainedOpening? retainedOpening;
    private SearchMemoryBudget localMemory=new();
    private SearchMemoryBudget Memory=>exchange?.Memory??localMemory;
    private BattleExchange.Position? livePosition;
    private string attempt="";
    private string? openingWitness,capturedWitness;
    private readonly System.Collections.Generic.List<TapeAction> tapeInputs=new();
    private string? archiveError;
    private OpeningResult? savedResult;
    private SeedRoute? archivedSeed;
    private AnytimeSearch? reuseSource;
    private SimulationMeter meter=new();

    private static string BoardStamp(CombatState state)=>KernelSession.CaptureLiveStamp(state)+"|ready:"+
        string.Join(",",state.Players.Where(CombatManager.Instance.IsPlayerReadyToEndTurn).Select(p=>p.NetId));

    private void ObserveOpening(CombatState? live,bool stable)
    {
        if(!ReferenceEquals(live,observedBoard))
        {
            SaveLearnedWeights(force:true);runKey=null;
            CancelArchivedRoute();reuseSavedSearch=true;clearSavedRoutesOnWrite=false;
            if(UseWorker && exchange!=null && openingKey!=null && openingWitness!=null)
                retainedOpening=new(openingKey,openingWitness,exchange,meter.Actions+exchange.ConnectedWork);
            WatchHealth(live);ClearTree();sharedOpening=false;localMemory=new();
            if(UseWorker)
            {
                liveAdvisor?.DropInactiveBattle(live);
                liveAdvisor?.completedInputs.Clear();
            }
            observedBoard=live;savedResult=null;archivedSeed=null;openingKey=null;openingChecked=false;battleKey=null;localRoutes=null;exchange=null;openingWitness=null;tapeInputs.Clear();attempt=Guid.NewGuid().ToString("N");
            if(live==null)return;
        }
        if(live==null || !stable || openingChecked)return;
        if(battleKey==null)OpenLocalSlot(live);
        if(live.RoundNumber==1 && live.Players.Any(p=>p.Creature.IsAlive &&
            p.PlayerCombatState?.Phase!=PlayerTurnPhase.Play))return;
        openingChecked=true;
        if(live.RoundNumber!=1 || live.Players.Any(CombatManager.Instance.IsPlayerReadyToEndTurn) ||
            CombatManager.Instance.History.Entries.Any(e=>e is PotionUsedEntry ||
                e is CardPlayStartedEntry card && !card.CardPlay.IsAutoPlay))
        {
            retainedOpening=null;openingKey=null;
            savedResult=localRoutes?.Opening is {Ending:{Verified:true},Seed:{Terminal:true}} archived?archived:null;
            if(savedResult?.Hud is {} plan)
            {
                dependencyGraphs.Restore(plan,savedResult.RoundGraphs);
                board=live;rootRound=plan.Start;openingWitness=localRoutes!.OpeningWitness;
            }
            nextHud=0;return;
        }
        if(!Capture(live)){root=null;board=null;openingChecked=!captureDeferred;return;}
        // Capture itself must not consume the automatic test's start request.
        if(caseOutput!=null && !openingWanted)caseStarted=false;
        string key=OpeningArchive.Key(live,stamp);
        openingWitness=SlCache.Witness(live);
        var retained=retainedOpening;
        if(retained?.Key!=key || retained.Witness!=openingWitness)retained=null;
        retainedOpening=null;
        exchange=new(openingWitness,root!,ReplayDirectory,retained?.Battle);sharedOpening=UseWorker;
        if(key!=openingKey){savedResult=null;openingKey=key;}
        try
        {
            var disk=localRoutes?.OpeningWitness==openingWitness?localRoutes.Opening:null;
            if(disk is {ResourceScoreVersion:<3} || disk?.Seed!=null && GoalResult.KindAt(root!)!=CombatGoal.None && disk.Hud?.Goal==null)
            {
                archivedSeed=disk.Seed;
                disk=null;
            }
            if(disk!=null)disk=RefreshArchivedDisplay(disk);
            if(disk?.Ending?.Verified==true && disk.Improves(savedResult))
            {
                savedResult=disk;
                if(disk.Hud is {} archivedPlan)dependencyGraphs.Restore(archivedPlan,disk.RoundGraphs);
                if(disk.Seed!=null)
                {
                    if(root!.Party.Count>1 && disk.Hud!=null)
                        archivedRoute=new(disk,root,new PausedRoute(root,null,null,null,null,openingWitness,false,localSeed:disk.Seed));
                    else archiveChoices=RecoverArchivedChoices(disk,root!).GetEnumerator();
                }
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {archiveError="开局路线读取失败："+e.Message;}
        exchange?.PublishOpening(savedResult?.Seed);
        meter=new();if(retained!=null)meter.Retain(retained.Visits);
        rotations=0;finishedNodes=finishedSettled=finishedReplays=0;
        nextHud=0;
    }

    // Earlier archives have choice tokens but no mapping from the sampled offer to the pile.
    // Replay only the necessary prefix, one action per frame; do not expand the search tree.
    private IEnumerator<bool>? archiveChoices;
    private sealed record ArchivedRoute(OpeningResult Saved,KernelSession Root,PausedRoute Replay);
    private ArchivedRoute? archivedRoute;

    private void CancelArchivedRoute()
    {
        if(archivedRoute is {} archive)archive.Replay.Cancelled=true;
        archivedRoute=null;archiveChoices?.Dispose();archiveChoices=null;
    }

    private void AdvanceArchivedChoices()
    {
        if(archivedRoute is {} pending)
        {
            if(!ReferenceEquals(pending.Saved,savedResult) || !ReferenceEquals(pending.Root,root))CancelArchivedRoute();
            else if(pending.Replay.Done)
            {
                archivedRoute=null;
                if(pending.Replay.Result is {Ending.Verified:true} recovered && pending.Saved.Seed is {} seed &&
                    recovered.Ending.Victory==seed.Won && recovered.EnemyHp==seed.EnemyHp &&
                    recovered.EnemyCount==seed.EnemyCount && recovered.Bottles==seed.Bottles &&
                    recovered.Hp.SequenceEqual(seed.Hp) && recovered.Steps.Length==seed.Steps.Length)
                {
                    shownLine=recovered;
                    dependencyGraphs.Track(pending.Saved.Hud!,pending.Root,recovered);
                    if(openingWitness is {} witness)QueueRouteArchive(pending.Saved,witness,true);
                }
                nextHud=0;
            }
        }
        if(archiveChoices==null)return;
        try{if(archiveChoices.MoveNext())return;}
        catch(Exception e){GD.PrintErr("[CoopSolver] Cached choice layout: "+e.Message);}
        archiveChoices.Dispose();archiveChoices=null;
    }
    private IEnumerable<bool> RecoverArchivedChoices(OpeningResult original,KernelSession origin)
    {
        var seed=original.Seed!;
        int last=Array.FindLastIndex(seed.Steps,s=>s.Fee==null && s.Slot<0 && s.Id!="end" || s.ChoiceLayouts==null && s.Menus.Any(ChoiceLayout.Ambiguous));
        if(last<0)yield break;
        var state=origin.ForkForReplay();
        var steps=(SavedStep[])seed.Steps.Clone();bool changed=false;
        for(int i=0;i<=last;i++)
        {
            if(!ReferenceEquals(root,origin) || !ReferenceEquals(savedResult,original))yield break;
            var input=steps[i];KernelTeamSearch.Action? resolved=null;
            ReplayCoverage.Begin(state);
            try
            {
                if(input.Recipe!=null){if(!ActionFrontier.Replay(state,input.Recipe,out resolved,out _))yield break;}
                else if(input.Bind(state) is not {} move || !state.ReplayPlanned(move,int.MaxValue,out _,out resolved))yield break;
            }finally{ReplayCoverage.End(resolved);}
            if(resolved!=null && (resolved.Card!=null && input.Fee==null || input.Menus.Any(ChoiceLayout.Ambiguous)))
            {
                var fresh=SavedStep.From(origin,resolved,input.Recipe);
                steps[i]=input with {Fee=fresh.Fee,ChoiceLayouts=fresh.ChoiceLayouts??input.ChoiceLayouts};
                changed=true;
            }
            if(i<last)yield return true;
        }
        if(!changed)yield break;
        savedResult=RefreshArchivedDisplay(original with {Seed=seed with {Steps=steps}});
        var replacement=savedResult;
        potions.ReplaceDisplay(d=>ReferenceEquals(d.Result,original)?d with {Result=replacement}:d);
        nextHud=0;
    }

    private void UpdateFollow(CombatState? live,bool stable,bool start,bool switched)
    {
        if(!ReferenceEquals(board,live) && live!=null || live==null && board!=null)
        {
            ClearTree();reuseSource?.Dispose();reuseSource=null;savedResult=null;meter=new();rotations=0;finishedNodes=finishedSettled=finishedReplays=0;
            board=live;
        }
        if(!stable || live==null)
        {
            if(!waitingForStable)
            {
                activeTime.Stop();engine?.Pump(false);
                waitingForStable=true;
            }
            return;
        }
        long now=Environment.TickCount64;
        var actionId=RunManager.Instance.ActionQueueSet.NextActionId;
        bool inspect=root==null || completedInputs.Count!=0 || waitingForStable || start || switched ||
            initialActionId!=actionId || revision!=KernelSession.LiveRevision || now>=nextStampCheck;
        if(inspect)
        {
            nextStampCheck=now+500;
            string next=BoardStamp(live);
            if(root==null || next!=stamp)
            {
                if(UseWorker)
                {
                    engine?.Pump(false);
                    var previousRoot=root;
                    if(previousRoot?.TurnNumber==live.RoundNumber && owner is {} controller)
                    {
                        var selected=ReferenceEquals(potionRoot,previousRoot)?displayedPotion:null;
                        var result=selected?.Result??savedResult;
                        if(result?.Ending?.Verified==true)
                            foreach(var input in completedInputs.Where(input=>input.Kind=="end"))
                                controller.beforeReady.TryAdd(input.Seat,new(previousRoot,result,selected?.Line??shownLine,
                                    ReferenceEquals(meter,controller.meter)?null:meter));
                    }
                    if(!Capture(live)){waitingForStable=true;if(!captureDeferred)root=null;return;}
                    meter=new SimulationMeter();
                    capturedWitness=SlCache.Witness(live);livePosition=owner?.exchange?.Current(capturedWitness);
                    ReconcileLive(root!,previousRoot);boundRoot=null;
                    if(followWanted && !start){running=true;activeTime.Start();}
                }
                else
                {
                var old=planner;planner=null;
                // A second native action can arrive while the previous transfer
                // is only half indexed. Do not chain superseded snapshots.
                if(old?.Recovering==true){old.Dispose();old=null;}
                reuseSource?.Dispose();reuseSource=null;
                ClearTree();savedResult=null;
                if(!Capture(live)){old?.Dispose();root=null;return;}
                reuseSource=old;
                }
            }
            completedInputs.Clear();initialActionId=actionId;revision=KernelSession.LiveRevision;
        }
        bool wasWaiting=waitingForStable;waitingForStable=false;
        if(running && (wasWaiting || switched))activeTime.Start();
        bool begin=followWanted && !running && (planner==null || switched);
        UpdateTree(live,true,start || begin);
    }

    private void DropInactiveBattle(CombatState? current)
    {
        if(board==null || ReferenceEquals(board,current))return;
        ClearTree();reuseSource?.Dispose();reuseSource=null;savedResult=null;
    }

    private void Publish(OpeningResult result)
    {
        if(result.Ending is not {Verified:true} || !result.Improves(savedResult))return;
        savedResult=result;
        var local=owner??this;
        string? witness=Follow?capturedWitness:openingWitness;
        if(result.Seed is {Terminal:true} seed && witness!=null && local.battleKey!=null && !local.FollowingHost)
        {
            if(Follow && livePosition!=null)local.exchange?.PublishLive(livePosition,seed);
            bool opening=!Follow || witness==local.openingWitness;
            if(opening)
            {
                if(result.Improves(local.savedResult))local.savedResult=result;
                local.exchange?.PublishOpening(local.savedResult?.Seed);
            }
            QueueRouteArchive(result,witness,opening);
        }
        local.nextHud=0;
    }

    private sealed record PendingRouteArchive(OpeningResult Result,string Battle,string Witness,bool Opening,int GraphCount);
    private PendingRouteArchive? pendingRouteArchive;
    private const ulong ArchiveGraphViewer=ulong.MaxValue-1;

    private void QueueRouteArchive(OpeningResult result,string witness,bool opening)
    {
        var local=owner??this;
        if(local.battleKey is not {} battle)return;
        pendingRouteArchive=new(result,battle,witness,opening,RouteGraphBook.VerifiedGraphCount(result.RoundGraphs));
        if(result.Hud?.Party.Length is not >1)FlushRouteArchive();
    }

    private void FlushRouteArchive()
    {
        if(pendingRouteArchive is not {} pending)return;
        var local=owner??this;
        if(local.battleKey!=pending.Battle || local.FollowingHost)
        {
            pendingRouteArchive=null;
            return;
        }
        var result=pending.Result;
        HudGraph[]? graphs=null;
        if(result.Hud is {Party.Length:>1} plan)
        {
            if(local.dependencyGraphs.Request(ArchiveGraphViewer,plan,plan.Start,out _,pending.Opening?0:1)==null)return;
            graphs=local.dependencyGraphs.Snapshot(plan);
            if(graphs.Length<=pending.GraphCount)return;
            result=result with {RoundGraphs=graphs};
        }
        pendingRouteArchive=graphs==null?null:pending with {GraphCount=graphs.Length};
        try
        {
            if(local.clearSavedRoutesOnWrite)local.ClearSavedRoutes();
            local.localRoutes=LocalRouteSlots.Save(LocalRouteSlots.FilePath,local.routeRole,pending.Battle,pending.Witness,result,
                opening:pending.Opening,runKey:local.runKey);
            local.archiveError=null;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {local.archiveError="本机路线未写入："+e.Message;}
    }

    private bool RotateIfNeeded()
    {
        var p=planner!;
        if(p.Recovering || Environment.TickCount64<nextRotation)return false;
        var gc=GC.GetGCMemoryInfo();
        bool pressure=gc.HighMemoryLoadThresholdBytes>0 && gc.MemoryLoadBytes>=gc.HighMemoryLoadThresholdBytes &&
            gc.HeapSizeBytes-initialHeap>192L*1024*1024;
        if(p.Nodes<24000 && p.RoundFileBytes<48L*1024*1024 && !(pressure && p.Nodes>=4000))return false;
        finishedNodes+=p.Nodes;finishedSettled+=p.SettledPlans;finishedReplays+=p.Replays;
        p.Dispose();rotations++;nextRotation=Environment.TickCount64+5000;
        planner=new AnytimeSearch(root!,roundSeed:171+rotations){Evaluation=(owner??this).evaluation,FollowEnabled=Follow};
        planner.IncumbentChanged+=PublishBest;
        if(caseOutput!=null)planner.IncumbentChanged+=RecordIncumbent;
        planner.KeepBestFromSameRoot(p);
        if(pressure)GC.Collect(2,GCCollectionMode.Optimized,blocking:false,compacting:false);
        return true;
    }
}
