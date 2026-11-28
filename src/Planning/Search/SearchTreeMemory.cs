using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using CoopBots.Kernel;

namespace CoopSolver;

// Census of search-owned storage, on the owning dispatcher. Native game assets,
// unrelated mods and the cumulative traffic of discarded simulation objects are excluded.
internal sealed class SearchTreeMemory
{
    private readonly HashSet<AnytimeSearch> planners=new();
    private AnytimeSearch.Node? battleRoot;
    private WeakReference<BattleExchange>? battle;
    private Survey? survey;
    private long bytes,next,estimateRevision;
    internal readonly record struct Estimate(long Revision,long Bytes);
    private readonly Queue<Estimate> publishedEstimates=new(16);
    private bool scheduled,idleMeasured,reclaimScheduled,recountAfterReclaim;
    private long nextReclaim,blockedLimit,blockedBytes;
    private int blockedGeneration;
    private long removedNodes,reopenedNodes,reclaimPasses,compactedBranches;
    internal (long Passes,long Removed,long Reopened,long Branches) Reclamation=>
        (Interlocked.Read(ref reclaimPasses),Interlocked.Read(ref removedNodes),Interlocked.Read(ref reopenedNodes),Interlocked.Read(ref compactedBranches));
    internal void CountReopened()=>Interlocked.Increment(ref reopenedNodes);
    private int generation;
    private long forkBytes,forkCount;
    internal sealed class Lane {internal KernelSession? Current;}
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int,Lane> lanes=new();
    internal volatile bool AtLimit;
    internal void WorkerReleased()
    {
        if(AtLimit)lock(planners)Invalidate(false);
    }
    internal Lane Worker()=>lanes.GetOrAdd(Environment.CurrentManagedThreadId,static _=>new());
    internal void SampleBoard(long size)
    {
        if(size<=0)return;
        Interlocked.Add(ref forkBytes,size);Interlocked.Increment(ref forkCount);
    }
    private long BoardBytes=>Interlocked.Read(ref forkBytes)/Math.Max(1,Interlocked.Read(ref forkCount));

    internal void Attach(AnytimeSearch p)
    {
        lock(planners)if(planners.Add(p))Invalidate(false);
    }
    internal void Detach(AnytimeSearch p)
    {
        lock(planners)if(planners.Remove(p))Invalidate(battleRoot==null && planners.Count==0);
    }
    internal void BattleRoot(AnytimeSearch.Node root,BattleExchange? owner=null)
    {lock(planners){battleRoot=root;battle=owner==null?null:new(owner);Invalidate(false);}}
    internal void Reset(){lock(planners){Invalidate(true);nextReclaim=blockedLimit=0;recountAfterReclaim=false;}}
    internal void RequestReclamation(long limit)
    {
        lock(planners)
        {
            if(reclaimScheduled || recountAfterReclaim || bytes<limit || Environment.TickCount64<nextReclaim ||
                blockedLimit==limit && blockedBytes==bytes && blockedGeneration==generation)return;
            reclaimScheduled=true;
        }
        InteractiveSearch.Dispatcher.Post(()=>Reclaim(limit));
    }

