using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

// One below-normal CPU lane for the host. UI ticks only post/coalesce work and
// read immutable publications; a slow native card effect cannot block a frame.
internal sealed class InteractiveSearch
{
    internal static class Dispatcher
    {
        private static readonly Queue<Action> utility=new(),opening=new(),live=new();
        private static readonly AutoResetEvent wake=new(false);
        private static long balance,openingTicks,liveTicks;
        internal static (long Opening,long Live) Times=>(Interlocked.Read(ref openingTicks),Interlocked.Read(ref liveTicks));
        internal static void Charge(bool follow,long elapsed)
        {
            if(follow)Interlocked.Add(ref liveTicks,elapsed);else Interlocked.Add(ref openingTicks,elapsed);
            lock(utility)if(split)balance+=elapsed*(follow?-SearchCapacity.OpeningPercent:100-SearchCapacity.OpeningPercent);
        }
        private static volatile bool split;
        private static bool preferLive=true;
        internal static void Split(bool active){lock(utility){if(split!=active)balance=0;split=active;}}
        static Dispatcher()
        {
            var thread=new Thread(Run){Name="CoopSolver simulation",IsBackground=true,Priority=ThreadPriority.BelowNormal};
            if(ExecutionContext.IsFlowSuppressed())thread.Start();else using(ExecutionContext.SuppressFlow())thread.Start();
        }
        internal static void Post(Action work,bool? follow=null)
        {
            lock(utility)(follow==null?utility:follow.Value?live:opening).Enqueue(work);
            wake.Set();
        }
        private static void Run()
        {
            while(true)
            {
                Action? work;bool? follow=null;
                lock(utility)
                {
                    if(!utility.TryDequeue(out work))
                    {
                        bool takeLive=live.Count!=0 && (opening.Count==0 || (split?balance>0:preferLive));
                        follow=takeLive;(takeLive?live:opening).TryDequeue(out work);preferLive=!takeLive;
                    }
                }
                if(work==null){wake.WaitOne();continue;}
                long begin=Stopwatch.GetTimestamp();
                try{work();}
                catch(Exception error) when(error is not OutOfMemoryException)
                {
                    // Utility callbacks also share this thread. A failed cache,
                    // memory measurement or cleanup must not terminate the game.
                    Godot.GD.PrintErr("[CoopSolver] Simulation dispatcher: "+error);
                }
                if(follow.HasValue)Charge(follow.Value,Stopwatch.GetTimestamp()-begin);
                // Charge overruns to this worker instead of the game's frame.
                Thread.Sleep(Math.Clamp((int)Stopwatch.GetElapsedTime(begin).TotalMilliseconds,5,60));
            }
        }
    }

