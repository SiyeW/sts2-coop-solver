using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

// Prefer the last verified opening and live routes, then recent tree nodes
// with a known terminal. Probe by action count and a coarse fingerprint.
internal sealed class VerifiedRouteJoins
{
    internal sealed record Route(KernelSession Root,int Start,InteractiveSearch.Line Line,ulong[] Shapes);
    internal sealed class Origin(KernelSession root,AnytimeSearch.Node top)
    {
        internal readonly WeakReference<KernelSession> Root=new(root);
        internal readonly WeakReference<AnytimeSearch.Node> Top=new(top);
    }
    internal sealed record Candidate(Route? Route=null,int Step=0,Origin? Source=null,AnytimeSearch.Node? Node=null);
    private readonly record struct Recent(int Depth,ulong Shape,Origin Origin,WeakReference<AnytimeSearch.Node> Node);
    private const int RecentSlots=2048;
    private Recent?[]? recent;
    private static int Bucket(int depth,ulong shape)
    {
        ulong key=shape ^ unchecked((ulong)(uint)depth*0x9e3779b97f4a7c15UL);
        return (int)((key^(key>>32))&(RecentSlots-1));
    }
    internal void RememberNode(int depth,ulong shape,Origin origin,AnytimeSearch.Node node)
    {
        if(shape==0)return;
        recent??=new Recent?[RecentSlots];
        int slot=Bucket(depth,shape);
        if(recent[slot] is {} old && old.Depth==depth && old.Shape==shape && old.Node.TryGetTarget(out var cached) &&
            (ReferenceEquals(cached,node) || cached.BestTerminal?.ReachedEnd is {Verified:true} &&
             node.BestTerminal?.ReachedEnd is not {Verified:true}))return;
        recent[slot]=new(depth,shape,origin,new(node));
    }
    internal sealed record Count(long Checked,long Matched,long Replayed,long Work);
    private readonly Route?[] routes=new Route?[2];
    private long checks,matches,replays,work;
    internal bool Nearby(int depth)
    {
        if(recent!=null)return true;
        foreach(var route in routes)
            if(route!=null && depth>route.Start && depth<route.Start+route.Shapes.Length)return true;
        return false;
    }

    internal void Remember(bool live,KernelSession root,int start,InteractiveSearch.Line line,ulong[] shapes)
    {
        if(line.Ending is {Verified:true} && line.Steps.Length==shapes.Length && shapes.Length>1)
            routes[live?1:0]=new(root,start,line,shapes);
    }
    internal Candidate[] Find(int depth,ulong shape,AnytimeSearch.Node node)
    {
        List<Candidate>? found=null;
        for(int role=1;role>=0;role--)
        {
            if(routes[role] is not {} route)continue;
            int i=depth-route.Start-1;
            if(i<0 || i>=route.Shapes.Length-1 || route.Shapes[i]!=shape)continue;
            (found??=new()).Add(new(route,i));
        }
        if(recent?[Bucket(depth,shape)] is {} entry && entry.Depth==depth && entry.Shape==shape &&
            entry.Node.TryGetTarget(out var donor) && !ReferenceEquals(donor,node) &&
            donor.BestTerminal?.ReachedEnd is {Verified:true})
            (found??=new()).Add(new(Source:entry.Origin,Node:donor));
        return found?.ToArray()??Array.Empty<Candidate>();
    }
    internal void Checked()=>checks++;
    internal void Matched()=>matches++;
    internal void Replayed()=>replays++;
    internal void Charge(long actions)=>work+=actions;
    internal Count Stats=>new(checks,matches,replays,work);

    internal void Measure(SearchTreeMemory.Survey survey)
    {
        foreach(var route in routes)if(route!=null)Measure(survey,route);
        if(recent!=null)
        {
            if(survey.Include(recent,SearchTreeMemory.Survey.Buffer(recent.Length,40)))
                foreach(var entry in recent)
                    if(entry is {} value)
                    {
                        survey.Include(value.Node,32);
                        survey.Include(value.Origin,96);
                    }
        }
    }
    internal static void Measure(SearchTreeMemory.Survey survey,Route route)
    {
        survey.Board(route.Root);survey.Line(route.Line);
        survey.Include(route.Shapes,SearchTreeMemory.Survey.Buffer(route.Shapes.Length,8));
    }
    internal void ForgetRemovedNodes(object stamp)
    {
        if(recent==null)return;
        for(int i=0;i<recent.Length;i++)
            if(recent[i] is {} item && (!item.Node.TryGetTarget(out var node) || ReferenceEquals(node.WalkStamp,stamp)))
                recent[i]=null;
    }
    internal void Clear()
    {
        recent=null;Array.Clear(routes);checks=matches=replays=work=0;
    }
}

internal sealed partial class AnytimeSearch
{
    private VerifiedRouteJoins? verifiedJoins;
    private bool joinLive,joinRootChecked;
    private int joinBase;
    private VerifiedRouteJoins.Origin? joinOrigin;
    private IEnumerator<bool>? joining;
    private Node? joinNode;
    private KernelSession? joinBoard;
    private VerifiedRouteJoins.Candidate[]? joinCandidates;
    private KernelSession? joinReplayBoard;
    private ulong[] bestJoinShapes=Array.Empty<ulong>();
    internal VerifiedRouteJoins.Count? JoinStats=>verifiedJoins?.Stats;

