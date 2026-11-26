using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

// A traversal policy over the planner's retained legal edges. It owns only
// its position and replay checkpoints; there is no second action enumerator.
internal sealed class SharedTreeWalk : IDisposable
{
    private sealed class Frame(AnytimeSearch.Node node,KernelSession? checkpoint)
    {
        internal readonly AnytimeSearch.Node Node=node;
        internal readonly KernelSession? Checkpoint=checkpoint;
        internal int Next;
    }
    private readonly KernelSession root;
    private readonly AnytimeSearch planner;
    private readonly bool exhaustive;
    internal bool ComparingKnownRoutes {get;}
    private readonly AnytimeSearch.Node startNode;
    private readonly long treeRevision;
    internal bool AtCurrentRoot=>treeRevision==planner.TreeRevision && ReferenceEquals(startNode,planner.MemoryRoot);
    private IEnumerator<bool>? traversal;
    private List<Frame>? checkpoints;
    internal void Measure(SearchTreeMemory.Survey survey)
    {
        if(checkpoints==null)return;
        survey.Owned(32+SearchTreeMemory.Survey.Buffer(checkpoints.Capacity,8)+32L*checkpoints.Count);
        foreach(var frame in checkpoints)survey.Board(frame.Checkpoint);
    }
    private bool blocked;
    internal bool Blocked=>blocked;
    internal bool Finished=>traversal==null;
    internal bool Complete {get;private set;}

