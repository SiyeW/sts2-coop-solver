using System;
using System.Collections.Generic;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed partial class AnytimeSearch
{
    internal SharedTreeWalk? MeasuredWalk;
    internal long TreeRevision {get;private set;}
    internal void ProtectRetainedWork(TreeReclamation trim)
    {
        trim.PreserveRoot(top);trim.AddScope(scope);
        trim.Pin(position);trim.Pin(rolloutRoot);trim.Pin(pendingRoundSeed);trim.Pin(joinNode);trim.Pin(humanReplayPosition);trim.Pin(defenseCursor);
        if(joinCandidates!=null)foreach(var donor in joinCandidates)trim.Pin(donor.Node);
        foreach(var mark in anchors)trim.Pin(mark.Node);
        foreach(var n in bestPathNodes)trim.Pin(n);
        foreach(var n in roundOrigins)trim.Pin(n);
        foreach(var n in queuedSeedNodes)trim.Pin(n);
        foreach(var frame in headStack)trim.Pin(frame.Node);
        if(replayToAnchor!=null)foreach(var n in replayToAnchor)trim.Pin(n);
        foreach(var node in roundCursors)trim.Pin(node);
        foreach(var node in roundReplies)trim.Pin(node);
    }
    private void CountReopened(Node node)
    {
        if(!node.Compacted)return;
        node.Compacted=false;
        treeMeter?.CountReopened();
    }
    internal void BeforeReclamation()
    {
        TreeRevision++;MeasuredWalk?.Dispose();
        lock(helperBoards)
        {
            helperGeneration++;
            while(helperResults.TryDequeue(out _)){}
        }
    }
    internal void ForgetRemovedNodes(object stamp)
    {
        boardSlots=0;
        foreach(var (hash,bucket) in boards)
        {
            if(bucket.RemoveAll(n=>ReferenceEquals(n.WalkStamp,stamp))!=0 && bucket.Count<bucket.Capacity/2)
                bucket.TrimExcess();
            if(bucket.Count==0)boards.Remove(hash);
            else boardSlots+=bucket.Capacity;
        }
        boards.TrimExcess();
        if(!InSharedTree)verifiedJoins?.ForgetRemovedNodes(stamp);
    }
    private readonly HashSet<KernelSession> helperBoards=new();
    internal void MeasureSearchStorage(SearchTreeMemory.Survey s)
    {
        long Map(int capacity,int entry)=>SearchTreeMemory.Survey.Buffer(capacity,4)+SearchTreeMemory.Survey.Buffer(capacity,entry);
        s.Owned(Map(boards.EnsureCapacity(0),24)+Map(roundBoards.EnsureCapacity(0),24)+Map(roundShapes.EnsureCapacity(0),24));
        s.Owned((boardSlots+roundBoardSlots)*8+56L*(boards.Count+roundBoards.Count)+(roundAgenda?.ResidentBytes??0));
        s.Owned((long)(roundOrigins.Capacity+bestPathNodes.Capacity+replayedShapes.Capacity+replayed.Capacity+
            replayWork.Capacity+verifyingStates.Capacity+anchors.Capacity)*8);
        s.Board(root);s.Board(working);s.Board(replay);s.Board(rollout?.State);s.Board(savingState);
        foreach(var checkpoint in anchors)s.Board(checkpoint.State);
        foreach(var pending in pendingAnchors)s.Board(pending.State);
        foreach(var entry in entries)s.Board(entry.State);
        s.Board(testingEntry?.State);
        foreach(var frame in headStack)s.Board(frame.State);
        foreach(var state in roundPositions)s.Board(state);
        lock(helperBoards)foreach(var state in helperBoards)s.Board(state);
        s.Rounds(BestRoundHp);
        foreach(var state in BestTrail)s.State(state);
        foreach(var state in verifyingStates)s.State(state);
        foreach(var row in PotionRoutes.Rows)s.Line(row.Value);
        if(joinBoard!=null)s.Board(joinBoard);
        s.Board(joinReplayBoard);
        if(joinCandidates!=null)
            foreach(var candidate in joinCandidates)
                if(candidate.Route!=null)VerifiedRouteJoins.Measure(s,candidate.Route);
        if(!InSharedTree)verifiedJoins?.Measure(s);
        HelperMeter?.Measure(s);MeasuredWalk?.Measure(s);
    }
}
