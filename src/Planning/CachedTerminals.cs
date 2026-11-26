using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed partial class AnytimeSearch
{
    // Ranking hint only; a caller at another observed root must replay the suffix.
    private void RememberTerminal(Node tip,TerminalRecord record)
    {
        var ending=comparablePrefix==null?record:OpeningScore(record);
        tip.ReachedEnd=ending;
        for(Node? n=tip;n!=null;n=n.Parent)
        {
            var best=n.BestTerminal;
            if(best!=null && !ReferenceEquals(best,tip) && best.ReachedEnd is {} previous &&
                ComparePlans(ending,tip.Depth-n.Depth,previous,best.Depth-n.Depth)<=0)break;
            n.BestTerminal=tip;
            if(n==top || n.Parent?.Observed==n)break;
        }
    }
    internal static Edge[]? ToTerminal(Node origin,Node leaf)
    {
        var steps=new Stack<Edge>();
        for(var n=leaf;n!=origin;n=n.Parent!)
        {
            if(n.Parent==null || n.Step==null)return null;
            steps.Push(n.Step);
        }
        return steps.ToArray();
    }
}

internal sealed partial class BattleExchange
{
    // Match only recorded edges. In particular, do not create a child for an
    // unsearched real input or open a new menu while the solver is paused.
    internal IEnumerable<bool> CachedNodes(Position goal,Action<AnytimeSearch.Node> found)
    {
        if(goal.Anchor!=null)
        {
            if(goal.Anchor.Observed!=null)found(goal.Anchor.Observed);
            found(goal.Anchor);
            if(goal.FromPreviousLive!=null)found(goal.FromPreviousLive);
        }
        Position[] prefix;
        lock(positions)
        {
            int at=positions.IndexOf(goal);
            if(at<0)yield break;
            prefix=positions.Take(at+1).ToArray();
        }
        foreach(var step in CachedFrom(Tree,Opening,prefix.Skip(1).Select(p=>p.Incoming!),found))yield return step;
    }
    internal static IEnumerable<bool> CachedFrom(AnytimeSearch.Node tree,KernelSession origin,IEnumerable<TapeGroup> groups,Action<AnytimeSearch.Node> found)
    {
        var cursors=new List<(AnytimeSearch.Node Node,KernelSession State)>{(tree,origin)};
        foreach(var group in groups)
        {
            foreach(var wanted in group.Actions)
            {
                var next=new List<(AnytimeSearch.Node,KernelSession)>();
                foreach(var cursor in cursors)
                {
                    for(int i=0,count=cursor.Node.Children.Count;i<count && i<cursor.Node.Children.Count;i++)
                    {
                        if((i&63)==63)yield return true;
                        var child=cursor.Node.Children[i];
                        if(child.BestTerminal==null && child.Observed==null && child.SubtreeWork==0)continue;
                        var edge=child.Step!;
                        var state=cursor.State;
                        if(edge.Exact is {} exact)
                        {
                            var m=exact.Move;
                            if(state.Party[m.Seat].NetId!=wanted.Seat || m.Target!=wanted.Target)continue;
                            if(wanted.Kind=="card" && (m.Kind!=MoveKind.Card || (wanted.Copy!=null && m.Key!=wanted.Copy.Key) ||
                                (uint)m.Slot>=(uint)state.Hand(state.Party[m.Seat]).Count || state.Hand(state.Party[m.Seat])[m.Slot].Id.Entry!=wanted.Id) ||
                                wanted.Kind=="potion" && (m.Kind!=MoveKind.Potion || m.Slot!=wanted.Slot) ||
                                wanted.Kind=="end" && m.Kind!=MoveKind.End)continue;
                        }
                        else
                        {
                            var a=edge.Prior??edge.Saved?.Bind(state);
                            if(a==null || a.Player.NetId!=wanted.Seat || a.Target?.CombatId!=wanted.Target ||
                                wanted.Kind=="card" && (a.Card?.Id.Entry!=wanted.Id || wanted.Copy!=null && a.CardStateKey!=wanted.Copy.Key) ||
                                wanted.Kind=="potion" && (a.Potion==null || CapturedBelt.Slot(state,a.Potion)!=wanted.Slot) ||
                                wanted.Kind=="end" && !a.EndTurn)continue;
                        }
                        var copy=state.ForkForReplay();
                        if(edge.Replay(copy,out _,out _))next.Add((child,copy));
                        yield return true;
                    }
                }
                cursors=next;
            }
            cursors.RemoveAll(c=>SlCache.Witness(c.State)!=group.Witness);
            if(cursors.Count==0)yield break;
        }
        foreach(var cursor in cursors)found(cursor.Node);
    }
}
