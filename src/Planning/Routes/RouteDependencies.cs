using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

// Analyze a fixed sequence of actions from a verified route. A shared progress
// counter represents one state only after every incoming ordering agrees.
internal static class RouteDependencies
{
    internal readonly record struct Edge(int Before,int After);
    internal readonly record struct ActionReading(ulong Before,int Energy,int Damage);
    internal readonly record struct DamageTransition(ulong Before,ulong After,int[] BySeat);
    internal readonly record struct ActionDisplay(int? Energy,int? Damage,int[]? EnergyValues=null,int[]? DamageValues=null);
    internal sealed record ActionReadings(ulong[] Radix,int[] LaneLengths,int[] Consumed,ActionReading[][] Actions,
        DamageTransition[]? DamageTransitions=null)
    {
        internal ActionReadings Retain(IEnumerable<int> steps,int[] consumed)
            =>this with {Actions=steps.Select(step=>Actions.ElementAtOrDefault(step)??Array.Empty<ActionReading>()).ToArray(),
                Consumed=Consumed.Zip(consumed,(prior,added)=>prior+added).ToArray()};

        internal ActionDisplay At(int step)
        {
            if((uint)step>=(uint)Actions.Length || Actions[step].Length==0)return default;
            var readings=Actions[step];
            if(readings.Length==1)return new(readings[0].Energy,readings[0].Damage);
            var energy=new SortedSet<int>();
            var damage=new SortedSet<int>();
            foreach(var reading in readings)
                if(ExtendsConsumed(reading.Before))
                {energy.Add(reading.Energy);damage.Add(reading.Damage);}
            if(energy.Count==0)return default;
            return new(energy.Min,damage.Min,
                energy.Count>1?energy.ToArray():null,damage.Count>1?damage.ToArray():null);
        }

        private bool ExtendsConsumed(ulong progress)
        {
            for(int seat=0;seat<Consumed.Length;seat++)
                if(progress/Radix[seat]%((ulong)LaneLengths[seat]+1)<(ulong)Consumed[seat])return false;
            return true;
        }

        // Each transition is in increasing progress order. Propagate each
        // player's attainable totals along complete, legal paths from the
        // actual prefix. Values for different players are not independent.
        internal HudDamageOptions[]? RoundDamageValues(int[]? closing=null)
        {
            if(DamageTransitions==null)return null;
            // Keep one additional value to detect a longer list. Since
            // each edge adds a fixed amount, later paths cannot promote
            // an omitted value ahead of the retained values on that path.
            const int shown=5;
            ulong begin=0,end=0;
            for(int i=0;i<Radix.Length;i++)
            {
                begin+=Radix[i]*(ulong)Consumed[i];
                end+=Radix[i]*(ulong)LaneLengths[i];
            }
            var totals=new Dictionary<ulong,SortedSet<int>[]>{
                [begin]=Radix.Select(_=>new SortedSet<int>{0}).ToArray()
            };
            foreach(var edge in DamageTransitions)
            {
                if(!totals.TryGetValue(edge.Before,out var previous))continue;
                if(!totals.TryGetValue(edge.After,out var next))
                    totals.Add(edge.After,next=Radix.Select(_=>new SortedSet<int>()).ToArray());
                for(int i=0;i<next.Length;i++)
                {
                    int amount=edge.BySeat.ElementAtOrDefault(i);
                    foreach(int value in previous[i])
                        if(next[i].Add(value+amount) && next[i].Count>shown+1)next[i].Remove(next[i].Max);
                }
            }
            if(!totals.TryGetValue(end,out var final) || final.All(values=>values.Count<=1))return null;
            return final.Select((values,seat)=>new HudDamageOptions(values.Take(shown)
                .Select(value=>value+(closing?.ElementAtOrDefault(seat)??0)).ToArray(),values.Count>shown)).ToArray();
        }
    }
    internal sealed record KillPath(int Step,[property:System.Text.Json.Serialization.JsonIgnore] Creature Enemy,int[] Before);
    internal sealed record Result(bool Verified,Edge[] Dependencies,int SimulatedActions,int Checks,string? Reason,
        KillPath[]? Kills=null,ActionReadings? Readings=null,bool OriginalOrderVerified=false);

