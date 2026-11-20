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
    private readonly ConcurrentQueue<Action> helperResults=new();

    private void TryExtraRollout(Node leaf,KernelSession branch)
    {
        var allowed=HelpersAllowed;var meter=HelperMeter;
        if(allowed==null || meter==null || SearchCapacity.Limit<=1)return;
        var policy=Policies[policies%Policies.Length];
        if(SearchCapacity.TryRun(id=>
        {
            try
            {
                using var counting=meter.Enter();
                var trial=new RolloutRun(branch,party,party,new RolloutOptions(policy){InvestmentLookahead=false,SequenceLookahead=false});
                while(!trial.Done)
                {
                    if(retireHelpers || id>=SearchCapacity.Limit-1 || !allowed())return;
                    long start=Stopwatch.GetTimestamp();
                    do {trial.Step();}while(!trial.Done && Stopwatch.GetElapsedTime(start).TotalMilliseconds<4);
                    HelperCharge?.Invoke(Stopwatch.GetTimestamp()-start);
                    Thread.Sleep(Math.Clamp((int)Stopwatch.GetElapsedTime(start).TotalMilliseconds,4,60));
                }
                var result=trial.Result!;
                var ending=WithInventory(result.Record,trial.State);
                if(!retireHelpers)helperResults.Enqueue(()=>
                {
                    if(result.Terminal)Submit(new(Path(leaf),result.Actions,ending,"parallel-rollout"));
                    else TruncatedRollouts++;
                    CompletedRollouts++;
                });
            }
            catch(Exception e)
            {
                if(!retireHelpers)helperResults.Enqueue(()=>Boundary("并行试打："+e.GetBaseException().Message));
            }
        }))policies++;
    }
}