    internal sealed record Line(KernelTeamSearch.Action[] Steps,KernelPlanningState[] Trail,TerminalRecord? Ending,
        int EndTurn,int[] Hp,int EnemyCount,int EnemyHp,int Bottles,string[] Risks,double Score,long FoundAt,long FoundNodes,int[]? FirstHp,TreeStep?[] Recipes,int BridgeAfter=0,KernelPlanningState? JoinProof=null,RouteKill[]? Kills=null,HudRoundHp[]? Rounds=null,bool RequireJoin=false,OrderedPrefixProof? OrderProof=null,bool RequireSameEnding=false);
    internal sealed record PlayedCopy(CardModel? Physical,CardCopy? Selector);
    internal sealed record OrderedPrefixProof(KernelSession Origin,KernelTeamSearch.Action[] Actions,TreeStep?[] Recipes,
        PlayedCopy?[]? PlayedCards=null);
    internal sealed record Stats(int Nodes,int Evaluations,int ReusedNodes,int ReusedJobs,bool Recovering,int Rotations,int[]? PassHp=null,int[]? DefendedHp=null,int LoadedSeeds=0,int HumanSteps=0,int Joined=0,int LiveInputs=0,int LivePlans=0,int LiveBranches=0,int OpeningContinuations=0,int SharedDepth=0,long SharedBackups=0,long InheritedWork=0)
    { internal int? ProvenTurnLoss {get;init;} internal VerifiedRouteJoins.Count? VerifiedJoins {get;init;} }
    internal sealed record Output(int Generation,Line? Result,Stats Metrics,string? Error,bool SuffixVerified=false,bool SuffixFailed=false,bool Exhausted=false)
    { public PotionRoute<Line>[]? Options {get;init;} public bool MinimumLossProven {get;init;} public int? ProofPercent {get;init;} public double? ProofCoverage {get;init;} public int ProofDone {get;init;} public int ProofTotal {get;init;} public bool ProofBlocked {get;init;} public bool OrderConfirmed {get;init;} }
    private sealed record Request(int Generation,KernelSession Root,Player[] Actors,bool Follow,SimulationMeter Meter,Line? Suffix,string? SeedDir,string Attempt,SeedRoute? Seed,BattleExchange? Exchange,BattleExchange.Position? Position,SearchMemoryBudget Memory,FightEvaluation Evaluation);
    private Request? wanted,owned,exhausted,memoryParked;
    private SharedTreeWalk? proof;
    private bool fullWalk;
    private bool tryHeuristic=true;
    // Before the first win, keep enumerating legal prefixes but reserve most
    // simulated actions for searches that can actually reach a terminal.
    private long preWinWalkDebt;
    internal long LossWalkSlices {get;private set;}
    internal bool IsExhausted=>Volatile.Read(ref wanted) is {} request && ReferenceEquals(request,Volatile.Read(ref exhausted));
    private Output? output;
    private int scheduled;
    private volatile bool paused;
    private AnytimeSearch? planner;
    // Read only on the search dispatcher, including the tree left by a finished worker.
    private AnytimeSearch.Node? stoppedTree;
    internal AnytimeSearch.Node? CurrentTree(KernelSession board)=>ReferenceEquals(owned?.Root,board)?planner?.MemoryRoot??stoppedTree:null;
    private TournamentRun? reference;
    private IEnumerator<bool>? warmup,checkSuffix;
    private object? lastSeed,lastResult;
    private bool potionPhase,suffixVerified,suffixFailed,orderConfirmed;
    private int slices;
    private bool attachmentChecked;
    private long lastSnapshot;
    private readonly SearchMemoryBudget fallbackMemory=new();
    internal bool IsRecovering=>Volatile.Read(ref planner)?.Recovering==true;
    private Line? line;
    private int[]? passHp,defendedHp;
    internal Output? Latest=>Volatile.Read(ref output);
    internal FightEvaluation Evaluation {get;set;}=new();
    internal void Bind(int generation,KernelSession root,Player[] actors,bool follow,SimulationMeter meter,Line? suffix,string? seedDir,string attempt,SeedRoute? seed,BattleExchange? exchange,BattleExchange.Position? position,SearchMemoryBudget? memory=null)
    {
        meter.BeginRoot(root);
        Volatile.Write(ref wanted,new(generation,root,actors,follow,meter,suffix,seedDir,attempt,seed,exchange,position,exchange?.Memory??memory??fallbackMemory,Evaluation));
    }
    internal void Pump(bool run)
    {
        paused=!run;
        if(run)Schedule();
    }
    internal void RetireForRestart()
    {
        Volatile.Write(ref wanted,null);paused=true;
        Dispatcher.Post(()=>{TearDown();Volatile.Write(ref output,null);});
    }
    internal void Release(){Volatile.Write(ref wanted,null);paused=true;Schedule();}
    private void Schedule()
    {
        var request=Volatile.Read(ref wanted);
        if(request!=null && (ReferenceEquals(request,Volatile.Read(ref exhausted)) ||
            ReferenceEquals(request,Volatile.Read(ref memoryParked)) && request.Memory.Waiting(!paused)))return;
        if(Interlocked.CompareExchange(ref scheduled,1,0)==0)Dispatcher.Post(Work,request?.Follow);
    }
    private void TearDown()
    {
        ReleaseWorkers();
        Volatile.Write(ref exhausted,null);Volatile.Write(ref memoryParked,null);
        owned=null;stoppedTree=null;line=null;lastResult=lastSeed=null;
    }
    private void ReleaseWorkers()
    {
        reference?.Dispose();reference=null;warmup?.Dispose();warmup=null;checkSuffix?.Dispose();checkSuffix=null;
        proof?.Dispose();proof=null;
        planner?.Dispose();planner=null;
    }
    private bool ProofMatchesLine()=>
        fullWalk && proof is {AtCurrentRoot:true,Complete:true} && planner?.Best is {Record:{} ending,Script:{} script} && line?.Ending is {} selected &&
        AnytimeSearch.ComparePlans(selected,line.Steps.Length,ending,script.Count,line.Rounds,planner.BestRoundHp)==0;
    private void Work()
    {
        var request=Volatile.Read(ref wanted);
        try
        {
            if(request==null){TearDown();return;}
            if(paused || ReferenceEquals(request,Volatile.Read(ref exhausted)))return;
            if(request.Memory.Waiting(active:true))
            {
                // Complete an already-started ownership transfer before a
                // reclamation job can mutate either of its graphs.
                if(planner?.Recovering==true && owned==request)
                {
                    using var recoveryWork=request.Meter.Enter();
                    planner.ResumeRecovery(Stopwatch.StartNew(),TimeSpan.FromMilliseconds(4));
                    return;
                }
                // An abandoned transfer cannot be used by the next request and
                // would otherwise keep reclamation waiting indefinitely.
                if(owned!=request && (planner?.Recovering==true || request.Follow && planner?.InSharedTree==true))
                    TearDown();
                Volatile.Write(ref memoryParked,request);
                return;
            }
            Volatile.Write(ref memoryParked,null);
            using var count=request.Meter.Enter();
            if(request.Follow)request.Exchange?.Prepare(request.Position);
            if(request!=owned ||
                request.Follow && planner?.InSharedTree==false && request.Position?.Anchor!=null && !attachmentChecked)
            {
                bool sameRoot=owned!=null && ReferenceEquals(request.Root,owned.Root);
                var previous=planner;planner=null;
                TearDown();
                owned=request;passHp=defendedHp=null;slices=0;potionPhase=false;suffixVerified=suffixFailed=orderConfirmed=false;
                attachmentChecked=request.Position?.Anchor!=null;
                proof=null;fullWalk=false;tryHeuristic=true;preWinWalkDebt=0;
                try
                {
                    planner=CreatePlanner(request);
                    if(request.SeedDir!=null || request.Seed!=null)planner.LoadSeeds(request.SeedDir,request.Attempt,request.Seed);
                    if(previous!=null && sameRoot)planner.KeepBestFromSameRoot(previous);
                    if(previous!=null && !sameRoot && request.Follow && !previous.InSharedTree && !planner.InSharedTree && !previous.Recovering)
                    {
                        planner.InheritCompatibleWork(previous);
                        previous=null;
                    }
                }
                finally {previous?.Dispose();}
                if(request.Suffix!=null || request.Follow && (planner.MemoryRoot.BestTerminal!=null || request.Position?.Anchor?.BestTerminal!=null))
                    checkSuffix=CheckCached(request).GetEnumerator();
                warmup=planner.DefensiveOpening().GetEnumerator();
            }
            var p=planner!;
            // Recovery rebases the tree across slices. Searches that retain a
            // node between slices must start after the new root is settled.
            if(p.Recovering && !p.ResumeRecovery(Stopwatch.StartNew(),TimeSpan.FromMilliseconds(5)))return;
            p.AdvanceBridge();p.AdvanceJoin();
            p.VerifyPendingWin(TimeSpan.FromMilliseconds(2));
            if(!request.Follow && request.Exchange?.ConfirmedOpening is {} promoted)Consider(promoted);
            var target=line?.Ending is {Victory:true,Verified:true} win?win:null;
            bool resourceDone=target!=null && (p.Proves(target) || p.MeetsOpeningBound(target));
            bool goals=GoalResult.KindAt(request.Root)!=CombatGoal.None;
            if(goals)resourceDone=false;
            p.Simplifying=resourceDone;
            p.Target=resourceDone || goals?null:target;
            bool finalize=p.AllTerminals;
            bool full=target==null || goals || finalize;
            bool needsWalk=!resourceDone || finalize;
            if(!needsWalk)
            {
                proof?.Dispose();proof=null;
            }
            else if(proof==null || !proof.AtCurrentRoot || full!=fullWalk || finalize!=proof.ComparingKnownRoutes)
            {
                proof?.Dispose();fullWalk=full;proof=new(p,request.Root,full,finalize);
            }
            // Both selectors grow the same graph. Prefer the current unexplored
            // branch while it rules out alternatives. Once certified, this root
            // runs only ordinary route search until every legal ending is known.
            bool seekingWin=target==null && !finalize && !p.Finished;
            long beforeSlice=SimulationMeter.ThreadActions;
            bool useWalk=(!seekingWin || preWinWalkDebt==0) && proof is {Finished:false} && (!tryHeuristic || finalize) && !p.Recovering && checkSuffix==null && warmup==null;
            if(useWalk)
            {
                if(!full)LossWalkSlices++;
                double before=target==null?0:p.Coverage(target);
                proof!.Advance(TimeSpan.FromMilliseconds(5));
                tryHeuristic=full || target==null || p.Coverage(target)<=before;
            }
            else tryHeuristic=false;
            bool finished=ProofMatchesLine();
            if(!finished && !useWalk)
            {
                if(checkSuffix!=null)
                {
                    long start=Stopwatch.GetTimestamp();
                    do
                    {
                        if(checkSuffix.MoveNext())continue;
                        checkSuffix.Dispose();checkSuffix=null;suffixFailed=!suffixVerified;lastSnapshot=0;break;
                    }while(Stopwatch.GetElapsedTime(start).TotalMilliseconds<4);
                }
                else if(warmup!=null)
                {
                    long start=Stopwatch.GetTimestamp();
                    do
                    {
                        if(warmup.MoveNext())continue;
                        warmup.Dispose();warmup=null;break;
                    }while(Stopwatch.GetElapsedTime(start).TotalMilliseconds<4);
                }
                else
                {
                    if(++slices%3==0 && (!potionPhase || reference!=null))
                    {
                        try{ReferenceSlice(request);}
                        catch(Exception e) when(e is IndexOutOfRangeException or ArgumentOutOfRangeException)
                        {
                            // Discard only the optional reference job for this root.
                            Godot.GD.PrintErr("[CoopSolver] Reference candidate rejected: "+e);
                            reference?.Dispose();reference=null;potionPhase=true;
                        }
                    }
                    else if(resourceDone || !p.AdvancePrefix(TimeSpan.FromMilliseconds(5)))
                    {
                        long before=SimulationMeter.ThreadActions;
                        p.Advance(TimeSpan.FromMilliseconds(5));
                        p.ChargeUnrestricted(SimulationMeter.ThreadActions-before);
                    }
                }
            }
            long spent=SimulationMeter.ThreadActions-beforeSlice;
            if(!seekingWin || spent==0)preWinWalkDebt=0;
            else if(useWalk)preWinWalkDebt=4*spent;
            else preWinWalkDebt=Math.Max(0,preWinWalkDebt-spent);
            if(checkSuffix==null && p.Best is {} best && !ReferenceEquals(best,lastResult))
            {
                lastResult=best;
                var end=best.Record!;
                Consider(new(best.Script!.ToArray(),p.BestTrail,end,p.BestTurnNumber,end.PostCombatHp.ToArray(),
                    end.RemainingEnemies,end.RemainingEnemyHp,p.BestBottlesLeft,p.BestRisks,0,DisplayVisits(request),p.Nodes,p.BestFirstHp,p.BestRecipes,Kills:p.BestKills,Rounds:p.BestRoundHp));
            }
            finished=finished && ProofMatchesLine();
            if(finished || Environment.TickCount64>=lastSnapshot || Latest?.Result!=line || Latest?.Generation!=request.Generation)
            {
                lastSnapshot=Environment.TickCount64+220;
                passHp??=p.PassHp;defendedHp??=p.DefendedHp;
                bool optimal=(!goals || finished) && line is {Ending:{Verified:true,Victory:true},Risks.Length:0} result &&
                    (finished || p.Proves(result.Ending!) || p.MeetsOpeningBound(result.Ending!));
                if(optimal)p.RecordSolution(line!);
                if(optimal && !request.Follow)request.Exchange?.CertifyOpening(line!.Ending!);
                var confirmed=line?.Ending is {Victory:true,Verified:true} ending?ending:null;
                double? coverage=confirmed!=null?p.Coverage(confirmed):null;
                Volatile.Write(ref output,new Output(request.Generation,line,new(p.Nodes,p.SettledPlans,p.ReusedNodes,p.ReusedRoundRows,p.Recovering,0,passHp,defendedHp,p.LoadedSeeds,p.LoadedHumanSteps,p.JoinedContinuations,p.LiveInputs,p.LivePlans,p.LiveBranches,p.OpeningContinuations,p.SharedDepth,p.SharedBackups,request.Meter.Inherited){ProvenTurnLoss=p.HeadComplete?p.MinimumHeadLoss:null,VerifiedJoins=p.JoinStats},null,suffixVerified,suffixFailed,Exhausted:finished){Options=p.PotionRoutes.Rows,MinimumLossProven=optimal,ProofCoverage=coverage,ProofBlocked=!optimal && proof?.Blocked==true,OrderConfirmed=orderConfirmed});
                if(finished)
                {
                    Volatile.Write(ref exhausted,request);
                    if(ReferenceEquals(request,Volatile.Read(ref wanted)))Dispatcher.Split(false);
                    stoppedTree=p.MemoryRoot;ReleaseWorkers();
                    return;
                }

            }
        }
        catch(Exception ex)
        {
            Godot.GD.PrintErr("[CoopSolver] Search worker: "+ex);
            TearDown();
            if(request!=null && ReferenceEquals(request,Volatile.Read(ref wanted)))
            {
                Volatile.Write(ref exhausted,request);
                Volatile.Write(ref output,new(request.Generation,null,new(0,0,0,0,false,0),ex.GetBaseException().Message));
            }
        }
        finally
        {
            Interlocked.Exchange(ref scheduled,0);
            if(Volatile.Read(ref wanted)==null?owned!=null:!paused)Schedule();
        }
    }
    private AnytimeSearch CreatePlanner(Request request)
    {
        var next=new AnytimeSearch(request.Root,roundSeed:171){Evaluation=request.Evaluation,FollowEnabled=request.Follow,TrackEvidence=true,HelperMeter=request.Meter,HelperCharge=t=>Dispatcher.Charge(request.Follow,t),HelpersAllowed=()=>!paused && !request.Memory.Waiting() && Volatile.Read(ref wanted)==request && !ReferenceEquals(request,Volatile.Read(ref exhausted))};
        next.Connect(request.Exchange,request.Position,request.Follow);next.TrackTree(request.Memory.Trees);
        if(request.Follow && next.InSharedTree)request.Meter.InheritGraphWork(next.FocusWork);return next;
    }
    private static long DisplayVisits(Request request)=>request.Meter.Actions+(request.Follow?0:request.Exchange?.ConnectedWork??0);
    private void Consider(Line candidate)
    {
        if(candidate.Ending is not {Verified:true})return;
        if(line!=null)
        {
            if(candidate.Ending==null && (line.Ending!=null || candidate.Score<=line.Score))return;
            if(candidate.Ending!=null && line.Ending!=null)
            {
                int rank=AnytimeSearch.ComparePlans(candidate.Ending,candidate.Steps.Length,line.Ending,line.Steps.Length,candidate.Rounds,line.Rounds);
                if(rank<=0)return;
            }
        }
        line=candidate;planner?.RememberJoinRoute(candidate);
    }
    private void ReferenceSlice(Request request)
    {
        TournamentOptions Options(bool potions,TournamentResult? seed)=>new(Policy:RolloutPolicyKind.Defend,EnableJointSearch:true,
            IncludePotions:potions,ContinuationActors:request.Root.Party,MaxDegreeOfParallelism:1){ReferenceResult=seed};
        reference??=new TournamentRun(request.Root,request.Root.Party,request.Actors,Options(false,null),40000);
        bool done=reference.Advance(TimeSpan.FromMilliseconds(4));
        var candidate=done?reference.Result:reference.BestCompleted;
        if(candidate?.Script!=null && !ReferenceEquals(candidate.Script,lastSeed))
        {lastSeed=candidate.Script;planner!.OfferReference(candidate);}
        if(!done)return;
        reference.Dispose();reference=null;
        if(!potionPhase)
        {potionPhase=true;reference=new TournamentRun(request.Root,request.Root.Party,request.Actors,Options(true,candidate),80000);}
    }
    private IEnumerable<bool> CheckCached(Request request)
    {
        void Accept(Line verified,KernelSession state)
        {
            suffixVerified=true;
            Consider(verified with {Score=planner!.Estimate(state)});
            planner!.ReuseVerifiedLine(verified.Steps,verified.Recipes,state);
        }
        if(request.Suffix!=null)
            foreach(var tick in ReplayKnown(request.Root,request.Suffix,null,Accept,checkedOrder:()=>{
                orderConfirmed=true;lastSnapshot=0;
            }))yield return tick;
        if(request.Follow)
        {
            foreach(var node in new[]{planner!.MemoryRoot,request.Position?.Anchor,request.Position?.FromPreviousLive}.Distinct())
                if(node?.BestTerminal is {} leaf && AnytimeSearch.ToTerminal(node,leaf) is {Length:>0} steps)
                    foreach(var tick in ReplayKnown(request.Root,null,null,Accept,steps))yield return tick;
        }
    }

