using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

// One instance per attempt. Only immutable action descriptions cross the two searches.
internal sealed partial class BattleExchange
{
    internal sealed record Continuation(SeedRoute Route,int Used);
    internal sealed class Position(TapeGroup? incoming,string witness,int actionCount=0)
    {
        internal readonly TapeGroup? Incoming=incoming;
        internal readonly string Witness=witness;
        internal readonly int ActionCount=actionCount;
        // Offsets point into immutable recipes. Advancing live play never consumes the ancestor's copy.
        internal readonly List<Continuation> Continuations=new();
        internal DeathSaves.Entry? PastRevives;
        internal int PastBottles,PastMaxHpLoss;
        internal bool RevivalSpent,FixedPrefixPrice;
        internal double PastRecovery;
        internal int PrefixRounds;
        internal bool Connected;
        internal readonly HashSet<SimulationMeter.Counter> Counters=new();
        private SeedRoute? best;
        internal AnytimeSearch.Node? Anchor;
        internal KernelPlanningState? ObservedState,WitnessedState;
        internal AnytimeSearch.EvidenceScope? LocalEvidence;
        internal long WitnessedBranchWork;
        internal TreeStep[]? IncomingSteps;
        internal AnytimeSearch.Node? FromPreviousLive;
        internal void Offer(SeedRoute? value){lock(this)best=value;}
        internal SeedRoute? Read(SeedRoute? seen){lock(this)return best==seen?null:best;}
    }
    private readonly List<SpentPotion> spentPotions=new();
    internal SpentPotion[] SpentPotions {get{lock(positions)return spentPotions.ToArray();}}
    private readonly Dictionary<ulong,int> potions=new();
    internal void MeasureRetained(SearchTreeMemory.Survey s)
    {
        lock(positions)
        {
            // A fresh/reset graph contains only the sentinel root.
            if(positions.Count==1 && Tree.Children.Count==0 && confirmedLine==null && promotions.Count==0)return;
            s.Board(Opening);s.State(LatestProof);
            s.Owned(SearchTreeMemory.Survey.Buffer(positions.Capacity,8)+SearchTreeMemory.Survey.Buffer(spentPotions.Capacity,8));
            foreach(var pos in positions)
            {
                s.Owned(144+SearchTreeMemory.Survey.Buffer(pos.Continuations.Capacity,8)+32L*pos.Continuations.Count+32L*pos.Counters.Count);
                s.Text(pos.Witness);s.State(pos.ObservedState);s.State(pos.WitnessedState);s.Seed(pos.Read(null));
                foreach(var cont in pos.Continuations)s.Seed(cont.Route);
            }
            foreach(var pending in promotions)s.Seed(pending.Route);
            s.Line(confirmedLine);s.Line(lastDelivered);VerifiedJoins.Measure(s);
            foreach(var row in openingOptions.Rows)s.Line(row.Value);
            foreach(var pair in menuSuffixes){s.Text(pair.Key);s.Line(pair.Value);}
            foreach(var pair in menuCombinations){s.Text(pair.Key.Witness);s.Text(pair.Key.Combo);s.Line(pair.Value);}
        }
    }
    internal void ProtectRetainedWork(TreeReclamation trim)
    {
        trim.PreserveRoot(Tree);trim.AddScope(Evidence);
        lock(positions)
            foreach(var position in positions)
            {
                trim.Pin(position.Anchor);trim.Pin(position.FromPreviousLive);
                if(position.LocalEvidence!=null)trim.AddScope(position.LocalEvidence);
            }
    }
    internal int UsedPotions(ulong seat){lock(positions)return potions.GetValueOrDefault(seat);}
    private readonly List<Position> positions;
    // A menu result is computed BEFORE the forced input. Save its suffix for
    // the board AFTER that exact answer; a different answer has its own key.
    private readonly Dictionary<string,InteractiveSearch.Line> menuSuffixes=new();
    internal void RememberMenu(string witness,InteractiveSearch.Line line)
    {
        var suffix=line with {Steps=line.Steps[1..],Recipes=line.Recipes.Skip(1).ToArray(),
            Trail=Array.Empty<KernelPlanningState>(),FirstHp=null,Rounds=null,Kills=null,BridgeAfter=0,JoinProof=null,RequireJoin=false};
        if(suffix.Steps.Length==0)return;
        lock(positions)
            if(!menuSuffixes.TryGetValue(witness,out var previous) ||
                AnytimeSearch.ComparePlans(suffix.Ending!,suffix.Steps.Length,previous.Ending!,previous.Steps.Length)>=0)
                menuSuffixes[witness]=suffix;
    }
    private readonly Dictionary<(string Witness,string Combo),InteractiveSearch.Line> menuCombinations=new();
    internal PotionCost WithPrefix(Position? position,PotionCost local)
    {
        if(position==null)return local;
        lock(positions)
        {
            var bottles=new List<SpentPotion>();
            foreach(var point in positions)
            {
                if(point.Incoming!=null)bottles.AddRange(point.Incoming.Actions.Where(a=>a.Kind=="potion").Select(a=>new SpentPotion(a.Seat,a.Id)));
                if(point==position)break;
            }
            for(var e=position.PastRevives;e!=null;e=e.Previous)
                if(e.Event.Potion)bottles.Add(new(e.Event.Seat,e.Event.Source));
            if(bottles.Count==0 && (local.Goal?.Kind!=CombatGoal.MaxHp || position.PastMaxHpLoss==0))return local;
            return local with {Goal=local.Goal is {Kind:CombatGoal.MaxHp} g?g with {Lost=g.Lost+position.PastMaxHpLoss}:local.Goal,Bottles=bottles.Concat(local.Bottles).OrderBy(b=>b.Seat).ThenBy(b=>b.Id,StringComparer.Ordinal).ToArray()};
        }
    }
    internal void RememberMenu(string witness,InteractiveSearch.Line line,PotionCost cost)
    {
        RememberMenu(witness,line);
        var key=(witness,cost.Key);
        lock(positions)
        {
            var candidate=line with {Steps=line.Steps[1..],Recipes=line.Recipes.Skip(1).ToArray(),Trail=Array.Empty<KernelPlanningState>(),
                FirstHp=null,Rounds=null,Kills=null,BridgeAfter=0,JoinProof=null,RequireJoin=false};
            if(candidate.Steps.Length!=0 && (!menuCombinations.TryGetValue(key,out var old) || AnytimeSearch.CompareLines(candidate,old)>=0))
                menuCombinations[key]=candidate;
        }
    }
    internal InteractiveSearch.Line? MenuSuffix(string witness,string? combo)
    { lock(positions)return combo!=null && menuCombinations.TryGetValue((witness,combo),out var line)?line:menuSuffixes.GetValueOrDefault(witness); }
    internal InteractiveSearch.Line? MenuSuffix(string witness)
    {lock(positions)return menuSuffixes.GetValueOrDefault(witness);}
    private SeedRoute? openingRoute;
    internal SeedRoute? OpeningRoute=>System.Threading.Volatile.Read(ref openingRoute);
    internal void PublishOpening(SeedRoute? route)
    {
        lock(positions)
        {
            if(ReferenceEquals(route,openingRoute))return;
            System.Threading.Volatile.Write(ref openingRoute,route);
            if(route?.Terminal==true)Remember(0,route);
        }
    }
    internal void PublishLive(Position position,SeedRoute route)
    {
        lock(positions)
        {
            position.Offer(route);
            if(route.Terminal && route.Won)promotions.Enqueue((position,route));
            int index=positions.IndexOf(position);
            if(index>=0 && route.Terminal)Remember(index,route);
        }
    }
    private static int AdvancePrefix(TapeGroup group,SeedRoute route,int used)
    {
        foreach(var a in group.Actions)
        {
            if(used>=route.Steps.Length)return -1;
            var s=route.Steps[used++];
            bool same=a.Kind switch {
                "end"=>s.Id=="end",
                "potion"=>s.Slot>=0 && s.Slot==a.Slot && s.Id==a.Id,
                "card"=>s.Slot<0 && s.Id==a.Id && (a.Copy==null || a.Copy.Key==s.Key),
                _=>false};
            if(!same || s.Seat!=a.Seat || s.Target!=a.Target)return -1;
        }
        return used;
    }
    // Also pass late opening discoveries down the already played part of the battle.
    private void Remember(int index,SeedRoute route)
    {
        for(int used=0;used>=0 && used<route.Steps.Length;index++)
        {
            var point=positions[index];
            if(point.Continuations.Any(c=>ReferenceEquals(c.Route,route) && c.Used==used))return;
            point.Continuations.Add(new(route,used));
            if(index+1==positions.Count)return;
            used=AdvancePrefix(positions[index+1].Incoming!,route,used);
        }
    }
    internal Continuation[] NewContinuations(Position position,ref int seen)
    {
        lock(positions)
        {
            var routes=position.Continuations;
            if(seen==routes.Count)return Array.Empty<Continuation>();
            var batch=routes.GetRange(seen,routes.Count-seen);
            seen=routes.Count;
            batch.Reverse();
            return batch.OrderByDescending(c=>c.Route.Won).ToArray();
        }
    }
    internal int MatchedPrefix(Position position,SeedRoute route)
    {
        lock(positions)
        {
            int used=0;
            foreach(var point in positions)
            {
                if(point.Incoming is {} group)used=AdvancePrefix(group,route,used);
                if(used<0 || ReferenceEquals(point,position))return used;
            }
            return -1;
        }
    }
    // Try the retained line after the player played a later card early. Only
    // proposes a recipe: the live planner replays it all the way to the end.
    internal SeedRoute? AfterEarlierCard(Position position,SeedRoute route)
    {
        if(position.Incoming?.Actions is not [var played] || played.Kind!="card")return null;
        Position before;
        lock(positions)
        {
            int index=positions.IndexOf(position);
            if(index<=0)return null;
            before=positions[index-1];
        }
        int used=MatchedPrefix(before,route);
        if(used<0 || used>=route.Steps.Length)return null;
        for(int i=used;i<route.Steps.Length;i++)
        {
            var s=route.Steps[i];
            if(s.Id=="end" || s.Slot>=0)break;
            if(s.Seat!=played.Seat || s.Id!=played.Id || s.Target!=played.Target ||
                played.Copy!=null && s.Key!=played.Copy.Key)continue;
            if(i==used)return null; // Already supplied by the exact-prefix path.
            return route with {Steps=route.Steps[used..i].Concat(route.Steps[(i+1)..]).ToArray()};
        }
        return null;
    }
    internal void Track(Position position,SimulationMeter meter)
    {
        lock(positions)position.Counters.Add(meter.Fresh);
    }
    internal void Confirm(Position position){lock(positions)position.Connected=true;}
    internal long AdditionalWork(Position? pos,SimulationMeter meter)
    {
        lock(positions)return pos?.Connected==true && pos.Counters.Contains(meter.Fresh)?0:meter.Fresh.Total;
    }
    private long undoneReadyWork;
    internal long ConnectedWork
    {
        get
        {
            long total=0;
            lock(positions)
            {
                total=undoneReadyWork;
                foreach(var p in positions)if(p.Connected)foreach(var c in p.Counters)total+=c.Total;
            }
            return total;
        }
    }
    private readonly Queue<(Position Position,SeedRoute Route)> promotions=new();
    private IEnumerator<bool>? activePromotion;
    private (Position Position,SeedRoute Route) currentPromotion;
    private bool scheduledPromotion;
    private readonly string? logDirectory;
    internal void PumpPromotions(SimulationMeter? meter)
    {
        lock(positions)
        {
            if(scheduledPromotion || activePromotion==null && promotions.Count==0 || Memory.Waiting())return;
            scheduledPromotion=true;
        }
        InteractiveSearch.Dispatcher.Post(()=>{
            try
            {
                // Previously found lines can finish verification while paused.
                using var count=meter?.Enter();
                if(activePromotion==null)
                {
                    (Position Position,SeedRoute Route) next;
                    lock(positions)next=promotions.Dequeue();
                    currentPromotion=next;
                    activePromotion=matcher.PromoteLive(this,next.Position,next.Route).GetEnumerator();
                }
                long start=System.Diagnostics.Stopwatch.GetTimestamp();
                do
                {
                    if(activePromotion.MoveNext())continue;
                    activePromotion.Dispose();activePromotion=null;break;
                }while(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds<2);
            }
            catch(Exception ex)
            {
                activePromotion?.Dispose();activePromotion=null;Failure(ex.GetBaseException().Message);
            }
            finally{lock(positions)scheduledPromotion=false;}
        },true);
    }
    internal void Failure(string message)
    {
        if(logDirectory==null)return;
        try
        {
            var path=System.IO.Path.Combine(logDirectory,"opening-replay.log");
            System.IO.Directory.CreateDirectory(logDirectory);
            if(!System.IO.File.Exists(path) || new System.IO.FileInfo(path).Length<256*1024)
                System.IO.File.AppendAllText(path,message+Environment.NewLine);
        }
        catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException){}
    }
    private readonly PotionFront<InteractiveSearch.Line> openingOptions=new();
    internal PotionRoute<InteractiveSearch.Line>[] OpeningOptions {get{lock(positions)return openingOptions.Rows;}}
    internal void OfferAlternative(Position position,SeedRoute seed)
    {
        lock(positions){promotions.Enqueue((position,seed));int index=positions.IndexOf(position);if(index>=0)Remember(index,seed);}
    }
    private InteractiveSearch.Line? confirmedLine,lastDelivered;
    internal InteractiveSearch.Line? ConfirmedOpening=>System.Threading.Volatile.Read(ref confirmedLine);
    internal void PublishLine(InteractiveSearch.Line line)
    {
        lock(positions)
        {
            if(line.Ending is {Verified:true})
                openingOptions.Offer(PotionCost.From(Opening,line),line,AnytimeSearch.CompareLines);
            if(confirmedLine==null || AnytimeSearch.ComparePlans(line.Ending!,line.Steps.Length,confirmedLine.Ending!,confirmedLine.Steps.Length,line.Rounds,confirmedLine.Rounds)>0)
                System.Threading.Volatile.Write(ref confirmedLine,line);
        }
    }
    internal InteractiveSearch.Line? TakeLine()
    {
        lock(positions)
        {
            if(ReferenceEquals(confirmedLine,lastDelivered))return null;
            return lastDelivered=confirmedLine;
        }
    }
    internal TreeStep[]? Prefix(Position end)
    {
        var path=new List<TreeStep>();
        lock(positions)foreach(var p in positions)
        {
            if(p.Incoming!=null)
            {
                if(p.IncomingSteps==null)return null;
                path.AddRange(p.IncomingSteps);
            }
            if(p==end)return path.ToArray();
        }
        return null;
    }
    internal SearchMemoryBudget Memory {get;}
    // Accessed on the search dispatcher. A settled descendant whose complete
    // resource result attains the opening optimum needs no second exclusion pass.
    internal TerminalRecord? OpeningOptimum {get;private set;}
    internal void ForgetOpeningBound()=>OpeningOptimum=null;
    internal void CertifyOpening(TerminalRecord ending)=>OpeningOptimum=ending;
    internal readonly AnytimeSearch.EvidenceScope Evidence=new();
    internal readonly VerifiedRouteJoins VerifiedJoins=new();
    internal readonly KernelSession Opening;
    internal AnytimeSearch.Node Tree {get;private set;}
    private AnytimeSearch matcher;
    private IEnumerator<bool>? matching;
    private int tapeRevision,matchingRevision;
    internal KernelPlanningState? LatestProof;
    internal Position? LatestMatch;
    internal long LiveBackups;
    internal BattleExchange(string witness,KernelSession initial,string? directory=null,BattleExchange? previous=null)
    {
        Memory=previous?.Memory??new SearchMemoryBudget();
        logDirectory=directory;Opening=initial;Tree=new(){IsBattleRoot=true};positions=new(){new(null,witness){Anchor=Tree}};
        Memory.Trees.BattleRoot(Tree,this);matcher=new(initial,false,false);
        if(previous!=null)InteractiveSearch.Dispatcher.Post(()=>AdoptOpeningTree(previous));
    }
    private void AdoptOpeningTree(BattleExchange previous)
    {
        // Only the identical opening archive key and witnessed table can enter here.
        // Native action/player objects belong to the old room. Bind portable actions
        // at replay, and enumerate menus and verify endings against the fresh capture.
        var tree=previous.Tree;
        var pending=new Stack<AnytimeSearch.Node>();
        var stamp=new object();
        pending.Push(tree);
        while(pending.TryPop(out var node))
        {
            if(ReferenceEquals(node.WalkStamp,stamp))continue;
            node.WalkStamp=stamp;
            if(node.Step is {Prior:{} action} edge)
                node.Step=new(edge.Exact,null,edge.Saved??SavedStep.From(previous.Opening,action,edge.Exact));
            node.Closed=false;node.HasFreeWin=false;node.ResetMenu();node.RecipeCursor=0;
            foreach(var child in node.Children)pending.Push(child);
            if(node.Observed!=null)pending.Push(node.Observed);
            if(node.RecipeSource!=null)pending.Push(node.RecipeSource);
        }
        Tree=tree;Memory.Trees.BattleRoot(tree,this);
        lock(positions)positions[0].Anchor=tree;
    }
    // Called on the simulation dispatcher after both old workers have been retired.
    internal void ReleaseSearchGraph()
    {
        OpeningOptimum=null;matching?.Dispose();matching=null;matcher.Dispose();VerifiedJoins.Clear();
        lock(positions)
        {
            activePromotion?.Dispose();activePromotion=null;promotions.Clear();
            openingRoute=null;confirmedLine=lastDelivered=null;openingOptions.Clear();
            currentPromotion=default;undoneReadyWork=0;
            menuSuffixes.Clear();menuCombinations.Clear();Tree=new(){IsBattleRoot=true};Memory.Trees.BattleRoot(Tree,this);Memory.Trees.Reset();matcher=new(Opening,false,false);
            LatestMatch=null;LatestProof=null;MatchingFailed=false;
            foreach(var p in positions)
            {
                p.Anchor=null;p.ObservedState=p.WitnessedState=null;p.LocalEvidence=null;p.FromPreviousLive=null;p.WitnessedBranchWork=0;p.Counters.Clear();p.Connected=false;
                p.Continuations.Clear();p.Offer(null);
            }
            positions[0].Anchor=Tree;
        }
    }
    internal bool MatchingFailed {get;private set;}
    internal void Prepare(Position? goal)
    {
        if(goal==null || goal.Anchor!=null)return;
        int revision=System.Threading.Volatile.Read(ref tapeRevision);
        if(matchingRevision!=revision)
        {
            matching?.Dispose();matching=null;MatchingFailed=false;matchingRevision=revision;
        }
        matching??=matcher.MatchPositions(this).GetEnumerator();
        long begin=System.Diagnostics.Stopwatch.GetTimestamp();
        while(goal.Anchor==null && !MatchingFailed)
        {
            if(!matching.MoveNext()){MatchingFailed=true;break;}
            if(System.Diagnostics.Stopwatch.GetElapsedTime(begin).TotalMilliseconds>=3)break;
        }
    }
    internal void Append(TapeGroup group)
    {
        lock(positions)
        {
            var actual=group.Actions;
            if(actual.Any(action=>action.Kind=="undo"))
            {
                // A cancelled readiness flag is not a move in the search tree.
                // Replay the remaining inputs from the retained ancestor and
                // require them to reproduce the new observed board.
                var undone=actual.Where(action=>action.Kind=="undo").Select(a=>(a.Seat,a.ReadyRound)).ToHashSet();
                int from=positions.FindIndex(p=>p.Incoming?.Actions.Any(a=>a.Kind=="end" && undone.Contains((a.Seat,a.ReadyRound)))==true);
                var remaining=new List<TapeAction>();
                var inputs=from<0?actual.AsEnumerable():positions.Skip(from).SelectMany(p=>p.Incoming!.Actions).Concat(actual);
                foreach(var input in inputs)
                {
                    if(input.Kind!="undo"){remaining.Add(input);continue;}
                    int end=remaining.FindLastIndex(a=>a.Kind=="end" && a.Seat==input.Seat && a.ReadyRound==input.ReadyRound);
                    if(end>=0)remaining.RemoveAt(end);
                }
                if(from>=1)
                {
                    for(int i=from;i<positions.Count;i++)
                        if(positions[i].Connected)foreach(var counter in positions[i].Counters)undoneReadyWork+=counter.Total;
                    positions.RemoveRange(from,positions.Count-from);
                    System.Threading.Interlocked.Increment(ref tapeRevision);
                }
                if(remaining.Count==0 && positions[^1].Witness==group.Witness)return;
                group=group with {Actions=remaining.ToArray()};
            }
            var next=new Position(group,group.Witness,positions[^1].ActionCount+group.Actions.Length);
            foreach(var candidate in positions[^1].Continuations)
            {
                int used=AdvancePrefix(group,candidate.Route,candidate.Used);
                if(used>=0 && used<candidate.Route.Steps.Length)next.Continuations.Add(new(candidate.Route,used));
            }
            positions.Add(next);
            foreach(var action in actual)if(action.Kind=="potion"){spentPotions.Add(new(action.Seat,action.Id));potions[action.Seat]=potions.GetValueOrDefault(action.Seat)+1;}
        }
    }
    internal Position? At(int i){lock(positions)return i<positions.Count?positions[i]:null;}
    internal Position? Current(string witness){lock(positions)return positions[^1].Witness==witness?positions[^1]:null;}
}