    internal static Result Analyze(KernelSession root,IReadOnlyList<KernelTeamSearch.Action> actions,
        IReadOnlyList<TreeStep?> recipes,int actionBudget)
    {
        Result? value=null;
        foreach(var _ in new Analysis(root,actions,recipes,actionBudget).Run(result=>value=result)){}
        return value!;
    }

    internal static IEnumerable<bool> AnalyzeSteps(KernelSession root,IReadOnlyList<KernelTeamSearch.Action> actions,
        IReadOnlyList<TreeStep?> recipes,int actionBudget,Action<Result> complete,Func<KernelSession,string?>? verifyClosingAction=null)=>
        new Analysis(root,actions,recipes,actionBudget).Run(complete,verifyClosingAction);

    internal static bool ReplayAction(KernelSession from,KernelTeamSearch.Action action,TreeStep? recipe,
        out KernelSession to,out string reason)
    {
        to=from;
        if(from.HasTerminal){reason="动作执行前战斗已结束";return false;}
        to=from.ForkForReplay();
        if(recipe!=null)recipe=ActionFrontier.RebindEquivalent(to,recipe,action.CardStateOccurrence);
        if(!ActionFrontier.ReplayRecorded(from,ref to,action,ref recipe,out _,out reason))return false;
        // A later, settled snapshot cannot certify an earlier unsupported effect.
        if(to.LastActionHadEnvironmentalRisk && !string.IsNullOrWhiteSpace(to.LastActionRiskDetail))
        {
            reason=to.LastActionRiskDetail;
            return false;
        }
        return true;
    }

    private sealed record Trace(int Action,Trace? Previous);
    private sealed record Point(KernelSession State,Trace? Path)
    {
        internal KernelPlanningState? Evidence;
    }
    private sealed record Conflict(Trace Path,Trace? Other,string Reason);

    private sealed class Analysis
    {
        private readonly KernelSession root;
        private readonly IReadOnlyList<KernelTeamSearch.Action> actions;
        private readonly IReadOnlyList<TreeStep?> recipes;
        private readonly int budget;
        private readonly ulong[] seats;
        private readonly int[] lane,ordinal;
        private readonly int[][] queues;
        private readonly ulong[] radix;
        private readonly List<Edge> dependencies=new();
        private KernelPlanningState? expected;
        private IReadOnlySet<CardModel>? originalCards;
        private Creature[] killTargets=Array.Empty<Creature>();
        private KillPath[] provedKills=Array.Empty<KillPath>();
        private ActionReadings? provedReadings;
        private int spent,checks;
        private bool exhausted,tooLarge;

        internal Analysis(KernelSession root,IReadOnlyList<KernelTeamSearch.Action> actions,
            IReadOnlyList<TreeStep?> recipes,int budget)
        {
            this.root=root;this.actions=actions;this.recipes=recipes;this.budget=budget;
            seats=root.Party.Select(p=>p.NetId).ToArray();
            lane=new int[actions.Count];ordinal=new int[actions.Count];
            var lists=seats.Select(_=>new List<int>()).ToArray();
            for(int i=0;i<actions.Count;i++)
            {
                int actor=Array.IndexOf(seats,actions[i].Player.NetId);
                if(actor<0)throw new ArgumentException("Route actor is absent from the battle.",nameof(actions));
                var list=lists[actor];
                lane[i]=actor;ordinal[i]=list.Count;list.Add(i);
            }
            queues=lists.Select(p=>p.ToArray()).ToArray();radix=new ulong[seats.Length];
            ulong place=1;
            for(int i=0;i<queues.Length;i++)
            {
                radix[i]=place;
                ulong size=(ulong)queues[i].Length+1;
                if(place>ulong.MaxValue/size){tooLarge=true;break;}
                place*=size;
            }
        }

