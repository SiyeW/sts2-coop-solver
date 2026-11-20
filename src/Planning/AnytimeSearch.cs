using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopSolver;

// Nodes own decisions and cursors, not a full combat snapshot per visit.
// Only the current traversal, rollout and verification need mutable forks.
internal sealed partial class AnytimeSearch : IDisposable
{
    private sealed record Edge(TreeStep? Exact,KernelTeamSearch.Action? Prior)
    {
        internal bool Replay(KernelSession state,out KernelTeamSearch.Action? action,out string reason)
        {
            if(Exact!=null)return ActionFrontier.Replay(state,Exact,out action,out reason);
            return state.ReplayPlanned(Prior!,int.MaxValue,out reason,out action);
        }
    }
    private sealed class Node(Node? parent=null,Edge? step=null)
    {
        internal Node? Parent=parent;
        internal Edge? Step=step;
        internal readonly List<Node> Children=new();
        internal ActionFrontier? Frontier;
        internal bool Closed;
        internal int Visits;
        internal long DirectWork;
        internal double Sum;
        internal int Depth=parent==null?0:parent.Depth+1;
        internal ulong Shape;
    }

    private sealed record Proposal(Edge[] Prefix,IReadOnlyList<KernelTeamSearch.Action> Tail,TerminalRecord Ending,string Origin);
    private sealed record Checkpoint(Node Node,KernelSession? State);
    private readonly KernelSession root;
    private readonly Player[] party;
    private Node top=new();
    private readonly Dictionary<string,int> boundaries=new();
    private readonly int initialEnemyHp;
    private readonly int initialBottles;
    private readonly double teamMaxHp;
    private IEnumerator<Proposal?>? potionTrials;
    internal long VisitTicks, RolloutTicks, VerifyTicks, PotionTrialTicks;
    internal int VisitSteps, RolloutSteps;
    private readonly bool useRoundSearch;
    internal int BestBottlesLeft {get;private set;}
    internal int BestTurnNumber {get;private set;}
    private KernelSession? working;
    private Node? position;
    private RolloutRun? rollout;
    private Node? rolloutRoot;
    private Proposal? verifying;
    private KernelSession? replay;
    private int replayIndex;
    private readonly List<KernelTeamSearch.Action> replayed=new();
    private Proposal? waitingTie;
    private readonly Queue<Proposal> waitingWins=new();
    private Proposal? waitingLoss;
    private int[]? verifyingFirstHp;
    internal int[]? BestFirstHp {get;private set;}
    private readonly List<KernelPlanningState> verifyingStates=new();
    internal TreeStep?[] BestRecipes {get;private set;}=Array.Empty<TreeStep?>();
    private RouteDeaths? verificationDeaths;
    internal HudRoundHp[] BestRoundHp {get;private set;}=Array.Empty<HudRoundHp>();
    internal RouteKill[] BestKills {get;private set;}=Array.Empty<RouteKill>();
    internal KernelPlanningState[] BestTrail {get;private set;}=Array.Empty<KernelPlanningState>();
    private readonly ulong[] controlled;
    private int cadence, policies;
    private int walks, anchorIndex, verificationRound;
    private readonly List<Checkpoint> anchors=new();
    private readonly List<(int Index,KernelSession? State)> pendingAnchors=new();
    private Stack<Node>? replayToAnchor;
    internal int RoundAnchorCount=>anchors.Count;
    private readonly HashSet<string> replayRisks=new();
    private static readonly RolloutPolicyKind[] Policies={RolloutPolicyKind.Defend,RolloutPolicyKind.Tactical,RolloutPolicyKind.Growth,RolloutPolicyKind.Kill};