internal sealed partial class AnytimeSearch
{
    private IEnumerator<bool>? bridgeReader;
    private BattleExchange? battle;
    private bool focusCursor;
    private bool sameOpeningObjective;
    private BattleExchange.Position? comparablePrefix;
    private TerminalRecord OpeningScore(TerminalRecord ending)=>comparablePrefix is {} previous?ending with {
        ResourcesConsumed=ending.ResourcesConsumed+previous.PastBottles,
        ConsumablePreservationPremium=ending.ConsumablePreservationPremium+previous.PastRecovery,
        RoundsSurvived=ending.RoundsSurvived-root.RoundsAdvanced+previous.PrefixRounds}:ending;
    internal bool MeetsOpeningBound(TerminalRecord ending)=>comparablePrefix!=null &&
        ending is {Victory:true,Verified:true} && battle?.OpeningOptimum is {} bound && Compare(OpeningScore(ending),bound)==0;
    internal bool InSharedTree=>battle!=null;
    internal long SharedBackups=>battle?.LiveBackups??0;
    internal int SharedDepth=>focusCursor?top.Depth:0;
    private long inheritedBranchWork;
    internal long FocusWork=>focusCursor?Math.Max(inheritedBranchWork,System.Threading.Interlocked.Read(ref top.SubtreeWork)):0;
    private double inheritedReviveCost;
    private int inheritedBottlesUsed;
    private DeathSaves.Entry? inheritedRevives;
    private int baselineBottles,baselineEnemyHp,baselineEnemies;