    private void Reclaim(long limit)
    {
        AnytimeSearch[] owners;
        AnytimeSearch.Node? root;
        BattleExchange? exchange=null;
        long before;
        lock(planners)
        {
            before=bytes;
            if(limit!=SearchCapacity.MemoryLimitMb*1_000_000L || limit<=0 || before<limit)
            {reclaimScheduled=false;return;}
            owners=System.Linq.Enumerable.ToArray(planners);
            root=battleRoot;battle?.TryGetTarget(out exchange);
            // A paused census can still retain the old graph in its stack.
            Invalidate(false);
        }

        bool freed=false,canRetry=false;
        try
        {
            // A live worker can be in the middle of rebasing a standalone
            // graph while the other worker is paused. The transfer owns both
            // graphs until it finishes.
            if(Array.Exists(owners,owner=>owner.Recovering)){canRetry=true;return;}
            // Recount after each bounded batch. A cap lowered substantially
            // mid-fight can take multiple passes without deleting the whole tree.
            var trim=new TreeReclamation(Math.Min(before-limit*85/100,limit/8));
            if(root!=null)trim.PreserveRoot(root);
            exchange?.ProtectRetainedWork(trim);
            foreach(var owner in owners)owner.ProtectRetainedWork(trim);
            trim.Run(()=>{
                foreach(var owner in owners)owner.BeforeReclamation();
            });
            if(trim.Bytes==0)return;
            freed=true;
            foreach(var owner in owners)owner.ForgetRemovedNodes(trim.Removed);
            exchange?.VerifiedJoins.ForgetRemovedNodes(trim.Removed);
            Interlocked.Increment(ref reclaimPasses);
            Interlocked.Add(ref removedNodes,trim.Nodes);
            Interlocked.Add(ref compactedBranches,trim.Branches);
        }
        catch(Exception error)
        {
            Godot.GD.PrintErr("[CoopSolver] Search tree reclamation: "+error);
        }
        finally
        {
            lock(planners)
            {
                reclaimScheduled=false;
                recountAfterReclaim=freed;
                nextReclaim=Environment.TickCount64+(freed?250:2000);
                blockedLimit=!freed && !canRetry?limit:0;
                blockedBytes=bytes;blockedGeneration=generation;
            }
        }
    }
    private void Invalidate(bool zero)
    {
        survey=null;next=0;idleMeasured=false;generation++;
        if(zero){bytes=0;publishedEstimates.Clear();}
    }

    internal void CopyEstimatesAfter(long revision,List<Estimate> target)
    {
        target.Clear();
        lock(planners)
        {
            if(publishedEstimates.Count==0 || revision==estimateRevision)return;
            if(revision<0)
            {
                target.Add(new(estimateRevision,bytes));
                return;
            }
            foreach(var estimate in publishedEstimates)
                if(estimate.Revision>revision)target.Add(estimate);
        }
    }

    private void PublishEstimate(long used)
    {
        bytes=used;
        if(publishedEstimates.Count==16)publishedEstimates.Dequeue();
        publishedEstimates.Enqueue(new(++estimateRevision,used));
    }

    internal long Read(bool active)
    {
        lock(planners)
        {
            if(battleRoot==null && planners.Count==0)return 0;
            if(active)idleMeasured=false;
            ScheduleSample(active);
            return bytes;
        }
    }

    internal void ContinueSurvey(bool active)
    {
        lock(planners)
        {
            // Finish a survey already in progress between HUD refreshes.
            if(survey!=null)ScheduleSample(active);
        }
    }

