using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace CoopSolver;
internal sealed partial class Advisor
{
    private bool UseWorker=>!legacyMode && caseOutput==null;
    private InteractiveSearch? engine;
    private KernelSession? boundRoot,failedWorkerRoot;
    private string? workerError;
    private string? CurrentWorkerError=>root!=null && ReferenceEquals(root,failedWorkerRoot)?workerError:null;
    private void ClearWorkerError(){failedWorkerRoot=null;workerError=null;}
    private int generation;
    private InteractiveSearch.Line? shownLine,lastPublication,pendingSuffix;
    // Keep displayed wins for immediate handoff, including lines with the same first input.
    // The battle exchange retains the compact recipes across later live positions.
    private readonly List<InteractiveSearch.Line> previousRecommendations=new();
    private sealed record DisplayedRound(HudPlan Plan,InteractiveSearch.Line Line,RouteGraphBook.VerifiedRound Proof);
    private readonly List<DisplayedRound> displayedRounds=new();
    private KernelSession? recommendationsRoot;
    private void ClearRecommendations()
    {
        previousRecommendations.Clear();
        displayedRounds.Clear();
        recommendationsRoot=null;
    }

    private void TrackRecommendationRoot()
    {
        if(ReferenceEquals(recommendationsRoot,root))return;
        ClearRecommendations();
        recommendationsRoot=root;
    }

    private void RememberDisplayedRound(HudPlan plan,HudGraph graph)
    {
        if(root==null || (owner??this).dependencyGraphs.PublishedRound(plan,root,graph) is not {} record)return;
        TrackRecommendationRoot();
        if(displayedRounds.Any(item=>ReferenceEquals(item.Proof,record.Proof)))return;
        displayedRounds.Add(new(plan,record.Line with {Trail=Array.Empty<KernelPlanningState>()},record.Proof));
    }

    private void RememberRecommendation(InteractiveSearch.Line line)
    {
        if(!Follow && !sharedOpening)return;
        TrackRecommendationRoot();
        if(root==null || line.Ending?.Victory!=true || line.Steps.Length<2)return;
        previousRecommendations.Add(line with {Trail=Array.Empty<KernelPlanningState>()});
    }

    private bool MatchesInputPrefix(InteractiveSearch.Line line,KernelSession? origin)
    {
        if(completedInputs.Count==0 || completedInputs.Count>=line.Steps.Length)return false;
        for(int i=0;i<completedInputs.Count;i++)
            if(!completedInputs[i].Matches(line.Steps[i],origin))return false;
        return true;
    }

    private int[] startHp=Array.Empty<int>();
    private int startBottles;
    private Dictionary<uint,string> battleTargetNames=new();
    private readonly Dictionary<uint,string> observedEnemyNames=new();
    private CombatState? targetLabelBattle;
    private Dictionary<uint,string> targetNames=>(owner??this).battleTargetNames;
    private string liveMessage="",lastTrim="";
    private bool pendingCheckVisible,routeDiverged;
    private HudPlan? transitionPlan;

    private void RejectLiveRoute()
    {
        if(transitionPlan!=null)(owner??this).ClearLiveHistory();
        routeDiverged|=transitionPlan!=null;
        transitionPlan=null;pendingCheckVisible=false;shownLine=pendingSuffix=null;orderedMove=null;savedResult=null;
        liveMessage="实战偏离原路线";
        if(owner!=null)owner.nextHud=0;
    }

    private void CaptureLabels(CombatState state)
    {
        var controller=owner??this;
        var known=controller.observedEnemyNames;
        if(!ReferenceEquals(controller.targetLabelBattle,state))
        {
            controller.targetLabelBattle=state;
            known.Clear();
        }
        foreach(var enemy in state.Enemies)
            if(enemy.CombatId is uint id)known[id]=enemy.Name.ToString();

        var ordered=state.Enemies.Where(enemy=>enemy.IsAlive)
            .Select(enemy=>(enemy,point:NCombatRoom.Instance?.GetCreatureNode(enemy)?.Hitbox.GetGlobalRect().GetCenter()))
            .OrderBy(item=>item.point?.X).ThenBy(item=>item.point?.Y).ToArray();
        bool positioned=ordered.All(item=>item.point.HasValue);
        var names=new Dictionary<uint,string>();
        for(int i=0;i<ordered.Length;i++)
        {
            var (enemy,point)=ordered[i];
            if(enemy.CombatId is not uint id)continue;
            string name=known[id];
            if(ordered.Length==1){names[id]=name;continue;}
            if(!positioned){names[id]=name+"（站位待确认）";continue;}
            string side=SideLabel(i,ordered.Length);
            var column=ordered.Where(item=>Math.Abs(item.point!.Value.X-point!.Value.X)<20)
                .OrderBy(item=>item.point!.Value.Y).ToArray();
            if(column.Length>1)side+=$"上{Array.FindIndex(column,item=>item.enemy==enemy)+1}";
            names[id]=$"{name}（{side}）";
        }
        foreach(var (id,name) in known)names.TryAdd(id,name);
        if(!Ui.SameTargetNames(names,targetNames))controller.battleTargetNames=names;
    }

