using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Players;

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
                        bool takeLive=live.Count!=0 && (opening.Count==0 || balance>0);
                        follow=takeLive;(takeLive?live:opening).TryDequeue(out work);
                    }
                }
                if(work==null){wake.WaitOne();continue;}
                long begin=Stopwatch.GetTimestamp();
                work();
                if(follow.HasValue)Charge(follow.Value,Stopwatch.GetTimestamp()-begin);
                // Charge overruns to this worker instead of the game's frame.
                Thread.Sleep(Math.Clamp((int)Stopwatch.GetElapsedTime(begin).TotalMilliseconds,5,60));
            }
        }
    }

    internal sealed record Line(KernelTeamSearch.Action[] Steps,KernelPlanningState[] Trail,TerminalRecord? Ending,
        int EndTurn,int[] Hp,int EnemyCount,int EnemyHp,int Bottles,string[] Risks,double Score,long FoundAt,long FoundNodes,int[]? FirstHp,TreeStep?[] Recipes,int BridgeAfter=0,KernelPlanningState? JoinProof=null,RouteKill[]? Kills=null,HudRoundHp[]? Rounds=null);
    internal sealed record Stats(int Nodes,int Evaluations,int ReusedNodes,int ReusedJobs,bool Recovering,int Rotations,int[]? PassHp=null,int[]? DefendedHp=null,int LoadedSeeds=0,int HumanSteps=0,int Joined=0,int LiveInputs=0,int LivePlans=0,int LiveBranches=0,int OpeningContinuations=0);
    internal sealed record Output(int Generation,Line? Result,Stats Metrics,string? Error,bool SuffixVerified=false,bool SuffixFailed=false,Line? Guard=null);
    private sealed record Request(int Generation,KernelSession Root,Player[] Actors,bool Follow,SimulationMeter Meter,Line? Suffix,string? SeedDir,string Attempt,SeedRoute? Seed,BattleExchange? Exchange,BattleExchange.Position? Position);
    private Request? wanted,owned;
    private Output? output;
    private int scheduled;
    private volatile bool paused;
    private AnytimeSearch? planner;
    private TournamentRun? reference;
    private IEnumerator<bool>? warmup,checkSuffix;
    private object? lastSeed,lastResult;
    private bool potionPhase,suffixVerified,suffixFailed;
    private int slices,rotations;
    private long lastSnapshot,heapBaseline;
    private Line? line,guard;
    private int[]? passHp,defendedHp;
    internal Output? Latest=>Volatile.Read(ref output);
    internal void Bind(int generation,KernelSession root,Player[] actors,bool follow,SimulationMeter meter,Line? suffix,string? seedDir,string attempt,SeedRoute? seed,BattleExchange? exchange,BattleExchange.Position? position)
    {
        meter.BeginRoot(root);
        Volatile.Write(ref wanted,new(generation,root,actors,follow,meter,suffix,seedDir,attempt,seed,exchange,position));
    }
    internal void Pump(bool run)
    {
        paused=!run;
        if(run)Schedule();
    }
    internal void Release(){Volatile.Write(ref wanted,null);paused=true;Schedule();}
    private void Schedule()
    {
        if(Interlocked.CompareExchange(ref scheduled,1,0)==0)Dispatcher.Post(Work,Volatile.Read(ref wanted)?.Follow);
    }
    private void TearDown()
    {
        reference?.Dispose();reference=null;warmup?.Dispose();warmup=null;checkSuffix?.Dispose();checkSuffix=null;
        planner?.Dispose();planner=null;owned=null;line=guard=null;lastResult=lastSeed=null;
    }
    private void Work()
    {
        var request=Volatile.Read(ref wanted);
        try
        {
            if(request==null){TearDown();return;}
            if(paused)return;
            using var count=request.Meter.Enter();
            if(request!=owned)
            {
                var previous=planner;planner=null;
                TearDown();
                owned=request;passHp=defendedHp=null;rotations=0;slices=0;potionPhase=false;suffixVerified=suffixFailed=false;heapBaseline=GC.GetGCMemoryInfo().HeapSizeBytes;
                planner=CreatePlanner(request);
                if(request.SeedDir!=null)planner.LoadSeeds(request.SeedDir,request.Attempt,request.Seed);
                if(previous!=null && request.Follow && !previous.Recovering)planner.InheritCompatibleWork(previous);
                else previous?.Dispose();
                if(request.Suffix!=null)checkSuffix=CheckSuffix(request,request.Suffix).GetEnumerator();
                warmup=planner.DefensiveOpening().GetEnumerator();
            }
            var p=planner!;
            p.AdvanceBridge();
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
                p.AdvancePreview();
            }
            else
            {
                if(++slices%3==0 && (!potionPhase || reference!=null))
                    ReferenceSlice(request);
                else p.Advance(TimeSpan.FromMilliseconds(5));
                p.AdvancePreview();
            }
            if(p.Best is {} best && !ReferenceEquals(best,lastResult))
            {
                lastResult=best;
                var end=best.Record!;
                Consider(new(best.Script!.ToArray(),p.BestTrail,end,p.BestTurnNumber,end.PostCombatHp.ToArray(),
                    end.RemainingEnemies,end.RemainingEnemyHp,p.BestBottlesLeft,p.BestRisks,0,request.Meter.Actions,p.Nodes,p.BestFirstHp,p.BestRecipes,Kills:p.BestKills,Rounds:p.BestRoundHp));
            }
            else if(p.Best==null && p.Preview is {} v && !ReferenceEquals(lastResult,v))
            {
                lastResult=v;
                var partial=new Line(v.Actions,v.Trail,null,v.Turn,v.Hp,v.Enemies,v.EnemyHp,v.Bottles,
                    v.Risks,v.Score,request.Meter.Actions,p.Nodes,v.FirstHp,v.Recipes,Kills:v.Kills,Rounds:v.Rounds);
                if(warmup!=null && p.DefendedHp!=null)guard??=partial;
                Consider(partial);
            }
            if(Environment.TickCount64>=lastSnapshot || Latest?.Result!=line || Latest?.Generation!=request.Generation)
            {
                lastSnapshot=Environment.TickCount64+220;
                passHp??=p.PassHp;defendedHp??=p.DefendedHp;
                Volatile.Write(ref output,new(request.Generation,line,new(p.Nodes,p.SettledPlans,p.ReusedNodes,p.ReusedRoundRows,p.Recovering,rotations,passHp,defendedHp,p.LoadedSeeds,p.LoadedHumanSteps,p.JoinedContinuations,p.LiveInputs,p.LivePlans,p.LiveBranches,p.OpeningContinuations),null,suffixVerified,suffixFailed,guard));
                var gc=GC.GetGCMemoryInfo();
                bool pressure=gc.HighMemoryLoadThresholdBytes>0 && gc.MemoryLoadBytes>=gc.HighMemoryLoadThresholdBytes &&
                    gc.HeapSizeBytes-heapBaseline>192L*1024*1024;
                if(pressure && gc.HeapSizeBytes-heapBaseline>768L*1024*1024)
                {
                    var snapshot=Latest!;
                    TearDown();paused=true;
                    Volatile.Write(ref output,snapshot with {Error="系统内存压力较高，已释放搜索缓存并保留路线。"});
                    return;
                }
                if(!p.Recovering && (p.Nodes>=24000 || p.RoundFileBytes>=48L*1024*1024 || pressure && p.Nodes>=4000))
                {
                    planner=CreatePlanner(request);planner.KeepBestFromSameRoot(p);p.Dispose();rotations++;
                }
            }
        }
        catch(Exception ex)
        {
            TearDown();
            if(request!=null)Volatile.Write(ref output,new(request.Generation,null,new(0,0,0,0,false,0),ex.GetBaseException().Message));
            paused=true;
        }
        finally
        {
            Interlocked.Exchange(ref scheduled,0);
            if(Volatile.Read(ref wanted)==null?owned!=null:!paused)Schedule();
        }
    }
    private AnytimeSearch CreatePlanner(Request request)
    {
        var next=new AnytimeSearch(request.Root,roundSeed:171+rotations){FollowEnabled=request.Follow,TrackPreview=true,HelperMeter=request.Meter,HelperCharge=t=>Dispatcher.Charge(request.Follow,t),HelpersAllowed=()=>!paused && Volatile.Read(ref wanted)==request};
        next.Connect(request.Exchange,request.Position,request.Follow);return next;
    }
    private void Consider(Line candidate)
    {
        if(line!=null && (candidate.Ending==null ? line.Ending!=null || candidate.Score<=line.Score :
            line.Ending!=null && AnytimeSearch.ComparePlans(candidate.Ending,candidate.Steps.Length,line.Ending,line.Steps.Length,candidate.Rounds,line.Rounds)<=0))return;
        line=candidate;
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
    private IEnumerable<bool> CheckSuffix(Request request,Line suffix)
    {
        var state=request.Root.ForkForReplay();var deaths=new RouteDeaths(state);var actual=new List<KernelTeamSearch.Action>();
        var rebound=new List<TreeStep?>();var states=new List<KernelPlanningState>();var risks=new HashSet<string>();
        var seats=state.Party.Select(p=>p.NetId).ToArray();
        bool stoppedEarly=false;bool joined=false;int[]? firstHp=null;
        foreach(var step in suffix.Steps)
        {
            if(state.HasTerminal)break;
            var child=state.ForkForReplay();
            ReplayCoverage.Begin(child,risks);
            bool ok;
            KernelTeamSearch.Action? action;
            string reason;
            TreeStep? recipe=actual.Count<suffix.Recipes.Length?suffix.Recipes[actual.Count]:null;
            try
            {
                if(recipe?.Move.Kind==MoveKind.Card && suffix.BridgeAfter>0)
                {
                    var hand=child.Hand(child.Party[recipe.Move.Seat]);
                    int slot=hand.ToList().FindIndex(c=>ReferenceEquals(c,step.Card) && child.CardStateKey(c)==recipe.Move.Key);
                    if(slot<0)slot=hand.Select((c,i)=>(c,i)).Where(v=>child.CardStateKey(v.c)==recipe.Move.Key)
                        .Skip(step.CardStateOccurrence).Select(v=>v.i).DefaultIfEmpty(-1).First();
                    if(slot>=0)recipe=recipe with {Move=recipe.Move with {Slot=slot}};
                }
                ok=recipe!=null ? ActionFrontier.Replay(child,recipe,out action,out reason)
                    : child.ReplayPlanned(step,int.MaxValue,out reason,out action);
            }
            finally{ReplayCoverage.End();}
            if(!ok)
            {
                if(Environment.GetEnvironmentVariable("STS2_UX_TEST") is {} trace)
                    System.IO.File.AppendAllText(trace+".sync.txt",$"suffix boundary at {actual.Count}: {reason}\n");
                stoppedEarly=true;break;
            }
            state=child;deaths.Observe(state,actual.Count,action);actual.Add(action!);rebound.Add(recipe);
            if(firstHp==null && (state.HasTerminal || state.RoundsAdvanced!=request.Root.RoundsAdvanced))firstHp=state.Party.Select(p=>state.Hp(p.Creature)).ToArray();
            ReplayCoverage.Collect(state,risks);states.Add(state.PlanningState(seats));
            if(actual.Count==suffix.BridgeAfter && suffix.JoinProof!=null)joined=states[^1].Matches(suffix.JoinProof);
            yield return false;
        }
        var hp=state.Party.Select(p=>state.Hp(p.Creature)).ToArray();var enemies=state.RemainingOpposition();
        int bottles=state.Party.Sum(state.HeldPotionCount);
        if(actual.Count==0)yield break;
        var record=state.HasTerminal?TerminalRecord.Capture(state,state.Party,true,"") with
            {ResourcesConsumed=request.Root.Party.Sum(request.Root.HeldPotionCount)-bottles}:null;
        suffixVerified=true;
        if(Environment.GetEnvironmentVariable("STS2_UX_TEST") is {} traceFile)
            System.IO.File.AppendAllText(traceFile+".sync.txt",$"replayed={actual.Count}/{suffix.Steps.Length} terminal={state.HasTerminal} cut={stoppedEarly} bridge={suffix.BridgeAfter} joined={joined}\n");
        Consider(suffix with {BridgeAfter=0,JoinProof=null,Kills=deaths.Freeze(),Rounds=deaths.Rounds(),Recipes=rebound.ToArray(),Steps=actual.ToArray(),Trail=states.ToArray(),Ending=record,Risks=risks.ToArray(),EndTurn=state.TurnNumber,
            Hp=hp,EnemyCount=enemies.Count,EnemyHp=enemies.Hp,Bottles=bottles,FirstHp=firstHp,Score=planner!.Estimate(state)});
        planner!.ReuseVerifiedLine(actual,rebound,state);
    }
}
