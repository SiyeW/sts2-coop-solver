using System;
using System.Collections.Generic;
using System.Linq;

namespace CoopSolver;

// A potion that still has a pending choice keeps its original step identity.
// Only its visible seat and label change. This projection never revises proof edges.
internal sealed record HudRouteProjection(HudGraphAction[] Actions,HudRouteProjection.Prompt[] Prompts,
    ILookup<int,HudRouteProjection.Source> Waits)
{
    internal readonly record struct Prompt(int? Step,int Seat,string Text);
    internal readonly record struct Source(int Step,int Seat,string Caption);

    internal static HudRouteProjection Create(HudGraph graph,HudPlan? plan,HudExecuted? progress,
        int readyRound,IReadOnlyCollection<ulong> ready,HudPotionMenu? menu)
    {
        var recorded=graph.Actions.Where(action=>(uint)action.Seat<(uint)graph.Party.Length)
            .DistinctBy(action=>action.Step).OrderBy(action=>action.Step).ToArray();
        var transferred=menu?.Round==graph.Round?menu.StartedSteps(recorded.Select(action=>(action.Step,action.Move)),
            graph.Party,progress):null;
        var actions=recorded.Where(action=>transferred?.ContainsKey(action.Step)!=true &&
            progress?.Includes(graph.Round,action.Step)!=true &&
            !(readyRound==graph.Round && action.EndTurn && ready.Contains(graph.Party[action.Seat].Id))).ToArray();
        var destinations=actions.ToDictionary(action=>action.Step);
        var sources=new Dictionary<int,Source>();
        var prompts=new List<Prompt>();
        if(menu?.Round==graph.Round)
        {
            var byPrompt=transferred!.ToDictionary(entry=>entry.Value,entry=>entry.Key);
            for(int i=0;i<menu.Prompts.Length;i++)
            {
                var hint=menu.Prompts[i];
                int seat=Array.FindIndex(graph.Party,member=>member.Id==hint.Chooser);
                if(seat<0)continue;
                int? step=byPrompt.TryGetValue(i,out int bound)?bound:null;
                var text=hint.Text();
                if(text==null && step is int dependency &&
                    graph.Dependencies.Any(edge=>edge.Before==dependency && destinations.ContainsKey(edge.After)))
                    text=Ui.Text("route.choice");
                if(text==null)continue;
                prompts.Add(new(step,seat,text));
                if(step.HasValue)sources.Add(step.Value,new(step.Value,seat,text));
            }
        }
        var waits=new List<(int Destination,Source Source)>();
        foreach(var edge in graph.Dependencies.Distinct())
        {
            if(edge.Before>=edge.After || !destinations.TryGetValue(edge.After,out var target))continue;
            if(!sources.TryGetValue(edge.Before,out var source))
            {
                if(!destinations.TryGetValue(edge.Before,out var before) || before.Seat==target.Seat)continue;
                sources.Add(edge.Before,source=new(before.Step,before.Seat,before.Caption(plan)));
            }
            if(source.Seat!=target.Seat)waits.Add((edge.After,source));
        }
        return new(actions,prompts.ToArray(),waits.ToLookup(pair=>pair.Destination,pair=>pair.Source));
    }

    internal static string[] WaitFields(IEnumerable<Source> sources)=>
        new[]{Ui.Text("route.wait_for",string.Join(Ui.Text("ui.044"),
            sources.Select(source=>$"P{source.Seat+1} {source.Caption}")))};
}