        internal IEnumerable<bool> Run(Action<Result> complete,Func<KernelSession,string?>? verifyClosingAction=null)
        {
            bool originalVerified=false;
            Result Fallback(string reason)=>new(false,Array.Empty<Edge>(),spent,checks,reason,OriginalOrderVerified:originalVerified);
            var original=root;
            var originalKills=new List<KillPath>();
            var counts=new int[seats.Length];
            for(int i=0;i<actions.Count;i++)
            {
                if(!Replay(original,i,out var next,out var reason))
                {
                    var action=actions[i];
                    string move=action.Card?.Id.Entry??action.Potion?.Id.Entry??"结束回合";
                    complete(Fallback(exhausted?"达到验证预算":
                        $"原路线未通过复算：#{i+1} P{lane[i]+1} {move} · {reason}"));
                    yield break;
                }
                RecordKills(original,next,i,counts,((SimulatedCombatState)next.Simulator.State.CombatState).KnownEnemies,originalKills);
                counts[lane[i]]++;original=next;yield return false;
            }
            if(verifyClosingAction!=null)
            {
                if(verifyClosingAction(original) is {} error)
                {complete(Fallback(error));yield break;}
                yield return false;
            }
            originalVerified=true;
            if(queues.Count(queue=>queue.Length>0)<2)
            {complete(new(true,Array.Empty<Edge>(),spent,checks,null,originalKills.ToArray()));yield break;}
            killTargets=originalKills.Select(kill=>kill.Enemy).Distinct().ToArray();
            expected=Evidence(original);
            if(tooLarge || !expected.Reusable)
            {
                if(tooLarge)complete(Fallback("动作进度组合超出验证范围"));
                else
                {
                    string detail=expected.FirstMismatch(expected)??"";
                    Godot.GD.Print("[CoopSolver] Round endpoint comparison: "+detail);
                    var reason=System.Text.RegularExpressions.Regex.Match(detail,@"expectedReason=(?<reason>\S+)").Groups["reason"].Value;
                    complete(Fallback("原路线终点缺少可比较的盘面"+(reason.Length==0?"":" · "+reason)));
                }
                yield break;
            }
            originalCards=root.PlanningState(seats).CardIdentities();
            for(;;)
            {
                Conflict? conflict=null;
                foreach(var tick in Validate(value=>conflict=value))yield return tick;
                if(exhausted){complete(Fallback("达到换序验证预算"));yield break;}
                if(conflict==null)break;
                if(ChooseDependency(conflict) is not {} edge){complete(Fallback(conflict.Reason));yield break;}
                dependencies.Add(edge);
            }
            // Lane order already holds. An edge may be necessary while its
            // endpoints are unnecessarily restrictive. Widen the allowed
            // schedules only after verifying the resulting graph.
            foreach(var edge in dependencies.OrderBy(e=>ordinal[e.After]).ThenByDescending(e=>ordinal[e.Before])
                .ThenBy(e=>e.Before).ThenBy(e=>e.After).ToArray())
            {
                Edge? retained=edge;
                foreach(var tick in Relax(edge,EarlySources(edge).Prepend(null),value=>retained=value))
                    yield return tick;
                if(exhausted)break;
                if(retained is {} remaining)
                    foreach(var tick in Relax(remaining,LateTargets(remaining),_=>{}))
                        yield return tick;
                if(exhausted)break;
            }
            complete(new(true,dependencies.OrderBy(e=>e.After).ThenBy(e=>e.Before).ToArray(),spent,checks,
                exhausted?"已验证，箭头简化达到预算":null,provedKills,provedReadings));
        }

        private IEnumerable<Edge?> EarlySources(Edge edge)=>queues[lane[edge.Before]]
            .Take(ordinal[edge.Before]).Select(step=>(Edge?)new Edge(step,edge.After));

        private IEnumerable<Edge?> LateTargets(Edge edge)=>queues[lane[edge.After]]
            .Skip(ordinal[edge.After]+1).Reverse().Select(step=>(Edge?)new Edge(edge.Before,step));

        private IEnumerable<bool> Relax(Edge edge,IEnumerable<Edge?> candidates,Action<Edge?> retain)
        {
            int index=dependencies.IndexOf(edge);
            dependencies.RemoveAt(index);
            foreach(var candidate in candidates)
            {
                if(candidate is {} replacement)
                {
                    if(dependencies.Contains(replacement))continue;
                    dependencies.Insert(index,replacement);
                }
                Conflict? conflict=null;
                foreach(var tick in Validate(value=>conflict=value))yield return tick;
                if(!exhausted && conflict==null){retain(candidate);yield break;}
                if(candidate.HasValue)dependencies.RemoveAt(index);
                if(exhausted)break;
            }
            dependencies.Insert(index,edge);
            retain(edge);
        }

        private bool Replay(KernelSession from,int i,out KernelSession to,out string reason)
        {
            to=from;
            if(spent>=budget){exhausted=true;reason="达到验证预算";return false;}
            spent++;
            return ReplayAction(from,actions[i],recipes.ElementAtOrDefault(i),out to,out reason);
        }

