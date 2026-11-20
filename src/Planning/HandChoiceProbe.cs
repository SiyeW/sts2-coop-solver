using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CoopBots.Kernel;

namespace CoopSolver;

// Optional counterfactual when the player opens a chooser with a different
// card than the current recommendation. Never sends a native choice.
internal sealed class HandChoiceProbe
{
    internal sealed record Answer(KernelChoice[] Choices);
    private volatile bool cancelled;
    private Answer? answer;
    internal Answer? Latest=>Volatile.Read(ref answer);
    internal void Cancel()=>cancelled=true;
    internal HandChoiceProbe(KernelSession root,Move move,SimulationMeter meter)
    {
        var scan=TryChoices(root,move).GetEnumerator();
        void Slice()
        {
            try
            {
                using var count=meter.Enter();
                long start=Stopwatch.GetTimestamp();
                while(!cancelled)
                {
                    if(!scan.MoveNext()){scan.Dispose();return;}
                    if(Stopwatch.GetElapsedTime(start).TotalMilliseconds<3)continue;
                    InteractiveSearch.Dispatcher.Post(Slice);return;
                }
                scan.Dispose();
            }
            catch(Exception ex){scan.Dispose();if(Environment.GetEnvironmentVariable("STS2_UX_TEST")!=null)Console.WriteLine("[ChoiceCheck] "+ex);} // Keep the route visible if this menu cannot be modeled.
        }
        InteractiveSearch.Dispatcher.Post(Slice);
    }
    private IEnumerable<bool> TryChoices(KernelSession root,Move move)
    {
        using var helper=new AnytimeSearch(root,false,false);
        var family=new MoveFamily(move);
        var seats=root.Party;
        int from=root.RoundsAdvanced;
        (bool Win,int Alive,int Hp,int Bottles,int Damage) Best(KernelSession state)=>
            (state.HasWon,seats.Count(p=>state.Hp(p.Creature)>0),seats.Sum(p=>state.Hp(p.Creature)),seats.Sum(state.HeldPotionCount),-state.RemainingOpposition().Hp);
        (bool,int,int,int,int)? best=null;
        do
        {
            var branch=root.ForkForReplay();
            if(family.Expand(branch,out _,out var actual,out _) && actual?.Choices?.Any(c=>c.Effect=="Discard")==true)
            {
                var choices=actual.Choices.ToArray();
                void Score(KernelSession s)
                {
                    AnytimeSearch.RoundPath? path=null;
                    if(!helper.CloseRound(s,from,0,ref path))return;
                    var result=Best(s);
                    if(best==null || result.CompareTo(best.Value)>0){best=result;Volatile.Write(ref answer,new Answer(choices));}
                }
                Score(branch.ForkForReplay());yield return false;
                if(!branch.HasTerminal && branch.RoundsAdvanced==from)
                    foreach(var reply in new ActionFrontier(branch,_=>{}).Families)
                    {
                        var test=branch.ForkForReplay();
                        if(reply.Expand(test,out _,out _,out _))Score(test);
                        yield return false;
                    }
            }
            yield return false;
        }while(!family.Finished);
    }
}