    private static string SideLabel(int index,int count)=>index+1<=count-index?$"左{index+1}":$"右{count-index}";

    private sealed record OrderedMove(HudPlan Plan,InteractiveSearch.Line Original,KernelSession Origin,
        RouteGraphBook.VerifiedRound Turn,int[] Consumed,InteractiveSearch.Line Tail,KernelSession Observed);
    private OrderedMove? orderedMove;
    private OrderedMove? ConfirmedOrder=>orderedMove is {} order && ReferenceEquals(order.Observed,root) &&
        (pausedRoute?.OrderConfirmed==true ||
            engine?.Latest is {OrderConfirmed:true} published && published.Generation==generation)?order:null;

    private static int[]? ConsumeOrder(InteractiveSearch.Line original,RouteGraphBook.VerifiedRound turn,
        IReadOnlyList<int> previous,IEnumerable<Played> played,KernelSession origin)
    {
        var used=new HashSet<int>(previous);var next=previous.ToList();
        var actions=turn.View.Actions;
        foreach(var input in played)
        {
            HudGraphAction? candidate=null;
            foreach(var a in actions)
                if(!used.Contains(a.Step) && original.Steps[a.Step].Player.NetId==input.Seat)
                {candidate=a;break;}
            if(candidate==null || !input.Matches(original.Steps[candidate.Step],origin) ||
                turn.Entry.Graph.Dependencies.Any(edge=>edge.After==candidate.Step && !used.Contains(edge.Before)))return null;
            used.Add(candidate.Step);next.Add(candidate.Step);
        }
        return next.Count>previous.Count?next.ToArray():null;
    }

    private bool PrepareOrderedMove(KernelSession actual,KernelSession previous,HudPlan? earlierPlan,OrderedMove? active)
    {
        if(completedInputs.Count==0 || owner==null)return false;
        if(active!=null && ReferenceEquals(active.Observed,previous) && ReferenceEquals(active.Tail,shownLine) &&
            ContinueOrderedMove(actual,active.Plan,active.Original,active.Origin,active.Turn,active.Consumed,
                active.Tail.OrderProof?.PlayedCards))return true;
        if(earlierPlan!=null && shownLine!=null &&
            owner.dependencyGraphs.TryGet(earlierPlan,previous,shownLine,previous.TurnNumber,out var found) &&
            found is {Entry:{Graph:{Verified:true},Round:{Start:0}}} &&
            ContinueOrderedMove(actual,earlierPlan,shownLine,previous,found,Array.Empty<int>()))return true;
        if(ReferenceEquals(recommendationsRoot,previous))
            for(int i=displayedRounds.Count-1;i>=0;i--)
            {
                var prior=displayedRounds[i];
                if(prior.Proof.Entry.Round.Start==0 &&
                    ContinueOrderedMove(actual,prior.Plan,prior.Line,previous,prior.Proof,Array.Empty<int>()))return true;
            }
        return false;
    }

    private bool ContinueOrderedMove(KernelSession actual,HudPlan plan,InteractiveSearch.Line line,KernelSession origin,
        RouteGraphBook.VerifiedRound round,int[] consumed,InteractiveSearch.PlayedCopy?[]? previousCopies=null)
    {
        if(ConsumeOrder(line,round,consumed,completedInputs,origin) is not {} used || used.Length>=line.Steps.Length)return false;
        var taken=used.ToHashSet();
        var keep=Enumerable.Range(0,line.Steps.Length).Where(i=>!taken.Contains(i)).ToArray();
        var copies=(previousCopies??new InteractiveSearch.PlayedCopy?[consumed.Length])
            .Concat(completedInputs.Select(input=>input.Kind=="card"?new InteractiveSearch.PlayedCopy(input.Physical,input.Copy):null))
            .ToArray();
        var proof=new InteractiveSearch.OrderedPrefixProof(origin,used.Select(i=>line.Steps[i]).ToArray(),
            used.Select(i=>line.Recipes.ElementAtOrDefault(i)).ToArray(),copies);
        var tail=line with {Steps=keep.Select(i=>line.Steps[i]).ToArray(),
            Recipes=keep.Select(i=>line.Recipes.ElementAtOrDefault(i)).ToArray(),
            Trail=Array.Empty<KernelPlanningState>(),Kills=null,Rounds=null,FirstHp=null,
            BridgeAfter=0,JoinProof=null,RequireJoin=false,OrderProof=proof};
        orderedMove=new(plan,line,origin,round,used,tail,actual);
        transitionPlan=plan;
        ClearRecommendations();
        shownLine=pendingSuffix=tail;savedResult=null;pendingCheckVisible=true;
        lastTrim=liveMessage="已验证队友动作的换序 · 正在核对盘面";
        return true;
    }