    private static bool RebindPlayedCopy(KernelSession state,PlayedCopy played,ref KernelTeamSearch.Action step,ref TreeStep? recipe)
    {
        if(step.Card==null)return false;
        var hand=state.Hand(step.Player).ToArray();
        string key=step.CardStateKey;
        if(string.IsNullOrEmpty(key))key=state.CardStateKey(step.Card);
        int slot=Array.FindIndex(hand,card=>ReferenceEquals(card,played.Physical) && state.CardStateKey(card)==key);
        var equivalent=Enumerable.Range(0,hand.Length).Where(i=>state.CardStateKey(hand[i])==key).ToArray();
        if(slot<0 && played.Selector is {} selector && selector.Key==key && (uint)selector.Occurrence<(uint)equivalent.Length)
            slot=equivalent[selector.Occurrence];
        if(slot<0)return false;
        int original=recipe?.Move.Kind==MoveKind.Card?ActionFrontier.RebindEquivalent(state,recipe,step.CardStateOccurrence).Move.Slot:
            (uint)step.CardStateOccurrence<(uint)equivalent.Length?equivalent[step.CardStateOccurrence]:-1;
        if(slot==original)return false;
        step=step with {Card=hand[slot],CardStateKey=key,CardStateOccurrence=Array.IndexOf(equivalent,slot)};
        if(recipe?.Move.Kind==MoveKind.Card)recipe=recipe with {Move=recipe.Move with {Slot=slot}};
        return true;
    }

