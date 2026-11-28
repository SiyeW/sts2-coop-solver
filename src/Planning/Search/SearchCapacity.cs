using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;

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
                    try{Job!(id);}
                    catch(Exception error) when(error is not OutOfMemoryException)
                    {Godot.GD.PrintErr("[CoopSolver] Parallel rollout worker: "+error);}
                    finally{lock(lanes)Job=null;}
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
    private static readonly int[] memoryLimits={0,25,50,75,100,250,500,750,1000,2500,5000};
    private const int DefaultLimitMb=500;
    private static int memoryLimitMb=DefaultLimitMb;
    internal static int MemoryLimitMb=>Volatile.Read(ref memoryLimitMb);
    internal static void SetMemoryLimit(int mb)
    {
        if(!memoryLimits.Contains(mb))throw new ArgumentOutOfRangeException(nameof(mb));
        Volatile.Write(ref memoryLimitMb,mb);
        var cfg=new Godot.ConfigFile();cfg.Load("user://coop-solver-window.cfg");
        cfg.SetValue("search","tree_limit_mb",mb);cfg.Save("user://coop-solver-window.cfg");
    }
    internal static void BindMemory(InlineMenus menus,Godot.Button memory)
    {
        var items=memoryLimits.Select(mb=>new InlineMenus.Item(mb,mb==0?Ui.Text("ui.107"):
            mb<1000?$"{mb} MB":(mb/1000d).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" GB")).ToArray();
        menus.Bind(memory,memory.Name+"Menu",items,()=>MemoryLimitMb,SetMemoryLimit);
    }
    private static int openingPercent=20;
    internal static int LiveOpeningPercent {get;private set;}=20;
    internal static int OpeningPercent=>Volatile.Read(ref openingPercent);
    internal static void SetOpeningPercent(int value,bool persist=true)
    {
        Volatile.Write(ref openingPercent,Math.Clamp(value,0,100));
        if(OpeningPercent<100)LiveOpeningPercent=OpeningPercent;
        if(!persist)return;
        var cfg=new Godot.ConfigFile();cfg.Load("user://coop-solver-window.cfg");
        cfg.SetValue("search","live_opening_percent",LiveOpeningPercent);
        cfg.SetValue("search","opening_percent",OpeningPercent);cfg.Save("user://coop-solver-window.cfg");
    }
    internal static void Load()
    {
        var cfg=new Godot.ConfigFile();if(cfg.Load("user://coop-solver-window.cfg")==Godot.Error.Ok)
        {
            int initial=cfg.GetValue("search","memory_reserve_percent",1).AsInt32()==0?0:DefaultLimitMb;
            int mb=cfg.GetValue("search","tree_limit_mb",initial).AsInt32();
            Volatile.Write(ref memoryLimitMb,memoryLimits.Contains(mb)?mb:DefaultLimitMb);
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
