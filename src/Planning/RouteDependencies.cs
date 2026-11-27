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
    internal readonly record struct ActionDisplay(int? Energy,int? Damage,int[]? EnergyValues=null,int[]? DamageValues=null);
    internal sealed record ActionReadings(ulong[] Radix,int[] LaneLengths,int[] Consumed,ActionReading[][] Actions)
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
    }
    internal sealed record KillPath(int Step,[property:System.Text.Json.Serialization.JsonIgnore] Creature Enemy,int[] Before);
    internal sealed record Result(bool Verified,Edge[] Dependencies,int SimulatedActions,int Checks,string? Reason,
        KillPath[]? Kills=null,ActionReadings? Readings=null);

    internal static Result Analyze(KernelSession root,IReadOnlyList<KernelTeamSearch.Action> actions,
        IReadOnlyList<TreeStep?> recipes,int actionBudget)
    {
        return Analyze(root,actions,recipes,actionBudget,true);
    }

    internal static Result Analyze(KernelSession root,IReadOnlyList<KernelTeamSearch.Action> actions,
        IReadOnlyList<TreeStep?> recipes,int actionBudget,bool useOrderContract)
    {
        Result? value=null;
        foreach(var _ in new Analysis(root,actions,recipes,actionBudget,useOrderContract).Run(result=>value=result)){}
        return value!;
    }

    internal static IEnumerable<bool> AnalyzeSteps(KernelSession root,IReadOnlyList<KernelTeamSearch.Action> actions,
        IReadOnlyList<TreeStep?> recipes,int actionBudget,Action<Result> complete)=>
        new Analysis(root,actions,recipes,actionBudget,true).Run(complete);

    private static readonly Func<KernelSession,KernelTeamSearch.Action,bool>? SupportsLocalRouteOrder=FindOrderContract();
    private static Func<KernelSession,KernelTeamSearch.Action,bool>? FindOrderContract()
    {
        var method=typeof(KernelSession).GetMethod("SupportsLocalRouteOrder",
            System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,new[]{typeof(KernelTeamSearch.Action)},null);
        return method?.ReturnType==typeof(bool)?method.CreateDelegate<Func<KernelSession,KernelTeamSearch.Action,bool>>():null;
    }

    internal static bool ReplayAction(KernelSession from,KernelTeamSearch.Action action,TreeStep? recipe,
        out KernelSession to,out string reason)=>ReplayAction(from,action,recipe,out to,out _,out reason);

    private static bool ReplayAction(KernelSession from,KernelTeamSearch.Action action,TreeStep? recipe,
        out KernelSession to,out KernelTeamSearch.Action? resolved,out string reason)
    {
        to=from;resolved=null;
        if(from.HasTerminal){reason="动作执行前战斗已结束";return false;}
        to=from.ForkForReplay();
        if(recipe!=null)recipe=ActionFrontier.RebindEquivalent(to,recipe,action.CardStateOccurrence);
        return ActionFrontier.ReplayRecorded(from,ref to,action,ref recipe,out resolved,out reason);
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
        private readonly bool useOrderContract;
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
            IReadOnlyList<TreeStep?> recipes,int budget,bool useOrderContract)
        {
            this.root=root;this.actions=actions;this.recipes=recipes;this.budget=budget;this.useOrderContract=useOrderContract;
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

        internal IEnumerable<bool> Run(Action<Result> complete)
        {
            Result Fallback(string reason)=>new(false,Array.Empty<Edge>(),spent,checks,reason);
            var original=root;
            var originalKills=new List<KillPath>();
            var counts=new int[seats.Length];
            bool allLocal=useOrderContract && SupportsLocalRouteOrder!=null && queues.Count(queue=>queue.Length>0)>1;
            for(int i=0;i<actions.Count;i++)
            {
                if(!Replay(original,i,out var next,out var resolved,out var reason))
                {complete(Fallback(exhausted?"达到验证预算":"原路线未通过复算："+reason));yield break;}
                if(allLocal)allLocal=resolved!=null && next.TurnNumber==root.TurnNumber && !next.HasTerminal &&
                    SupportsLocalRouteOrder!(original,resolved);
                if(!allLocal)
                    RecordKills(original,next,i,counts,((SimulatedCombatState)next.Simulator.State.CombatState).KnownEnemies,originalKills);
                counts[lane[i]]++;original=next;yield return false;
            }
            if(queues.Count(queue=>queue.Length>0)<2)
            {complete(new(true,Array.Empty<Edge>(),spent,checks,null,originalKills.ToArray()));yield break;}
            killTargets=originalKills.Select(kill=>kill.Enemy).Distinct().ToArray();
            expected=Evidence(original);
            if(tooLarge || !expected.Reusable)
            {
                if(!tooLarge)Godot.GD.Print("[CoopSolver] Round endpoint comparison: "+expected.FirstMismatch(expected));
                complete(Fallback(tooLarge?"动作进度组合超出验证范围":"原路线终点缺少可比较的盘面"));yield break;
            }
            var initial=root.PlanningState(seats);
            if(allLocal && original.Party.Any(original.CanAct) && initial.Reusable)
            {complete(new(true,Array.Empty<Edge>(),spent,checks,null));yield break;}
            originalCards=initial.CardIdentities();
            for(;;)
            {
                Conflict? conflict=null;
                foreach(var tick in Validate(value=>conflict=value))yield return tick;
                if(exhausted){complete(Fallback("达到换序验证预算"));yield break;}
                if(conflict==null)break;
                if(ChooseDependency(conflict) is not {} edge){complete(Fallback(conflict.Reason));yield break;}
                dependencies.Add(edge);
            }
            // Greedily free the earliest blocked actions first. Keep the last
            // graph proved safe even when the optional simplification runs out.
            foreach(var edge in dependencies.OrderBy(e=>ordinal[e.After]).ThenByDescending(e=>ordinal[e.Before])
                .ThenBy(e=>e.Before).ThenBy(e=>e.After).ToArray())
            {
                dependencies.Remove(edge);
                Conflict? conflict=null;
                foreach(var tick in Validate(value=>conflict=value))yield return tick;
                if(conflict!=null || exhausted)dependencies.Add(edge);
                if(exhausted)break;
            }
            complete(new(true,dependencies.OrderBy(e=>e.After).ThenBy(e=>e.Before).ToArray(),spent,checks,
                exhausted?"已验证，箭头简化达到预算":null,provedKills,provedReadings));
        }

        private bool Replay(KernelSession from,int i,out KernelSession to,out KernelTeamSearch.Action? resolved,out string reason)
        {
            to=from;resolved=null;
            if(spent>=budget){exhausted=true;reason="达到验证预算";return false;}
            spent++;
            return ReplayAction(from,actions[i],recipes.ElementAtOrDefault(i),out to,out resolved,out reason);
        }

        private IEnumerable<bool> Validate(Action<Conflict?> complete)
        {
            checks++;
            var requirements=Enumerable.Range(0,actions.Count).Select(_=>new List<int>()).ToArray();
            foreach(var edge in dependencies)requirements[edge.After].Add(edge.Before);
            var kills=new List<KillPath>();
            var readings=new List<ActionReading>?[actions.Count];
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
                        if(!Replay(point.State,action,out var state,out _,out var reason))
                        {complete(new(trace,null,reason));yield break;}
                        if(!actions[action].EndTurn)
                            (readings[action]??=new()).Add(new(key,state.Energy(actions[action].Player),
                                DamageAfter(point.State,state,actions[action].Player.NetId)));
                        RecordKills(point.State,state,action,progress,killTargets,kills);
                        ulong target=key+radix[actor];
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
            foreach(var point in active.Values)
            {
                var actual=point.Evidence??=Evidence(point.State);
                if(!Same(expected!,actual) && point.Path!=null)
                {complete(new(point.Path,null,actual.Reusable?"重排后的段末与原路线不同":"换序后缺少可比较的盘面"));yield break;}
            }
            provedKills=kills.ToArray();
            provedReadings=new(radix,queues.Select(queue=>queue.Length).ToArray(),new int[seats.Length],
                readings.Select(CompactReadings).ToArray());
            complete(null);
        }

        private static ActionReading[] CompactReadings(List<ActionReading>? readings)
            =>readings is not {Count:>0}?Array.Empty<ActionReading>():
                readings.All(item=>item.Energy==readings[0].Energy && item.Damage==readings[0].Damage)
                    ?new[]{readings[0]}:readings.ToArray();

        private static int DamageAfter(KernelSession before,KernelSession after,ulong player)
        {
            int total=0;
            var combat=(SimulatedCombatState)after.Simulator.State.CombatState;
            foreach(var entry in after.Simulator.History.EntriesFrom(before.Simulator.History.Entries.Count))
                if(entry is CombatPredictionDamageReceivedEntry hit && combat.KnownEnemies.Contains(hit.Receiver) &&
                    DamageAttribution.Seat(hit,DamageAttribution.Get(hit))==player)
                    total+=DamageAttribution.Amount(hit);
            return total;
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
            Edge? best=null;
            int bestGap=int.MaxValue;
            for(int after=0;after<actions.Count;after++)
                for(int before=after-1;before>=0;before--)
                {
                    if(lane[before]==lane[after])continue;
                    var pair=new Edge(before,after);
                    if(dependencies.Contains(pair))continue;
                    bool violated=first[after]<first[before];
                    bool changed=other!=null && violated!=(other[after]<other[before]);
                    if(other==null?!violated:!changed)continue;
                    int gap=after-before;
                    if(other==null && after==conflict.Path.Action)gap-=actions.Count;
                    if(gap<bestGap){best=pair;bestGap=gap;}
                }
            return best;
        }
    }
}
