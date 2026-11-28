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
    internal sealed record Edge(TreeStep? Exact,KernelTeamSearch.Action? Prior,SavedStep? Saved=null)
    {
        private static bool SameList<T>(IReadOnlyList<T>? a,IReadOnlyList<T>? b)=>
            ReferenceEquals(a,b) || a!=null && b!=null && a.SequenceEqual(b);
        private static bool SameChoices(IReadOnlyList<KernelChoice>? a,IReadOnlyList<KernelChoice>? b)
        {
            if(ReferenceEquals(a,b))return true;
            if(a==null || b==null || a.Count!=b.Count)return false;
            for(int i=0;i<a.Count;i++)
            {
                var x=a[i];var y=b[i];
                if(ReferenceEquals(x,y))continue;
                if(x.Owner!=y.Owner || x.Source!=y.Source || x.Effect!=y.Effect || x.Pile!=y.Pile || x.Context!=y.Context ||
                    x.Minimum!=y.Minimum || x.Maximum!=y.Maximum || x.RequestedMinimum!=y.RequestedMinimum ||
                    x.RequestedMaximum!=y.RequestedMaximum || x.MenuSource!=y.MenuSource || x.Timing!=y.Timing ||
                    x.NativeRound!=y.NativeRound || x.PlayerTurnNumber!=y.PlayerTurnNumber ||
                    !SameList(x.Cards,y.Cards) || !SameList(x.Options,y.Options))return false;
            }
            return true;
        }
        private static bool SameTurn(KernelTurnStartChoiceContract? a,KernelTurnStartChoiceContract? b)=>
            ReferenceEquals(a,b) || a!=null && b!=null && a.FromRound==b.FromRound && a.ToRound==b.ToRound &&
            a.Complete==b.Complete && a.OwnersTurnNumbers.Count==b.OwnersTurnNumbers.Count &&
            a.OwnersTurnNumbers.All(p=>b.OwnersTurnNumbers.TryGetValue(p.Key,out int n) && n==p.Value) && SameChoices(a.Choices,b.Choices);
        internal bool Matches(Edge? other)
        {
            if(ReferenceEquals(this,other))return true;
            if(other==null)return false;
            if(Exact!=null || other.Exact!=null)
                return Exact!=null && other.Exact!=null && Exact.Move==other.Exact.Move &&
                    Exact.Digits.AsSpan().SequenceEqual(other.Exact.Digits) && Exact.MenuEvidence==other.Exact.MenuEvidence;
            if(Saved is {} s && other.Saved is {} t)
                return s.Seat==t.Seat && s.Id==t.Id && s.Slot==t.Slot && s.Target==t.Target && s.Key==t.Key && s.Occurrence==t.Occurrence &&
                    SameList(s.Ordinals,t.Ordinals) && SameChoices(s.Choices,t.Choices) && SameTurn(s.Turn,t.Turn);
            return SameAction(Prior,other.Prior);
        }
        internal static bool SameAction(KernelTeamSearch.Action? a,KernelTeamSearch.Action? b)
        {
            if(a==null || b==null)return false;
            return ReferenceEquals(a,b) || ReferenceEquals(a.Player,b.Player) && ReferenceEquals(a.Card,b.Card) &&
                ReferenceEquals(a.Target,b.Target) && (ReferenceEquals(a.Potion,b.Potion) || CapturedBelt.SameGenerated(a.Potion,b.Potion)) && a.EndTurn==b.EndTurn &&
                a.CardStateKey==b.CardStateKey && a.CardStateOccurrence==b.CardStateOccurrence &&
                SameList(a.ChoiceOrdinals,b.ChoiceOrdinals) && SameChoices(a.Choices,b.Choices) && SameTurn(a.TurnStartChoices,b.TurnStartChoices);
        }
        internal bool Replay(KernelSession state,out KernelTeamSearch.Action? action,out string reason)
        {
            if(Exact!=null)return ActionFrontier.Replay(state,Exact,out action,out reason);
            var next=Prior??Saved?.Bind(state);
            if(next==null){action=null;reason="保存的动作与当前盘面不符";return false;}
            return state.ReplayPlanned(next,int.MaxValue,out reason,out action);
        }
    }
    internal sealed class Node(Node? parent=null,Edge? step=null)
    {
        internal bool IsBattleRoot;
        internal int Created=1;
        private static int DepthAt(Node? parent)
        {
            if(parent==null)return 0;
            if(parent.SharedRoot is {} tree)tree.Created++;
            else if(parent.IsBattleRoot)parent.Created++;
            return parent.Depth+1;
        }
        internal readonly Node? SharedRoot=parent?.SharedRoot??(parent?.IsBattleRoot==true?parent:null);
        internal Node? Parent=parent;
        internal Edge? Step=step;
        internal readonly List<Node> Children=new();
        internal ActionFrontier? Frontier;
        internal List<Node>? Legal;
        internal int Families;
        internal double Share;
        internal bool MenuGap,UnknownMenu;
        internal NodeEvidence? Evidence;
        internal void ResetMenu()
        {
            Frontier=null;Legal=null;MenuGap=UnknownMenu=false;Families=0;Evidence=null;
        }
        internal bool Closed,HasFreeWin,Compacted;
        internal Node? BestTerminal;
        internal TerminalRecord? ReachedEnd;
        internal int Turn;
        internal int Visits;
        internal long DirectWork,SubtreeWork;
        internal Node? Observed,RecipeSource;
        internal object? WalkStamp;
        internal int RecipeCursor;
        internal double Sum;
        internal int Depth=DepthAt(parent);
        internal ulong Shape;
    }

    private sealed record Proposal(Edge[] Prefix,IReadOnlyList<KernelTeamSearch.Action> Tail,TerminalRecord Ending,string Origin)
    {internal bool Verified;}
    private sealed record Checkpoint(Node Node,KernelSession? State);
    private readonly KernelSession root;
    private readonly Player[] party;
    private Node top=new();
    internal Node MemoryRoot=>top;
    private SearchTreeMemory? treeMeter;
    internal void TrackTree(SearchTreeMemory meter){treeMeter=meter;if(HelperMeter!=null)HelperMeter.Memory=meter;meter.Attach(this);}
    private readonly Dictionary<string,int> boundaries=new();
    private readonly int initialEnemyHp;
    private readonly int initialBottles;
    private readonly double teamMaxHp;
    internal long VisitTicks, RolloutTicks, VerifyTicks;
    internal int VisitSteps, RolloutSteps;
    private readonly bool useRoundSearch;
    internal int BestBottlesLeft {get;private set;}
    internal int BestTurnNumber {get;private set;}
    private KernelSession? working;
    private Node? position;
    private RolloutRun? rollout;
    private long rolloutWork;
    private Node? rolloutRoot;
    private Proposal? verifying;
    private KernelSession? replay;
    private int replayIndex;
    private readonly List<KernelTeamSearch.Action> replayed=new();
    private readonly List<long> replayWork=new();
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
    private int walks, anchorIndex, verificationRound, lastHurtTurn, lateRolloutStarts, lateAnchorStarts;
    private bool deferTail;
    private int lastPaidStep,previousBottles,previousRevive;
    private int[] previousVerificationHp=Array.Empty<int>();
    internal const int CoveredStride=16;
    internal long CoveredTreeExpansions {get;private set;}
    internal long CoveredRoundExpansions {get;private set;}
    private void UpdateFocus()
    {
        lastHurtTurn=Best?.Record?.Victory==true?
            BestRoundHp.Where(r=>r.Loss.Any(n=>n>0)).Select(r=>r.Round).DefaultIfEmpty(0).Max():0;
    }
    private readonly List<Checkpoint> anchors=new();
    private readonly List<(int Index,int Turn,KernelSession? State)> pendingAnchors=new();
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
    internal bool Finished=>top.Closed && savingTrial==null && rollout==null && verifying==null && waitingWins.Count==0 && waitingPotions.Count==0 && waitingTie==null && waitingLoss==null && seedReader==null && (!useRoundSearch || !HasRoundWork);
    internal IReadOnlyDictionary<string,int> Boundaries=>boundaries;
    internal TournamentResult? Best {get;private set;}
    internal event Action? IncumbentChanged;

    internal Move? RequiredFirst;
    private ActionFrontier NewFrontier(KernelSession state,bool first)=>first && RequiredFirst is {} forced?
        new ActionFrontier(state,forced):new ActionFrontier(state,Boundary);
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
    private Edge[] Path(Node leaf)
    {
        var path=new Edge[leaf.Depth-top.Depth];
        for(var n=leaf;n!=top;n=n.Parent!)path[n.Depth-top.Depth-1]=n.Step!;
        return path;
    }
    internal double Estimate(KernelSession state)
    {
        if(state.HasTerminal)return TreeReward(WithInventory(TerminalRecord.Capture(state,party,true,""),state));

        return ScoreFeatures(ReadFeatures(state));
    }
    private TerminalRecord WithInventory(TerminalRecord ending,KernelSession state)
        => DeathSaves.WithInventory(ending,root,state);

    // Verified records retain pre-cleanup HP for replay. The adjustment is
    // actual recovery less the native one-HP return for each fallen teammate.
    internal static double HealthValue(TerminalRecord ending)=>ending.PostCombatHp.Sum()-ending.ConsumablePreservationPremium;
    internal static int Compare(TerminalRecord a,TerminalRecord b)
    {
        if(a is {Victory:false,Verified:true} && b is {Victory:false,Verified:true} && a.RemainingEnemyHp!=b.RemainingEnemyHp)
            return b.RemainingEnemyHp.CompareTo(a.RemainingEnemyHp);
        if(a is not {Victory:true,Verified:true} || b is not {Victory:true,Verified:true})
            return TerminalComparer.Compare(a,b,KernelPotionPolicy.Objective);
        int theft=b.OutstandingStolenResource.CompareTo(a.OutstandingStolenResource);
        if(theft!=0)return theft;
        int hp=HealthValue(a).CompareTo(HealthValue(b));
        if(hp!=0)return hp;
        int bottles=b.ResourcesConsumed.CompareTo(a.ResourcesConsumed);
        return bottles!=0?bottles:a.PostCombatHp.Sum().CompareTo(b.PostCombatHp.Sum());
    }

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
        // Preserve the ordering by stolen resources, effective team HP and bottles.
        double utility=HealthValue(ending)+0.25/(1+ending.ResourcesConsumed);
        double resources=0.5+Math.Atan(utility/teamMaxHp)/Math.PI;
        double withinSeats=1.0/(2+ending.OutstandingStolenResource-resources);
        return 1+withinSeats;
    }

    // A settled defeat is ranked by remaining opposition. Simulation source and
    // unused bottles cannot change that order. Scale by the same opening for all branches.
    internal static double DefeatReward(int enemyHp,int openingHp)=>
        -(double)enemyHp/(Math.Max(1,openingHp)+(double)enemyHp);

    private double TerminalValue(TerminalRecord ending)=>ending.Victory?WinReward(ending,teamMaxHp)
        :DefeatReward(ending.RemainingEnemyHp,InSharedTree?baselineEnemyHp:initialEnemyHp);

    private double TreeReward(TerminalRecord record)=>TerminalValue(focusCursor && record.Victory?
        record with {ResourcesConsumed=record.ResourcesConsumed+inheritedBottlesUsed,
            ConsumablePreservationPremium=record.ConsumablePreservationPremium+inheritedReviveCost}:record);

    // Rollouts can switch to another simulation fork while resolving an action.
    // Bind both the outcome and its resource ledger to the completed run.
    internal TerminalRecord RolloutEnding(RolloutRun run)=>WithInventory(run.Result!.Record,run.State);
    private static void CreditWork(Node node,long work)
    {
        if(work<=0)return;
        node.DirectWork+=work;
        for(Node? n=node;n!=null;n=n.Parent)System.Threading.Interlocked.Add(ref n.SubtreeWork,work);
    }
    // The playout was already charged to its starting node. Once its path is retained,
    // move one actual played action to each edge. Alternative previews stay at the start.
    private static void AttributeCommittedMoves(Node start,Node tip,int steps,long charged)
    {
        if(steps==0 || steps>charged)return;
        start.DirectWork-=steps;
        long suffix=0;
        for(var n=tip;n!=start;n=n.Parent!)
        {
            n.DirectWork++;
            System.Threading.Interlocked.Add(ref n.SubtreeWork,++suffix);
        }
    }
    private void Record(Node node,double value)
    {
        for(Node? n=node;n!=null;n=n.Parent){n.Visits++;n.Sum+=value;}
        if(focusCursor)battle!.LiveBackups++;
    }
    private void Submit(Proposal proposal,Node? tip=null)
    {
        if(!proposal.Ending.Verified)return;
        if((TrackEvidence || InSharedTree) && proposal.Origin is not ("reference" or "prefix-reference" or "solved-subtree" or "potion-omission"))
        {
            tip??=Descend(top,proposal.Prefix.Concat(proposal.Tail.Select(a=>new Edge(null,a))));
            RememberTerminal(tip,proposal.Ending);
        }
        // External records can count potion actions instead of terminal inventory.
        // A winning reference needs replay before we can compare the two belts.
        bool needsInventory=(proposal.Origin is "reference" or "prefix-reference") && proposal.Ending.Victory;
        if(!needsInventory && Best?.Record is {} old)
        {
            int rank=ComparePlans(proposal.Ending,0,old,0);
            if(rank<0){QueuePotionAlternative(proposal);return;}
            if(rank==0)
            {
                if(proposal.Ending.Victory && old.Victory && proposal.Ending.ResourcesConsumed==old.ResourcesConsumed &&
                    proposal.Ending.PostCombatHp.SequenceEqual(old.PostCombatHp) &&
                    proposal.Ending.ConsumablePreservationPremium==old.ConsumablePreservationPremium)
                {
                    waitingTie??=proposal;
                }
                QueuePotionAlternative(proposal);
                return;
            }
        }
        if(proposal.Ending.Victory)waitingWins.Enqueue(proposal);
        else if(waitingLoss==null)waitingLoss=proposal;
        else if(Compare(proposal.Ending,waitingLoss.Ending)>0){QueuePotionAlternative(waitingLoss);waitingLoss=proposal;}
        else QueuePotionAlternative(proposal);
    }
    internal void KeepBestFromSameRoot(AnytimeSearch previous)
    {
        if(!ReferenceEquals(root,previous.root))throw new InvalidOperationException("Incumbent root changed");
        if(previous.Best==null)return;
        BestRoundHp=previous.BestRoundHp;BestKills=previous.BestKills;BestRecipes=previous.BestRecipes;BestFirstHp=previous.BestFirstHp;BestTrail=previous.BestTrail;Best=previous.Best;BestOrigin=previous.BestOrigin;BestRisks=previous.BestRisks;
        BestBottlesLeft=previous.BestBottlesLeft;BestTurnNumber=previous.BestTurnNumber;
        UpdateFocus();
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
        if(!ResumeRecovery(watch,slice))return;
        bool hadWin=Best?.Record?.Victory==true;
        do
        {
            // Give all three jobs service without waiting for one playout to finish.
            StartNextProposal();
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
            // One policy step may preview many cards across all three seats.
            // Meter real work, otherwise a cheap walk gets just one call per costly step.
            else if(savingTrial!=null && cadence==1)AdvancePotionSavings();
            else if(useRoundSearch && HasRoundWork && (top.Closed || RoundTicks<=3*VisitTicks))
            {
                RoundOne();
                RoundTicks+=Stopwatch.GetTimestamp()-started;
            }
            else if(rollout!=null && (top.Closed || (useRoundSearch
                ? RolloutTicks*5<=RoundTicks+VisitTicks : RolloutTicks<=2*VisitTicks)))
            {
                RollOne();
                RolloutSteps++;
                RolloutTicks+=Stopwatch.GetTimestamp()-started;
            }
            else if(!top.Closed)
            {
                var accountingNode=position??top;
                long before=SimulationMeter.ThreadActions;
                VisitOne(ref accountingNode);
                CreditWork(accountingNode,SimulationMeter.ThreadActions-before);
                VisitSteps++;
                VisitTicks+=Stopwatch.GetTimestamp()-started;
            }
            else if(verifying!=null){VerifyOne();VerifyTicks+=Stopwatch.GetTimestamp()-started;}
            if(Finished || (!hadWin && Best?.Record?.Victory==true))return;
        }while(watch.Elapsed<slice);
    }

    internal static double SelectionScore(Node child,int parentVisits,double? reachedValue=null)
    {
        if(child.Visits==0)return double.PositiveInfinity;
        double value=reachedValue??child.Sum/child.Visits;
        return value+0.7*Math.Sqrt(Math.Log(parentVisits+1)/child.Visits);
    }

    private double? ReachedValue(Node child)
    {
        if(child.BestTerminal?.ReachedEnd is not {Verified:true} end)return null;
        // Cached terminals already include any comparable opening prefix. This
        // is a selection hint only; a cached line must still replay to be shown.
        return TerminalValue(end);
    }

    private void VisitOne(ref Node accountingNode)
    {
        if(working==null)
        {
            position=top;working=root;
            Checkpoint? anchor;
            do
            {
                walks++;
                anchor=walks%8==0 && anchors.Count!=0?anchors[anchorIndex++%anchors.Count]:null;
            }while(!Simplifying && anchor!=null && !anchor.Node.Closed && anchor.Node.HasFreeWin &&
                ++lateAnchorStarts%CoveredStride!=0);
            deferTail=!Simplifying && walks%CoveredStride!=0;
            if(anchor!=null && !anchor.Node.Closed)
            {
                AnchorVisits++;
                if(anchor.State!=null){position=anchor.Node;working=anchor.State;}
                else
                {
                    replayToAnchor=new Stack<Node>();
                    for(var n=anchor.Node;n!=top;n=n.Parent!)replayToAnchor.Push(n);
                }
            }
            working=working.ForkForReplay();
        }
        if(replayToAnchor!=null && replayToAnchor.TryPop(out var transit))
        {
            if(transit.Step!.Replay(working,out _,out var reason))accountingNode=position=transit;
            else {Boundary(reason);replayToAnchor.Clear();working=null;position=null;}
            if(replayToAnchor.Count==0)replayToAnchor=null;
            return;
        }
        var node=accountingNode=position!;
        node.Turn=working.TurnNumber;
        if(TrackEvidence && !Simplifying && deferTail && node!=top && Facts(node).Solved is {} solution)
        {
            OfferSolution(node,solution);Record(node,TreeReward(solution.Ending));
            working=null;position=null;return;
        }
        if(working.HasTerminal)
        {
            node.Closed=true;
            Submit(new(Path(node),Array.Empty<KernelTeamSearch.Action>(),
                WithInventory(TerminalRecord.Capture(working,party,true,""),working),"retained-tree"),node);
            working=null;position=null;return;
        }
        var source=node.Observed??node.RecipeSource;
        if(source!=null && node.RecipeCursor<source.Children.Count)
        {
            var saved=source.Children[node.RecipeCursor++];
            var edge=saved.Step!;
            var trial=working.ForkForReplay();
            if(edge.Replay(trial,out _,out _))
            {
                var child=accountingNode=Descend(node,new[]{edge});
                if(!ReferenceEquals(child,saved))child.RecipeSource=saved;
                Record(child,Estimate(trial));
                if(trial.HasTerminal)Submit(new(Path(child),Array.Empty<KernelTeamSearch.Action>(),WithInventory(TerminalRecord.Capture(trial,party,true,""),trial),"observed-continuation"),child);
            }
            position=null;working=null;return;
        }
        if(TrackEvidence)OpenMenu(node,working);
        else if(node.Frontier==null){node.Frontier=NewFrontier(working,node==top);CountReopened(node);}
        int width=1+(int)(2*Math.Sqrt(node.Visits+1));
        if(!node.Frontier!.Finished && (node.Children.Count<width || node.Children.All(n=>n.Closed)))
        {
            var branch=working.ForkForReplay();
            if(node.HasFreeWin)CoveredTreeExpansions++;
            TreeStep? step;string reason;Node? registered=null;
            bool expanded=TrackEvidence?ExpandMenu(node,branch,out registered,out step,out _,out reason):
                node.Frontier.Expand(branch,out step,out _,out reason);
            if(expanded)
            {
                var known=registered??FindChild(node,new(step,null));
                var child=accountingNode=known??new Node(node,new Edge(step,null));
                child.Turn=branch.TurnNumber;
                if(step!.Move.Kind==MoveKind.Potion)
                {
                    if(working.RoundsAdvanced==0)OpeningPotionEdges++;else LaterPotionEdges++;
                }
                ulong shape=FollowEnabled || verifiedJoins!=null?ShapeOf(branch):0;
                if(FollowEnabled)Index(child,shape);
                if(known==null){node.Children.Add(child);Nodes++;}
                MaxDepth=Math.Max(MaxDepth,child.Depth);
                if(branch.HasTerminal)
                {
                    child.Closed=true;
                    var path=Path(child);
                    var terminal=WithInventory(TerminalRecord.Capture(branch,party,true,""),branch);
                    Record(child,TreeReward(terminal));
                    Submit(new(path,Array.Empty<KernelTeamSearch.Action>(),terminal,"tree"),child);
                }
                else
                {
                    var features=ReadFeatures(branch);
                    Record(child,ScoreFeatures(features));
                    if(useRoundSearch && (pendingRoundSeed==null || Nodes%127==0))
                        pendingRoundSeed=child;
                    // Spend fewer fresh playouts on alternatives to a verified free suffix.
                    // Already-started continuations and terminal verification always finish.
                    bool startRollout=Simplifying || !(node.HasFreeWin || child.HasFreeWin) || ++lateRolloutStarts%CoveredStride==0;
                    if(startRollout && !QueueJoin(child,branch,shape))
                    {
                        if(useRollouts && rollout==null)
                        {
                            rolloutRoot=child;rolloutWork=0;
                            var policy=Policies[policies++%Policies.Length];
                            rolloutFeatures=features;rolloutPolicy=policy;
                            rollout=new RolloutRun(branch,party,party,new RolloutOptions(policy){InvestmentLookahead=false,SequenceLookahead=false});
                        }
                        else TryExtraRollout(child,branch,features);
                    }
                    IndexJoinNode(child,shape);
                }
            }
            else {Boundary(reason);Record(node,Estimate(working));}
            working=null;position=null;return;
        }
        Node? pick=null;
        double highest=double.NegativeInfinity;int bestBand=int.MinValue;
        foreach(var c in node.Children)
        {
            if(c.Closed)continue;
            double q=SelectionScore(c,node.Visits,ReachedValue(c));
            // Prefer untested continuations, keeping occasional ordinary UCT walks.
            int band=deferTail && (c.HasFreeWin || TrackEvidence && Facts(c).Solved!=null)?-1:0;
            if(deferTail && TrackEvidence && Target is {} target && Facts(c).Upper is {} upper && Compare(upper,target)<=0)
                band-=2;
            if(band>bestBand || band==bestBand && q>highest){pick=c;highest=q;bestBand=band;}
        }
        if(pick==null){node.Closed=true;working=null;position=null;return;}
        if(!pick.Step!.Replay(working,out _,out var mismatch))
        {
            Boundary("重放分歧："+mismatch);if(TrackEvidence)RejectState(pick);pick.Closed=true;working=null;position=null;return;
        }
        position=accountingNode=pick;
    }
    private void RollOne()
    {
        var leaf=rolloutRoot!;
        long before=SimulationMeter.ThreadActions;
        try {rollout!.Step();}
        catch(Exception e) when(e is InvalidOperationException or NotSupportedException)
        {
            Boundary("续打："+e.GetBaseException().Message);
            rollout=null;rolloutRoot=null;return;
        }
        finally
        {
            long work=SimulationMeter.ThreadActions-before;
            rolloutWork+=work;CreditWork(leaf,work);
        }
        if(!rollout.Done)return;
        var result=rollout.Result!;
        var ending=RolloutEnding(rollout);
        if(result.Terminal && rolloutFeatures!=null)LearnFromRollout(leaf,rolloutPolicy,rolloutFeatures,ending);
        rolloutFeatures=null;
        Node? rolloutTip=null;
        if(InSharedTree)
        {
            var tip=rolloutTip=Descend(leaf,result.Actions.Select(a=>new Edge(null,a)));
            AttributeCommittedMoves(leaf,tip,result.Actions.Count,rolloutWork);
            Record(tip,result.Terminal?TreeReward(ending):Estimate(rollout.State));
        }
        else if(result.Terminal)Record(leaf,TreeReward(ending));
        else if(!useRoundSearch)Record(leaf,Estimate(rollout.State));
        if(result.Terminal)
        {
            var path=Path(leaf);
            Submit(new(path,result.Actions,ending,"tree-rollout"),rolloutTip);
        }
        else TruncatedRollouts++;
        CompletedRollouts++;
        rollout=null;rolloutRoot=null;
    }
    private void StartNextProposal()
    {
        if(verifying==null && (waitingWins.Count>0 || waitingPotions.Count>0 || waitingTie!=null || waitingLoss!=null))
        {
            if(waitingWins.TryDequeue(out var win))verifying=win;
            else if(waitingLoss!=null){verifying=waitingLoss;waitingLoss=null;}
            else if(waitingPotions.Count!=0){var entry=waitingPotions.First();waitingPotions.Remove(entry.Key);verifying=entry.Value;}
            else {verifying=waitingTie;waitingTie=null;}
            BeginVerification(verifying!);
        }
    }
    internal bool HasPendingWin=>waitingWins.Count>0 || verifying?.Ending.Victory==true;
    internal void VerifyPendingWin(TimeSpan slice)
    {
        if(waitingWins.Count==0 && verifying?.Ending.Victory!=true)return;
        var timer=Stopwatch.StartNew();
        do
        {
            if(verifying==null && waitingWins.Count==0)break;
            StartNextProposal();
            VerifyOne();
        }while(timer.Elapsed<slice);
    }
    private void BeginVerification(Proposal candidate)
    {
        verifying=candidate;
        replay=root.ForkForReplay();replayIndex=0;replayed.Clear();replayWork.Clear();
        pendingAnchors.Clear();replayedShapes.Clear();verifyingStates.Clear();verificationDeaths=new(root);
        lastPaidStep=0;previousBottles=DeathSaves.UsedBottles(root);previousRevive=DeathSaves.Restored(root);
        previousVerificationHp=party.Select(p=>root.Hp(p.Creature)).ToArray();verifyingFirstHp=null;replayRisks.Clear();verificationRound=replay.RoundsAdvanced;
    }
    private void VerifyOne()
    {
        var p=verifying!;
        var state=replay!;
        int length=p.Prefix.Length+p.Tail.Count;
        if(replayIndex==length)
        {
            var record=WithInventory(TerminalRecord.Capture(state,party,state.HasTerminal,state.HasTerminal?"":"重放未到终局"),state);
            bool resourcesMatch=(p.Origin is "reference" or "prefix-reference") ||
                record.ResourcesConsumed==p.Ending.ResourcesConsumed &&
                record.ConsumablePreservationPremium==p.Ending.ConsumablePreservationPremium;
            if(resourcesMatch && (GoalResult.For(p.Ending) is not {} expectedGoal || expectedGoal==GoalResult.For(record)) && record.OutstandingStolenResource==p.Ending.OutstandingStolenResource &&
                record.Verified && record.Victory==p.Ending.Victory && record.RoundsSurvived==p.Ending.RoundsSurvived &&
                record.PostCombatHp.SequenceEqual(p.Ending.PostCombatHp)
                && record.RemainingEnemies==p.Ending.RemainingEnemies && record.RemainingEnemyHp==p.Ending.RemainingEnemyHp)
            {
                if((TrackEvidence || InSharedTree) && p.Origin is "reference" or "prefix-reference" or "solved-subtree" or "potion-omission")
                    RememberTerminal(Descend(top,p.Prefix.Concat(p.Tail.Select(a=>new Edge(null,a)))),record);
                if(TrackEvidence || InSharedTree)
                {
                    var cursor=top;
                    for(int i=0;i<replayWork.Count;i++)
                    {
                        var edge=i<p.Prefix.Length?p.Prefix[i]:new Edge(null,p.Tail[i-p.Prefix.Length]);
                        var child=FindChild(cursor,edge);
                        if(child==null)break;
                        CreditWork(child,replayWork[i]);cursor=child;
                    }
                }
                Replays++;p.Verified=true;RememberPotionRoute(p,record,state);
                if(p.Origin=="retained-best" && ReferenceEquals(Best?.Script,p.Tail))
                {
                    InstallValidatedPath(p,TreeReward(record));
                }
                if(Best?.Record is not {} old || ComparePlans(record,replayed.Count,old,Best.Script!.Count,verificationDeaths!.Rounds(),BestRoundHp)>0)
                {
                    Best=new TournamentResult(replayed.FirstOrDefault(),record,Array.Empty<TerminalRecord>(),CompletedRollouts,0,"已从原盘面复算"){Script=replayed.ToArray()};
                    BestRisks=ReplayCoverage.RouteDetails(replayRisks.Order().ToArray(),replayed);
                    if(record.Victory)StartPotionSavings(Best.Script!);
                    BestOrigin=p.Origin;
                    bestJoinShapes=replayedShapes.ToArray();
                    BestRoundHp=verificationDeaths!.Rounds();BestKills=verificationDeaths.Freeze();BestTrail=verifyingStates.ToArray();BestFirstHp=verifyingFirstHp;
                    UpdateFocus();
                    BestRecipes=new TreeStep?[replayed.Count];
                    for(int i=0;i<p.Prefix.Length;i++)BestRecipes[i]=p.Prefix[i].Exact;
                    BestBottlesLeft=party.Sum(state.HeldPotionCount);
                    BestTurnNumber=state.TurnNumber;
                    InstallValidatedPath(p,TreeReward(record));
                    IncumbentChanged?.Invoke();
                }
                else if(record.Victory)
                {
                    // Other verified wins can also cover positions already in the tree.
                    var existing=top;
                    if(lastPaidStep==0)existing.HasFreeWin=true;
                    for(int i=0;i<replayed.Count;i++)
                    {
                        var edge=i<p.Prefix.Length?p.Prefix[i]:new Edge(null,p.Tail[i-p.Prefix.Length]);
                        existing=FindChild(existing,edge);
                        if(existing==null)break;
                        if(i+1>=lastPaidStep)existing.HasFreeWin=true;
                    }
                }
            }
            else {RejectedReplays++;Boundary("候选终局与重放不一致");}
            verifying=null;replay=null;return;
        }
        string reason="";
        KernelTeamSearch.Action? resolved=null;
        bool ok;
        long actionStart=SimulationMeter.ThreadActions;
        ReplayCoverage.Begin(state);
        try
        {
            ok=!state.HasTerminal && (replayIndex<p.Prefix.Length
                ? p.Prefix[replayIndex].Replay(state,out resolved,out reason)
                : state.ReplayPlanned(p.Tail[replayIndex-p.Prefix.Length],int.MaxValue,out reason,out resolved));
        }
        finally {ReplayCoverage.End(resolved);}
        if(!ok){RejectedReplays++;Boundary("候选复算："+reason);verifying=null;replay=null;return;}
        int bottles=DeathSaves.UsedBottles(state),revive=DeathSaves.Restored(state);
        bool paid=bottles!=previousBottles || revive!=previousRevive;
        for(int seat=0;seat<party.Length;seat++)
        {
            int hp=state.Hp(party[seat].Creature);
            paid|=hp<previousVerificationHp[seat];
            previousVerificationHp[seat]=hp;
        }
        if(paid)lastPaidStep=replayIndex+1;
        previousBottles=bottles;previousRevive=revive;
        replayWork.Add(SimulationMeter.ThreadActions-actionStart);
        verificationDeaths!.Observe(state,replayIndex,resolved);replayed.Add(resolved!); replayIndex++;
        if(verifyingFirstHp==null && (state.HasTerminal || state.RoundsAdvanced!=root.RoundsAdvanced))
            verifyingFirstHp=party.Select(p=>state.Hp(p.Creature)).ToArray();
        if(FollowEnabled || verifiedJoins!=null)replayedShapes.Add(ShapeOf(state));
        if(FollowEnabled)verifyingStates.Add(state.PlanningState(controlled));
        ReplayCoverage.Collect(state,replayRisks);
        if(!state.HasTerminal && (replayIndex==2 || state.RoundsAdvanced!=verificationRound))
        {
            // Every boundary is eligible. Cache only a few complete boards;
            // later boundaries without a cache can be restored from the path.
            int spacing=Math.Max(1,(p.Ending.RoundsSurvived+7)/8);
            bool cache=replayIndex==2 || state.RoundsAdvanced%spacing==0;
            pendingAnchors.Add((replayIndex,state.TurnNumber,cache?state.ForkForReplay():null));
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
        if(proposal.Ending.Victory && lastPaidStep==0)top.HasFreeWin=true;
        for(int i=0;i<replayed.Count;i++)
        {
            var edge=i<proposal.Prefix.Length?proposal.Prefix[i]:new Edge(null,proposal.Tail[i-proposal.Prefix.Length]);
            var child=FindChild(node,edge);
            if(child==null)
            {
                child=new Node(node,edge){Visits=1,Sum=value};
                node.Children.Add(child);Nodes++;
            }
            node=child;
            if(proposal.Ending.Victory && i+1>=lastPaidStep)node.HasFreeWin=true;
            if(FollowEnabled){Index(node,replayedShapes[i]);bestPathNodes.Add(node);}
            if(checkpoint<pendingAnchors.Count && pendingAnchors[checkpoint].Index==i+1)
            {
                var saved=pendingAnchors[checkpoint++];
                node.Turn=saved.Turn;
                var anchor=new Checkpoint(node,saved.State);
                anchors.Add(anchor);
            }
        }
        node.Closed=true;
    }
}
