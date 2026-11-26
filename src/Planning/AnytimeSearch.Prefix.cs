using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

// Supplemental work on the ordinary legal action frontier of a verified win.
// The unrestricted worker shares its pending rows and is always available.
internal sealed partial class AnytimeSearch
{
    private const int PrefixLane=3;
    private int prefixLanePolicy;
    private double prefixBalance;
    private sealed record EntryCandidate(Edge[] Path,KernelSession State,double Utility,double Board);
    private readonly List<EntryCandidate> entries=new();
    private readonly HashSet<string> sampledEntries=new();
    private EntryCandidate? testingEntry;
    private TournamentRun? entryRun;
    private object? entryScript;
    private long entrySpent,entryCredit;
    private int lastEntryCutoff;
    internal long EntryVisits {get;private set;}
    internal int EntryTrials {get;private set;}
    internal long PrefixVisits {get;private set;}
    internal int PrefixJobs {get;private set;}
    // Only extra scheduling is deduplicated. Every underlying action path remains
    // in the ordinary queue, including those not admitted to this small portfolio.
    private void OfferEntry(Node node,KernelSession state,double board)
    {
        if(Best?.Record is not {Victory:true} best || BestTurnNumber<=lastHurtTurn)return;
        if(lastEntryCutoff!=lastHurtTurn){entries.Clear();sampledEntries.Clear();lastEntryCutoff=lastHurtTurn;}
        var entry=WithInventory(TerminalRecord.Capture(state,party,false,""),state);
        // Compare both positions with the same victory-return accounting. This is
        // only a priority hint; a tied ending can still save a bottle.
        double utility=HealthValue(entry)+(party.Length>1?entry.PostCombatHp.Count(h=>h<=0):0);
        if(utility<HealthValue(best))return;
        int at=entries.FindIndex(e=>utility>e.Utility || utility==e.Utility && board>e.Board);
        if(at<0)at=entries.Count;
        const int capacity=8;
        if(at>=capacity)return;
        // This hash only suppresses optional repeat probes, never tree expansion.
        string key=SlCache.Witness(state);
        if(sampledEntries.Count>=4096)sampledEntries.Clear();
        if(!sampledEntries.Add(key))return;
        if(entries.Count==capacity)entries.RemoveAt(capacity-1);
        entries.Insert(at,new(Path(node),state.ForkForReplay(),utility,board));
    }

    private bool AdvanceEntry()
    {
        long before=SimulationMeter.ThreadActions;
        if(entryRun==null)
        {
            if(entries.Count==0)return false;
            testingEntry=entries[0];entries.RemoveAt(0);
            var options=new TournamentOptions(Policy:RolloutPolicyKind.Defend,EnableJointSearch:true,IncludePotions:true,
                ContinuationActors:party,MaxDegreeOfParallelism:1);
            entryRun=new TournamentRun(testingEntry.State,party,party,options,40000);
            entryScript=null;entrySpent=0;EntryTrials++;
        }
        bool done=entryRun.Advance(TimeSpan.FromMilliseconds(1));
        long spent=SimulationMeter.ThreadActions-before;
        entrySpent+=spent;EntryVisits+=spent;
        var result=done?entryRun.Result:entryRun.BestCompleted;
        if(result?.Script is {} script && result.Record is {Verified:true,Victory:true} ending && !ReferenceEquals(script,entryScript))
        {
            entryScript=script;
            Submit(new(testingEntry!.Path,script,ending,"prefix-reference"));
        }
        if(done || entrySpent>=1500)
        {
            entryRun.Dispose();entryRun=null;testingEntry=null;
        }
        return true;
    }

