using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

// One instance per attempt. Only immutable action descriptions cross the two searches.
internal sealed class BattleExchange
{
    internal sealed class Position(TapeGroup? incoming,string witness)
    {
        internal readonly TapeGroup? Incoming=incoming;
        internal readonly string Witness=witness;
        private SeedRoute? best;
        private readonly Queue<TreeStep[]> branches=new();
        internal void Offer(SeedRoute value){lock(this)best=value;}
        internal void Offer(TreeStep[] path)
        {
            lock(this){if(branches.Count==8)branches.Dequeue();branches.Enqueue(path);}
        }
        internal (SeedRoute? Best,TreeStep[]? Branch) Read(SeedRoute? seen)
        {
            lock(this)
            {
                if(best!=null && best!=seen)return(best,null);
                return(null,branches.TryDequeue(out var path)?path:null);
            }
        }
    }
    private readonly List<Position> positions;
    private SeedRoute? openingRoute;
    internal SeedRoute? OpeningRoute=>System.Threading.Volatile.Read(ref openingRoute);
    internal void PublishOpening(SeedRoute? route)=>System.Threading.Volatile.Write(ref openingRoute,route);
    internal int MatchedPrefix(Position position,SeedRoute route)
    {
        lock(positions)
        {
            int used=0;
            foreach(var point in positions)
            {
                if(point.Incoming is {} group)foreach(var a in group.Actions)
                {
                    if(used>=route.Steps.Length)return -1;
                    var s=route.Steps[used++];
                    bool same=a.Kind switch {
                        "end"=>s.Id=="end",
                        "potion"=>s.Slot>=0 && s.Slot==a.Slot,
                        "card"=>s.Slot<0 && s.Id==a.Id,
                        _=>false};
                    if(!same || s.Seat!=a.Seat || s.Target!=a.Target)return -1;
                }
                if(ReferenceEquals(point,position))return used;
            }
            return -1;
        }
    }
    internal BattleExchange(string witness)=>positions=new(){new(null,witness)};
    internal void Append(TapeGroup group){lock(positions)positions.Add(new(group,group.Witness));}
    internal Position? At(int i){lock(positions)return i<positions.Count?positions[i]:null;}
    internal Position? Current(string witness){lock(positions)return positions[^1].Witness==witness?positions[^1]:null;}
}

internal sealed partial class AnytimeSearch
{
    private IEnumerator<bool>? bridgeReader;
    private BattleExchange.Position? exportPosition;
    internal int OpeningContinuations {get;private set;}
    internal int LiveInputs {get;private set;}
    internal int LivePlans {get;private set;}
    internal int LiveBranches {get;private set;}
    internal void Connect(BattleExchange? exchange,BattleExchange.Position? origin,bool follow)
    {
        if(exchange==null)return;
        if(follow)
        {
            exportPosition=origin;
            if(origin!=null)bridgeReader=AcceptOpening(exchange,origin).GetEnumerator();
        }
        else bridgeReader=AcceptLive(exchange).GetEnumerator();
    }
    internal void AdvanceBridge()
    {
        if(bridgeReader==null)return;
        long start=System.Diagnostics.Stopwatch.GetTimestamp();
        do
        {
            if(!bridgeReader.MoveNext()){bridgeReader.Dispose();bridgeReader=null;return;}
        }while(bridgeReader.Current && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds<2);
    }
    private void OfferBranch(Node child)
    {
        if(exportPosition==null || child.Depth>128 || Nodes>16 && Nodes%32!=0)return;
        var path=Path(child);
        if(path.All(e=>e.Exact!=null))exportPosition.Offer(path.Select(e=>e.Exact!).ToArray());
    }
    private IEnumerable<bool> AcceptOpening(BattleExchange exchange,BattleExchange.Position position)
    {
        SeedRoute? checkedRoute=null;
        while(true)
        {
            var route=exchange.OpeningRoute;
            if(route!=null && !ReferenceEquals(route,checkedRoute))
            {
                checkedRoute=route;
                int used=exchange.MatchedPrefix(position,route);
                if(used>=0 && used<route.Steps.Length)
                {
                    var suffix=used==0?route:route with {Steps=route.Steps[used..]};
                    foreach(var tick in ReplaySeed(suffix,root,top,()=>OpeningContinuations++,fromOpening:true))yield return true;
                }
            }
            yield return false;
        }
    }
    private IEnumerable<bool> AcceptLive(BattleExchange exchange)
    {
        var state=root.ForkForReplay();var node=top;int index=0;
        var position=exchange.At(0)!;
        if(SlCache.Witness(state)!=position.Witness)yield break;
        SeedRoute? seen=null;
        while(true)
        {
            var (best,branch)=position.Read(seen);
            if(best!=null)
            {
                seen=best;
                foreach(var tick in ReplaySeed(best,state,node,()=>{if(index>0)LivePlans++;}))yield return tick;
            }
            else if(branch!=null)
            {
                var check=state.ForkForReplay();var edges=new List<Edge>();
                foreach(var step in branch)
                {
                    var edge=new Edge(step,null);
                    if(check.HasTerminal || !edge.Replay(check,out _,out _))break;
                    edges.Add(edge);yield return false;
                }
                if(edges.Count==branch.Length){InstallSeed(node,edges,check,"实时分支");if(index>0)LiveBranches++;}
            }
            var next=exchange.At(index+1);
            if(next!=null)
            {
                KernelSession? matched=null;
                foreach(var tick in MatchGroup(state,next.Incoming!, (s,edges)=>
                    {matched=s;node=InstallSeed(node,edges,s,"本次实战");LiveInputs+=edges.Count;}))yield return tick;
                if(matched==null)yield break;
                state=matched;position=next;index++;seen=null;
            }
            yield return false;
        }
    }
}
