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
    private readonly SearchTreeMemory.Survey estimate=new(null,Array.Empty<AnytimeSearch>());
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
        if(IsLeaf(node) && (node.Parent==null || pinned.Contains(node.Parent)))return;
        var end=node.BestTerminal?.ReachedEnd;
        double score=node.Visits>0?node.Sum/node.Visits:0;
        int band=node.Evidence?.Full==true?0:node.Evidence?.Solved!=null?1:end is {Victory:true,Verified:true}?3:2;
        candidates.Enqueue(node,(band,score,-node.Depth));
    }

    private static bool IsLeaf(Node node)=>node.Children.Count==0 && node.Frontier==null && node.Legal==null;

    private long EstimateCut(Node keep,long max,bool removeLeaf)
    {
        estimate.ResetNodeEstimate();
        if(removeLeaf)
        {
            estimate.MeasureNode(keep,scopes);
            return estimate.Bytes;
        }
        estimate.MeasureMenu(keep);
        pending.Clear();
        foreach(var child in keep.Children)pending.Push(child);
        while(pending.TryPop(out var node))
        {
            estimate.MeasureNode(node,scopes);
            if(estimate.Bytes>max){pending.Clear();return estimate.Bytes;}
            foreach(var child in node.Children)pending.Push(child);
        }
        return estimate.Bytes;
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
        ForgetMenu(keep);
    }

    private void ForgetLeaf(Node leaf)
    {
        var parent=leaf.Parent!;
        leaf.WalkStamp=Removed;Nodes++;
        parent.Children.Remove(leaf);
        if(parent.Children.Count<parent.Children.Capacity/2)parent.Children.TrimExcess();
        ForgetMenu(parent);
    }

    private void ForgetMenu(Node keep)
    {
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
        long slack=Math.Clamp(target/16,512,2_000_000);
        // A small remainder is not worth evicting higher-ranked branches.
        while(Bytes<target-target/10 && candidates.TryDequeue(out var node,out _))
        {
            bool leaf=IsLeaf(node);
            if(leaf && (node.Parent==null || pinned.Contains(node.Parent)))continue;
            long largest=target-Bytes+slack;
            long size=EstimateCut(node,largest,leaf);
            if(size>largest)
            {
                foreach(var child in node.Children)Offer(child);
                continue;
            }
            if(size==0)continue;
            if(Bytes==0)beforeFirstCut();
            if(leaf)ForgetLeaf(node);else Forget(node);
            Bytes+=size;
        }
        // Terminal links used for route reuse must still point into a retained path.
        foreach(var n in ancestors)
        {
            if(n.BestTerminal is {} old && ReferenceEquals(old.WalkStamp,Removed))
                n.BestTerminal=null;
        }
    }
}