    private sealed record HeadFrame(KernelSession State,ActionFrontier Moves,Node Node);
    private readonly Stack<HeadFrame> headStack=new();
    internal bool HeadComplete {get;private set;}
    internal bool HeadLimited {get;private set;}
    internal int HeadExpansions {get;private set;}
    internal int HeadEndpoints {get;private set;}
    internal int? MinimumHeadLoss {get;private set;}
    internal int? MinimumNoPotionHeadLoss {get;private set;}
    private bool headUnsupported;
    private bool validateHead;
    private void PushHead(KernelSession state,Node node)
    {
        var moves=TrackEvidence?ReadMenu(node,state):RequiredFirst!=null && node==top?NewFrontier(state,true):
            new ActionFrontier(state,reason=>{headUnsupported=true;Boundary(reason);},measureCoverage:false);
        if(moves.Finished || node.MenuGap)headUnsupported=true;
        headStack.Push(new(state,moves,node));
    }
    private void EnumerateHead()
    {
        if(headStack.Count==0)
        {
            validateHead=RequiredFirst==null && ProofEvidence.Supported(root,root) && Monotone;
            PushHead(root.ForkForReplay(),top);
        }
        var frame=headStack.Peek();
        if(frame.Moves.Finished)
        {
            headUnsupported|=frame.Node.MenuGap;
            headStack.Pop();
            if(headStack.Count==0)
            {
                HeadComplete=!headUnsupported;HeadLimited=headUnsupported;
            }
            return;
        }
        // Bound the optional proof's live snapshots; the normal tree keeps its
        // actions on hitting the depth limit. Never publish a bound from a partial pass.
        if(headStack.Count>=64)
        {HeadLimited=true;headStack.Clear();return;}
        var next=frame.State.ForkForReplay();
        long before=SimulationMeter.ThreadActions;
        if(!frame.Moves.Expand(next,out var step,out _,out var reason))
        {headUnsupported=true;Boundary(reason);return;}
        if(validateHead && !ProofEvidence.Supported(root,next))headUnsupported=true;
        HeadExpansions++;
        var child=Descend(frame.Node,new[]{new Edge(step,null)});
        child.Turn=next.TurnNumber;
        CreditWork(frame.Node,SimulationMeter.ThreadActions-before);
        if(!next.HasTerminal && next.TurnNumber==root.TurnNumber){PushHead(next,child);return;}
        HeadEndpoints++;
        int loss=party.Sum(p=>root.Hp(p.Creature)-next.Hp(p.Creature))+(int)DeathSaves.RecoverySince(root,next);
        MinimumHeadLoss=Math.Min(MinimumHeadLoss??int.MaxValue,loss);
        if(((CoopBots.Kernel.Vendor.SimulatedCombatState)next.Simulator.State.CombatState).PotionUses.Count==
            ((CoopBots.Kernel.Vendor.SimulatedCombatState)root.Simulator.State.CombatState).PotionUses.Count)
            MinimumNoPotionHeadLoss=Math.Min(MinimumNoPotionHeadLoss??int.MaxValue,loss);
        var outcome=WithInventory(TerminalRecord.Capture(next,party,next.HasTerminal,""),next);
        double score=Estimate(next);
        Record(child,score);
        if(next.HasTerminal)Submit(new(Path(child),Array.Empty<KernelTeamSearch.Action>(),outcome,"head-frontier"));
        else OfferEntry(child,next,score);
    }

    // Count actual simulated actions across slices. A short head does not
    // create artificial work: once its pending rows run out, return the lane.
    internal bool AdvancePrefix(TimeSpan slice)
    {
        bool tracingHead=lastHurtTurn>0 && BestTurnNumber>lastHurtTurn;
        if(!tracingHead)
        {
            prefixBalance=0;entries.Clear();entryRun?.Dispose();entryRun=null;testingEntry=null;
        }
        if(!useRoundSearch || RoundStorageError!=null || prefixBalance>0 || verifying!=null ||
            waitingWins.Count!=0 || recovery!=null || roundAgenda==null || !roundAgenda.HasDiskSpace())return false;
        bool oneTurn=tracingHead && lastHurtTurn==root.TurnNumber;
        bool eligibleRows=tracingHead && (!oneTurn || HeadLimited) && roundAgenda.HasPrefix(lastHurtTurn);
        bool exactPending=oneTurn && !HeadLimited && !HeadComplete;
        if(roundWorkers[PrefixLane]==null && entryRun==null && entries.Count==0 && !eligibleRows && !exactPending)return false;
        long before=SimulationMeter.ThreadActions,entryBefore=EntryVisits;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            do
            {
                bool exactHead=oneTurn && !HeadComplete && !HeadLimited;
                bool headReady=tracingHead && (exactHead || roundWorkers[PrefixLane]!=null ||
                    (!oneTurn || HeadLimited) && roundAgenda.HasPrefix(lastHurtTurn));
                if((entryCredit+SimulationMeter.ThreadActions-before-3*(EntryVisits-entryBefore)>=0 || !headReady) && AdvanceEntry())continue;
                if(exactHead){EnumerateHead();continue;}
                if(oneTurn && HeadComplete && roundWorkers[PrefixLane]==null)break;
                if(roundWorkers[PrefixLane]==null)
                {
                    if(!tracingHead || !roundAgenda.HasPrefix(lastHurtTurn))break;
                    if(!roundAgenda.TakePrefix(prefixLanePolicy%3,lastHurtTurn,out long id))continue;
                    prefixLanePolicy=(prefixLanePolicy+1)%3;
                    PrefixJobs++;activeRoundIds[PrefixLane]=id;
                    roundWorkers[PrefixLane]=ExpandRound(id,PrefixLane).GetEnumerator();
                }
                var job=roundWorkers[PrefixLane]!;
                if(!job.MoveNext())
                {
                    job.Dispose();roundWorkers[PrefixLane]=null;activeRoundIds[PrefixLane]=null;
                }
            }while(watch.Elapsed<slice && waitingWins.Count==0);
        }
        catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException)
        {
            RoundStorageError=ex.Message;Boundary("回合搜索临时文件："+ex.Message);
        }
        long spent=SimulationMeter.ThreadActions-before;
        entryCredit=Math.Clamp(entryCredit+spent-3*(EntryVisits-entryBefore),-512,512);
        PrefixVisits+=spent;prefixBalance+=spent;
        return spent>0;
    }
    internal void ChargeUnrestricted(long visits)
    {
        // Cap the preference at 4:1 while both classes have work; never accumulate
        // a whole battle of debt to spend when a new incumbent moves the cutoff.
        int head=lastHurtTurn-root.TurnNumber+1;
        double ratio=head>0?Math.Clamp((BestTurnNumber-lastHurtTurn)/(double)head,0,4):0;
        prefixBalance=Math.Max(-1024,prefixBalance-ratio*visits);
    }
}
