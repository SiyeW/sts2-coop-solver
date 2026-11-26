using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private PausedRoute? pausedRoute;
    private KernelSession? pausedOrigin;
    private readonly List<TapeGroup> pausedInputs=new();
    private void TrackPausedInput(KernelSession fresh,KernelSession? previous)
    {
        if(owner is not {} controller || !controller.manuallyPaused && !controller.Memory.Waiting() || previous==null || completedInputs.Count==0)
        {pausedOrigin=null;pausedInputs.Clear();return;}
        pausedOrigin??=previous;
        pausedInputs.Add(new(completedInputs.Select(a=>new TapeAction(a.Seat,a.Kind,a.Id,a.Slot,a.Target,a.Copy)).ToArray(),SlCache.Witness(fresh)));
    }
    private bool pausedRouteRead;

    private void CancelPausedRoute()
    {
        if(pausedRoute!=null)pausedRoute.Cancelled=true;
        pausedRoute=null;pausedRouteRead=false;
    }

    private void UpdatePausedRoute()
    {
        if(root==null || capturedWitness==null || pausedRouteRead || pausedRoute==null && savedResult?.Ending?.Verified==true)return;
        if(pausedRoute==null)
        {
            var suffix=pendingSuffix??(pendingCheckVisible?shownLine:null);
            pausedRoute=new PausedRoute(root,suffix,owner?.exchange,livePosition,owner?.SeedDirectory,capturedWitness,owner?.livePotion.Manual!=null,engine??owner?.engine,pausedOrigin,pausedInputs.ToArray(),owner?.localRoutes?.ForPosition(capturedWitness)?.Seed);
        }
        var verified=pausedRoute.Result;
        if(verified!=null && !ReferenceEquals(verified,pausedRoute.Published))
        {
            pausedRoute.Published=verified;
            RenderLine(verified);pendingSuffix=verified;
            pendingCheckVisible=false;lastTrim=liveMessage="";
            owner!.nextHud=0;
        }
        if(!pausedRoute.Done)return;
        // Acquire the final publication even if the worker finished between reads.
        if(!ReferenceEquals(verified,pausedRoute.Result))return;
        pausedRouteRead=true;
        meter.InheritGraphWork(pausedRoute.BranchWork);
        pendingCheckVisible=false;
        if(verified==null)
        {
            RejectLiveRoute();
            liveMessage="当前盘面没有已核实的路线";
        }
        owner!.nextHud=0;
    }

    // Run only retained scripts against a frozen board. No planner, new moves,
    // visits or estimates. The published result is consumed on the game thread.
    internal sealed class PausedRoute
    {
        internal volatile bool Cancelled,Done,OrderConfirmed;
        internal volatile InteractiveSearch.Line? Result;
        internal InteractiveSearch.Line? Published;
        internal long BranchWork;
        internal bool TreeHit {get;private set;}
        private readonly IEnumerator<bool> job;
        internal PausedRoute(KernelSession board,InteractiveSearch.Line? line,BattleExchange? battle,
            BattleExchange.Position? position,string? directory,string witness,bool manual,InteractiveSearch? engine=null,KernelSession? origin=null,TapeGroup[]? inputs=null,SeedRoute? localSeed=null)
        {
            job=Check(board,line,battle,position,directory,witness,manual,engine,origin,inputs,localSeed).GetEnumerator();
            InteractiveSearch.Dispatcher.Post(Slice,true);
        }
        private void Slice()
        {
            try
            {
                var clock=Stopwatch.StartNew();
                while(!Cancelled)
                {
                    if(!job.MoveNext())break;
                    if(clock.ElapsedMilliseconds<3)continue;
                    InteractiveSearch.Dispatcher.Post(Slice,true);return;
                }
            }
            catch(Exception e){Godot.GD.PrintErr("[CoopSolver] Cached route: "+e.GetBaseException().Message);}
            job.Dispose();Done=true;
        }
        private IEnumerable<bool> Check(KernelSession board,InteractiveSearch.Line? line,BattleExchange? battle,
            BattleExchange.Position? position,string? directory,string witness,bool manual,InteractiveSearch? engine=null,KernelSession? origin=null,TapeGroup[]? inputs=null,SeedRoute? localSeed=null)
        {
            void Keep(InteractiveSearch.Line value,KernelSession state)
            {
                if(value.Ending is not {Verified:true})return;
                if(Result==null || AnytimeSearch.CompareLines(value,Result)>0)Result=value;
            }
            if(line!=null)
            {
                // Preserve the selected route, including a manual potion choice.
                foreach(var tick in InteractiveSearch.ReplayKnown(board,line,null,Keep,checkedOrder:()=>OrderConfirmed=true))yield return tick;

            }
            var roots=new HashSet<AnytimeSearch.Node>();
            if(origin!=null && inputs!=null && engine?.CurrentTree(origin) is {} previous)
                foreach(var tick in BattleExchange.CachedFrom(previous,origin,inputs,n=>roots.Add(n)))yield return tick;
            if(position!=null && battle!=null)
                foreach(var tick in battle.CachedNodes(position,n=>roots.Add(n)))yield return tick;
            foreach(var n in roots)
            {
                BranchWork=Math.Max(BranchWork,System.Threading.Interlocked.Read(ref n.SubtreeWork));
                if(manual && Result!=null || n.BestTerminal is not {} best)continue;
                bool matched=false;
                IEnumerable<bool> Replay(AnytimeSearch.Node tip)
                {
                    var edges=AnytimeSearch.ToTerminal(n,tip);
                    if(edges==null || edges.Length==0)yield break;
                    foreach(var tick in InteractiveSearch.ReplayKnown(board,null,null,(v,s)=>{
                        Keep(v,s);TreeHit=true;
                        matched=v.Ending!.Victory==tip.ReachedEnd!.Victory &&
                            v.Hp.SequenceEqual(tip.ReachedEnd.PostCombatHp);
                    },edges))yield return tick;
                }
                foreach(var tick in Replay(best))yield return tick;
                if(matched)continue;
                // An observed board can invalidate the highest-ranked script.
                // Other known leaves remain eligible, but no missing edge is generated.
                var stack=new Stack<AnytimeSearch.Node>();stack.Push(n);
                while(stack.TryPop(out var child))
                {
                    if(child!=best && child.ReachedEnd!=null)foreach(var tick in Replay(child))yield return tick;
                    foreach(var next in child.Children)if(next.BestTerminal!=null)stack.Push(next);
                    yield return true;
                }
            }
            if(position!=null && battle!=null)
            {
                int seen=0;
                foreach(var saved in battle.NewContinuations(position,ref seen))
                {
                    if(!saved.Route.Terminal)continue;
                    var suffix=saved.Route with {Steps=saved.Route.Steps[saved.Used..]};
                    foreach(var tick in InteractiveSearch.ReplayKnown(board,null,suffix,Keep))yield return tick;
                }
            }
            if(localSeed is {Terminal:true})
                foreach(var tick in InteractiveSearch.ReplayKnown(board,null,localSeed,Keep))yield return tick;
            if(directory==null)yield break;
            string file=Path.Combine(directory,witness+".continuation");
            if(!File.Exists(file))yield break;
            var disk=JsonSerializer.Deserialize<OpeningResult>(File.ReadAllBytes(file));
            if(disk?.Seed is {Terminal:true} seed)
                foreach(var tick in InteractiveSearch.ReplayKnown(board,null,seed,Keep))yield return tick;
        }
    }
}