    private void ReconcileLive(KernelSession fresh,KernelSession? previousRoot)
    {
        CancelPausedRoute();TrackPausedInput(fresh,previousRoot);
        var earlierPlan=savedResult?.Hud??transitionPlan;
        if(displayedPotion?.Line is {} selected && ReferenceEquals(potionRoot,previousRoot))
        {shownLine=selected;earlierPlan=displayedPotion.Result.Hud;}
        var activeOrder=orderedMove;orderedMove=null;
        pendingSuffix=null;lastPublication=null;pendingCheckVisible=false;lastTrim="";
        transitionPlan=earlierPlan;
        var actual=fresh.PlanningState(fresh.Party.Select(p=>p.NetId));
        if(owner is {} controller)
        {
            foreach(var undo in completedInputs.Where(input=>input.Kind=="undo"))
            {
                if(!controller.beforeReady.Remove(undo.Seat,out var before) ||
                    before.Root.TurnNumber!=fresh.TurnNumber ||
                    !before.Root.PlanningState(fresh.Party.Select(p=>p.NetId)).Matches(actual))continue;
                // Restore the verified route and its graph at the exact board
                // that was active before the reversible ready action.
                root=before.Root;meter=before.Meter??meter;
                savedResult=before.Result;shownLine=pendingSuffix=before.Line;
                transitionPlan=null;routeDiverged=false;liveMessage="";
                controller.nextHud=0;
                return;
            }
        }
        if(capturedWitness!=null && owner?.exchange?.MenuSuffix(capturedWitness,owner.livePotion.Manual?.Key) is {} chosen)
        {
            ClearRecommendations();
            shownLine=pendingSuffix=chosen;savedResult=null;pendingCheckVisible=true;
            liveMessage="正在核对选牌后的余线";return;
        }
        if(previousRoot!=null && PrepareOrderedMove(fresh,previousRoot,earlierPlan,activeOrder))return;
        int used=0;
        if(shownLine!=null && activeOrder==null)
            for(int i=0;i<shownLine.Trail.Length && i+1<shownLine.Steps.Length;i++)
                if(shownLine.Trail[i].Matches(actual)){used=i+1;break;}
        if(used==0 && ReferenceEquals(recommendationsRoot,previousRoot) &&
            (shownLine==null || !MatchesInputPrefix(shownLine,previousRoot)))
        {
            var earlier=previousRecommendations.LastOrDefault(line=>MatchesInputPrefix(line,previousRoot));
            if(earlier!=null)shownLine=earlier;
        }
        ClearRecommendations();
        if(shownLine==null)
        {
            if(transitionPlan!=null)RejectLiveRoute();
            else {savedResult=null;liveMessage="正在搜索当前盘面";}
            return;
        }
        bool exact=used>0;
        if(!exact && activeOrder==null && MatchesInputPrefix(shownLine,previousRoot))
        {
            used=completedInputs.Count;
            if(used<=shownLine.Trail.Length && owner?.ReplayDirectory is {} directory)
                RecordDrift(directory,shownLine.Trail[used-1].FirstMismatch(actual));
        }
        if(used>0)
        {
            var rest=shownLine with {Kills=null,Rounds=null,Steps=shownLine.Steps.Skip(used).ToArray(),Trail=shownLine.Trail.Skip(used).ToArray(),Recipes=shownLine.Recipes.Skip(used).ToArray(),FirstHp=null};
            savedResult=null;shownLine=rest;pendingSuffix=rest;pendingCheckVisible=true;
            lastTrim=liveMessage=$"已执行 {used} 步 · "+(exact?"盘面匹配，复核余线":"动作匹配，复核余线");
            return;
        }
        if(previousRoot!=null && TryEarlyEnd(fresh,previousRoot))return;
        // Test one nearby alternative only when the player actually deviates.
        // No end-turn, forced choice or potion is crossed, and an ambiguous
        // duplicate is never removed by guessing. All remaining steps are replayed.
        if(completedInputs.Count==1 && completedInputs[0].Kind=="card")
        {
            int at=-1;
            for(int i=0;i<shownLine.Steps.Length;i++)
            {
                var step=shownLine.Steps[i];
                if(step.Card==null || step.Choices is {Count:>0} || step.TurnStartChoices is { } t &&
                    (t.ToRound!=t.FromRound || t.Choices.Count!=0))break;
                if(!completedInputs[0].Matches(step,previousRoot))continue;
                if(at>=0){at=-1;break;}
                at=i;
            }
            if(at>0)
            {
                var original=shownLine;
                var keep=Enumerable.Range(0,original.Steps.Length).Where(i=>i!=at).ToArray();
                pendingSuffix=original with {Steps=keep.Select(i=>original.Steps[i]).ToArray(),
                    Recipes=keep.Select(i=>i<original.Recipes.Length?original.Recipes[i]:null).ToArray(),
                    Trail=Array.Empty<KernelPlanningState>(),FirstHp=null,BridgeAfter=at,
                    JoinProof=at<original.Trail.Length?original.Trail[at]:null};
                shownLine=pendingSuffix;savedResult=null;pendingCheckVisible=true;
                lastTrim=liveMessage=$"提前执行第 {at+1} 步 · 正在从现盘试接余线";
                // Keep the previous line marked as reference until the bridge
                // succeeds; its old endpoint is not a prediction for the deviation.
                return;
            }
        }
        RejectLiveRoute();
    }

