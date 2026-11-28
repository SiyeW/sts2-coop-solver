using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed partial class AnytimeSearch
{
    internal bool FollowEnabled {get;set;}
    internal int ReusedNodes {get;private set;}
    internal int ReusedRoundRows {get;private set;}
    internal bool ReusedSuffix {get;private set;}
    internal bool Recovering=>System.Threading.Volatile.Read(ref recovery)!=null;
    private IEnumerator<bool>? recovery;
    private AnytimeSearch? transferSource;
    private readonly Dictionary<ulong,List<Node>> boards=new();
    private readonly Dictionary<ulong,List<long>> roundBoards=new();
    private readonly Dictionary<long,ulong> roundShapes=new();
    private long boardSlots,roundBoardSlots;
    private readonly List<ulong> replayedShapes=new();
    private readonly List<Node> bestPathNodes=new();
    private readonly long?[] activeRoundIds=new long?[PrefixLane+1];

    // Fast prefilter only. Exact kernel evidence includes physical cards, RNG,
    // turn history, piles, powers, intents, potions and seat readiness.
    private static ulong ShapeOf(KernelSession s)
    {
        ulong hash=1469598103934665603;
        void Add(int n)=>hash=unchecked((hash^(uint)n)*1099511628211);
        Add(s.TurnNumber);
        foreach(var p in s.Party)
        {
            Add(s.Hp(p.Creature));Add(s.Block(p.Creature));Add(s.Energy(p));Add(s.Simulator.State.GetPlayerCombatState(p).Hand.Cards.Count);
            Add(s.IsReady(p)?1:0);Add(s.HeldPotionCount(p));
        }
        foreach(var e in s.Enemies){Add((int)(e.CombatId??0));Add(s.Hp(e));Add(s.Block(e));}
        return hash;
    }
    private void Index(Node n,ulong hash)
    {
        if(n.Shape==hash && hash!=0)return;
        n.Shape=hash;
        if(!boards.TryGetValue(hash,out var group))boards.Add(hash,group=new());
        int capacity=group.Capacity;group.Add(n);boardSlots+=group.Capacity-capacity;
    }
    private void IndexRound(long id,KernelSession s)=>IndexRound(id,ShapeOf(s));
    private void IndexRound(long id,ulong hash)
    {
        roundShapes[id]=hash;
        if(!roundBoards.TryGetValue(hash,out var group))roundBoards.Add(hash,group=new());
        int capacity=group.Capacity;group.Add(id);roundBoardSlots+=group.Capacity-capacity;
    }

    internal void InheritCompatibleWork(AnytimeSearch previous)
    {
        transferSource=previous;
        recovery=Recover(previous).GetEnumerator();
    }
    internal bool ResumeRecovery(System.Diagnostics.Stopwatch watch,TimeSpan slice)
    {
        while(recovery!=null)
        {
            if(!recovery.MoveNext()){recovery.Dispose();recovery=null;break;}
            if(watch.Elapsed>=slice)return false;
        }
        return true;
    }
    private Edge[] DiskPath(long id)
    {
        var path=new Stack<Edge>();
        while(id>=0)path.Push(new Edge(roundAgenda!.ReadStep(id,out id),null));
        return id==-1 ? path.ToArray() : Path(roundOrigins[(int)(-id-2)]).Concat(path).ToArray();
    }

    private IEnumerable<bool> Recover(AnytimeSearch old)
    {
        try
        {
            var players=party.Select(p=>p.NetId).ToArray();
            var current=root.PlanningState(players);
            if(!current.Reusable)yield break;
            ulong hash=ShapeOf(root);
            Node? match=null,recipeMatch=null;
            bool treeRecipesOnly=false;
            bool SameValues(KernelPlanningState predicted)=>predicted.Reusable && current.Reusable &&
                string.Equals(predicted.Semantics,current.Semantics,StringComparison.Ordinal);
            var diskMatches=new Dictionary<long,bool>();
            // Prefer a matching verified principal variation when several paths converge.
            IEnumerable<Node> candidates=old.bestPathNodes.Where(n=>n.Shape==hash);
            if(old.boards.TryGetValue(hash,out var other))candidates=candidates.Concat(other);
            foreach(var n in candidates.Distinct())
            {
                var predicted=old.root.ForkForReplay();
                bool legal=true;
                foreach(var edge in old.Path(n))
                {
                    legal=edge.Replay(predicted,out _,out _);
                    yield return false;
                    if(!legal)break;
                }
                if(legal)
                {
                    var evidence=predicted.PlanningState(players);
                    if(evidence.Matches(current)){match=n;break;}
                    if(recipeMatch==null && SameValues(evidence))recipeMatch=n;
                }
                yield return false;
            }
            if(match==null && recipeMatch!=null){match=recipeMatch;treeRecipesOnly=true;}
            if(old.roundBoards.TryGetValue(hash,out var disk))
            {
                foreach(long id in disk)
                {
                    var predicted=old.root.ForkForReplay();
                    bool legal=true;
                    foreach(var edge in old.DiskPath(id))
                    {
                        legal=edge.Replay(predicted,out _,out _);
                        yield return false;
                        if(!legal)break;
                    }
                    if(legal)
                    {
                        var evidence=predicted.PlanningState(players);
                        if(evidence.Matches(current))diskMatches.Add(id,false);
                        else if(SameValues(evidence))diskMatches.Add(id,true);
                    }
                    yield return false;
                }
            }
            if(match==null && diskMatches.Count==0)yield break;
            var offsets=new Dictionary<long,(long Offset,bool RecipesOnly)>();
            if(match!=null)
            {
                int at=old.bestPathNodes.IndexOf(match);
                if(!treeRecipesOnly && at>=0 && old.Best?.Script is {} line && at+1<line.Count)
                {
                    var rest=line.Skip(at+1).ToArray();
                    var trial=root.ForkForReplay();
                    bool legal=true;
                    foreach(var action in rest)
                    {
                        legal=trial.ReplayPlanned(action,int.MaxValue,out _);
                        yield return false;
                        if(!legal)break;
                    }
                    if(legal && trial.HasTerminal)
                    {
                        var ending=WithInventory(TerminalRecord.Capture(trial,party,true,""),trial);
                        Submit(new(Array.Empty<Edge>(),rest,ending,"live-suffix"));
                        ReusedSuffix=true;
                    }
                }

                match.Parent=null;match.Step=null;match.Depth=0;top=match;
                double neutral=Estimate(root);
                var kept=new HashSet<Node>();
                var todo=new Stack<Node>();todo.Push(match);Nodes=0;
                while(todo.TryPop(out var n))
                {
                    kept.Add(n);Nodes++;ReusedNodes++;
                    if(!treeRecipesOnly)HelperMeter?.Retain(n.DirectWork);else n.DirectWork=n.SubtreeWork=0;
                    // Keep exact continuations and menu cursors. Old return
                    // averages included the old root's health/bottle baselines.
                    n.Visits=1;n.Sum=neutral;
                    if(treeRecipesOnly)
                    {
                        // Value-only paths are hints. A generated card such as Dazed
                        // can be a new native object even with identical pile/RNG state.
                        n.Closed=false;n.BestTerminal=null;n.ReachedEnd=null;n.ResetMenu();
                        n.Children.RemoveAll(c=>c.Step?.Exact==null);
                    }
                    else if(n.Closed){n.Closed=false;if(n.Legal==null)n.ResetMenu();}
                    foreach(var child in n.Children)
                    {
                        child.Depth=n.Depth+1;MaxDepth=Math.Max(MaxDepth,child.Depth);
                        ulong shape=child.Shape;child.Shape=0;Index(child,shape);todo.Push(child);
                    }
                    yield return false;
                }
                for(int i=0;i<old.roundOrigins.Count;i++)
                {
                    var n=old.roundOrigins[i];
                    if(!kept.Contains(n))continue;
                    if(n==match)offsets[-i-2]=(-1,treeRecipesOnly);
                    else{roundOrigins.Add(n);offsets[-i-2]=(-roundOrigins.Count-1,treeRecipesOnly);}
                }
            }
            if(old.roundAgenda!=null && (offsets.Count!=0 || diskMatches.Count!=0))
            {
                roundAgenda=new RoundAgenda();
                foreach(var (id,recipesOnly) in diskMatches)offsets[id]=(-1,recipesOnly);
                var active=old.activeRoundIds.Where(v=>v.HasValue).Select(v=>v!.Value).ToHashSet();
                foreach(var item in old.roundAgenda.Rows())
                {
                    if(!diskMatches.ContainsKey(item.Id) && offsets.TryGetValue(item.Parent,out var entry))
                    {
                        if(item.Cursor)
                        {
                            if(!entry.RecipesOnly && (item.Pending || active.Contains(item.Id)))
                            {
                                long cursor=item.Id;
                                var f=old.roundAgenda.ReadCursor(ref cursor,out var first);
                                roundAgenda.DeferCursor(entry.Offset,f!,first!.Value,item.Turn,0,roundOrder.Next());ReusedRoundRows++;
                            }
                        }
                        else
                        {
                            var step=old.roundAgenda.ReadStep(item.Id,out _);
                            long id=roundAgenda.Append(entry.Offset,step,entry.RecipesOnly || item.Pending || active.Contains(item.Id),item.Turn,0,roundOrder.Next());
                            offsets[item.Id]=(id,entry.RecipesOnly);ReusedRoundRows++;
                            if(!entry.RecipesOnly)HelperMeter?.Retain(1);
                            if(old.roundShapes.TryGetValue(item.Id,out ulong shape))IndexRound(id,shape);
                        }
                    }
                    yield return false;
                }
            }
        }
        finally {old.Dispose();transferSource=null;}
    }
}
