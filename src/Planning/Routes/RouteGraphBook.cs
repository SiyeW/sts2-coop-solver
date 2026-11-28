using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed class RouteGraphBook
{
    internal sealed record VerifiedRound(HudGraph View,RouteDependencySession.Entry Entry);
    internal sealed record RoundFailure(string Reason,bool OriginalOrderVerified=false);
    private sealed class Source(HudPlan plan,KernelSession root,InteractiveSearch.Line line)
    {
        internal readonly HudPlan Plan=plan;
        internal readonly KernelSession Root=root;
        internal readonly InteractiveSearch.Line Line=line;
        internal readonly RouteDependencySession Session=new(root,line);
        internal readonly ConcurrentDictionary<int,VerifiedRound?> Results=new();
        internal readonly ConcurrentDictionary<int,RoundFailure> Failures=new();
    }
    private sealed class Work(Source source,int round)
    {
        internal readonly Source Source=source;
        internal readonly int Round=round;
        internal volatile bool Cancelled,Finished;
        internal IEnumerator<bool>? Steps;
    }

    private ConditionalWeakTable<HudPlan,Source> sources=new();
    private ConditionalWeakTable<HudPlan,HudGraph[]> restoredGraphs=new();

    // Rebuild cached action readings that omitted teammates' triggered damage.
    private const int VerificationVersion=12;
    private static bool CanRestore(HudGraph graph)=>graph.VerificationVersion==VerificationVersion && !graph.Conservative;
    internal static int VerifiedGraphCount(HudGraph[]? graphs)=>graphs?.Count(CanRestore)??0;

    internal void Restore(HudPlan plan,HudGraph[]? graphs)
    {
        if(graphs is not {Length:>0})return;
        var verified=graphs.Where(graph=>CanRestore(graph) && graph.Round>=plan.Start && graph.Round<=plan.End &&
                graph.Party.Select(seat=>seat.Id).SequenceEqual(plan.Party.Select(seat=>seat.Id)) &&
                graph.Actions.Select(action=>action.Step).SequenceEqual(Enumerable.Range(0,graph.Actions.Length)) &&
                graph.Actions.All(action=>(uint)action.Seat<(uint)plan.Party.Length) &&
                graph.Dependencies.All(edge=>edge.Before>=0 && edge.Before<edge.After && edge.After<graph.Actions.Length))
            .GroupBy(graph=>graph.Round).Where(group=>group.Count()==1).Select(group=>group.Single()).ToArray();
        if(verified.Length>0)restoredGraphs.GetValue(plan,_=>verified);
    }

    internal HudGraph[] Snapshot(HudPlan plan)
    {
        restoredGraphs.TryGetValue(plan,out var cached);
        if(!sources.TryGetValue(plan,out var source))return cached??Array.Empty<HudGraph>();
        return source.Results.Values.OfType<VerifiedRound>().Select(round=>round.View)
            .Concat((cached??Array.Empty<HudGraph>()).Where(graph=>!source.Results.ContainsKey(graph.Round)))
            .OrderBy(graph=>graph.Round).ToArray();
    }
    private readonly Dictionary<(ulong Viewer,int Slot),Work> requests=new();
    private long revision;
    internal long Revision=>Interlocked.Read(ref revision);

    internal void Track(HudPlan plan,KernelSession root,InteractiveSearch.Line line)
    {
        if(line.Ending is {Verified:true})sources.GetValue(plan,_=>new(plan,root,line));
    }
    internal bool ReuseRound(HudPlan plan,KernelSession root,InteractiveSearch.Line line,VerifiedRound previous,int[] retained)
    {
        var prior=previous.Entry;
        if(!prior.Graph.Verified || prior.Round.Number!=root.TurnNumber || !sources.TryGetValue(plan,out var source) ||
            !ReferenceEquals(source.Root,root) || !ReferenceEquals(source.Line,line))return false;
        var round=source.Session.Rounds.FirstOrDefault(item=>item.Number==root.TurnNumber);
        if(round==null || round.Start!=0 || prior.Round.Start!=0)return false;
        int oldEnd=prior.Round.Transition is int oldBoundary?oldBoundary+1:prior.Round.End;
        int newEnd=round.Transition is int newBoundary?newBoundary+1:round.End;
        var kept=retained.Take(newEnd).ToArray();
        if(kept.Length!=newEnd || kept.Any(index=>index<0 || index>=oldEnd) ||
            kept.Zip(kept.Skip(1),(a,b)=>a>=b).Any(outOfOrder=>outOfOrder))return false;
        var indices=kept.Select((old,pos)=>(old,pos)).ToDictionary(item=>item.old,item=>item.pos);
        if(prior.Graph.Dependencies.Any(edge=>indices.ContainsKey(edge.Before) && !indices.ContainsKey(edge.After)))
            return false;
        var edges=prior.Graph.Dependencies.Where(e=>indices.ContainsKey(e.Before) && indices.ContainsKey(e.After))
            .Select(e=>new RouteDependencies.Edge(indices[e.Before],indices[e.After])).ToArray();
        var consumed=new int[plan.Party.Length];
        var consumedForReadings=new int[plan.Party.Length];
        var retainedSeats=new HashSet<int>();
        foreach(var move in previous.View.Actions)
            if(indices.ContainsKey(move.Step))retainedSeats.Add(move.Seat);
            else
            {
                if(retainedSeats.Contains(move.Seat))return false;
                consumed[move.Seat]++;
                if(move.Step<prior.Round.End)consumedForReadings[move.Seat]++;
            }
        var kills=prior.Graph.Kills?.Where(k=>indices.ContainsKey(k.Step) &&
            k.Before.Select((count,seat)=>count>=consumed[seat]).All(enough=>enough))
            .Select(k=>k with {Step=indices[k.Step],
                Before=k.Before.Select((count,seat)=>count-consumed[seat]).ToArray()}).ToArray();
        var readings=prior.Graph.Readings?.Retain(retained.Take(newEnd),consumedForReadings);
        var entry=new RouteDependencySession.Entry(round,new(true,edges,0,0,null,kills,readings),0);
        if(!source.Results.TryAdd(round.Number,new(Build(source,entry),entry)))return false;
        Interlocked.Increment(ref revision);
        return true;
    }
    internal void Clear()
    {
        CancelPending();sources=new();restoredGraphs=new();
    }
    internal void CancelPending()
    {
        foreach(var work in requests.Values)work.Cancelled=true;
        requests.Clear();
    }
    internal void Cancel(ulong viewer,int slot=0)
    {
        if(requests.Remove((viewer,slot),out var old) && !requests.Values.Contains(old))old.Cancelled=true;
    }
    internal bool TryGet(HudPlan plan,KernelSession root,InteractiveSearch.Line line,int round,out VerifiedRound? result)
    {
        result=null;
        return sources.TryGetValue(plan,out var source) && ReferenceEquals(source.Root,root) &&
            ReferenceEquals(source.Line,line) && source.Results.TryGetValue(round,out result) && result!=null;
    }
    internal (InteractiveSearch.Line Line,VerifiedRound Proof)? PublishedRound(HudPlan plan,KernelSession root,HudGraph graph)
    {
        if(sources.TryGetValue(plan,out var source) && ReferenceEquals(source.Root,root) &&
            source.Results.TryGetValue(graph.Round,out var result) && result!=null && ReferenceEquals(result.View,graph))
            return (source.Line,result);
        return null;
    }

    internal RoundFailure? Failure(HudPlan plan,int round)
    {
        if(!sources.TryGetValue(plan,out var source))return new("没有找到与这条路线对应的模拟盘面");
        if(source.Failures.TryGetValue(round,out var reason))return reason;
        return source.Session.Rounds.Any(entry=>entry.Number==round)?null:new("路线缺少该回合的动作");
    }

    internal HudGraph? Request(ulong viewer,HudPlan? plan,int round,out bool waiting,int slot=0,bool settled=true)
    {
        waiting=false;
        HudGraph? cached=plan!=null && restoredGraphs.TryGetValue(plan,out var restored)
            ?restored.FirstOrDefault(item=>item.Round==round):null;
        if(!settled)
        {
            Cancel(viewer,slot);
            if(plan!=null && sources.TryGetValue(plan,out var current) &&
                current.Results.TryGetValue(round,out var verified))
            {waiting=verified==null;return verified?.View;}
            waiting=cached==null && plan!=null;
            return cached;
        }
        if(plan==null || !sources.TryGetValue(plan,out var source) || !source.Session.Rounds.Any(r=>r.Number==round))
        {Cancel(viewer,slot);return cached;}
        if(source.Results.TryGetValue(round,out var already)){Cancel(viewer,slot);return already?.View;}
        waiting=cached==null;var key=(viewer,slot);
        if(requests.TryGetValue(key,out var active) && ReferenceEquals(active.Source,source) && active.Round==round && !active.Finished)
            return cached;
        Cancel(viewer,slot);
        var shared=requests.Values.FirstOrDefault(other=>ReferenceEquals(other.Source,source) && other.Round==round && !other.Finished);
        if(shared!=null){requests[key]=shared;return cached;}
        var work=new Work(source,round);requests[key]=work;
        void Finish(){work.Steps?.Dispose();work.Steps=null;work.Finished=true;Interlocked.Increment(ref revision);}
        void Slice()
        {
            if(work.Cancelled || source.Results.ContainsKey(round)){Finish();return;}
            try
            {
                work.Steps??=source.Session.RequestSteps(round,4096,entry=>
                {
                    if(work.Cancelled)return;
                    if(!entry.Graph.Verified)
                    {
                        string reason=entry.Graph.Reason??"无法复算原行动顺序";
                        source.Failures[round]=new(reason,entry.Graph.OriginalOrderVerified);
                        Godot.GD.Print($"[CoopSolver] Round {round} route display unavailable: {reason}");
                    }
                    source.Results.TryAdd(round,entry.Graph.Verified?new(Build(source,entry),entry):null);
                }).GetEnumerator();
                long start=Stopwatch.GetTimestamp();
                do { if(!work.Steps.MoveNext()){Finish();return;} }
                while(!work.Cancelled && Stopwatch.GetElapsedTime(start).TotalMilliseconds<3);
                InteractiveSearch.Dispatcher.Post(Slice,true);
            }
            catch(Exception e) when(e is not OutOfMemoryException)
            {
                if(!work.Cancelled)
                {
                    source.Failures[round]=new(e.GetBaseException().Message);
                    source.Results.TryAdd(round,null);
                }
                Godot.GD.PrintErr("[CoopSolver] Round dependency graph: "+e.GetBaseException().Message);
                Finish();
            }
        }
        InteractiveSearch.Dispatcher.Post(Slice,true);
        return cached;
    }

    private static HudGraph Build(Source source,RouteDependencySession.Entry entry)
    {
        var plan=source.Plan;var steps=source.Line.Steps;var round=entry.Round;
        var names=plan.Steps.Split('\n').Select(row=>HudRouteView.TryPlayerMove(row,out _,out var move)?move:null)
            .OfType<string>().ToArray();
        int end=round.Transition is int transition?transition+1:round.End;
        var moves=new List<(int Step,int Seat,string Caption,bool End,uint? EnemyId)>();
        for(int i=round.Start;i<end;i++)
        {
            int seat=Array.FindIndex(plan.Party,p=>p.Id==steps[i].Player.NetId);
            if(seat<0)continue;
            string caption=names.ElementAtOrDefault(i)??(steps[i].EndTurn?"结束回合":steps[i].Card?.Id.Entry??steps[i].Potion?.Id.Entry??"选择");
            var target=steps[i].Target;
            moves.Add((i,seat,caption,steps[i].EndTurn,target?.Monster!=null?target.CombatId:null));
        }
        var visible=entry.Graph.Dependencies.Where(edge=>round.Transition is not int closer ||
            edge.After!=closer-round.Start || !steps[edge.Before+round.Start].EndTurn).ToArray();
        var kills=new List<HudKill>();
        var routeKills=source.Line.Kills??Array.Empty<RouteKill>();
        var enemyNames=routeKills.Zip(plan.Kills??Array.Empty<HudKill>(),(kill,hud)=>(kill.Enemy,hud.Name))
            .GroupBy(item=>item.Enemy).ToDictionary(group=>group.Key,group=>group.First().Name);
        foreach(var hit in (entry.Graph.Kills??Array.Empty<RouteDependencies.KillPath>()).DistinctBy(k=>(k.Step,k.Enemy)))
        {
            int step=round.Start+hit.Step;
            if(source.Line.Steps[step].EndTurn)continue;
            string? position=enemyNames.GetValueOrDefault(hit.Enemy);
            kills.Add(new(step,position??hit.Enemy.Name.ToString(),ReferenceEquals(hit.Enemy,source.Line.Steps[step].Target),
                EnemyId:hit.Enemy.CombatId,Target:HudTarget.Of(hit.Enemy,position:position)));
        }
        foreach(var kill in plan.Kills??Array.Empty<HudKill>())
            if((kill.CreditedSeat.HasValue || kill.Effect!=TurnKillEffect.None) && kill.Step>=round.Start &&
                kill.Step<=(round.Transition??round.End-1) && source.Line.Steps[kill.Step].EndTurn)
                kills.Add(kill);
        return BuildView(plan,round.Number,round.Start,moves,visible,kills.ToArray(),entry.Graph.Readings,round.Transition);
    }

    private static HudGraph BuildView(HudPlan plan,int round,int start,
        List<(int Step,int Seat,string Caption,bool End,uint? EnemyId)> moves,RouteDependencies.Edge[] edges,HudKill[] kills,
        RouteDependencies.ActionReadings? readings,int? transition)
    {
        var snapshot=plan.Rounds?.FirstOrDefault(row=>row.Round==round);
        var positions=moves.Select(m=>(m.Step,m.Seat,m.End)).ToArray();
        var damageByAction=snapshot?.DamageByAction(positions);
        var deaths=HudRoundHp.PlaceEndKills(kills,plan.Party,positions);
        var endLoss=snapshot?.LossByAction(positions);
        HudGraphAction Describe(int i,int seat,string caption,uint? enemyId)
        {
            var move=plan.Moves?.ElementAtOrDefault(i);
            int pick=caption.IndexOf('〔');
            string? choices=pick<0?null:caption[pick..].Replace("〕〔","、").Trim('〔','〕');
            string[] action=(pick<0?caption:caption[..pick]).Split(" → ",2,StringSplitOptions.None);
            var amounts=i>=start?readings?.At(i-start):null;
            int damage=amounts?.Damage??damageByAction?.GetValueOrDefault(i)??0;
            int? energy=amounts?.Energy??(snapshot?.EnergyAfter?.TryGetValue(i,out int left)==true?left:null);
            int? lost=endLoss?.TryGetValue(i,out int health)==true && health!=0?health:null;
            var killed=deaths.Where(k=>k.Step==i).ToArray();
            bool targetKilled=(move!=null?move.Target!=null:action.Length>1) && killed.Any(k=>k.IsTarget);
            var otherKills=killed.Where(k=>!targetKilled || !k.IsTarget).ToArray();
            string others=string.Join("、",otherKills.Select(k=>k.Name));
            var energies=amounts?.EnergyValues;
            var damages=amounts?.DamageValues;
            return new(i>=0?i-start:i,seat,action[0],action.ElementAtOrDefault(1),choices,damage,energy,lost,
                targetKilled,others.Length>0?others:null,enemyId,energies==null?null:string.Join("|",energies),
                damages==null?null:string.Join("|",damages),move,
                otherKills.All(k=>k.Target!=null)?otherKills.Select(k=>k.Target!).ToArray():null,energies,damages);
        }
        var actions=moves.Select(m=>Describe(m.Step,m.Seat,m.Caption,m.EnemyId)).ToArray();
        var closing=transition is int ending?snapshot?.StepDamage?.GetValueOrDefault(ending):null;
        return new(round,plan.Party,actions,edges,VerificationVersion:VerificationVersion,
            RoundDamageValues:readings?.RoundDamageValues(closing));
    }
}