    internal SharedTreeWalk(AnytimeSearch planner,KernelSession root,bool exhaustive,bool comparingKnownRoutes=false)
    {
        this.planner=planner;this.root=root;this.exhaustive=exhaustive;startNode=planner.MemoryRoot;
        treeRevision=planner.TreeRevision;ComparingKnownRoutes=comparingKnownRoutes;
        planner.MeasuredWalk=this;
        traversal=(comparingKnownRoutes?CompareKnownRoutes():Explore()).GetEnumerator();
    }
    internal void Advance(TimeSpan slice)
    {
        if(traversal==null)return;
        var clock=Stopwatch.StartNew();
        try
        {
            do{if(!traversal.MoveNext()){Dispose();return;}}while(clock.Elapsed<slice);
        }
        catch(Exception e) when(e is InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {blocked=true;Dispose();}
    }

    // All legal endings are already recorded. Only compare candidate routes
    // that can improve the remaining tie-breaks; this never expands a menu.
    private IEnumerable<bool> CompareKnownRoutes()
    {
        var stack=new Stack<(AnytimeSearch.Node Node,int Next)>();
        stack.Push((startNode,0));
        while(stack.TryPop(out var frame))
        {
            var f=planner.EvidenceAt(frame.Node);
            if(!f.Full){blocked=true;yield break;}
            if(f.Terminal is {} ending)
            {
                foreach(var tick in planner.VerifyLeaf(frame.Node,ending,true,()=>blocked=true))yield return tick;
            }
            else if(frame.Next<frame.Node.Legal!.Count)
            {
                stack.Push((frame.Node,frame.Next+1));stack.Push((frame.Node.Legal[frame.Next],0));
            }
            yield return true;
        }
        Complete=!blocked && planner.AllTerminals && planner.Best!=null;
    }

    private IEnumerable<bool> Explore()
    {
        var top=planner.MemoryRoot;
        planner.ReadState(top,root);
        if(!planner.EvidenceAt(top).Qualified){blocked=true;yield break;}
        var path=new List<TreeStep>();
        if(root.HasTerminal)
        {
            foreach(var tick in planner.VerifyLeaf(top,planner.EvidenceAt(top).Terminal!,exhaustive,()=>blocked=true))yield return tick;
            Complete=!blocked;yield break;
        }
        planner.OpenMenu(top,root);
        int previousLimit=0,cutoffLeaves=0;
        for(int limit=8;;)
        {
            bool truncated=false;
            int stride=1+(limit-1)/8;
            var stack=checkpoints=new List<Frame>{new(top,root)};
            long revision=planner.MemoryScope.Revision;
            while(stack.Count!=0)
            {
                // A live worker may have solved an ancestor while this walk was
                // yielded inside it. Jump back to its receipt on the shared path.
                if(revision!=planner.MemoryScope.Revision)
                {
                    revision=planner.MemoryScope.Revision;
                    int solved=stack.FindIndex(f=>planner.EvidenceAt(f.Node).Solved!=null);
                    if(solved>=0 && solved+1<stack.Count)
                    {
                        stack.RemoveRange(solved+1,stack.Count-solved-1);
                        path.RemoveRange(solved,path.Count-solved);
                    }
                }
                var frame=stack[^1];var node=frame.Node;
                if(planner.EvidenceAt(node).Solved is {} receipt)
                {
                    foreach(var tick in planner.UseSolution(node,receipt,()=>blocked=true))yield return tick;
                    stack.RemoveAt(stack.Count-1);if(path.Count>0)path.RemoveAt(path.Count-1);
                    yield return true;continue;
                }
                var target=exhaustive?null:planner.Target;
                if(target!=null && planner.Proves(target))yield break;
                if(frame.Next>=node.Legal!.Count && node.Frontier!.Finished ||
                    target!=null && planner.EvidenceAt(node).Upper is {} cap && AnytimeSearch.Compare(cap,target)<=0)
                {
                    blocked|=node.MenuGap;
                    planner.RefreshBounds(node);
                    stack.RemoveAt(stack.Count-1);
                    if(path.Count>0)path.RemoveAt(path.Count-1);
                    yield return true;continue;
                }

                AnytimeSearch.Node? child=frame.Next<node.Legal.Count?node.Legal[frame.Next]:null;
                var cached=child==null?null:planner.EvidenceAt(child);
                if(cached?.Inspected==true)
                {
                    if(cached.Solved is {} known)
                    {
                        foreach(var tick in planner.UseSolution(child!,known,()=>blocked=true))yield return tick;
                        frame.Next++;yield return true;continue;
                    }
                    if(!cached.Qualified){blocked=true;frame.Next++;yield return true;continue;}
                    if(target!=null && cached.Upper is {} upper && AnytimeSearch.Compare(upper,target)<=0)
                    {frame.Next++;yield return true;continue;}
                    if(path.Count+1==limit && cached.Terminal==null)
                    {truncated=true;frame.Next++;yield return true;continue;}
                    if(cached.Terminal is {} ending)
                    {
                        frame.Next++;path.Add(child!.Step!.Exact!);
                        if(path.Count>previousLimit || !exhaustive)
                            foreach(var tick in planner.VerifyLeaf(child!,ending,exhaustive,()=>blocked=true))yield return tick;
                        path.RemoveAt(path.Count-1);yield return true;continue;
                    }
                }

                int anchor=stack.Count-1;
                while(stack[anchor].Checkpoint==null)anchor--;
                var next=stack[anchor].Checkpoint!.ForkForReplay();
                bool restored=true;
                for(int i=anchor;i<path.Count;i++)
                {
                    long before=SimulationMeter.ThreadActions;
                    if(!ActionFrontier.Replay(next,path[i],out _,out _)){restored=false;break;}
                    planner.ChargeReplay(stack[i+1].Node,before);
                    yield return true;
                }
                if(!restored){blocked=true;yield break;}
                if(child==null)
                {
                    long before=SimulationMeter.ThreadActions;
                    if(!planner.ExpandMenu(node,next,out child,out _,out _,out _))
                    {blocked=true;yield return true;continue;}
                    planner.ChargeExpansion(node,child!,before);
                }
                else
                {
                    long before=SimulationMeter.ThreadActions;
                    if(!child.Step!.Replay(next,out _,out _)){planner.RejectState(child);blocked=true;frame.Next++;yield return true;continue;}
                    planner.ChargeReplay(child,before);
                    planner.ReadState(child,next);
                }
                frame.Next++;
                var evidence=planner.EvidenceAt(child!);
                if(!evidence.Qualified){blocked=true;yield return true;continue;}
                path.Add(child!.Step!.Exact!);
                if(evidence.Terminal is {} terminal)
                {
                    if(path.Count>previousLimit || !exhaustive)
                        foreach(var tick in planner.VerifyLeaf(child,terminal,exhaustive,()=>blocked=true))yield return tick;
                }
                else if(target==null || evidence.Upper==null || AnytimeSearch.Compare(evidence.Upper,target)>0)
                {
                    if(path.Count==limit)
                    {
                        truncated=true;
                        // This branch is no longer needed by the traversal. An idle worker
                        // can try its continuation without copying or changing a checkpoint.
                        if(SearchCapacity.Limit>1 && planner.Best?.Record?.Victory!=true && ++cutoffLeaves%8==1)
                            planner.TryExtraRollout(child,next);
                    }
                    else
                    {
                        planner.OpenMenu(child,next);
                        stack.Add(new(child,path.Count%stride==0?next:null));
                        yield return true;continue;
                    }
                }
                path.RemoveAt(path.Count-1);yield return true;
            }
            if(!truncated){Complete=exhaustive && !blocked && planner.AllTerminals && planner.Best!=null;yield break;}
            if(limit==int.MaxValue)yield break;
            previousLimit=limit;limit=limit>int.MaxValue/2?int.MaxValue:limit*2;
        }
    }

    public void Dispose(){traversal?.Dispose();traversal=null;checkpoints=null;if(ReferenceEquals(planner.MeasuredWalk,this))planner.MeasuredWalk=null;}
}