    private bool TryEarlyEnd(KernelSession fresh,KernelSession previous)
    {
        if(shownLine==null || completedInputs.Count!=1 || completedInputs[0].Kind!="end" ||
            EarlyEndContinuation(shownLine,previous,fresh,completedInputs[0].Seat) is not {} reordered)return false;
        shownLine=pendingSuffix=reordered;savedResult=null;pendingCheckVisible=true;
        lastTrim=liveMessage="提前结束回合 · 正在复核余线";
        return true;
    }

    private static bool StartsTurn(KernelTeamSearch.Action step)=>step.TurnStartChoices is {} t &&
        (t.ToRound!=t.FromRound || t.OwnersTurnNumbers.Count!=0 || t.Choices.Count!=0);

    internal static InteractiveSearch.Line? EarlyEndContinuation(InteractiveSearch.Line line,
        KernelSession previous,KernelSession now,ulong seat)
    {
        var actor=previous.Party.FirstOrDefault(p=>p.NetId==seat);
        if(actor==null || previous.TurnNumber!=now.TurnNumber || previous.IsReady(actor) || !now.IsReady(actor))
            return null;
        int at=Array.FindIndex(line.Steps,s=>s.Player.NetId==seat || StartsTurn(s));
        if(at<=0 || line.Ending is not {Verified:true} || !line.Steps[at].EndTurn || line.Steps[at].Player.NetId!=seat)
            return null;
        var steps=line.Steps.Where((_,i)=>i!=at).ToArray();
        var recipes=Enumerable.Range(0,line.Steps.Length).Where(i=>i!=at).Select(i=>line.Recipes.ElementAtOrDefault(i)).ToArray();
        if(StartsTurn(line.Steps[at]))
        {
            // Advancing the side now belongs to the last other end-turn input.
            // Its existing no-transition receipt cannot be replayed as-is.
            if(!steps[at-1].EndTurn || StartsTurn(steps[at-1]))return null;
            steps[at-1]=steps[at-1] with {TurnStartChoices=line.Steps[at].TurnStartChoices};
            recipes[at-1]=null;
        }
        return line with {Steps=steps,Recipes=recipes,Trail=Array.Empty<KernelPlanningState>(),
            Kills=null,Rounds=null,FirstHp=null,BridgeAfter=at,JoinProof=line.Trail.ElementAtOrDefault(at),
            RequireJoin=false,RequireSameEnding=true};
    }

