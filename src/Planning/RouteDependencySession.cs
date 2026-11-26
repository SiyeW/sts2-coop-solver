using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

// Build only the round requested by the view. The route is fixed for this
// session; a replacement PV or a new observed root starts a new session.
internal sealed class RouteDependencySession
{
    internal sealed record Round(int Number,int Start,int End,int? Transition);
    internal sealed record Entry(Round Round,RouteDependencies.Result Graph,int PrefixActions);
    private readonly KernelSession root;
    private readonly InteractiveSearch.Line route;
    private readonly Dictionary<int,Entry> analyzed=new();
    private KernelSession checkpoint;
    private int checkpointIndex;
    internal IReadOnlyList<Round> Rounds {get;}
    internal int ComputedRounds=>analyzed.Count;

    internal RouteDependencySession(KernelSession root,InteractiveSearch.Line route)
    {
        if(route.Ending is not {Verified:true})throw new ArgumentException("A verified route is required.",nameof(route));
        this.root=checkpoint=root;this.route=route;
        var rounds=new List<Round>();
        int turn=root.TurnNumber,start=0;
        for(int i=0;i<route.Steps.Length;i++)
        {
            if(route.Steps[i].TurnStartChoices is not {} next || next.FromRound==next.ToRound)continue;
            // The action that advances the side stays at the end of this round.
            // Dependency inference covers only the actions before it.
            rounds.Add(new(turn,start,i,i));
            turn=next.ToRound;start=i+1;
        }
        if(start<route.Steps.Length)rounds.Add(new(turn,start,route.Steps.Length,null));
        Rounds=rounds;
    }

    internal Entry Request(int number,int actionBudget)
    {
        Entry? value=null;
        foreach(var _ in RequestSteps(number,actionBudget,entry=>value=entry)){}
        return value!;
    }

    internal IEnumerable<bool> RequestSteps(int number,int actionBudget,Action<Entry> complete)
    {
        if(analyzed.TryGetValue(number,out var ready)){complete(ready with {PrefixActions=0});yield break;}
        var round=Rounds.First(r=>r.Number==number);
        var state=checkpointIndex<=round.Start?checkpoint:root;
        int start=checkpointIndex<=round.Start?checkpointIndex:0,spent=0;
        var actions=route.Steps[round.Start..round.End];
        var recipes=route.Recipes.Skip(round.Start).Take(actions.Length).ToArray();
        for(int i=start;i<round.Start;i++)
        {
            spent++;
            if(!RouteDependencies.ReplayAction(state,route.Steps[i],route.Recipes.ElementAtOrDefault(i),out var next,out var reason))
            {
                var graph=new RouteDependencies.Result(false,Array.Empty<RouteDependencies.Edge>(),0,0,"无法恢复该回合起点："+reason);
                complete(analyzed[number]=new(round,graph,spent));yield break;
            }
            state=next;yield return false;
        }
        checkpoint=state;checkpointIndex=round.Start;
        foreach(var tick in RouteDependencies.AnalyzeSteps(state,actions,recipes,actionBudget,result=>
            complete(analyzed[number]=WithTransition(round,result,spent))))yield return tick;
    }

    private Entry WithTransition(Round round,RouteDependencies.Result graph,int spent)
    {
        if(!graph.Verified || round.Transition is not int advance)return new(round,graph,spent);
        var lastPerSeat=new Dictionary<ulong,int>();
        for(int i=round.Start;i<round.End;i++)lastPerSeat[route.Steps[i].Player.NetId]=i;
        var before=lastPerSeat.Where(p=>p.Key!=route.Steps[advance].Player.NetId)
            .Select(p=>new RouteDependencies.Edge(p.Value-round.Start,advance-round.Start));
        return new(round,graph with {Dependencies=graph.Dependencies.Concat(before).ToArray()},spent);
    }
}
