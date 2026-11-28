using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed partial class AnytimeSearch
{
    internal Func<bool>? HelpersAllowed;
    internal Action<long>? HelperCharge;
    internal SimulationMeter? HelperMeter;
    private volatile bool retireHelpers;
    private volatile int helperGeneration;
    private readonly ConcurrentQueue<Action> helperResults=new();

    internal void TryExtraRollout(Node leaf,KernelSession branch,double[]? features=null)
    {
        var allowed=HelpersAllowed;var meter=HelperMeter;
        if(allowed==null || meter==null || SearchCapacity.Limit<=1)return;
        var policy=Policies[policies%Policies.Length];
        int generation=helperGeneration;
        void Deliver(Action callback)
        {
            lock(helperBoards)
                if(!retireHelpers && generation==helperGeneration)helperResults.Enqueue(callback);
        }
        if(SearchCapacity.TryRun(id=>
        {
            lock(helperBoards)helperBoards.Add(branch);
            long begin=SimulationMeter.ThreadActions;bool delivered=false;
            try
            {
                using var counting=meter.Enter();
                features??=ReadFeatures(branch);
                var trial=new RolloutRun(branch,party,party,new RolloutOptions(policy){InvestmentLookahead=false,SequenceLookahead=false});
                while(!trial.Done)
                {
                    if(retireHelpers || generation!=helperGeneration || id>=SearchCapacity.Limit-1 || !allowed())return;
                    long start=Stopwatch.GetTimestamp();
                    do {trial.Step();}while(!trial.Done && Stopwatch.GetElapsedTime(start).TotalMilliseconds<4);
                    HelperCharge?.Invoke(Stopwatch.GetTimestamp()-start);
                    Thread.Sleep(Math.Clamp((int)Stopwatch.GetElapsedTime(start).TotalMilliseconds,4,60));
                }
                var result=trial.Result!;
                var ending=RolloutEnding(trial);
                if(retireHelpers)return;
                long completedWork=SimulationMeter.ThreadActions-begin;
                Deliver(()=>
                {
                    if(result.Terminal)LearnFromRollout(leaf,policy,features,ending);
                    CreditWork(leaf,completedWork);
                    if(InSharedTree)
                    {
                        var tip=Descend(leaf,System.Linq.Enumerable.Select(result.Actions,a=>new Edge(null,a)));
                        AttributeCommittedMoves(leaf,tip,result.Actions.Count,completedWork);
                        Record(tip,result.Terminal?TreeReward(ending):Estimate(trial.State));
                    }
                    if(result.Terminal)Submit(new(Path(leaf),result.Actions,ending,"parallel-rollout"));
                    else TruncatedRollouts++;
                    CompletedRollouts++;
                });
                delivered=true;
            }
            catch(Exception e)
            {
                Deliver(()=>Boundary("并行试打："+e.GetBaseException().Message));
            }
            finally
            {
                lock(helperBoards)helperBoards.Remove(branch);
                treeMeter?.WorkerReleased();
                long work=SimulationMeter.ThreadActions-begin;
                if(work>0 && !delivered)Deliver(()=>CreditWork(leaf,work));
            }
        }))policies++;
    }
}