        private IEnumerable<bool> Validate(Action<Conflict?> complete)
        {
            checks++;
            var requirements=Enumerable.Range(0,actions.Count).Select(_=>new List<int>()).ToArray();
            foreach(var edge in dependencies)requirements[edge.After].Add(edge.Before);
            var kills=new List<KillPath>();
            var readings=new List<ActionReading>?[actions.Count];
            var transitions=new List<DamageTransition>();
            var knownDamage=new int[]?[actions.Count];
            bool variableDamage=false;
            var active=new Dictionary<ulong,Point>{{0,new(root,null)}};
            var progress=new int[seats.Length];
            for(int depth=0;depth<actions.Count;depth++)
            {
                var next=new Dictionary<ulong,Point>();
                foreach(var (key,point) in active)
                {
                    for(int actor=0;actor<seats.Length;actor++)
                        progress[actor]=(int)(key/radix[actor]%((ulong)queues[actor].Length+1));
                    for(int actor=0;actor<seats.Length;actor++)
                    {
                        if(progress[actor]>=queues[actor].Length)continue;
                        int action=queues[actor][progress[actor]];
                        if(requirements[action].Any(before=>progress[lane[before]]<=ordinal[before]))continue;
                        var trace=new Trace(action,point.Path);
                        if(!Replay(point.State,action,out var state,out var reason))
                        {complete(new(trace,null,reason));yield break;}
                        var bySeat=DamageAfter(point.State,state);
                        if(knownDamage[action] is not {} known)knownDamage[action]=bySeat;
                        else if(!variableDamage)variableDamage=!known.SequenceEqual(bySeat);
                        ulong target=key+radix[actor];
                        transitions.Add(new(key,target,bySeat));
                        if(!actions[action].EndTurn)
                            (readings[action]??=new()).Add(new(key,state.Energy(actions[action].Player),
                                bySeat.Sum()));
                        RecordKills(point.State,state,action,progress,killTargets,kills);
                        // All arrivals at this progress have the same remaining inputs.
                        // Retain one state only when the full continuation evidence agrees.
                        if(next.TryGetValue(target,out var prior))
                        {
                            var a=prior.Evidence??=Evidence(prior.State);
                            var b=Evidence(state);
                            if(!Same(a,b))
                            {
                                complete(new(trace,prior.Path,!a.Reusable || !b.Reusable?
                                    "换序后缺少可比较的盘面":"同一进度的盘面不同"));
                                yield break;
                            }
                        }
                        else next.Add(target,new Point(state,trace));
                        yield return false;
                    }
                }
                active=next;
            }
            if(active.Count==0)
                throw new InvalidOperationException("No complete action order reaches the end of the round.");
            foreach(var point in active.Values)
            {
                var actual=point.Evidence??=Evidence(point.State);
                if(!Same(expected!,actual) && point.Path!=null)
                {complete(new(point.Path,null,actual.Reusable?"重排后的段末与原路线不同":"换序后缺少可比较的盘面"));yield break;}
            }
            provedKills=kills.ToArray();
            provedReadings=new(radix,queues.Select(queue=>queue.Length).ToArray(),new int[seats.Length],
                readings.Select(CompactReadings).ToArray(),variableDamage?transitions.ToArray():null);
            complete(null);
        }

        private static ActionReading[] CompactReadings(List<ActionReading>? readings)
            =>readings is not {Count:>0}?Array.Empty<ActionReading>():
                readings.All(item=>item.Energy==readings[0].Energy && item.Damage==readings[0].Damage)
                    ?new[]{readings[0]}:readings.ToArray();

        private int[] DamageAfter(KernelSession before,KernelSession after)
        {
            int[]? bySeat=null;
            foreach(var hit in DamageAttribution.EnemyHits(after,before.Simulator.History.Entries.Count))
            {
                if(hit.Seat is not ulong seat)continue;
                int index=Array.IndexOf(seats,seat);
                if(index>=0)(bySeat??=new int[seats.Length])[index]+=hit.Amount;
            }
            return bySeat??Array.Empty<int>();
        }