    internal static IEnumerable<bool> ReplayKnown(KernelSession root,Line? suffix,SeedRoute? seed,Action<Line,KernelSession> accept,IReadOnlyList<AnytimeSearch.Edge>? edges=null,Action? checkedOrder=null)
    {
        bool cardsRebound=false;
        if(suffix?.OrderProof is {} proof)
        {
            var simulated=proof.Origin;
            bool comparable=true;
            for(int i=0;i<proof.Actions.Length;i++)
            {
                var action=proof.Actions[i];var recipe=proof.Recipes[i];
                if(proof.PlayedCards?.ElementAtOrDefault(i) is {} played)
                    cardsRebound|=RebindPlayedCopy(simulated,played,ref action,ref recipe);
                if(!RouteDependencies.ReplayAction(simulated,action,recipe,out var after,out _))
                {comparable=false;break;}
                simulated=after;yield return false;
            }
            if(comparable)
            {
                var controlled=root.Party.Select(p=>p.NetId).ToArray();
                var expectedPrefix=simulated.PlanningState(controlled);var observed=root.PlanningState(controlled);
                if(expectedPrefix.Reusable && observed.Reusable)
                {
                    if(!expectedPrefix.Matches(observed))
                    {
                        var existing=proof.Origin.PlanningState(controlled).CardIdentities();
                        if(!expectedPrefix.Matches(observed,existing))yield break;
                        cardsRebound=true;
                    }
                    // An equivalent copy can leave a different hand order.
                    // In that case replay the suffix without inheriting the graph.
                    if(!cardsRebound)checkedOrder?.Invoke();
                    yield return false;
                }
            }
            // When a full-state comparison is unavailable, the retained
            // suffix may still be checked from the observed board to its end.
        }
        var state=root.ForkForReplay();var deaths=new RouteDeaths(state);var actual=new List<KernelTeamSearch.Action>();
        var rebound=new List<TreeStep?>();var states=new List<KernelPlanningState>();var risks=new HashSet<string>();
        var seats=state.Party.Select(p=>p.NetId).ToArray();
        bool joined=false;int[]? firstHp=null;
        int length=suffix?.Steps.Length??edges?.Count??seed!.Steps.Length;
        for(int index=0;index<length;index++)
        {
            if(state.HasTerminal)break;
            var child=state.ForkForReplay();
            var edge=edges?[index];
            var saved=seed?.Steps[index]??edge?.Saved;
            TreeStep? recipe=suffix?.Recipes.ElementAtOrDefault(index)??edge?.Exact??saved?.Recipe;
            var step=suffix?.Steps[index]??edge?.Prior??(recipe==null?saved?.Bind(child):null);
            if(recipe!=null)recipe=ActionFrontier.RebindEquivalent(child,recipe,step?.CardStateOccurrence??saved?.Occurrence??0);
            var input=step;
            if(recipe==null && step?.Card!=null && !string.IsNullOrEmpty(step.CardStateKey) &&
                child.FindSimulatedCardByKey(step.Player,step.CardStateKey,step.CardStateOccurrence)==null)
                input=step with {CardStateOccurrence=0};
            if(recipe==null && input?.Card!=null && !string.IsNullOrEmpty(input.CardStateKey) &&
                child.FindSimulatedCardByKey(input.Player,input.CardStateKey,input.CardStateOccurrence)==null)
                break;
            if(!ActionFrontier.ReplayRecorded(state,ref child,input,ref recipe,out var action,out _))break;
            state=child;deaths.Observe(state,actual.Count,action);actual.Add(action!);rebound.Add(recipe);
            if(firstHp==null && (state.HasTerminal || state.RoundsAdvanced!=root.RoundsAdvanced))firstHp=state.Party.Select(p=>state.Hp(p.Creature)).ToArray();
            ReplayCoverage.Collect(state,risks);states.Add(state.PlanningState(seats));
            if(suffix!=null && actual.Count==suffix.BridgeAfter && suffix.JoinProof!=null)
            {
                joined=states[^1].Matches(suffix.JoinProof);
                if(suffix.RequireJoin && !joined)yield break;
            }
            yield return false;
        }
        var hp=state.Party.Select(p=>state.Hp(p.Creature)).ToArray();var enemies=state.RemainingOpposition();
        int bottles=state.Party.Sum(state.HeldPotionCount);
        if(actual.Count==0 || !state.HasTerminal || suffix?.RequireJoin==true && !joined)yield break;
        var record=DeathSaves.WithInventory(TerminalRecord.Capture(state,state.Party,true,""),root,state);
        if((suffix?.OrderProof!=null || suffix?.RequireSameEnding==true) && suffix.Ending is {} expected)
        {
            if(record is not {Verified:true} || record.Victory!=expected.Victory ||
                !record.PostCombatHp.SequenceEqual(expected.PostCombatHp) ||
                record.RemainingEnemyHp!=expected.RemainingEnemyHp || record.RemainingEnemies!=expected.RemainingEnemies)
                yield break;
            if(cardsRebound && (actual.Count!=length || state.TurnNumber!=suffix.EndTurn ||
                bottles!=suffix.Bottles || record.OutstandingStolenResource!=expected.OutstandingStolenResource))
                yield break;
            if(suffix.RequireSameEnding && (state.TurnNumber!=suffix.EndTurn ||
                record.ResourcesConsumed!=expected.ResourcesConsumed ||
                record.ConsumablePreservationPremium!=expected.ConsumablePreservationPremium ||
                record.OutstandingStolenResource!=expected.OutstandingStolenResource ||
                GoalResult.For(record)!=GoalResult.For(expected)))yield break;
        }
        accept(new(actual.ToArray(),states.ToArray(),record,state.TurnNumber,hp,enemies.Count,enemies.Hp,bottles,ReplayCoverage.RouteDetails(risks.ToArray(),actual),
            suffix?.Score??0,suffix?.FoundAt??-1,suffix?.FoundNodes??0,firstHp,rebound.ToArray(),
            Kills:deaths.Freeze(),Rounds:deaths.Rounds()),state);
    }
}