    private void ScheduleSample(bool active)
    {
        if(reclaimScheduled || scheduled || idleMeasured || Environment.TickCount64<next)return;
        scheduled=true;
        InteractiveSearch.Dispatcher.Post(()=>Sample(active),false);
    }
    private void Sample(bool active)
    {
        try{SampleCore(active);}
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            lock(planners)
            {
                Invalidate(false);
                scheduled=false;
                recountAfterReclaim=false;
                next=Environment.TickCount64+500;
            }
            throw;
        }
    }

    private void SampleCore(bool active)
    {
        int version;bool recount;
        Survey? current;AnytimeSearch.Node? root;BattleExchange? exchange;AnytimeSearch[] owners;
        lock(planners)
        {
            version=generation;recount=recountAfterReclaim;current=survey;root=battleRoot;exchange=null;battle?.TryGetTarget(out exchange);
            owners=current==null?System.Linq.Enumerable.ToArray(planners):Array.Empty<AnytimeSearch>();
        }
        if(current==null)
        {
            current=new Survey(root,owners,BoardBytes);
            exchange?.MeasureRetained(current);
            foreach(var lane in lanes.Values)current.Board(Volatile.Read(ref lane.Current));
        }
        bool done=current.Advance(recount?4:1);
        lock(planners)
        {
            if(version==generation)
            {
                if(done){PublishEstimate(current.Bytes);survey=null;idleMeasured=!active;recountAfterReclaim=false;}
                else
                {
                    survey=current;
                    if(recount)
                    {
                        InteractiveSearch.Dispatcher.Post(()=>Sample(active));
                        return;
                    }
                    if(current.Bytes>bytes)PublishEstimate(current.Bytes);
                }
                next=Environment.TickCount64+(done?500:8);
            }
            scheduled=false;
        }
    }

    internal sealed class Survey
    {
        private readonly Stack<AnytimeSearch.Node> todo=new();
        private readonly HashSet<AnytimeSearch.EvidenceScope> scopes=new();
        private readonly object stamp=new();
        private readonly Dictionary<object,long> payload=new(ReferenceEqualityComparer.Instance);
        // Always count unusually large strings/arrays exactly. The number of
        // these references is bounded by the amount of payload they represent.
        private readonly HashSet<object> large=new(ReferenceEqualityComparer.Instance);
        private uint mask;
        private long structuralBytes,sampleBytes,largeBytes;
        internal long Bytes=>structuralBytes+largeBytes+sampleBytes*(mask+1L);
        internal void Owned(long amount)=>structuralBytes+=amount;
        private readonly ConditionalWeakTable<KernelSession,object> sessions=new();
        internal int BoardCount {get;private set;}
        private readonly long boardBytes;
        private readonly HashSet<SeedRoute> seeds=new(ReferenceEqualityComparer.Instance);
        private readonly Queue<SeedRoute> pendingSeeds=new();
        internal void Seed(SeedRoute? seed){if(seed!=null && seeds.Add(seed))pendingSeeds.Enqueue(seed);}
        private readonly HashSet<InteractiveSearch.Line> lines=new(ReferenceEqualityComparer.Instance);
        private readonly Queue<InteractiveSearch.Line> pendingLines=new();
        internal void Line(InteractiveSearch.Line? line){if(line!=null && lines.Add(line))pendingLines.Enqueue(line);}
        private void ReadLine(InteractiveSearch.Line line)
        {
            Owned(144);
            if(List(line.Steps))foreach(var a in line.Steps)Action(a);
            if(List(line.Recipes))foreach(var step in line.Recipes)Step(step);
            if(List(line.Trail))foreach(var state in line.Trail)State(state);
            Ending(line.Ending);Numbers(line.Hp);Numbers(line.FirstHp);State(line.JoinProof);
            if(line.OrderProof is {} proof)
            {
                Board(proof.Origin);
                if(List(proof.Actions))foreach(var action in proof.Actions)Action(action);
                if(List(proof.Recipes))foreach(var recipe in proof.Recipes)Step(recipe);
            }
            if(List(line.Risks))foreach(string risk in line.Risks)Text(risk);
            if(line.Kills is {} kills && List(kills))foreach(var kill in kills)Include(kill,32);
            Rounds(line.Rounds);
        }
        private bool Map<T>(Dictionary<int,T>? values,int entryBytes)
        {
            if(values==null)return false;
            int capacity=values.EnsureCapacity(0);
            return Include(values,80+Buffer(capacity,4)+Buffer(capacity,entryBytes));
        }
        internal void Rounds(HudRoundHp[]? rounds)
        {
            if(rounds==null || !List(rounds))return;
            foreach(var r in rounds)
            {
                if(!Include(r,120))continue;
                Numbers(r.Loss);Numbers(r.EndHp);Numbers(r.Damage);Numbers(r.Recovered);Numbers(r.Potions);
                Map(r.CardDamage,16);Map(r.EnergyAfter,16);Map(r.UnassignedDamage,16);
                if(Map(r.StepDamage,24))foreach(var v in r.StepDamage!.Values)Numbers(v);
                if(Map(r.EndLoss,24))foreach(var v in r.EndLoss!.Values)Numbers(v);
                if(Map(r.DamageNotes,24))foreach(var v in r.DamageNotes!.Values)Text(v);
                if(Map(r.Revives,24))foreach(var v in r.Revives!.Values)
                    if(List(v))foreach(var item in v)if(Include(item,48))Text(item.Source);
            }
        }
        internal void Board(KernelSession? state){if(state!=null && sessions.TryAdd(state,stamp)){BoardCount++;Owned(boardBytes);}}
        internal void State(KernelPlanningState? state)
        {
            if(state!=null && Include(state,56)){Text(state.Semantics);Text(state.SearchMetadata);}
        }
        internal Survey(AnytimeSearch.Node? root,IEnumerable<AnytimeSearch> owners,long boardBytes=0)
        {
            this.boardBytes=boardBytes;
            if(root!=null)todo.Push(root);
            foreach(var owner in owners){todo.Push(owner.MemoryRoot);scopes.Add(owner.MemoryScope);owner.MeasureSearchStorage(this);}
        }
        internal bool Advance(int milliseconds=1)
        {
            long start=Stopwatch.GetTimestamp();
            do
            {
                if(pendingLines.TryDequeue(out var line)){ReadLine(line);continue;}
                if(pendingSeeds.TryDequeue(out var seed))
                {
                    Owned(56);Numbers(seed.Hp);
                    if(seed.Steps is {} steps && List(steps))foreach(var step in steps)Saved(step);
                    continue;
                }
                if(!todo.TryPop(out var node))return true;
                if(ReferenceEquals(node.WalkStamp,stamp))continue;
                node.WalkStamp=stamp;
                MeasureNode(node,scopes);
                foreach(var child in node.Children)todo.Push(child);
                if(node.Observed!=null)todo.Push(node.Observed);
                if(node.RecipeSource!=null)todo.Push(node.RecipeSource);
            }while(Stopwatch.GetElapsedTime(start).TotalMilliseconds<milliseconds);
            return todo.Count==0 && pendingSeeds.Count==0 && pendingLines.Count==0;
        }
        internal void MeasureNode(AnytimeSearch.Node node,IEnumerable<AnytimeSearch.EvidenceScope> evidenceScopes)
        {
            // The empty sentinel is also present before the first expansion.
            if(node.Parent!=null || node.Children.Count!=0 || node.Frontier!=null || node.Observed!=null)
                structuralBytes+=184+32;
            MeasureMenu(node);
            Ending(node.ReachedEnd);
            if(node.Evidence is {} evidence)
            {
                Evidence(evidence);
                foreach(var scope in evidenceScopes)
                    if(!ReferenceEquals(scope,evidence.Scope) && scope.Other.TryGetValue(node,out var other))
                        Evidence(other);
            }
            Edge(node.Step);
        }

        internal void MeasureMenu(AnytimeSearch.Node node)
        {
            structuralBytes+=Buffer(node.Children.Capacity,8);
            if(node.Legal!=null)structuralBytes+=32+Buffer(node.Legal.Capacity,8);
            node.Frontier?.Measure(this);
        }

        internal void ResetNodeEstimate()
        {
            structuralBytes=largeBytes=sampleBytes=0;
            mask=0;
            payload.Clear();large.Clear();
        }
        // x64 managed layouts, including alignment and allocated collection capacity.
        internal static long Buffer(int capacity,int elementSize)=>capacity==0?0:24+((capacity*(long)elementSize+7)&~7L);
        private static uint Hash(object o)
        {
            uint h=(uint)RuntimeHelpers.GetHashCode(o);
            h^=h>>16;h*=0x7feb352d;h^=h>>15;h*=0x846ca68b;return h^(h>>16);
        }
        // Count shared descriptors by reference. On large trees uniformly sample
        // their identities, keeping the meter's own memory bounded. Unsampled
        // containers still have their children surveyed. Node accounting stays exact.
        internal bool Include(object item,long size)
        {
            if(size>=16384)
            {
                if(!large.Add(item))return false;
                largeBytes+=size;return true;
            }
            if((Hash(item)&mask)!=0)return true;
            if(!payload.TryAdd(item,size))return false;
            sampleBytes+=size;
            if(payload.Count>=4096)
            {
                mask=(mask<<1)|1;
                foreach(var pair in payload)
                    if((Hash(pair.Key)&mask)!=0){sampleBytes-=pair.Value;payload.Remove(pair.Key);}
            }
            return true;
        }
        private void Edge(AnytimeSearch.Edge? edge)
        {
            if(edge!=null && Include(edge,40))
            {
                Step(edge.Exact);
                Saved(edge.Saved);Action(edge.Prior);
            }
        }
        private void Saved(SavedStep? s)
        {
            if(s==null || !Include(s,112))return;
            Text(s.Id);Text(s.Key);Text(s.Fee);Numbers(s.Ordinals);Choices(s.Choices);Turn(s.Turn);Step(s.Recipe);
            if(s.ChoiceLayouts is {} ls && Include(ls,Buffer(ls.Length,8)))foreach(var layout in ls)Layout(layout);
        }
        internal void Actions(IReadOnlyList<KernelTeamSearch.Action> list)
        {
            if(List(list))foreach(var action in list)Action(action);
        }
        private void Action(KernelTeamSearch.Action? a)
        {
            if(a==null || !Include(a,96))return;
            Text(a.CardStateKey);Numbers(a.ChoiceOrdinals);Choices(a.Choices);Turn(a.TurnStartChoices);
        }
        private void Ending(TerminalRecord? record)
        {
            if(record!=null && Include(record,96)){Numbers(record.PostCombatHp);Text(record.CutoffReason);}
        }
        private void Evidence(AnytimeSearch.NodeEvidence evidence)
        {
            structuralBytes+=88;
            Ending(evidence.Local);Ending(evidence.Upper);Ending(evidence.CoverageTarget);Ending(evidence.CompactedUpper);
            if(evidence.Solved is {} sol && Include(sol,32))
            {
                Ending(sol.Ending);
                if(Include(sol.Suffix,Buffer(sol.Suffix.Length,8)))foreach(var edge in sol.Suffix)Edge(edge);
            }
        }
        internal void Text(string? v)
        {if(!string.IsNullOrEmpty(v))Include(v,(22L+v.Length*2L+7)&~7L);}
        internal void Numbers(IReadOnlyList<int>? v)
        {if(v is {Count:>0})Include(v,Buffer(v is List<int> l?l.Capacity:v.Count,4)+(v is int[]?0:32));}
        internal void Move(Move m){if(Include(m,56))Text(m.Key);}
        private void Step(TreeStep? s)
        {if(s!=null && Include(s,40)){Move(s.Move);Numbers(s.Digits);Text(s.MenuEvidence);}}
        private bool List<T>(IReadOnlyList<T> list)=>
            Include(list,Buffer(list is List<T> l?l.Capacity:list.Count,8)+(list is T[]?0:32));
        private void Cards(IReadOnlyList<KernelChoiceCard>? list)
        {
            if(list==null || list.Count==0 || !List(list))return;
            foreach(var c in list)if(Include(c,40)){Text(c.Id);Text(c.StateKey);}
        }
        private void Layout(ChoiceLayout? layout)
        {
            if(layout!=null && Include(layout,32)){Numbers(layout.Source);Numbers(layout.Runs);}
        }
        private void Choices(IReadOnlyList<KernelChoice>? list)
        {
            if(list==null || list.Count==0 || !List(list))return;
            foreach(var c in list)if(Include(c,120))
            {Text(c.Source);Text(c.Context);Text(c.MenuSource);Text(c.Timing);Cards(c.Cards);Cards(c.Options);Layout(ChoiceLayout.For(c));}
        }
        private void Turn(KernelTurnStartChoiceContract? t)
        {
            if(t==null || !Include(t,48))return;
            Include(t.OwnersTurnNumbers,80+32L*t.OwnersTurnNumbers.Count);Choices(t.Choices);
        }
    }
}
