using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;
internal sealed partial class AnytimeSearch
{
    // This is an initial feasible recommendation. The subsequent search keeps
    // access to every legal action, including setup which costs health now.
    private Node? defenseCursor;
    internal int[]? PassHp {get;private set;}
    internal int[]? DefendedHp {get;private set;}
    internal IEnumerable<bool> DefensiveOpening()
    {
        if(GoalResult.KindAt(root) is CombatGoal.Card or CombatGoal.Training)
            foreach(var tick in WaitForEscape())yield return tick;
        try
        {
            var current=root.ForkForReplay();
            RoundPath? prefix=null;var node=defenseCursor=top;
            int from=current.RoundsAdvanced;
            (int Alive,int Hp,double Worst,int Bottles,int Damage) Rank(KernelSession s)=>
                (party.Count(p=>s.Hp(p.Creature)>0),party.Sum(p=>s.Hp(p.Creature))-DeathSaves.Restored(s),-party.Max(p=>Math.Max(0,root.Hp(p.Creature)-s.Hp(p.Creature))/(double)Math.Max(1,root.Hp(p.Creature))),party.Sum(s.HeldPotionCount),-s.RemainingOpposition().Hp);
            while(!current.HasTerminal && current.RoundsAdvanced==from)
            {
                var stop=current.ForkForReplay();var stopPath=prefix;
                if(!CloseRound(stop,from,0,ref stopPath))yield break;
                PassHp??=party.Select(p=>stop.Hp(p.Creature)).ToArray();
                var strongest=Rank(stop);
                KernelSession? next=null;RoundPath? nextPath=null;
                var frontier=TrackEvidence?ReadMenu(node,current):new ActionFrontier(current,Boundary);
                int families=frontier.FamilyCount;
                for(int i=0;i<families && !frontier.Finished;i++)
                {
                    var branch=current.ForkForReplay();
                    if(frontier.Expand(branch,out var step,out _,out _))
                    {
                        var path=new RoundPath(prefix,step!);
                        var after=branch.ForkForReplay();RoundPath? pathEnd=path;
                        if(CloseRound(after,from,0,ref pathEnd))
                        {
                            if(after.HasWon){OfferRoundEnding(after,Array.Empty<Edge>(),pathEnd);yield break;}
                            if(Rank(after).CompareTo(strongest)>0)
                            {strongest=Rank(after);next=branch;nextPath=path;stop=after;stopPath=pathEnd;}
                        }
                    }
                    yield return false;
                }
                if(next==null || next.RoundsAdvanced!=from)
                {
                    DefendedHp=party.Select(p=>stop.Hp(p.Creature)).ToArray();
                    if(stop.HasTerminal){OfferRoundEnding(stop,Array.Empty<Edge>(),stopPath);yield break;}
                    yield return false;
                    var suffix=new RolloutRun(stop.ForkForReplay(),party,party,new RolloutOptions(RolloutPolicyKind.Defend)
                        {InvestmentLookahead=false,SequenceLookahead=false});
                    while(!suffix.Done){suffix.Step();yield return false;}
                    if(suffix.Result is {Terminal:true} result)
                        Submit(new(RoundRoute(Array.Empty<Edge>(),stopPath),result.Actions,RolloutEnding(suffix),"defensive-opening"));
                    yield break;
                }
                current=next;prefix=nextPath;
                if(TrackEvidence)node=defenseCursor=FindChild(node,new(nextPath!.Step,null))!;
            }
        }
        finally{defenseCursor=null;}
    }
    // A cheap first witness for accepting the escape. Normal legal expansion
    // still explores blocks, setup and faster kills.
    private IEnumerable<bool> WaitForEscape()
    {
        if(RequiredFirst!=null)yield break;
        var state=root.ForkForReplay();var prefix=new List<Edge>();
        while(!state.HasTerminal && state.RoundsAdvanced-root.RoundsAdvanced<6)
        {
            var seat=Array.FindIndex(party,state.CanAct);
            if(seat<0 || !new ActionFrontier(state,new Move(seat,MoveKind.End,-1,"",null)).Expand(state,out var step,out _,out _))yield break;
            prefix.Add(new(step,null));yield return true;
        }
        if(state.HasWon)Submit(new(prefix.ToArray(),Array.Empty<KernelTeamSearch.Action>(),
            WithInventory(TerminalRecord.Capture(state,party,true,""),state),"escape"));
    }

}