    private void SetJoinScope(BattleExchange? exchange,BattleExchange.Position? position,bool live)
    {
        joinLive=live;
        joinBase=live?position?.ActionCount??0:0;
        verifiedJoins=exchange!=null && (!live || position!=null)?exchange.VerifiedJoins:new();
    }
    internal void RememberJoinRoute(InteractiveSearch.Line line)
    {
        if(verifiedJoins==null || Best?.Record==null || !ReferenceEquals(line.Ending,Best.Record))return;
        verifiedJoins.Remember(joinLive,root,joinBase,line,bestJoinShapes);
    }
    private void IndexJoinNode(Node node,ulong shape)
    {
        if(verifiedJoins==null)return;
        if(joinOrigin==null || !joinOrigin.Top.TryGetTarget(out var anchor) || !ReferenceEquals(anchor,top))
            joinOrigin=new(root,top);
        verifiedJoins.RememberNode(joinBase+node.Depth-top.Depth,shape,joinOrigin,node);
    }
    private bool QueueJoin(Node node,KernelSession board,ulong shape=0,bool frozen=false)
    {
        int depth=joinBase+node.Depth-top.Depth;
        if(verifiedJoins is not {} cache || joining!=null || !cache.Nearby(depth) ||
            node.BestTerminal?.ReachedEnd is {Verified:true} || board.HasTerminal)return false;
        var matches=cache.Find(depth,shape!=0?shape:ShapeOf(board),node);
        if(matches.Length==0)return false;
        joinNode=node;joinBoard=frozen?board.ForkForReplay():board;joinCandidates=matches;
        joining=ReplayJoin(cache,node,joinBoard,matches).GetEnumerator();
        return true;
    }
    internal void AdvanceJoin()
    {
        if(!joinRootChecked)
        {
            joinRootChecked=true;
            if(joinLive)QueueJoin(top,root,frozen:true);
        }
        if(joining==null)return;
        long before=SimulationMeter.ThreadActions;
        long start=Stopwatch.GetTimestamp();
        var owner=joinNode!;
        try
        {
            do
            {
                if(!joining.MoveNext()){StopJoin();return;}
            }while(Stopwatch.GetElapsedTime(start).TotalMilliseconds<1);
        }
        finally{CreditWork(owner,SimulationMeter.ThreadActions-before);}
    }
    private void StopJoin()
    {
        joining?.Dispose();joining=null;joinNode=null;joinBoard=null;joinCandidates=null;joinReplayBoard=null;
    }
    private IEnumerable<bool> ReplayJoin(VerifiedRouteJoins cache,Node node,KernelSession board,VerifiedRouteJoins.Candidate[] candidates)
    {
        var actual=board.PlanningState(controlled);
        if(!actual.Reusable)yield break;
        foreach(var candidate in candidates)
        {
            if(!ReadDonor(candidate,out var donorRoot,out var proof,out var prefix,out var continuation))continue;
            cache.Checked();
            if(proof==null)
            {
                var old=joinReplayBoard=donorRoot.ForkForReplay();
                bool legal=true;
                foreach(var edge in prefix)
                {
                    long before=SimulationMeter.ThreadActions;
                    legal=edge.Replay(old,out _,out _);
                    cache.Charge(SimulationMeter.ThreadActions-before);
                    yield return false;
                    if(!legal)break;
                }
                joinReplayBoard=null;
                if(!legal)continue;
                proof=old.PlanningState(controlled);
            }
            if(!proof.Matches(actual)){yield return false;continue;}
            cache.Matched();
            var trial=joinReplayBoard=board.ForkForReplay();
            var suffix=new List<Edge>();
            foreach(var edge in continuation)
            {
                if(trial.HasTerminal)break;
                long before=SimulationMeter.ThreadActions;
                bool legal=edge.Replay(trial,out _,out _);
                cache.Charge(SimulationMeter.ThreadActions-before);
                yield return false;
                if(!legal){suffix.Clear();break;}
                suffix.Add(edge);
            }
            joinReplayBoard=null;
            if(suffix.Count==0 || !trial.HasTerminal)continue;
            var ending=WithInventory(TerminalRecord.Capture(trial,party,true,""),trial);
            if(!ending.Verified)continue;
            cache.Replayed();
            Submit(new(Path(node).Concat(suffix).ToArray(),Array.Empty<KernelTeamSearch.Action>(),ending,"verified-join"));
            yield break;
        }
    }
    private static bool ReadDonor(VerifiedRouteJoins.Candidate candidate,out KernelSession donorRoot,
        out KernelPlanningState? proof,out Edge[] prefix,out Edge[] continuation)
    {
        if(candidate.Route is {} pv)
        {
            donorRoot=pv.Root;
            proof=pv.Line.Trail.ElementAtOrDefault(candidate.Step);
            prefix=proof==null?Enumerable.Range(0,candidate.Step+1).Select(i=>JoinEdge(pv.Line,i)).ToArray():Array.Empty<Edge>();
            int first=candidate.Step+1;
            continuation=Enumerable.Range(first,pv.Line.Steps.Length-first).Select(i=>JoinEdge(pv.Line,i)).ToArray();
            return true;
        }
        donorRoot=null!;proof=null;prefix=continuation=Array.Empty<Edge>();
        if(candidate.Source is not {} source || candidate.Node is not {} node ||
            !source.Root.TryGetTarget(out var frozen) || !source.Top.TryGetTarget(out var anchor) ||
            node.BestTerminal is not {} leaf || ToTerminal(node,leaf) is not {Length:>0} suffix ||
            ToTerminal(anchor,node) is not {} path)return false;
        donorRoot=frozen;prefix=path;continuation=suffix;
        return true;
    }
    private static Edge JoinEdge(InteractiveSearch.Line line,int index)
    {
        var recipe=line.Recipes.ElementAtOrDefault(index);
        return new(recipe,recipe==null?line.Steps[index]:null);
    }
}