    internal IEnumerable<bool> MatchPositions(BattleExchange exchange)
    {
        var state=exchange.Opening.ForkForReplay();var node=exchange.Tree;
        for(int index=1;;index++)
        {
            var next=exchange.At(index);
            if(next?.Incoming==null || exchange.At(index-1) is not {} previous)yield break;
            KernelSession? matched=null;string? matchFailure=null;
            foreach(var tick in MatchGroup(state,next.Incoming,(s,edges)=>{
                matched=s;next.IncomingSteps=edges.Select(e=>e.Exact!).ToArray();
                var observed=previous.Anchor?.Observed??previous.FromPreviousLive;
                if(observed!=null)next.FromPreviousLive=Descend(observed,edges);
                node=Descend(node,edges);},reason=>matchFailure=reason))yield return tick;
            if(!ReferenceEquals(exchange.At(index),next))yield break;
            if(matched==null)
            {
                var inputs=string.Join(", ",next.Incoming.Actions.Select(a=>$"{a.Seat}/{a.Kind}/{a.Id}/slot={a.Slot}/target={a.Target}"));
                exchange.Failure($"From turn {state.TurnNumber}, input group {index} ({inputs}): {matchFailure}; expected board {next.Witness}.");
                yield break;
            }
            state=matched;
            var proof=state.PlanningState(controlled);
            next.WitnessedState=proof;
            exchange.LatestProof=proof;exchange.LatestMatch=next;
            next.WitnessedBranchWork=Math.Max(System.Threading.Interlocked.Read(ref node.SubtreeWork),
                next.FromPreviousLive==null?0:System.Threading.Interlocked.Read(ref next.FromPreviousLive.SubtreeWork));
            next.PastRevives=DeathSaves.Read(state);next.PastBottles=DeathSaves.UsedBottles(state)-DeathSaves.UsedBottles(exchange.Opening);
            next.FixedPrefixPrice=ProofEvidence.Supported(exchange.Opening,state);
            next.PastMaxHpLoss=exchange.Opening.Party.Sum(p=>Math.Max(0,exchange.Opening.MaxHp(p.Creature)-state.MaxHp(p.Creature)));
            next.PastRecovery=DeathSaves.RecoverySince(exchange.Opening,state);
            next.PrefixRounds=state.RoundsAdvanced-exchange.Opening.RoundsAdvanced;
            next.RevivalSpent=DeathSaves.Restored(state)!=DeathSaves.Restored(exchange.Opening);
            next.Anchor=node;exchange.Confirm(next);
            yield return true;
        }
    }
    private static Node Descend(Node start,IEnumerable<Edge> path)
    {
        var n=start;
        foreach(var edge in path)
        {
            var child=FindChild(n,edge);
            if(child==null){child=new(n,edge){Visits=1};n.Children.Add(child);for(var a=n;a!=null && a.Closed;a=a.Parent)a.Closed=false;}
            n=child;
        }
        return n;
    }
    private static Node? FindChild(Node parent,Edge edge)=>parent.Children.FirstOrDefault(c=>edge.Matches(c.Step));