    internal int Nodes {get;private set;}=1;
    internal int RootOptions=>top.Children.Count(c=>c.Step!.Exact!=null);
    internal int RootMoves=>top.Children.Where(c=>c.Step!.Exact!=null).Select(c=>c.Step!.Exact!.Move).Distinct().Count();
    internal int AnchorVisits {get;private set;}
    internal string[] BestRisks {get;private set;}=Array.Empty<string>();
    internal string? BestOrigin {get;private set;}
    internal int MaxDepth {get;private set;}
    internal int CompletedRollouts {get;private set;}
    internal int OpeningPotionEdges {get;private set;}
    internal int LaterPotionEdges {get;private set;}
    internal int TruncatedRollouts {get;private set;}
    internal int Replays {get;private set;}
    internal int RejectedReplays {get;private set;}
    internal bool Finished=>top.Closed && rollout==null && verifying==null && waitingWins.Count==0 && waitingTie==null && waitingLoss==null && potionTrials==null && seedReader==null && (!useRoundSearch || !HasRoundWork);
    internal IReadOnlyDictionary<string,int> Boundaries=>boundaries;
    internal TournamentResult? Best {get;private set;}
    internal event Action? IncumbentChanged;

    private readonly bool useRollouts;

    internal AnytimeSearch(KernelSession start,bool useRollouts=true,bool useRoundSearch=true,int roundSeed=171)
    {
        roundOrder=new Random(roundSeed);
        this.useRoundSearch=useRoundSearch;
        this.useRollouts=useRollouts;
        root=start;
        party=start.Party.ToArray();
        controlled=party.Select(p=>p.NetId).ToArray();
        initialEnemyHp=Math.Max(1,start.RemainingOpposition().Hp);
        initialBottles=party.Sum(start.HeldPotionCount);
        teamMaxHp=Math.Max(1,party.Sum(p=>start.MaxHp(p.Creature)));
    }
    private void Boundary(string reason)
    {
        if(string.IsNullOrWhiteSpace(reason)) reason="未报告原因的模拟边界";
        boundaries[reason]=boundaries.GetValueOrDefault(reason)+1;
    }
    private static Edge[] Path(Node leaf)
    {
        var path=new Edge[leaf.Depth];
        for(var n=leaf;n.Parent!=null;n=n.Parent) path[n.Depth-1]=n.Step!;
        return path;
    }
    internal double Estimate(KernelSession state)
    {

        double life=party.Average(p=>Math.Clamp(state.Hp(p.Creature)/(double)Math.Max(1,state.MaxHp(p.Creature)),0,1));
        double alive=party.Count(p=>state.Hp(p.Creature)>0)/(double)party.Length;
        double damage=1-Math.Clamp(state.RemainingOpposition().Hp/(double)initialEnemyHp,0,1);
        double potions=party.Sum(state.HeldPotionCount)/(double)Math.Max(1,initialBottles);
        double enemyProgress=1-state.Enemies.Sum(e=>Math.Clamp(state.Hp(e)/(double)Math.Max(1,state.MaxHp(e)),0,1))/
            Math.Max(1,root.Enemies.Count);
        return 0.30*damage+0.20*Math.Clamp(enemyProgress,0,1)+0.26*life+0.20*alive+0.04*Math.Min(1,potions);
    }
    private TerminalRecord WithInventory(TerminalRecord ending,KernelSession state)
        => ending with {ResourcesConsumed=initialBottles-party.Sum(state.HeldPotionCount)};

    internal static int Compare(TerminalRecord a,TerminalRecord b)
        => TerminalComparer.Compare(a,b,KernelPotionPolicy.Objective);

    internal static int ComparePlans(TerminalRecord a,int stepsA,TerminalRecord b,int stepsB,HudRoundHp[]? roundsA=null,HudRoundHp[]? roundsB=null)
    {
        int primary=Compare(a,b);
        if(primary!=0 || !a.Victory || !b.Victory || a.ResourcesConsumed!=b.ResourcesConsumed ||
            a.ConsumablePreservationPremium!=b.ConsumablePreservationPremium || !a.PostCombatHp.SequenceEqual(b.PostCombatHp))
            return primary;
        int rounds=b.RoundsSurvived.CompareTo(a.RoundsSurvived);
        if(rounds!=0)return rounds;
        if(roundsA!=null && roundsB!=null && roundsA.Length==roundsB.Length)
        {
            for(int i=0;i<roundsA.Length;i++)
            {
                var x=roundsA[i];var y=roundsB[i];
                if(x.Round!=y.Round || x.EnemyHp==null || y.EnemyHp==null)break;
                int progress=y.EnemyHp.Value.CompareTo(x.EnemyHp.Value);
                if(progress!=0)return progress;
            }
        }
        return stepsB.CompareTo(stepsA);
    }

