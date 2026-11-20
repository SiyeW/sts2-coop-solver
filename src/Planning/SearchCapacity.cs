using System;
using System.Collections.Generic;
using System.Threading;

namespace CoopSolver;

// One serial coordinator is shared by both modes. Additional lanes only own
// private simulation forks; their completed proposals return to the owner.
internal static class SearchCapacity
{
    private sealed class Lane
    {
        internal readonly AutoResetEvent Wake=new(false);
        internal Action<int>? Job;
        internal Lane(int id)
        {
            var thread=new Thread(()=>
            {
                while(true)
                {
                    Wake.WaitOne();
                    try {Job!(id);}finally{lock(lanes)Job=null;}
                }
            }){Name="CoopSolver rollout "+(id+2),IsBackground=true,Priority=ThreadPriority.BelowNormal};
            if(ExecutionContext.IsFlowSuppressed())thread.Start();
            else using(ExecutionContext.SuppressFlow())thread.Start();
        }
    }
    private static readonly List<Lane> lanes=new();
    internal static readonly int Available=Math.Max(1,Environment.ProcessorCount);
    private static int limit=1;
    internal static long Started;
    internal static int Limit{get=>Volatile.Read(ref limit);set=>Volatile.Write(ref limit,Math.Clamp(value,1,Available));}
    internal static void SetAndSave(int limit)
    {
        Limit=limit;var cfg=new Godot.ConfigFile();cfg.Load("user://coop-solver-window.cfg");
        cfg.SetValue("search","cores",Limit);cfg.Save("user://coop-solver-window.cfg");
    }
    private static int openingPercent=20;
    internal static int LiveOpeningPercent {get;private set;}=20;
    internal static int OpeningPercent=>Volatile.Read(ref openingPercent);
    internal static void SetOpeningPercent(int value)
    {
        Volatile.Write(ref openingPercent,Math.Clamp(value,0,100));
        var cfg=new Godot.ConfigFile();cfg.Load("user://coop-solver-window.cfg");
        if(OpeningPercent<100)LiveOpeningPercent=OpeningPercent;
        cfg.SetValue("search","live_opening_percent",LiveOpeningPercent);
        cfg.SetValue("search","opening_percent",OpeningPercent);cfg.Save("user://coop-solver-window.cfg");
    }
    internal static void Load()
    {
        var cfg=new Godot.ConfigFile();if(cfg.Load("user://coop-solver-window.cfg")==Godot.Error.Ok)
        {
            Limit=cfg.GetValue("search","cores",1).AsInt32();
            openingPercent=Math.Clamp(cfg.GetValue("search","opening_percent",20).AsInt32(),0,100);
            LiveOpeningPercent=openingPercent<100?openingPercent:Math.Clamp(cfg.GetValue("search","live_opening_percent",20).AsInt32(),0,99);
        }
    }
    internal static bool TryRun(Action<int> job)
    {
        int count=Limit-1;
        if(count==0)return false;
        lock(lanes)
        {
            for(int i=0;i<count;i++)
            {
                if(i==lanes.Count)lanes.Add(new(i));
                if(lanes[i].Job!=null)continue;
                lanes[i].Job=job;Interlocked.Increment(ref Started);lanes[i].Wake.Set();return true;
            }
        }
        return false;
    }
}
