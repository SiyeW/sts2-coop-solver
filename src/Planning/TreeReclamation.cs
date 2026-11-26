using System;
using System.Collections.Generic;
using Node=CoopSolver.AnytimeSearch.Node;
using Scope=CoopSolver.AnytimeSearch.EvidenceScope;

namespace CoopSolver;

// Retain the incoming edge and its search statistics at each cut. All workers
// that use the graph run on the simulation dispatcher.
internal sealed class TreeReclamation(long target)
{
    internal readonly object Removed=new();
    private readonly object visited=new();
    private readonly HashSet<Node> pinned=new();
    private readonly HashSet<Scope> scopes=new();
    private readonly Stack<Node> pending=new();
    private readonly PriorityQueue<Node,(int Confidence,double Score,int Depth)> candidates=new();
    private readonly HashSet<Node> ancestors=new();
    internal int Nodes {get;private set;}
    internal int Branches {get;private set;}
    internal long Bytes {get;private set;}

    internal void Pin(Node? node)
    {
        for(var n=node;n!=null;n=n.Parent)
        {
            if(!pinned.Add(n))break;
            if(n.Parent==null)pending.Push(n);
        }
    }
    internal void PreserveRoot(Node node)
    {
        pending.Push(node);
        Pin(node);
        Pin(node.BestTerminal);
    }
    internal void AddScope(Scope scope)=>scopes.Add(scope);

    private void DiscoverLinks()
    {
        while(pending.TryPop(out var node))
        {
            if(ReferenceEquals(node.WalkStamp,visited))continue;
            node.WalkStamp=visited;
            if(node.Observed is {} observed){Pin(observed);pending.Push(observed);}
            if(node.RecipeSource is {} recipe){Pin(recipe);pending.Push(recipe);}
            foreach(var child in node.Children)pending.Push(child);
        }
        foreach(var node in pinned)
            foreach(var child in node.Children)
                if(!pinned.Contains(child))Offer(child);
    }

    private void Offer(Node node)
    {
        if(node.Children.Count==0 && node.Frontier==null)return;
        var end=node.BestTerminal?.ReachedEnd;
        double score=node.Parent is {} parent && node.Visits>0?
            AnytimeSearch.SelectionScore(node,parent.Visits):double.PositiveInfinity;
        int band=node.Evidence?.Full==true?0:node.Evidence?.Solved!=null?1:end is {Victory:true,Verified:true}?3:2;
        candidates.Enqueue(node,(band,score,-node.Depth));
    }

    private static long MenuBytes(Node node)=>SearchTreeMemory.Survey.Buffer(node.Children.Capacity,8)+
        (node.Legal==null?0:32+SearchTreeMemory.Survey.Buffer(node.Legal.Capacity,8))+
        (node.Frontier?.RetainedBytes??0);

    private long EstimateCut(Node keep,long max)
    {
        pending.Clear();
        foreach(var child in keep.Children)pending.Push(child);
        long bytes=MenuBytes(keep);
        while(pending.TryPop(out var node))
        {
            bytes+=216+MenuBytes(node)+(node.Step==null?0:96)+(node.Evidence==null?0:88);
            if(bytes>max){pending.Clear();return bytes;}
            foreach(var child in node.Children)pending.Push(child);
        }
        return bytes;
    }

    private void Forget(Node keep)
    {
        foreach(var child in keep.Children)pending.Push(child);
        while(pending.TryPop(out var node))
        {
            node.WalkStamp=Removed;Nodes++;
            foreach(var child in node.Children)pending.Push(child);
        }
        keep.Children.Clear();keep.Children.TrimExcess();
        keep.Frontier=null;keep.Legal=null;keep.Families=0;keep.MenuGap=keep.UnknownMenu=false;
        keep.RecipeCursor=0;
        keep.Compacted=true;Branches++;
        for(Node? n=keep;n!=null;n=n.Parent)
        {
            if(!ancestors.Add(n) && !ReferenceEquals(n,keep))break;
            var f=n.Evidence;
            if(f!=null)ForgetCoverage(f,ReferenceEquals(n,keep));
            foreach(var scope in scopes)
                if(!ReferenceEquals(scope,f?.Scope) && scope.Other.TryGetValue(n,out var other))
                    ForgetCoverage(other,ReferenceEquals(n,keep));
            n.Closed=false;
        }
    }

    private static void ForgetCoverage(AnytimeSearch.NodeEvidence evidence,bool cut)
    {
        if(cut && evidence.Qualified && evidence.Upper!=null)
            evidence.CompactedUpper=evidence.Upper;
        evidence.Full=evidence.Terminal!=null;
        evidence.Upper=evidence.LocalUpper;
        evidence.CoverageTarget=null;evidence.Fraction=0;
    }

    internal void Run(Action beforeFirstCut)
    {
        DiscoverLinks();
        while(Bytes<target && candidates.TryDequeue(out var node,out _))
        {
            long largest=Math.Max(target-Bytes,target/2)*2;
            long size=EstimateCut(node,largest);
            if(size>largest && node.Children.Count>1)
            {
                foreach(var child in node.Children)Offer(child);
                continue;
            }
            if(Bytes==0)beforeFirstCut();
            Forget(node);Bytes+=size;
        }
        // Terminal links used for route reuse must still point into a retained path.
        foreach(var n in ancestors)
        {
            if(n.BestTerminal is {} old && ReferenceEquals(old.WalkStamp,Removed))
                n.BestTerminal=null;
        }
    }
}