    internal static double WinReward(TerminalRecord ending,double teamMaxHp)
    {
        // All wins exceed the unfinished-state range [0,1]. Within each surviving
        // seat count, map utility monotonically to (0,1) without clipping gains.
        double utility=TerminalComparer.ResourceUtility(ending,KernelPotionPolicy.Objective);
        double resources=0.5+Math.Atan(utility/teamMaxHp)/Math.PI;
        double withinSeats=1.0/(2+ending.OutstandingStolenResource-resources);
        int seats=ending.PostCombatHp.Count;
        return 1+(seats-ending.IrreversibleLoss+withinSeats)/(seats+1);
    }

    private IEnumerable<Proposal?> RefineWin(IReadOnlyList<KernelTeamSearch.Action> line)
    {
        foreach(var trial in TrySavingPotions(line))yield return trial;
        if(line.Count==0 || !line[0].EndTurn)yield break;
        var actor=line[0].Player;
        int initial=root.RemainingOpposition().Hp;
        foreach(var family in new ActionFrontier(root,Boundary).Families)
        {
            if(family.Move.Kind!=MoveKind.Card || party[family.Move.Seat]!=actor)continue;
            var state=root.ForkForReplay();
            bool played=family.Expand(state,out var extra,out _,out _);
            yield return null;
            if(!played || state.RemainingOpposition().Hp>=initial)continue;
            var tail=new List<KernelTeamSearch.Action>();
            foreach(var old in line)
            {
                if(state.HasTerminal || !state.ReplayPlanned(old,int.MaxValue,out _,out var action))break;
                tail.Add(action!);yield return null;
            }
            if(state.HasWon)
            {
                var ending=WithInventory(TerminalRecord.Capture(state,party,true,""),state);
                yield return new(new[]{new Edge(extra,null)},tail,ending,"early-damage");
            }
        }
    }

    private IEnumerable<Proposal?> TrySavingPotions(IReadOnlyList<KernelTeamSearch.Action> line)
    {
        for(int omit=0;omit<line.Count;omit++)
        {
            if(line[omit].Potion==null)continue;
            var state=root.ForkForReplay();
            var actual=new List<KernelTeamSearch.Action>(line.Count-1);
            for(int i=0;i<line.Count && !state.HasTerminal;i++)
            {
                if(i==omit)continue;
                if(!state.ReplayPlanned(line[i],int.MaxValue,out _,out var action))break;
                actual.Add(action!);
                yield return null;
            }
            if(state.HasWon)
            {
                var ending=WithInventory(TerminalRecord.Capture(state,party,true,""),state);
                yield return new Proposal(Array.Empty<Edge>(),actual,ending,"potion-omission");
            }
            yield return null;
        }
    }