        private static void RecordKills(KernelSession before,KernelSession after,int step,int[] progress,
            IEnumerable<Creature> targets,List<KillPath> kills)
        {
            var first=(SimulatedCombatState)before.Simulator.State.CombatState;
            var last=(SimulatedCombatState)after.Simulator.State.CombatState;
            foreach(var enemy in targets)
            {
                if(!first.KnownEnemies.Contains(enemy) || !last.KnownEnemies.Contains(enemy) || last.EscapedCreatures.Contains(enemy))continue;
                var final=after.Simulator.State.GetCreature(enemy);
                if(final.CurrentHp>0 || last.EffectiveEnemyHp(enemy,final)>0 ||
                    first.EffectiveEnemyHp(enemy,before.Simulator.State.GetCreature(enemy))<=0)continue;
                // Keep the largest verified prefixes. Smaller points add no possible
                // last hitters when filtering this graph after more moves are played.
                bool Covers(int[] a,int[] b)
                {
                    for(int i=0;i<a.Length;i++)if(a[i]<b[i])return false;
                    return true;
                }
                if(kills.Any(k=>k.Step==step && ReferenceEquals(k.Enemy,enemy) && Covers(k.Before,progress)))continue;
                kills.RemoveAll(k=>k.Step==step && ReferenceEquals(k.Enemy,enemy) && Covers(progress,k.Before));
                kills.Add(new(step,enemy,(int[])progress.Clone()));
            }
        }

        private KernelPlanningState Evidence(KernelSession state)
        {
            var exact=state.PlanningState(seats);
            if(exact.Reusable || !state.HasWon || !state.CanFork || state.LastActionHadEnvironmentalRisk)return exact;
            var states=state.Party.Select(p=>state.Simulator.State.GetPlayerCombatState(p)).ToArray();
            var playing=states.SelectMany(p=>p.PlayPile.Cards).ToArray();
            if(playing.Length!=1)return state.PlanningState(seats,includeVictoryPlayPile:true);
            var attack=playing[0].Preview;
            if(attack.GetType().Assembly!=typeof(CardModel).Assembly || attack.Type!=CardType.Attack ||
                attack.Affliction!=null || attack.Enchantment!=null || attack.IsDupe ||
                attack.ExhaustOnNextPlay || attack.Keywords.Contains(CardKeyword.Exhaust))
                return state.PlanningState(seats,includeVictoryPlayPile:true);

            // Victory can stop AddToPile while leaving the winning attack in
            // PlayPile. Compare the otherwise complete board after normalizing
            // that placement in a detached copy. Live deaths keep exact rules.
            var normalized=state.ForkForReplay();
            var owner=normalized.Simulator.State.GetPlayerCombatState(attack.Owner);
            var card=owner.PlayPile.Cards.SingleOrDefault();
            if(card==null)return state.PlanningState(seats,includeVictoryPlayPile:true);
            owner.PlayPile.Remove(card);owner.DiscardPile.Add(card);
            var settled=normalized.PlanningState(seats);
            return settled.Reusable?settled:state.PlanningState(seats,includeVictoryPlayPile:true);
        }

        private bool Same(KernelPlanningState a,KernelPlanningState b)=>
            a.Matches(b,originalCards) && a.SearchMetadata==b.SearchMetadata;

        private Edge? ChooseDependency(Conflict conflict)
        {
            int[] Ranks(Trace? from)
            {
                var rank=Enumerable.Repeat(int.MaxValue,actions.Count).ToArray();
                int at=actions.Count;
                for(var t=from;t!=null;t=t.Previous)rank[t.Action]=--at;
                return rank;
            }
            var first=Ranks(conflict.Path);
            var other=conflict.Other==null?null:Ranks(conflict.Other);
            bool SplitsConflict(Edge edge)
            {
                bool violated=first[edge.After]<first[edge.Before];
                return other==null?violated:violated!=(other[edge.After]<other[edge.Before]);
            }
            Edge? best=null;
            int bestGap=int.MaxValue;
            for(int after=0;after<actions.Count;after++)
                for(int before=after-1;before>=0;before--)
                {
                    if(lane[before]==lane[after])continue;
                    var pair=new Edge(before,after);
                    if(dependencies.Contains(pair))continue;
                    if(!SplitsConflict(pair))continue;
                    int gap=after-before;
                    if(other==null && after==conflict.Path.Action)gap-=actions.Count;
                    if(gap<bestGap){best=pair;bestGap=gap;}
                }
            if(best is not {} chosen)return null;
            foreach(var earlier in EarlySources(chosen))
                if(earlier is {} edge && !dependencies.Contains(edge) && SplitsConflict(edge))
                    return edge;
            return chosen;
        }
    }
}