    internal int OpeningContinuations {get;private set;}
    internal int LiveInputs {get;private set;}
    internal int LivePlans {get;private set;}
    internal int LiveBranches {get;private set;}
    internal void Connect(BattleExchange? exchange,BattleExchange.Position? origin,bool follow)
    {
        SetJoinScope(exchange,origin,follow);
        if(exchange==null)return;
        if(follow)
        {
            if(origin?.Anchor is {} anchor)
            {
                var actual=root.PlanningState(controlled);
                var proof=origin.Incoming==null?exchange.Opening.PlanningState(controlled):origin.WitnessedState;
                if(Environment.GetEnvironmentVariable("STS2_TURN_WORK")!=null)
                    Godot.GD.Print($"[RoundCarry] path={anchor.Depth}, subtree={anchor.SubtreeWork}, previous-focus={origin.FromPreviousLive?.SubtreeWork}, {proof?.FirstMismatch(actual)}");
                if(proof?.Matches(actual)==true)
                {
                    top=anchor;
                    if(origin.FixedPrefixPrice && root.Party.All(p=>root.CapturedPotions(p).All(exchange.Opening.CapturedPotions(p).Contains)))
                        comparablePrefix=origin;
                    sameOpeningObjective=origin.PastRecovery==0 && origin.PastBottles==0 && !origin.RevivalSpent && origin.PastRevives==null &&
                        root.Party.All(p=>exchange.Opening.CapturedPotions(p).SequenceEqual(root.CapturedPotions(p)));
                }
                else
                {
                    // Same witnessed real prefix, different hidden evidence. The observed
                    // state gets its own expansion cursor. Its evaluations still back up
                    // to the witnessed ancestor. Recipes cross this boundary by replay.
                    if(anchor.Observed==null || origin.ObservedState?.Matches(actual)!=true)
                    {
                        var hints=anchor.Observed??origin.FromPreviousLive??anchor;
                        anchor.Observed=new Node(anchor){Depth=anchor.Depth,Visits=1,RecipeSource=hints};
                        origin.ObservedState=actual;origin.LocalEvidence=null;
                    }
                    top=anchor.Observed;
                    for(Node? a=anchor;a!=null;a=a.Parent)a.Closed=false;
                }
                // Count the work under the reproduced action path exactly once. An
                // observation cursor still rechecks recipes before adopting their state.
                inheritedBottlesUsed=origin.PastBottles;inheritedRevives=origin.PastRevives;inheritedReviveCost=origin.PastRecovery;
                inheritedBranchWork=origin.WitnessedBranchWork;focusCursor=true;
            }
            if(origin!=null)bridgeReader=AcceptOpening(exchange,origin).GetEnumerator();
        }
        else {top=exchange.Tree;bridgeReader=AcceptLive(exchange).GetEnumerator();}
        if(!follow || sameOpeningObjective){scope=exchange.Evidence;scopeRound=follow?origin!.PrefixRounds:0;}
        else if(focusCursor)scope=origin!.LocalEvidence??=new();
        if(!follow || focusCursor)
        {
            battle=exchange;
            var initial=exchange.Opening;
            baselineBottles=initial.Party.Sum(initial.HeldPotionCount);
            baselineEnemyHp=Math.Max(1,initial.RemainingOpposition().Hp);baselineEnemies=Math.Max(1,initial.Enemies.Count);
        }
    }
    internal void AdvanceBridge()
    {
        if(bridgeReader==null)return;
        long start=System.Diagnostics.Stopwatch.GetTimestamp();
        do
        {
            if(!bridgeReader.MoveNext()){bridgeReader.Dispose();bridgeReader=null;return;}
        }while(bridgeReader.Current && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds<2);
    }
    internal IEnumerable<bool> PromoteLive(BattleExchange exchange,BattleExchange.Position position,SeedRoute seed)
    {
        while(position.Anchor==null && !exchange.MatchingFailed)
        {exchange.Prepare(position);yield return true;}
        var prefix=exchange.Prefix(position);
        if(prefix==null)yield break;
        var state=exchange.Opening.ForkForReplay();
        var events=new RouteDeaths(state);var risks=new HashSet<string>();
        var actions=new List<KernelTeamSearch.Action>();var recipes=new List<TreeStep?>();int[]? firstHp=null;
        for(int i=0;i<prefix.Length+seed.Steps.Length;i++)
        {
            if(state.HasTerminal){exchange.Failure($"Turn {state.TurnNumber}, step {i+1}: combat ended before the recorded route.");yield break;}
            if(i==prefix.Length && SlCache.Witness(state)!=position.Witness)
            {exchange.Failure($"Turn {state.TurnNumber}, step {i+1}: actual board {SlCache.Witness(state)}, expected {position.Witness}.");yield break;}
            var saved=i>=prefix.Length?seed.Steps[i-prefix.Length]:null;
            var recipe=i<prefix.Length?prefix[i]:saved!.Recipe;
            KernelTeamSearch.Action? actual=null;
            var child=recipe==null?state.ForkForReplay():state;
            if(recipe!=null && saved!=null)recipe=ActionFrontier.RebindEquivalent(child,recipe,saved.Occurrence);
            var input=recipe==null?saved!.Bind(child):null;
            bool ok=ActionFrontier.ReplayRecorded(state,ref child,input,ref recipe,out actual,out var reason);
            ReplayCoverage.Collect(child,risks);
            state=child;
            if(!ok){exchange.Failure($"Turn {state.TurnNumber}, step {i+1} ({saved?.Id??recipe?.Move.Key}): {reason}");yield break;}
            actions.Add(actual!);recipes.Add(recipe);events.Observe(state,i,actual);
            if(firstHp==null && (state.HasTerminal || state.TurnNumber!=exchange.Opening.TurnNumber))
                firstHp=party.Select(p=>state.Hp(p.Creature)).ToArray();
            yield return true;
        }
        var bottles=party.Sum(state.HeldPotionCount);
        var hp=party.Select(p=>state.Hp(p.Creature)).ToArray();
        if(!state.HasTerminal || !state.HasWon || state.TurnNumber!=seed.Turn || !hp.SequenceEqual(seed.Hp) ||
            bottles!=seed.Bottles || state.RemainingOpposition()!=(seed.EnemyCount,seed.EnemyHp))
        {
            exchange.Failure($"End after {actions.Count} steps: turn {state.TurnNumber}/{seed.Turn}, win {state.HasWon}/{seed.Won}, HP {string.Join(",",hp)}/{string.Join(",",seed.Hp)}, bottles {bottles}/{seed.Bottles}, enemies {state.RemainingOpposition()}/({seed.EnemyCount},{seed.EnemyHp}).");
            yield break;
        }
        Descend(exchange.Tree,actions.Select((a,i)=>new Edge(recipes[i],recipes[i]==null?a:null)));
        var ending=DeathSaves.WithInventory(TerminalRecord.Capture(state,party,true,""),exchange.Opening,state);
        exchange.PublishLine(new(actions.ToArray(),Array.Empty<KernelPlanningState>(),ending,state.TurnNumber,
            ending.PostCombatHp.ToArray(),ending.RemainingEnemies,ending.RemainingEnemyHp,bottles,ReplayCoverage.RouteDetails(risks.ToArray(),actions),0,
            exchange.ConnectedWork,Nodes,firstHp,recipes.ToArray(),Kills:events.Freeze(),Rounds:events.Rounds()));
    }
    private IEnumerable<bool> AcceptOpening(BattleExchange exchange,BattleExchange.Position position)
    {
        SeedRoute? checkedRoute=null;
        int seen=0;
        while(true)
        {
            foreach(var continuation in exchange.NewContinuations(position,ref seen))
            {
                // A recipe match only makes the suffix a candidate. Replay resolves actual choices,
                // hidden state, and the terminal result before exposing it as a recommendation.
                var saved=continuation.Route;
                var suffix=continuation.Used==0?saved:saved with {Steps=saved.Steps[continuation.Used..]};
                foreach(var tick in ReplaySeed(suffix,root,top,()=>OpeningContinuations++,fromOpening:true))yield return true;
            }
            var route=exchange.OpeningRoute;
            if(route!=null && !ReferenceEquals(route,checkedRoute))
            {
                checkedRoute=route;
                int used=exchange.MatchedPrefix(position,route);
                var suffix=used<0?exchange.AfterEarlierCard(position,route):
                    !route.Terminal && used<route.Steps.Length?route with {Steps=route.Steps[used..]}:null;
                if(suffix!=null)
                    foreach(var tick in ReplaySeed(suffix,root,top,()=>OpeningContinuations++,fromOpening:true))yield return true;
            }
            yield return false;
        }
    }
    private IEnumerable<bool> AcceptLive(BattleExchange exchange)
    {
        var state=root.ForkForReplay();var node=top;int index=0;
        var position=exchange.At(0)!;
        if(SlCache.Witness(state)!=position.Witness)yield break;
        exchange.Confirm(position);
        SeedRoute? seen=null;
        while(true)
        {
            var best=position.Read(seen);
            if(best!=null)
            {
                seen=best;
                foreach(var tick in ReplaySeed(best,state,node,()=>{if(index>0)LivePlans++;}))yield return true;
            }
            var next=exchange.At(index+1);
            exchange.Prepare(next);
            if(next!=null)
            {
                if(next.IncomingSteps==null || next.Anchor==null){yield return false;continue;}
                foreach(var step in next.IncomingSteps)
                {
                    if(!ActionFrontier.Replay(state,step,out _,out _))yield break;
                    yield return true;
                }
                if(SlCache.Witness(state)!=next.Witness)yield break;
                node=next.Anchor;LiveInputs+=next.IncomingSteps.Length;
                position=next;index++;seen=null;
            }
            yield return false;
        }
    }
}