    private static void Record(Node node,double value)
    {
        for(Node? n=node;n!=null;n=n.Parent){n.Visits++;n.Sum+=value;}
    }
    private void Submit(Proposal proposal)
    {
        if(!proposal.Ending.Verified)return;
        // External records can count potion actions instead of terminal inventory.
        // A winning reference needs replay before we can compare the two belts.
        bool needsInventory=proposal.Origin=="reference" && proposal.Ending.Victory;
        if(!needsInventory && Best?.Record is {} old)
        {
            int rank=ComparePlans(proposal.Ending,0,old,0);
            if(rank<0)return;
            if(rank==0)
            {
                if(proposal.Ending.Victory && old.Victory && proposal.Ending.ResourcesConsumed==old.ResourcesConsumed &&
                    proposal.Ending.PostCombatHp.SequenceEqual(old.PostCombatHp) &&
                    proposal.Ending.ConsumablePreservationPremium==old.ConsumablePreservationPremium)
                {
                    if(proposal.Origin=="early-damage")waitingWins.Enqueue(proposal);
                    else waitingTie??=proposal;
                }
                return;
            }
        }
        if(proposal.Ending.Victory)waitingWins.Enqueue(proposal);
        else if(waitingLoss==null || Compare(proposal.Ending,waitingLoss.Ending)>0)waitingLoss=proposal;
    }
    internal void KeepBestFromSameRoot(AnytimeSearch previous)
    {
        if(!ReferenceEquals(root,previous.root))throw new InvalidOperationException("Incumbent root changed");
        if(previous.Best==null)return;
        BestRoundHp=previous.BestRoundHp;BestKills=previous.BestKills;BestRecipes=previous.BestRecipes;BestFirstHp=previous.BestFirstHp;BestTrail=previous.BestTrail;Best=previous.Best;BestOrigin=previous.BestOrigin;BestRisks=previous.BestRisks;
        BestBottlesLeft=previous.BestBottlesLeft;BestTurnNumber=previous.BestTurnNumber;
        if(Best.Record is {} ending && Best.Script is {} tail)
        {
            var proposal=new Proposal(Array.Empty<Edge>(),tail,ending,"retained-best");
            if(ending.Victory)waitingWins.Enqueue(proposal);else waitingLoss=proposal;
        }
    }

    internal void OfferReference(TournamentResult candidate)
    {
        if(candidate.Record is {Verified:true} ending && candidate.Script is {Count:>0} tail)
            Submit(new(Array.Empty<Edge>(),tail,ending,"reference"));
    }

