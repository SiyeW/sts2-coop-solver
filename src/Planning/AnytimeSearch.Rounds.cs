using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed partial class AnytimeSearch
{
    internal sealed record RoundPath(RoundPath? Previous,TreeStep Step);
    private readonly Random roundOrder;
    private readonly IEnumerator<bool>?[] roundWorkers=new IEnumerator<bool>?[PrefixLane+1];
    private readonly List<Node> roundOrigins=new();
    private RoundAgenda? roundAgenda;
    private bool rootStarted;
    private int roundLane,roundStarts,restoredParents;
    private readonly KernelSession?[] roundPositions=new KernelSession?[PrefixLane+1];
    private readonly Node?[] roundCursors=new Node?[PrefixLane+1],roundReplies=new Node?[PrefixLane+1];
    private readonly bool[] coveredWorkers=new bool[3];
    private readonly int[] tailWait=new int[3];
    private Node? pendingRoundSeed;
    internal int TreeSeededPasses {get;private set;}
    internal int SettledPlans {get;private set;}
    internal int FarthestRootTurn {get;private set;}
    internal int FarthestTurn {get;private set;}
    internal long RoundTicks;
    internal long DeferredRoundJobs=>roundAgenda?.Pending??0;
    internal long RoundFileBytes=>roundAgenda?.Bytes??0;
    internal string? RoundStorageError {get;private set;}
    internal bool HasRoundWork=>RoundStorageError==null && roundAgenda?.HasDiskSpace()!=false && (!rootStarted || queuedSeedNodes.Count!=0 || DeferredRoundJobs!=0 || roundWorkers.Any(w=>w!=null));
    internal Action<TreeStep>? RootBranchReopened;
    internal Action<TreeStep,int,bool>? RootChoiceObserved;
    internal Action<TreeStep,KernelTeamSearch.Action>? RootReplyObserved;
    internal Action<object>? RoundSample;

    private static Edge[] RoundRoute(Edge[] prefix,RoundPath? path)
    {
        var suffix=new Stack<Edge>();
        for(;path!=null;path=path.Previous)suffix.Push(new Edge(path.Step,null));
        return prefix.Concat(suffix).ToArray();
    }

    // Settle a concrete "end here" continuation, including real enemy actions,
    // extra turns and recorded next-turn menus. Never mutate the in-turn prefix.
    internal bool CloseRound(KernelSession state,int from,int firstSeat,ref RoundPath? path)
    {
        for(int i=0;i<party.Length && !state.HasTerminal && state.RoundsAdvanced==from;i++)
        {
            int seat=(firstSeat+i)%party.Length;
            if(!state.CanAct(party[seat]))continue;
            var move=new Move(seat,MoveKind.End,-1,"",null);
            if(!ActionFrontier.Apply(state,move,Array.Empty<int>(),out _,out var evidence,null,out var reason))
            {Boundary(reason);return false;}
            path=new RoundPath(path,new TreeStep(move,Array.Empty<int>(),evidence));
        }
        return state.HasTerminal || state.RoundsAdvanced>from;
    }

    private void OfferRoundEnding(KernelSession state,Edge[] prefix,RoundPath? path)
    {
        var ending=WithInventory(TerminalRecord.Capture(state,party,true,""),state);
        // A primary-score tie can still finish a verified win sooner.
        if(!CollectPotions && Best?.Record is {} old && (Compare(ending,old)<0 || !ending.Victory && Compare(ending,old)==0))return;
        Submit(new(RoundRoute(prefix,path),Array.Empty<KernelTeamSearch.Action>(),ending,"round-search"));
    }

    private void CountRound(KernelSession state,bool fromRoot)
    {
        SettledPlans++;
        FarthestTurn=Math.Max(FarthestTurn,state.TurnNumber);
        if(fromRoot)FarthestRootTurn=Math.Max(FarthestRootTurn,state.TurnNumber);
    }

    // Rank the particular path that actually earned the boundary score. A
    // menu parent must not inherit a free bonus from the number of replies.
    private long StoreRoundAction(long parentId,RoundPath path,KernelSession after,int from,int firstSeat,
        bool queueAfter,bool fromRoot,Edge[] prefix,int startTurn,long expansionWork,out bool closed,out double score)
    {
        bool partial=!after.HasTerminal && after.RoundsAdvanced==from;
        var settled=partial ? after.ForkForReplay() : after;
        RoundPath? closure=path;
        long before=SimulationMeter.ThreadActions;
        closed=CloseRound(settled,from,firstSeat,ref closure);
        score=Estimate(closed?settled:after);
        if(InSharedTree || TrackEvidence)
        {
            var afterNode=Descend(top,RoundRoute(prefix,path));
            CreditWork(afterNode,expansionWork);
            var pathNode=ReferenceEquals(path,closure) || !closed?afterNode:Descend(top,RoundRoute(prefix,closure));
            long settlement=SimulationMeter.ThreadActions-before;
            CreditWork(afterNode,settlement);
            AttributeCommittedMoves(afterNode,pathNode,pathNode.Depth-afterNode.Depth,settlement);
            if(TrackEvidence){ReadState(afterNode,after);if(closed)ReadState(pathNode,settled);}
            if(settled.HasTerminal)RememberTerminal(pathNode,WithInventory(TerminalRecord.Capture(settled,party,true,""),settled));
            Record(pathNode,score);
            if(!Simplifying && closed && startTurn<=lastHurtTurn && !settled.HasTerminal && settled.TurnNumber>startTurn)
                OfferEntry(pathNode,settled,score);
            if(focusCursor)LiveBranches++;
        }
        if(closed)
        {
            CountRound(settled,fromRoot);
            if(settled.HasTerminal)OfferRoundEnding(settled,prefix,closure);
        }
        if(after.HasTerminal)return -1;
        double? prefixScore=null;
        if(!Simplifying && lastHurtTurn>0 && closed && startTurn<=lastHurtTurn && Best?.Record is {Victory:true})
        {
            var resources=WithInventory(TerminalRecord.Capture(settled,party,false,""),settled);
            prefixScore=HealthValue(resources)+score;
        }
        long child=roundAgenda!.Append(parentId,path.Step,queueAfter,after.TurnNumber,score,roundOrder.Next(),prefixScore);
        if(FollowEnabled)IndexRound(child,after);
        if(partial && closed && !settled.HasTerminal)
        {
            var endings=new Stack<TreeStep>();
            for(var link=closure;link!=null && !ReferenceEquals(link,path);link=link.Previous)endings.Push(link.Step);
            long offset=child;
            while(endings.TryPop(out var end))
                offset=roundAgenda.Append(offset,end,endings.Count==0,settled.TurnNumber,score,roundOrder.Next(),prefixScore);
            if(FollowEnabled)IndexRound(offset,settled);
        }
        return child;
    }

    private IEnumerable<bool> ExpandRound(long id,int lane)
    {
        var agenda=roundAgenda!;
        var continued=agenda.ReadCursor(ref id,out var savedFirstSeat);
        var restore=new Stack<TreeStep>();
        long origin=id;
        while(origin>=0)
        {
            restore.Push(agenda.ReadStep(origin,out origin));
            yield return false;
        }
        var originNode=origin==-1 ? top : roundOrigins[checked((int)(-origin-2))];
        var prefix=originNode==top ? Array.Empty<Edge>() : Path(originNode);
        var state=root.ForkForReplay();
        foreach(var edge in prefix)
        {
            if(!edge.Replay(state,out _,out var reason)){Boundary(reason);yield break;}
            yield return false;
        }
        RoundPath? parentPath=null;
        Node? reached=roundCursors[lane]=originNode;
        foreach(var saved in restore)
        {
            if(!ActionFrontier.Replay(state,saved,out _,out var reason)){Boundary(reason);yield break;}
            reached=reached==null?null:TrackEvidence?Descend(reached,new[]{new Edge(saved,null)}):FindChild(reached,new Edge(saved,null));
            roundCursors[lane]=reached;parentPath=new(parentPath,saved);
            yield return false;
        }
        if(origin==-1 && restore.Count>0)RootBranchReopened?.Invoke(restore.Peek());
        int turn=state.TurnNumber;
        if(lane!=PrefixLane)coveredWorkers[lane]=!Simplifying && reached!=null &&
            (reached.HasFreeWin || TrackEvidence && Facts(reached).Solved!=null);
        roundPositions[lane]=state;
        int from=state.RoundsAdvanced;
        double bestBoundary=double.NegativeInfinity;
        restoredParents++;
        if(RoundSample!=null && (restoredParents==1 || restoredParents%128==0))
            RoundSample(new {restoredParents,fromRoot=origin==-1,turn=state.TurnNumber,depth=restore.Count,
                enemies=state.RemainingOpposition().Hp,hp=party.Sum(p=>state.Hp(p.Creature)),bottles=party.Sum(state.HeldPotionCount),
                deferred=DeferredRoundJobs,bytes=RoundFileBytes});
        int firstSeat=savedFirstSeat??roundOrder.Next(party.Length);
        var frontier=TrackEvidence && reached!=null?ReadMenu(reached,state,continued?.ReadIndex??0):
            continued??NewFrontier(state,id==-1);
        int batch=0;
        while(!frontier.Finished)
        {
            // Keep a dedicated root enumerator. Other parents yield their place
            // without forgetting untried menu digits or losing queue order.
            if(id!=-1 && (batch++==8 || lane==PrefixLane && (turn>lastHurtTurn || BestTurnNumber<=lastHurtTurn)))
            {
                agenda.DeferCursor(id,frontier,firstSeat,turn,double.IsFinite(bestBoundary)?bestBoundary:Estimate(state),roundOrder.Next());
                yield break;
            }
            var after=state.ForkForReplay();
            if(reached?.HasFreeWin==true)CoveredRoundExpansions++;
            long beforeExpand=SimulationMeter.ThreadActions;
            if(!frontier.Expand(after,out var expanded,out var action,out var reason))
            {Boundary(reason);yield return false;continue;}
            var step=expanded!;
            var path=new RoundPath(parentPath,step);
            bool lookAhead=!after.HasTerminal && after.RoundsAdvanced==from && action?.Choices is {Count:>0};
            long child=StoreRoundAction(id,path,after,from,firstSeat,!lookAhead,origin==-1,prefix,turn,SimulationMeter.ThreadActions-beforeExpand,out var closed,out var priority);
            int followups=0;
            yield return false;
            if(lookAhead)
            {
                // Visit each legal action/target once. Expand rotates the same
                // cursor whose remaining nested menu options we place on disk.
                var replyNode=TrackEvidence && reached!=null?FindChild(reached,new(step,null)):null;
                roundReplies[lane]=replyNode;
                var replies=replyNode!=null?ReadMenu(replyNode,after):new ActionFrontier(after,Boundary);
                int families=replies.FamilyCount;
                for(int i=0;i<families;i++)
                {
                    var probe=after.ForkForReplay();
                    long beforeReply=SimulationMeter.ThreadActions;
                    if(replies.Expand(probe,out var played,out var response,out var failure))
                    {
                        StoreRoundAction(child,new(path,played!),probe,from,firstSeat,true,origin==-1,prefix,turn,SimulationMeter.ThreadActions-beforeReply,out var resolved,out _);
                        if(resolved)
                        {
                            followups++;
                            if(id==-1)RootReplyObserved?.Invoke(step,response!);
                        }
                    }
                    else Boundary(failure);
                    yield return false;
                }
                if(!replies.Finished)agenda.DeferCursor(child,replies,firstSeat,turn,priority,roundOrder.Next());
                roundReplies[lane]=null;
            }
            if(closed)bestBoundary=Math.Max(bestBoundary,priority);
            if(id==-1)RootChoiceObserved?.Invoke(step,followups,closed);
            yield return true;
        }
        if(id< -1 && double.IsFinite(bestBoundary))Record(originNode,bestBoundary);
    }

    private void RoundOne()
    {
        try {AdvanceRoundWorker();}
        catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException)
        {
            RoundStorageError=ex.Message;
            Boundary("回合搜索临时文件："+ex.Message);
        }
    }

    private void AdvanceRoundWorker()
    {
        roundAgenda??=new RoundAgenda();
        int lane=roundLane;
        if(Simplifying)roundLane=(roundLane+1)%PrefixLane;
        else lane=NextRoundLane(coveredWorkers,tailWait,ref roundLane);
        if(!rootStarted)
        {
            rootStarted=true;
            roundWorkers[lane]=ExpandRound(-1,lane).GetEnumerator();
        }
        if(roundWorkers[lane]==null)
        {
            long id;
            // Extra seeds supplement the root/FIFO lane and never interrupt a
            // partially enumerated parent. Store a tree node, not a snapshot.
            if(lane==2 && queuedSeedNodes.TryDequeue(out var persisted))
            {roundOrigins.Add(persisted);id=-roundOrigins.Count-1;}
            else if(lane==2 && ++roundStarts%16==0 && (pendingRoundSeed!=null || anchors.Count>0))
            {
                var seed=pendingRoundSeed!=null && (anchors.Count==0 || roundStarts%32==0)
                    ? pendingRoundSeed : anchors[roundOrder.Next(anchors.Count)].Node;
                if(ReferenceEquals(seed,pendingRoundSeed)){pendingRoundSeed=null;TreeSeededPasses++;}
                roundOrigins.Add(seed);
                id=-roundOrigins.Count-1;
            }
            else if(!roundAgenda.Take(lane,out id))return;
            activeRoundIds[lane]=id;
            roundWorkers[lane]=ExpandRound(id,lane).GetEnumerator();
        }
        if(roundWorkers[lane]!.MoveNext())return;
        roundWorkers[lane]!.Dispose();
        roundWorkers[lane]=null;roundPositions[lane]=null;roundCursors[lane]=roundReplies[lane]=null;activeRoundIds[lane]=null;coveredWorkers[lane]=false;tailWait[lane]=0;
    }

    // Only lanes expanding a position with a verified free winning suffix yield.
    // The cursor stays in place; when every lane is covered the rotation runs normally.
    internal static int NextRoundLane(bool[] covered,int[] wait,ref int next)
    {
        while(true)
        {
            int lane=next;
            next=(next+1)%covered.Length;
            if(!covered[lane]){wait[lane]=0;return lane;}
            if(wait[lane]==0){wait[lane]=CoveredStride-1;return lane;}
            wait[lane]--;
        }
    }

    public void Dispose()
    {
        savingTrial?.Dispose();savingTrial=null;savingState=null;
        treeMeter?.Detach(this);treeMeter=null;
        entryRun?.Dispose();entryRun=null;
        retireHelpers=true;bridgeReader?.Dispose();bridgeReader=null;seedReader?.Dispose();seedReader=null;
        StopJoin();recovery?.Dispose();recovery=null;
        transferSource?.Dispose();transferSource=null;
        foreach(var worker in roundWorkers)worker?.Dispose();Array.Clear(roundPositions);
        roundAgenda?.Dispose();
    }
}
