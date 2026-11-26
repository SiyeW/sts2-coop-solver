using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed partial class AnytimeSearch
{
    // A menu belongs to a position. Independent policies keep only a read offset.
    // An imported route can propose an edge, but only the native legal enumerator
    // can give it weight or establish that the menu has no remaining choices.
    internal sealed class EvidenceScope
    {
        internal readonly ConditionalWeakTable<Node,NodeEvidence> Other=new();
        internal readonly Dictionary<int,TerminalRecord> HpBounds=new();
        internal long Revision;
    }
    internal sealed record Solution(TerminalRecord Ending,Edge[] Suffix);
    internal sealed class NodeEvidence(EvidenceScope scope)
    {
        internal readonly EvidenceScope Scope=scope;
        internal Solution? Solved;
        internal bool Inspected,Qualified,Full;
        internal TerminalRecord? Local,Upper,Terminal,CoverageTarget;
        internal TerminalRecord? CompactedUpper;
        internal TerminalRecord? LocalUpper
        {
            get
            {
                var known=Solved?.Ending??Local;
                return CompactedUpper is {} cap && (known==null || Compare(cap,known)<0)?cap:known;
            }
        }
        internal double Fraction;
        internal double Covered(TerminalRecord target)
        {
            if(Upper!=null && Compare(Upper,target)<=0)return 1;
            return CoverageTarget!=null && Compare(CoverageTarget,target)<=0?Fraction:0;
        }
    }
    private EvidenceScope scope=new();
    internal EvidenceScope MemoryScope=>scope;
    internal bool TrackEvidence;
    private bool? monotone;
    internal bool Monotone=>monotone??=NoHealingBound.Applies(root);
    internal TerminalRecord? Target;
    internal bool Simplifying;
    internal bool AllTerminals=>Facts(top).Full;
    internal event Action<Node>? WalkExpanded;
    internal void ChargeReplay(Node n,long before)=>CreditWork(n,SimulationMeter.ThreadActions-before);
    internal void ChargeExpansion(Node parent,Node child,long before)
    {CreditWork(child,SimulationMeter.ThreadActions-before);WalkExpanded?.Invoke(parent);}
    private int scopeRound;
    internal int MenuExpansions {get;private set;}
    private NodeEvidence Facts(Node n)=>Facts(n,scope);
    private static NodeEvidence Facts(Node n,EvidenceScope scope)
    {
        n.Evidence??=new(scope);
        return ReferenceEquals(n.Evidence.Scope,scope)?n.Evidence:scope.Other.GetValue(n,_=>new(scope));
    }
    private void Inspect(Node n,KernelSession state)
    {
        var f=Facts(n);
        if(f.Inspected)return;
        f.Inspected=true;
        if(!(f.Qualified=ProofEvidence.Supported(root,state))){RejectState(n);return;}
        if(state.HasTerminal)
        {
            var ending=WithInventory(TerminalRecord.Capture(state,party,true,""),state);
            if(ending.Verified){RememberTerminal(n,ending);f.Local=f.Terminal=GoalResult.Tag(ending with {RoundsSurvived=ending.RoundsSurvived-root.RoundsAdvanced+scopeRound},GoalResult.For(ending));f.Full=true;}else f.Qualified=false;
        }
        else if(Monotone)
        {
            int hp=state.Hp(party[0].Creature);
            if(!scope.HpBounds.TryGetValue(hp,out var bound))
                scope.HpBounds[hp]=bound=new(true,EvidenceKind.VerifiedTerminal,"",new[]{hp},0,0);
            f.Local=bound;
        }
        f.Upper=f.LocalUpper;
    }
    internal void OpenMenu(Node n,KernelSession state)
    {
        Inspect(n,state);
        if(n.Frontier!=null && n.Legal!=null)return;
        CountReopened(n);
        n.Legal=new();
        if(ReferenceEquals(n,top) && RequiredFirst is {} forced)
        {
            n.UnknownMenu=n.MenuGap=true;
            n.Frontier=new ActionFrontier(state,forced);
        }
        else n.Frontier=new ActionFrontier(state,reason=>{n.MenuGap=n.UnknownMenu=true;Boundary(reason);},measureCoverage:true);
        n.Families=n.Frontier.FamilyCount;
        if(n.Families==0)n.MenuGap=true;
    }
    internal ActionFrontier ReadMenu(Node n,KernelSession state,int index=0)
    {
        OpenMenu(n,state);
        return new(this,n,index<=n.Legal!.Count?index:0);
    }
    internal bool ExpandMenu(Node n,KernelSession branch,out Node? child,out TreeStep? step,
        out KernelTeamSearch.Action? action,out string reason)
    {
        child=null;
        if(!n.Frontier!.Expand(branch,out step,out action,out reason))
        {n.MenuGap=true;return false;}
        child=FindChild(n,new(step,null));
        if(child==null){child=new Node(n,new(step,null));n.Children.Add(child);Nodes++;}
        child.Share=n.Frontier.ChoiceShare/n.Families;
        n.Legal!.Add(child);MenuExpansions++;
        child.Turn=branch.TurnNumber;
        Inspect(child,branch);RefreshBounds(n);
        return true;
    }
    // Back up the best optimistic score among ALL legal children. A gap or
    // unfinished menu can contribute partial coverage, never a closed bound.
    internal void RefreshBounds(Node? n)=>RefreshBounds(n,scope,Target);
    private static void RefreshBounds(Node? n,EvidenceScope scope,TerminalRecord? target)
    {
        while(n!=null)
        {
            var f=Facts(n,scope);
            if(!f.Inspected || !f.Qualified || n.Legal==null)break;
            var upper=f.LocalUpper;
            bool complete=n.Frontier!.Finished && !n.MenuGap && n.Legal.Count>0;
            bool full=complete;
            TerminalRecord? highest=null;
            double fraction=0;
            foreach(var child in n.Legal)
            {
                var c=Facts(child,scope);
                full&=c.Full;
                if(c.Upper==null)complete=false;
                else if(highest==null || Compare(c.Upper,highest)>0)highest=c.Upper;
                if(target!=null)fraction+=child.Share*c.Covered(target);
            }
            if(n.UnknownMenu)fraction=0;
            if(complete && (upper==null || Compare(highest!,upper)<0))upper=highest;
            bool changed=full!=f.Full || !ReferenceEquals(upper,f.Upper) || target!=null &&
                (!ReferenceEquals(f.CoverageTarget,target) || fraction!=f.Fraction);
            f.Full=full;f.Upper=upper;
            if(target!=null){f.CoverageTarget=target;f.Fraction=Math.Clamp(fraction,0,1);}
            if(!changed || n.Parent?.Legal==null || n.Share==0)break;
            n=n.Parent;
        }
    }
    internal void RejectState(Node node)
    {
        var f=Facts(node);
        // A failed replay invalidates any receipt that depended on this branch.
        for(var a=node;a!=null;a=a.Share>0?a.Parent:null)
        {
            var previous=Facts(a);
            if(previous.Solved==null && previous.CompactedUpper==null)continue;
            previous.Solved=null;previous.CompactedUpper=null;scope.Revision++;
            if(ReferenceEquals(a,battle?.Tree))battle!.ForgetOpeningBound();
        }
        f.Inspected=true;f.Solved=null;f.Qualified=f.Full=false;f.Local=f.Terminal=f.Upper=f.CompactedUpper=f.CoverageTarget=null;f.Fraction=0;
        for(var a=node.Parent;a!=null;a=a.Share>0?a.Parent:null)RefreshBounds(a);
    }

    private InteractiveSearch.Line? recordedSolution;
    internal int ReusedSolutions {get;private set;}
    internal void RecordSolution(InteractiveSearch.Line line)
    {
        if(ReferenceEquals(line,recordedSolution))return;
        recordedSolution=line;
        var suffix=new Edge[line.Steps.Length];
        for(int i=0;i<suffix.Length;i++)
        {
            var exact=line.Recipes.ElementAtOrDefault(i);
            suffix[i]=new(exact,null,exact==null?SavedStep.From(root,line.Steps[i],null):null);
        }
        void Store(EvidenceScope destination,TerminalRecord ending)
        {
            var f=Facts(top,destination);
            if(f.Solved==null)destination.Revision++;
            f.Solved=new(ending,suffix);f.Upper=ending;
            if(top.Share>0)RefreshBounds(top.Parent,destination,ReferenceEquals(destination,scope)?Target:null);
        }
        var result=line.Ending!;
        Store(scope,result with {RoundsSurvived=result.RoundsSurvived-root.RoundsAdvanced+scopeRound});
        if(focusCursor && comparablePrefix!=null && !ReferenceEquals(scope,battle!.Evidence))
            Store(battle.Evidence,OpeningScore(result));
    }
    private Solution? lastOfferedSolution;
    private void OfferSolution(Node node,Solution solution)
    {
        if(ReferenceEquals(lastOfferedSolution,solution))return;
        var ending=solution.Ending with {RoundsSurvived=solution.Ending.RoundsSurvived-scopeRound+root.RoundsAdvanced};
        if(Best?.Record is {} old && Compare(ending,old)<=0)return;
        lastOfferedSolution=solution;
        Submit(new(Path(node).Concat(solution.Suffix).ToArray(),Array.Empty<KernelTeamSearch.Action>(),ending,"solved-subtree"));
    }
    internal IEnumerable<bool> UseSolution(Node node,Solution solution,Action failed)
    {
        ReusedSolutions++;
        return VerifyLeaf(node,solution.Ending,false,failed,solution.Suffix);
    }
    internal IEnumerable<bool> VerifyLeaf(Node node,TerminalRecord expected,bool full,Action failed,Edge[]? suffix=null)
    {
        expected=GoalResult.Tag(expected with {RoundsSurvived=expected.RoundsSurvived-scopeRound+root.RoundsAdvanced},GoalResult.For(expected));
        if(!full && Target!=null && Compare(expected,Target)<=0)yield break;
        if(Best?.Record is {} old)
        {
            int rank=Compare(expected,old);
            if(rank<0 || rank==0 && (!full || !expected.Victory))
            {
                if(full && (expected.Victory || !old.Victory) && CollectPotions)
                {
                    var alternative=new Proposal(suffix==null?Path(node):Path(node).Concat(suffix).ToArray(),Array.Empty<KernelTeamSearch.Action>(),expected,"shared-tree");
                    if(QueuePotionAlternative(alternative))
                    {
                        StartNextProposal();
                        while(verifying!=null || waitingPotions.Count!=0){StartNextProposal();VerifyOne();yield return true;}
                    }
                }
                yield break;
            }
        }
        // Finish an outstanding proposal before feeding this leaf into the same
        // verifier. Tied terminals must all be compared before declaring exhaustion.
        while(verifying!=null){VerifyOne();yield return true;}
        var candidate=new Proposal(suffix==null?Path(node):Path(node).Concat(suffix).ToArray(),Array.Empty<KernelTeamSearch.Action>(),expected,"shared-tree");
        BeginVerification(candidate);
        while(ReferenceEquals(verifying,candidate)){VerifyOne();yield return true;}
        if(!candidate.Verified){RejectState(node);failed();}
    }
    internal NodeEvidence EvidenceAt(Node n)=>Facts(n);
    internal void ReadState(Node n,KernelSession state)
    {
        if(Facts(n).Inspected)return;
        Inspect(n,state);RefreshBounds(n.Parent);
    }
    internal bool Proves(TerminalRecord result)=>Facts(top).Upper is {} upper && Compare(upper,result)==0;
    internal double Coverage(TerminalRecord result)=>Facts(top).Covered(result);
}