    internal void Advance(TimeSpan slice)
    {
        var watch=Stopwatch.StartNew();
        if(helperResults.TryDequeue(out var completed))completed();
        while(recovery!=null)
        {
            if(!recovery.MoveNext()){recovery.Dispose();recovery=null;break;}
            if(watch.Elapsed>=slice)return;
        }
        bool hadWin=Best?.Record?.Victory==true;
        do
        {
            // Give all three jobs service without waiting for one playout to finish.
            if(verifying==null && (waitingWins.Count>0 || waitingTie!=null || waitingLoss!=null))
            {
                if(waitingWins.TryDequeue(out var win))verifying=win;
                else if(waitingTie!=null){verifying=waitingTie;waitingTie=null;}
                else {verifying=waitingLoss;waitingLoss=null;}
                replay=root.ForkForReplay();replayIndex=0;replayed.Clear();
                pendingAnchors.Clear();replayedShapes.Clear();verifyingStates.Clear();verificationDeaths=new(root);verifyingFirstHp=null;replayRisks.Clear();verificationRound=replay.RoundsAdvanced;
            }
            cadence=(cadence+1)%4;
            long started=Stopwatch.GetTimestamp();
            if(seedReader!=null && cadence==1)
            {
                try { if(!seedReader.MoveNext()){seedReader.Dispose();seedReader=null;} }
                catch(Exception e) when(e is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                { seedReader?.Dispose();seedReader=null;RestoreBoundaries++; }
            }
            else if(verifying!=null && (cadence==0 || verifying.Ending.Victory))
            {
                VerifyOne();
                VerifyTicks+=Stopwatch.GetTimestamp()-started;
            }
            else if(potionTrials!=null && (cadence==2 || top.Closed && rollout==null))
            {
                if(!potionTrials.MoveNext()){potionTrials.Dispose();potionTrials=null;}
                else if(potionTrials.Current is {} trial)Submit(trial);
                PotionTrialTicks+=Stopwatch.GetTimestamp()-started;
            }
            // One policy step may preview many cards across all three seats.
            // Meter real work, otherwise a cheap walk gets just one call per costly step.
            else if(useRoundSearch && HasRoundWork && (top.Closed || RoundTicks<=3*VisitTicks))
            {
                RoundOne();
                RoundTicks+=Stopwatch.GetTimestamp()-started;
            }
            else if(rollout!=null && (top.Closed || (useRoundSearch
                ? RolloutTicks*5<=RoundTicks+VisitTicks : RolloutTicks<=2*VisitTicks)))
            {
                var leaf=rolloutRoot;
                long before=SimulationMeter.ThreadActions;
                RollOne();
                if(leaf!=null)leaf.DirectWork+=SimulationMeter.ThreadActions-before;
                RolloutSteps++;
                RolloutTicks+=Stopwatch.GetTimestamp()-started;
            }
            else if(!top.Closed)
            {
                var accountingNode=position??top;
                long before=SimulationMeter.ThreadActions;
                VisitOne();
                accountingNode.DirectWork+=SimulationMeter.ThreadActions-before;
                VisitSteps++;
                VisitTicks+=Stopwatch.GetTimestamp()-started;
            }
            else if(verifying!=null){VerifyOne();VerifyTicks+=Stopwatch.GetTimestamp()-started;}
            if(Finished || (!hadWin && Best?.Record?.Victory==true))return;
        }while(watch.Elapsed<slice);
    }

    private void VisitOne()
    {
        if(working==null)
        {
            position=top;working=root;
            // Keep most new traversals free to change the opening. One in eight
            // can start deeper on the incumbent without replaying its prefix.
            if(++walks%8==0 && anchors.Count>0)
            {
                var anchor=anchors[anchorIndex++%anchors.Count];
                if(!anchor.Node.Closed)
                {
                    AnchorVisits++;
                    if(anchor.State!=null){position=anchor.Node;working=anchor.State;}
                    else
                    {
                        replayToAnchor=new Stack<Node>();
                        for(var n=anchor.Node;n.Parent!=null;n=n.Parent)replayToAnchor.Push(n);
                    }
                }
            }
            working=working.ForkForReplay();
        }
        if(replayToAnchor!=null && replayToAnchor.TryPop(out var transit))
        {
            if(transit.Step!.Replay(working,out _,out var reason))position=transit;
            else {Boundary(reason);replayToAnchor.Clear();working=null;position=null;}
            if(replayToAnchor.Count==0)replayToAnchor=null;
            return;
        }
        var node=position!;
        if(working.HasTerminal)
        {
            node.Closed=true;
            Submit(new(Path(node),Array.Empty<KernelTeamSearch.Action>(),
                WithInventory(TerminalRecord.Capture(working,party,true,""),working),"retained-tree"));
            working=null;position=null;return;
        }
        node.Frontier??=new ActionFrontier(working,Boundary);
        int width=1+(int)(2*Math.Sqrt(node.Visits+1));
        if(!node.Frontier.Finished && (node.Children.Count<width || node.Children.All(n=>n.Closed)))
        {
            var branch=working.ForkForReplay();
            if(node.Frontier.Expand(branch,out var step,out _,out var reason))
            {
                var known=ReusedNodes==0 && !importedEdges?null:node.Children.FirstOrDefault(c=>c.Step?.Exact is {} prior &&
                    prior.Move==step!.Move && prior.Digits.AsSpan().SequenceEqual(step.Digits) && prior.MenuEvidence==step.MenuEvidence);
                var child=known??new Node(node,new Edge(step,null));
                if(step!.Move.Kind==MoveKind.Potion)
                {
                    if(working.RoundsAdvanced==0)OpeningPotionEdges++;else LaterPotionEdges++;
                }
                if(FollowEnabled)Index(child,ShapeOf(branch));
                if(known==null){node.Children.Add(child);Nodes++;OfferBranch(child);}
                MaxDepth=Math.Max(MaxDepth,child.Depth);
                if(branch.HasTerminal)
                {
                    child.Closed=true;
                    var path=Path(child);
                    var terminal=WithInventory(TerminalRecord.Capture(branch,party,true,""),branch);
                    Record(child,terminal.Victory?WinReward(terminal,teamMaxHp):Estimate(branch));
                    Submit(new(path,Array.Empty<KernelTeamSearch.Action>(),terminal,"tree"));
                }
                else
                {
                    Record(child,Estimate(branch));
                    if(useRoundSearch && (pendingRoundSeed==null || Nodes%127==0))
                        pendingRoundSeed=child;
                    if(useRollouts && rollout==null)
                    {
                        rolloutRoot=child;
                        var policy=Policies[policies++%Policies.Length];
                        rollout=new RolloutRun(branch,party,party,new RolloutOptions(policy){InvestmentLookahead=false,SequenceLookahead=false});
                    }
                    else TryExtraRollout(child,branch);
                }
            }
            else {Boundary(reason);Record(node,Estimate(working));}
            working=null;position=null;return;
        }
        Node? pick=null;
        double highest=double.NegativeInfinity;
        foreach(var c in node.Children)
        {
            if(c.Closed)continue;
            double q=c.Sum/c.Visits+0.7*Math.Sqrt(Math.Log(node.Visits+1)/c.Visits);
            if(q>highest){pick=c;highest=q;}
        }
        if(pick==null){node.Closed=true;working=null;position=null;return;}
        if(!pick.Step!.Replay(working,out _,out var mismatch))
        {
            Boundary("重放分歧："+mismatch);pick.Closed=true;working=null;position=null;return;
        }
        position=pick;
    }
    private void RollOne()
    {
        try {rollout!.Step();}
        catch(Exception e) when(e is InvalidOperationException or NotSupportedException)
        {
            Boundary("续打："+e.GetBaseException().Message);
            rollout=null;rolloutRoot=null;return;
        }
        if(!rollout.Done)return;
        var result=rollout.Result!;
        var leaf=rolloutRoot!;
        var ending=WithInventory(result.Record,rollout.State);
        if(ending.Victory)Record(leaf,WinReward(ending,teamMaxHp));
        else if(!useRoundSearch)Record(leaf,Estimate(rollout.State));
        if(result.Terminal)
        {
            var path=Path(leaf);
            Submit(new(path,result.Actions,ending,"tree-rollout"));
        }
        else TruncatedRollouts++;
        CompletedRollouts++;
        rollout=null;rolloutRoot=null;
    }
    private void VerifyOne()
    {
        var p=verifying!;
        var state=replay!;
        int length=p.Prefix.Length+p.Tail.Count;
        if(replayIndex==length)
        {
            var record=WithInventory(TerminalRecord.Capture(state,party,state.HasTerminal,state.HasTerminal?"":"重放未到终局"),state);
            bool resourcesMatch=p.Origin=="reference" ||
                record.ResourcesConsumed==p.Ending.ResourcesConsumed &&
                record.ConsumablePreservationPremium==p.Ending.ConsumablePreservationPremium;
            if(resourcesMatch && record.OutstandingStolenResource==p.Ending.OutstandingStolenResource &&
                record.Verified && record.Victory==p.Ending.Victory && record.RoundsSurvived==p.Ending.RoundsSurvived &&
                record.PostCombatHp.SequenceEqual(p.Ending.PostCombatHp)
                && record.RemainingEnemies==p.Ending.RemainingEnemies && record.RemainingEnemyHp==p.Ending.RemainingEnemyHp)
            {
                Replays++;
                if(p.Origin=="retained-best" && ReferenceEquals(Best?.Script,p.Tail))
                {
                    InstallValidatedPath(p,record.Victory?WinReward(record,teamMaxHp):Estimate(state));
                    if(record.Victory)potionTrials=RefineWin(p.Tail).GetEnumerator();
                }
                if(Best?.Record is not {} old || ComparePlans(record,replayed.Count,old,Best.Script!.Count,verificationDeaths!.Rounds(),BestRoundHp)>0)
                {
                    Best=new TournamentResult(replayed.FirstOrDefault(),record,Array.Empty<TerminalRecord>(),CompletedRollouts,0,"已从原盘面复算"){Script=replayed.ToArray()};
                    BestRisks=replayRisks.Order().ToArray();
                    BestOrigin=p.Origin;
                    BestRoundHp=verificationDeaths!.Rounds();BestKills=verificationDeaths.Freeze();BestTrail=verifyingStates.ToArray();BestFirstHp=verifyingFirstHp;
                    BestRecipes=new TreeStep?[replayed.Count];
                    for(int i=0;i<p.Prefix.Length;i++)BestRecipes[i]=p.Prefix[i].Exact;
                    BestBottlesLeft=party.Sum(state.HeldPotionCount);
                    BestTurnNumber=state.TurnNumber;
                    StopPreview();
                    InstallValidatedPath(p,record.Victory?WinReward(record,teamMaxHp):Estimate(state));
                    potionTrials?.Dispose();
                    potionTrials=record.Victory ? RefineWin(Best.Script!).GetEnumerator() : null;
                    IncumbentChanged?.Invoke();
                }
            }
            else {RejectedReplays++;Boundary("候选终局与重放不一致");}
            verifying=null;replay=null;return;
        }
        string reason="";
        KernelTeamSearch.Action? resolved=null;
        bool ok;
        ReplayCoverage.Begin(state,replayRisks);
        try
        {
            ok=!state.HasTerminal && (replayIndex<p.Prefix.Length
                ? p.Prefix[replayIndex].Replay(state,out resolved,out reason)
                : state.ReplayPlanned(p.Tail[replayIndex-p.Prefix.Length],int.MaxValue,out reason,out resolved));
        }
        finally {ReplayCoverage.End();}
        if(!ok){RejectedReplays++;Boundary("候选复算："+reason);verifying=null;replay=null;return;}
        verificationDeaths!.Observe(state,replayIndex,resolved);replayed.Add(resolved!); replayIndex++;
        if(verifyingFirstHp==null && (state.HasTerminal || state.RoundsAdvanced!=root.RoundsAdvanced))
            verifyingFirstHp=party.Select(p=>state.Hp(p.Creature)).ToArray();
        if(FollowEnabled){replayedShapes.Add(ShapeOf(state));verifyingStates.Add(state.PlanningState(controlled));}
        ReplayCoverage.Collect(state,replayRisks);
        if(!state.HasTerminal && (replayIndex==2 || state.RoundsAdvanced!=verificationRound))
        {
            // Every boundary is eligible. Cache only a few complete boards;
            // later boundaries without a cache can be restored from the path.
            int spacing=Math.Max(1,(p.Ending.RoundsSurvived+7)/8);
            bool cache=replayIndex==2 || state.RoundsAdvanced%spacing==0;
            pendingAnchors.Add((replayIndex,cache?state.ForkForReplay():null));
        }
        verificationRound=state.RoundsAdvanced;
    }

    // All untried actions remain reachable from the main tree. Checkpoints
    // only accelerate revisits to later turns of a root-verified incumbent.
    private void InstallValidatedPath(Proposal proposal,double value)
    {
        var node=top;
        anchors.Clear();anchorIndex=0;bestPathNodes.Clear();
        int checkpoint=0;
        for(int i=0;i<replayed.Count;i++)
        {
            var edge=i<proposal.Prefix.Length?proposal.Prefix[i]:new Edge(null,proposal.Tail[i-proposal.Prefix.Length]);
            var child=node.Children.FirstOrDefault(n=>ReferenceEquals(n.Step,edge) ||
                edge.Prior!=null && ReferenceEquals(n.Step?.Prior,edge.Prior));
            if(child==null)
            {
                child=new Node(node,edge){Visits=1,Sum=value};
                node.Children.Add(child);Nodes++;
            }
            node=child;
            if(FollowEnabled){Index(node,replayedShapes[i]);bestPathNodes.Add(node);}
            if(checkpoint<pendingAnchors.Count && pendingAnchors[checkpoint].Index==i+1)
                anchors.Add(new(node,pendingAnchors[checkpoint++].State));
        }
        node.Closed=true;
    }
}
