using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CoopBots.Kernel;

namespace CoopSolver;

// Optional counterfactual when the player opens a chooser with a different
// card than the current recommendation. Never sends a native choice.
internal sealed class HandChoiceProbe
{
    internal sealed record Answer(KernelChoice[] Choices,InteractiveSearch.Line? Route=null);
    internal static KernelChoice[] FirstChoices(InteractiveSearch.Line? line)=>Choices(line?.Steps.FirstOrDefault());
    internal static KernelChoice[] Choices(KernelTeamSearch.Action? action)=>
        (action?.Choices??Array.Empty<KernelChoice>()).Concat(action?.TurnStartChoices?.Choices??Array.Empty<KernelChoice>()).ToArray();
    private volatile bool cancelled,paused;
    internal void SetPaused(bool value){paused=value;if(!value)Pump();}
    private Answer? answer;
    internal PotionRoute<InteractiveSearch.Line>[] Options=>Volatile.Read(ref options);
    private PotionRoute<InteractiveSearch.Line>[] options=Array.Empty<PotionRoute<InteractiveSearch.Line>>();
    internal Answer? Latest=>Volatile.Read(ref answer);
    private readonly SearchMemoryBudget memory;
    private readonly Action slice;
    private int scheduled;
    private volatile bool finished;
    internal void Cancel(){cancelled=true;Pump();}
    internal void Pump()
    {
        if(finished || !cancelled && (paused || memory.Waiting(active:true)) || Interlocked.CompareExchange(ref scheduled,1,0)!=0)return;
        InteractiveSearch.Dispatcher.Post(slice,true);
    }
    internal HandChoiceProbe(KernelSession root,Move move,SimulationMeter meter,SearchMemoryBudget memory,BattleExchange? battle=null,BattleExchange.Position? before=null,string? directory=null,SeedRoute? incumbent=null,FightEvaluation? evaluation=null)
    {
        this.memory=memory;
        var scan=TryChoices(root,move,meter,battle,before,directory,incumbent,evaluation).GetEnumerator();
        slice=()=>{
            try
            {
                using var count=meter.Enter();
                long start=Stopwatch.GetTimestamp();
                while(!cancelled && !paused && !memory.Waiting(active:true))
                {
                    if(!scan.MoveNext()){finished=true;break;}
                    if(Stopwatch.GetElapsedTime(start).TotalMilliseconds>=3)break;
                }
            }
            catch(Exception ex){finished=true;if(Environment.GetEnvironmentVariable("STS2_TRANSFIGURE")!=null)Console.WriteLine("[ChoiceCheck] "+ex);}
            finally
            {
                if(cancelled || finished){finished=true;scan.Dispose();}
                Interlocked.Exchange(ref scheduled,0);Pump();
            }
        };
        Pump();
    }
    private static bool Matches(SavedStep step,Move move)=>move.Kind switch
    {
        MoveKind.Card=>step.Slot<0 && step.Key==move.Key,
        MoveKind.Potion=>step.Slot==move.Slot && step.Id==move.Key,
        _=>step.Id=="end"
    };
    private IEnumerable<bool> TryChoices(KernelSession root,Move move,SimulationMeter meter,BattleExchange? battle,BattleExchange.Position? before,string? directory,SeedRoute? incumbent,FightEvaluation? evaluation)
    {
        using var search=new AnytimeSearch(root){Evaluation=evaluation??new(),RequiredFirst=move,HelperMeter=meter};search.TrackTree(memory.Trees);
        var family=new ActionFrontier(root,move);
        object? published=null;
        void Publish()
        {
            InteractiveSearch.Line? line=null;
            var found=search.PotionRoutes.Rows;
            if(!ReferenceEquals(found,Options))
            {
                var prior=Options;
                if(battle!=null)foreach(var row in found)
                {
                    if(prior.Any(p=>ReferenceEquals(p.Value,row.Value)))continue;
                    var alternative=SeedRoute.FromLine(root,row.Value);var head=alternative.Steps[0];
                    var next=root.ForkForReplay();
                    if(head.Recipe is {} recipe?ActionFrontier.Replay(next,recipe,out _,out _):
                        head.Bind(next) is {} input && next.ReplayPlanned(input,int.MaxValue,out _))
                    {
                        battle.RememberMenu(SlCache.Witness(next),row.Value,battle.WithPrefix(before,row.Cost));
                        if(before!=null)battle.OfferAlternative(before,alternative);
                    }
                }
                Volatile.Write(ref options,found);
            }
            if(search.Best is {Record:{} end,Script:{} actions} win && !ReferenceEquals(published,win))
            {
                published=win;
                line=new(actions.ToArray(),search.BestTrail,end,search.BestTurnNumber,end.PostCombatHp.ToArray(),
                    end.RemainingEnemies,end.RemainingEnemyHp,search.BestBottlesLeft,search.BestRisks,0,meter.Actions,search.Nodes,
                    search.BestFirstHp,search.BestRecipes,Kills:search.BestKills,Rounds:search.BestRoundHp);
            }
            if(line!=null && FirstChoices(line) is {Length:>0} menu)
            {
                // The forced move is the FIRST action of this seed (with its
                // actual menu answer). Attach to the position BEFORE playing it.
                if(line.Ending is {Victory:true,Verified:true} && battle!=null)
                {
                    var seed=SeedRoute.FromLine(root,line);
                    var after=root.ForkForReplay();var first=seed.Steps[0];
                    bool matches=first.Seat==root.Party[move.Seat].NetId && first.Target==move.Target &&
                        Matches(first,move);
                    if(matches && (first.Recipe is {} recipe?ActionFrontier.Replay(after,recipe,out _,out _):
                        first.Bind(after) is {} input && after.ReplayPlanned(input,int.MaxValue,out _)))
                    {
                        battle.RememberMenu(SlCache.Witness(after),line);
                        if(before!=null)battle.PublishLive(before,seed);
                    }
                }
                Volatile.Write(ref answer,new Answer(menu.ToArray(),line));
            }
        }

        IEnumerable<SeedRoute> Retained()
        {
            if(incumbent?.Won==true)yield return incumbent;
            if(before==null || battle==null)yield break;
            int seen=0;
            foreach(var entry in battle.NewContinuations(before,ref seen))
                if(entry.Route.Won && !(entry.Used==0 && ReferenceEquals(entry.Route,incumbent)))
                    yield return entry.Route with {Steps=entry.Route.Steps[entry.Used..]};
        }
        foreach(var retained in Retained())
        {
            if(retained.Steps.FirstOrDefault() is not {} input || input.Seat!=root.Party[move.Seat].NetId ||
                input.Target!=move.Target || !Matches(input,move))continue;
            var candidate=retained;
            if(move.Kind==MoveKind.Card)
            {
                int copy=root.Hand(root.Party[move.Seat]).Take(move.Slot).Count(c=>root.CardStateKey(c)==move.Key);
                if(input.Occurrence!=copy || input.Recipe!=null && input.Recipe.Move.Slot!=move.Slot)
                {
                    var steps=(SavedStep[])retained.Steps.Clone();
                    steps[0]=input with {Occurrence=copy,Recipe=input.Recipe is {} recipe?recipe with {Move=move}:null};
                    candidate=retained with {Steps=steps};
                }
            }
            foreach(var tick in search.ReplaySeed(candidate,root,search.MemoryRoot))yield return tick;
            while(search.HasPendingWin){search.VerifyPendingWin(TimeSpan.FromMilliseconds(2));Publish();yield return true;}
        }
        int initialMenus=0;
        while(!family.Finished && initialMenus++<12)
        {
            var branch=root.ForkForReplay();
            if(family.Expand(branch,out var firstStep,out var actual,out _) && Choices(actual).Length>0)
            {
                if(directory!=null)
                    foreach(var tick in search.RestoreChoice(directory,firstStep!,branch))yield return tick;

            }
            yield return false;
        }
        while(!cancelled && !search.Finished)
        {
            search.Advance(TimeSpan.FromMilliseconds(3));Publish();
            yield return false;
        }
    }
}