    private void RecordDrift(string directory,string? difference)
    {
        if(difference==null)return;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path=System.IO.Path.Combine(directory,"replay-differences.jsonl");
            var info=new System.IO.FileInfo(path);
            if(info.Exists && info.Length>=256*1024)return;
            var item=new {attempt=owner!.attempt,round=rootRound,actionId=MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionQueueSet.NextActionId,
                inputs=completedInputs.Select(a=>new {a.Seat,a.Kind,a.Id,a.Target,a.Slot}),difference};
            System.IO.File.AppendAllText(path,System.Text.Json.JsonSerializer.Serialize(item)+"\n");
        }
        catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException){}
    }

    private int restarting;
    private void ClearSavedRoutes()
    {
        if(battleKey==null)return;
        var weights=evaluation.Weights;
        localRoutes=LocalRouteSlots.ClearRoutes(LocalRouteSlots.FilePath,routeRole,battleKey,runKey,weights);
        lastWrittenWeights=weights;clearSavedRoutesOnWrite=false;
    }

    private void RestartSearch()
    {
        if(hudRoom==null || FollowingHost || System.Threading.Interlocked.Exchange(ref restarting,1)!=0)return;
        ClearCaptureWarnings();tickError=null;
        requested=false;searchEnabled=true;manuallyPaused=false;pauseReason=null;nextHud=nextCheck=0;
        StopPlayback();CancelArchivedRoute();beforeReady.Clear();dependencyGraphs.Clear();
        presentedRoute=null;
        openingHistory.Clear();ClearLiveHistory();
        openingChanges.Observe(null,null);liveChanges.Observe(null,null);
        openingPotion.Manual=livePotion.Manual=null;playbackCandidate=null;
        hud?.ResetLiveDisplay();window?.Panel.ResetLiveDisplay();
        reuseSavedSearch=false;localRoutes=null;
        if(battleKey!=null)
        {
            clearSavedRoutesOnWrite=true;
            try{ClearSavedRoutes();}
            catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {archiveError="本机旧路线未清除："+error.Message;}
        }
        foreach(var side in new[]{this,liveAdvisor!})
        {
            side.ClearWorkerError();side.CancelPausedRoute();
            side.engine?.RetireForRestart();side.engine=null;side.boundRoot=null;side.meter=new SimulationMeter();
            side.savedResult=null;side.archivedSeed=null;side.pendingRouteArchive=null;
            side.shownLine=side.pendingSuffix=side.lastPublication=null;
            side.ClearRecommendations();side.orderedMove=null;
            side.transitionPlan=null;side.routeDiverged=side.pendingCheckVisible=false;side.lastTrim=side.liveMessage="";
            side.potions.Clear();side.displayedPotion=null;side.lastSaved=side.lastOffers=side.lastPromotions=null;
            side.running=false;side.activeTime.Stop();side.openingWanted=side.followWanted=false;
        }
        foreach(var slot in PendingChoices)
        {
            slot.Probe?.Cancel();slot.Probe=null;slot.Rendered=slot.LastMenuLine=null;
            slot.Display=null;slot.Incumbent=null;slot.LastOptions=null;
            slot.PotionRows=Array.Empty<PotionRoute<DisplayedPotion>>();
            slot.Choices=Array.Empty<KernelChoice>();
        }
        retainedOpening=null;var battle=exchange;var budget=Memory;
        InteractiveSearch.Dispatcher.Split(false);
        // Retire the old workers before clearing their tree and verified routes.
        // The real action tape and the evaluation weights remain available.
        InteractiveSearch.Dispatcher.Post(()=>{
            try
            {
                battle?.ReleaseSearchGraph();budget.Trees.Reset();
                GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,blocking:true,compacting:false);
            }
            finally{System.Threading.Volatile.Write(ref restarting,0);}
        });
    }

    private void UpdateInteractive(bool start)
    {
        if(root==null)return;
        if(start){running=!running;if(running)activeTime.Start();else activeTime.Stop();}
        if(!running)
        {
            engine?.Pump(false);
            if(Follow && owner?.searchEnabled==false)UpdatePausedRoute();
            return;
        }
        if(Follow && owner?.Memory.Waiting(active:true)==true)
        {
            engine?.Pump(engine.IsRecovering);
            UpdatePausedRoute();
            return;
        }
        if(CurrentWorkerError!=null)return;
        if(failedWorkerRoot!=null)ClearWorkerError();
        CancelPausedRoute();pausedInputs.Clear();pausedOrigin=null;
        if(!ReferenceEquals(boundRoot,root))
        {
            boundRoot=root;generation++;lastPublication=null;
            engine??=new InteractiveSearch();
            var first=owner??this;
            bool atOpening=!Follow || capturedWitness!=null && capturedWitness==first.openingWitness;
            if(Follow && livePosition!=null)first.exchange?.Track(livePosition,meter);
            engine.Evaluation=first.evaluation;
            engine.Bind(generation,root,actors,Follow,meter,pendingSuffix,atOpening?first.SeedDirectory:null,first.attempt,atOpening?(first.savedResult?.Seed??first.archivedSeed):first.localRoutes?.ForPosition(capturedWitness)?.Seed,first.exchange,livePosition,first.Memory);pendingSuffix=null;
        }
        var update=engine!.Latest;
        if(update?.Generation==generation)
        {
            if(update.Error!=null)
            {
                if(update.Result!=null)RenderLine(update.Result);
                failedWorkerRoot=root;workerError=update.Error;(owner??this).nextHud=0;
                activeTime.Stop();engine.Release();engine=null;boundRoot=null;
                GD.PrintErr("[CoopSolver] "+update.Error);return;
            }
            if(update.SuffixFailed && pendingCheckVisible)
            {
                RejectLiveRoute();
            }
            if(update.Result is {} line && !ReferenceEquals(lastPublication,line))
            {
                lastPublication=line;
                RenderLine(line);
            }
            if(update.SuffixVerified && pendingCheckVisible)
            {
                pendingCheckVisible=false;
                if(lastTrim.Length>0)liveMessage=lastTrim.Split('·')[0]+"· 已复算可继续执行的步骤";
                (owner??this).nextHud=0;
            }
        }
        // Don't queue work faster than the render loop can consume. Both
        // modes share one serial worker regardless of UI refresh frequency.
        engine.Pump(true);
    }

    private void RenderLine(InteractiveSearch.Line value)
    {
        if(value.Ending is not {Verified:true})return;
        var record=RouteRecord(value);
        var model=record.Hud!;var seed=record.Seed;
        if(!record.Improves(savedResult))
        {
            if(shownLine==null && savedResult?.Seed!=null && seed!=null &&
                savedResult.Seed.Steps.Length==seed.Steps.Length && record.Route==savedResult.Route)
            {
                shownLine=value;RememberRecommendation(value);
                if(savedResult.Hud is {} archived && root!=null)
                {
                    (owner??this).dependencyGraphs.Track(archived,root,value);
                    (owner??this).nextHud=0;
                }
            }
            return;
        }
        RememberRecommendation(value);
        if(ConfirmedOrder is {} confirmed && root!=null && value.Steps.Length==confirmed.Tail.Steps.Length &&
            value.Steps.Zip(confirmed.Tail.Steps,AnytimeSearch.Edge.SameAction).All(same=>same))
        {
            var removed=confirmed.Consumed.ToHashSet();
            var retained=Enumerable.Range(0,confirmed.Original.Steps.Length).Where(i=>!removed.Contains(i)).ToArray();
            (owner??this).dependencyGraphs.ReuseRound(model,root,value,confirmed.Turn,retained);
        }
        bool reconciled=pendingCheckVisible && lastTrim.Length>0;
        orderedMove=null;transitionPlan=null;routeDiverged=pendingCheckVisible=false;
        shownLine=value;(owner??this).nextHud=0;
        liveMessage=Follow?(reconciled?lastTrim.Split('·')[0]+"· 已复算可继续执行的步骤":"当前盘面已复算"):"";
        Publish(record);
    }

    private OpeningResult RouteRecord(InteractiveSearch.Line value)
    {
        string label=value.Ending?.Victory==true?"模拟胜利":"完整路线 · 全队倒下";
        int rounds=Math.Max(0,value.EndTurn-rootRound);
        string health=string.Join("\n",participants.Select((p,i)=>
        {
            var revives=DeathSaves.Events(value.Rounds,p.NetId);
            int delta=value.Rounds?.Sum(r=>r.Loss.ElementAtOrDefault(i))??startHp[i]-value.Hp[i]+revives.Sum(e=>e.Hp);
            string change=delta<0?$"回复 {-delta}":$"损失 {delta}";
            int deltaTurn=value.FirstHp==null?0:value.Rounds?.FirstOrDefault(r=>r.Round==rootRound)?.Loss.ElementAtOrDefault(i)??startHp[i]-value.FirstHp[i]+DeathSaves.Events(value.Rounds,p.NetId,rootRound).Sum(e=>e.Hp);
            string turn=value.FirstHp==null?"":(deltaTurn>=0?$"本轮扣血 {deltaTurn}":$"本轮净回复 {-deltaTurn}")+"  ·  ";
            int recovered=value.Rounds?.Sum(r=>r.Recovered?.ElementAtOrDefault(i)??0)??0;
            string recovery=recovered>0?$"  ·  回 {recovered} 血":"";
            string remaining=value.Ending?.Victory==true && participants.Length>1 && value.Hp[i]==0?"胜后复活，余 1 血":$"剩 {value.Hp[i]} 血";
            return $"{p.Character.Title.GetFormattedText()}   {turn}至路线末{change}{recovery}  ·  {remaining}"+string.Concat(revives.Select(e=>$"  ·  {DeathSaves.Name(e)}复活 +{e.Hp}"));
        }));
        string summary=$"{label}  ·  第 {value.EndTurn} 回合（距当前 {rounds} 回合）\n{health}\n"+
            $"用药 {value.Steps.Count(a=>a.Potion!=null)+participants.Sum(p=>DeathSaves.Events(value.Rounds,p.NetId).Count(e=>e.Potion))} 瓶 · 余 {value.Bottles} 瓶   |   {value.Steps.Length} 步"+
            (value.FoundAt>=0?$"   |   发现时搜索 {Compact(value.FoundAt)} 次":"   |   发现时搜索量未记录");
        if(value.Ending?.Victory!=true)summary+=$"\n剩 {value.EnemyCount} 名敌人，共 {value.EnemyHp} 血";
        string route=label+"\n"+FormatSteps(board!,value.Steps,rootRound,root,targetNames);
        if(GoalResult.For(value.Ending) is {} objective)summary=objective.Label()+"  ·  "+summary;
        var model=MakeHud(value,route,summary);
        var seed=SeedRoute.FromLine(root!,value);
        return new OpeningResult(value.Ending,value.Steps.Length,value.Score,route,summary,value.Risks,model,seed,ResourceScoreVersion:4);
    }

    private OpeningResult RefreshArchivedDisplay(OpeningResult saved)
    {
        string NormalizeCount(string text)=>System.Text.RegularExpressions.Regex.Replace(text,
            @"(发现时(?:搜索|模拟|计算) )([0-9]+(?:\.[0-9]+)?)([kMB])(?= 次)",m=>
                decimal.TryParse(m.Groups[2].Value,System.Globalization.NumberStyles.AllowDecimalPoint,System.Globalization.CultureInfo.InvariantCulture,out var n)
                ?m.Groups[1].Value+n.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+m.Groups[3].Value:m.Value);
        saved=saved with {Summary=NormalizeCount(saved.Summary),Hud=saved.Hud is {} h?h with {Result=NormalizeCount(h.Result)}:null};
        var route=System.Text.RegularExpressions.Regex.Replace(saved.Route,@"(?<name>[^\s→]+) #(?<id>\d+)",m=>
            uint.TryParse(m.Groups["id"].Value,out var id) && targetNames.TryGetValue(id,out var label) ? label : m.Groups["name"].Value+"（历史站位未记录）");
        var enemies=board!.Enemies.Where(e=>e.CombatId.HasValue && targetNames.ContainsKey(e.CombatId.Value)).ToArray();
        string ResolveOldName(string name)
        {
            // A bare ordinal existed only when every enemy had the same name.
            if(System.Text.RegularExpressions.Regex.IsMatch(name,@"^[左右]\d+(?:上\d+)?$") &&
                enemies.Length>1 && enemies.All(e=>e.Name.ToString()==enemies[0].Name.ToString()))
                return enemies[0].Name+"（"+name+"）";
            var legacy=System.Text.RegularExpressions.Regex.Match(name,@"^(.+?)（(?:同列从上第 \d+ 只 · )?同名左起第 (\d+) 只，起点 \d+ 血）$");
            if(legacy.Success && int.TryParse(legacy.Groups[2].Value,out int index))
            {
                var siblings=targetNames.Where(k=>enemies.Any(e=>e.CombatId==k.Key && e.Name.ToString()==legacy.Groups[1].Value)).ToArray();
                if(index>0 && index<=siblings.Length)return siblings[index-1].Value;
            }
            var single=enemies.Where(e=>e.Name.ToString()==name).ToArray();
            return single.Length==1?targetNames[single[0].CombatId!.Value]:name;
        }
        string TargetAt(int step,string oldName)
        {
            var steps=saved.Seed?.Steps;
            return steps!=null && step>=0 && step<steps.Length && steps[step].Target is uint id && targetNames.TryGetValue(id,out var label)
                ?label:ResolveOldName(oldName);
        }
        string RefreshSteps(string text)
        {
            var lines=text.Split('\n');int step=-1;
            var steps=saved.Seed?.Steps;
            var pending=new Queue<KernelChoice>();
            for(int i=0;i<lines.Length;i++)
            {
                bool action=lines[i].StartsWith("P",StringComparison.Ordinal);
                if(action)
                {
                    step++;pending.Clear();
                    if(steps!=null && step<steps.Length)
                    {
                        var input=steps[step];input.RestoreLayouts();
                        foreach(var c in input.Menus)pending.Enqueue(c);
                        if(input.Slot>=0)
                        {
                            int colon=lines[i].IndexOf('：');
                            if(colon>=0 && !lines[i][(colon+1)..].StartsWith("用药 "))lines[i]=lines[i].Insert(colon+1,"用药 ");
                        }
                    }
                }
                if(pending.Count>0 && lines[i].Contains('〔'))
                    lines[i]=System.Text.RegularExpressions.Regex.Replace(lines[i],@"〔(P\d+ )(选 |跳过选择)([^〕]*)〕",m=>{
                        if(pending.Count==0)return m.Value;
                        var c=pending.Dequeue();
                        return m.Groups[2].Value=="选 "?"〔"+m.Groups[1].Value+"选 "+ChoiceNames(c)+"〕":m.Value;
                    });
                if(!action)continue;
                if(steps!=null && step<steps.Length && steps[step] is {Slot:<0,Id:not "end"} play &&
                    !string.IsNullOrEmpty(play.Key) && ModelDb.GetByIdOrNull<CardModel>(new ModelId("CARD",play.Id)) is {} card)
                {
                    int colon=lines[i].IndexOf('：');
                    int stop=lines[i].IndexOfAny(new[]{'→','〔'},colon+1);
                    if(stop<0)stop=lines[i].Length;
                    if(colon>=0)
                    {
                        string oldTitle=lines[i][(colon+1)..stop].TrimEnd();
                        lines[i]=lines[i][..(colon+1)]+CardPresentation.PlayTitle(card,play.Key,play.Fee??"")+lines[i][(colon+1+oldTitle.Length)..];
                    }
                }
                int arrow=lines[i].IndexOf(" → ",StringComparison.Ordinal);
                if(arrow<0)continue;
                int start=arrow+3,end=lines[i].IndexOf('〔',start);
                if(end<0)end=lines[i].Length;
                lines[i]=lines[i][..start]+TargetAt(step,lines[i][start..end])+lines[i][end..];
            }
            return string.Join("\n",lines);
        }
        route=RefreshSteps(route);
        if(saved.Hud is {} historical)saved=saved with {Hud=historical with {Steps=RefreshSteps(historical.Steps),
            Kills=historical.Kills?.Select(k=>k with {Name=k.IsTarget?TargetAt(k.Step,k.Name):ResolveOldName(k.Name)}).ToArray()}};
        if(saved.Hud==null)
        {
            var hp=saved.Ending?.PostCombatHp;
            var seats=participants.Select((p,i)=>new HudSeat(p.NetId,playerNames[i],p.Character.Title.GetFormattedText(),null,
                hp?.Count==participants.Length?startHp[i]-hp[i]:null,false,hp?.Count==participants.Length && hp[i]<=0,null)).ToArray();
            int turn=System.Text.RegularExpressions.Regex.Matches(route,@"(?m)^第 (\d+) 回合").Select(m=>int.Parse(m.Groups[1].Value)).DefaultIfEmpty(rootRound).Last();
            saved=saved with {Hud=new HudPlan(rootRound,turn,seats,route,saved.Summary,string.Join("\n",saved.Risks))};
        }
        if(saved.Hud is {} oldHud && oldHud.Party.Length==participants.Length && oldHud.Party.Where((s,i)=>s.Color!=CharacterInk(participants[i].Character)).Any())
        {
            var party=oldHud.Party.Select((s,i)=>s with {Color=CharacterInk(participants[i].Character),
                Hp=s.Hp??(s.ToEnd is int loss?startHp[i]-loss:null)}).ToArray();
            saved=saved with {Hud=oldHud with {Party=party}};
        }
        if(saved.Hud is {EnemyTargets:null} original && saved.Seed is {Steps:{} actions})
        {
            var playerTargets=board!.Players.Select(p=>p.Creature.CombatId).ToHashSet();
            saved=saved with {Hud=original with {EnemyTargets=actions.Select((action,step)=>(action.Target,step))
                .Where(item=>item.Target.HasValue && !playerTargets.Contains(item.Target))
                .ToDictionary(item=>item.step,item=>item.Target!.Value)}};
        }
        if(saved.Hud is {EndEnemyHp:null} hud && saved.Ending is {} terminal)
            saved=saved with {Hud=hud with {EndEnemyHp=terminal.RemainingEnemyHp}};
        if(saved.Ending is not {} end || saved.Summary.Contains("发现时"))return saved with {Route=route};
        var health=participants.Select((p,i)=>$"{p.Character.Title.GetFormattedText()}  损失 {startHp[i]-end.PostCombatHp[i]+DeathSaves.Events(saved.Hud?.Rounds,p.NetId).Sum(e=>e.Hp)} · 剩 {end.PostCombatHp[i]} 血");
        int last=System.Text.RegularExpressions.Regex.Matches(route,@"(?m)^第 (\d+) 回合").Select(m=>int.Parse(m.Groups[1].Value)).DefaultIfEmpty(rootRound).Last();
        string summary=(end.Victory?"模拟胜利":"完整路线 · 全队倒下")+$" · 第 {last} 回合（距当前 {last-rootRound} 回合）\n"+string.Join("\n",health)+"\n"+
            (saved.Summary.Split('\n').LastOrDefault(x=>x.Contains("用药"))??$"药水净消耗 {end.ResourcesConsumed}")+$" · {saved.Steps} 步 · 发现时搜索量未记录";
        if(!end.Victory)summary+=$"\n剩 {end.RemainingEnemies} 名敌人，共 {end.RemainingEnemyHp} 血";
        return saved with {Route=route,Summary=summary};
    }

    internal static string Compact(long value)=>value switch
    {
        >=1_000_000_000=>(value/1_000_000_000d).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+"B",
        >=1_000_000=>(value/1_000_000d).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+"M",
        >=1_000=>(value/1_000d).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+"k",
        _=>value.ToString()
    };
}
