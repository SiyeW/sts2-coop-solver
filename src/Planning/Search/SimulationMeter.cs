using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

// Scoped per advisor, including reference-search tasks that inherit its execution context.
internal sealed class SimulationMeter
{
    private static readonly AsyncLocal<SimulationMeter?> owner=new();
    [ThreadStatic] internal static long ThreadActions;
    private long sampleAt,sampleCount,rate;
    internal long Rate(long now,bool running)
    {
        long count=Plays+Potions+Ends;
        if(!running || sampleAt==0){sampleAt=now;sampleCount=count;return rate=0;}
        if(now-sampleAt>=1000)
        {
            rate=(count-sampleCount)*1000/(now-sampleAt);sampleAt=now;sampleCount=count;
        }
        return rate;
    }
    private long retained,matchedWork;
    private int hasGraphCarry;
    internal void InheritGraphWork(long work)
    {
        // A paused route check may finish before the live branch is attached.
        // An empty result must leave the later, verified handoff eligible.
        if(work<=0)return;
        if(Interlocked.Exchange(ref hasGraphCarry,1)==0)
            Interlocked.Exchange(ref matchedWork,Math.Max(work,Interlocked.Read(ref matchedWork)));
    }
    internal void Retain(long count)=>Interlocked.Add(ref retained,count);
    internal sealed class Counter
    {
        internal long Plays,Potions,Ends;
        internal long Total=>Interlocked.Read(ref Plays)+Interlocked.Read(ref Potions)+Interlocked.Read(ref Ends);
    }
    internal readonly Counter Fresh=new();
    internal long Plays=>Interlocked.Read(ref Fresh.Plays);
    internal long Potions=>Interlocked.Read(ref Fresh.Potions);
    internal long Ends=>Interlocked.Read(ref Fresh.Ends);
    internal long Inherited=>Math.Max(Interlocked.Read(ref matchedWork),Interlocked.Read(ref retained));
    internal long Actions=>Fresh.Total+Inherited;
    private readonly record struct FirstMove(ulong Seat,string Id,int Slot,uint? Target);
    private sealed class Bucket {internal long Count;}
    private sealed class Book
    {
        internal ulong[] Players=Array.Empty<ulong>();
        internal readonly ConcurrentDictionary<FirstMove,Bucket> Branches=new();
        internal readonly Dictionary<CardModel,int> Cards=new();
        internal readonly Dictionary<PotionModel,int> Potions=new();
    }
    private sealed record Tag(Book Book,Bucket? Branch);
    private readonly ConditionalWeakTable<KernelSession,Tag> tags=new();
    private volatile Book? currentBook;
    internal SearchTreeMemory? Memory;
    [ThreadStatic] private static int forkSamples;
    internal void Measure(SearchTreeMemory.Survey survey)
    {
        if(currentBook is {} b && survey.Include(b,64))
            survey.Owned(96+64L*b.Branches.Count+32L*b.Cards.EnsureCapacity(0)+32L*b.Potions.EnsureCapacity(0));
    }
    internal void BeginRoot(KernelSession root)
    {
        var book=new Book{Players=root.Party.Select(p=>p.NetId).ToArray()};
        foreach(var p in root.Party)
        {
            var hand=root.Hand(p);
            for(int i=0;i<hand.Count;i++)book.Cards[hand[i]]=i;
            foreach(var bottle in root.CapturedPotions(p))book.Potions[bottle]=CapturedBelt.Slot(root,bottle);
        }
        currentBook=book;tags.AddOrUpdate(root,new(book,null));
    }
    internal long BranchVisits(KernelSession root,KernelTeamSearch.Action step)
    {
        if(!tags.TryGetValue(root,out var tag))return 0;
        int slot=step.Card!=null?tag.Book.Cards.GetValueOrDefault(step.Card,-1):
            step.Potion!=null?tag.Book.Potions.GetValueOrDefault(step.Potion,-1):-1;
        var key=new FirstMove(step.Player.NetId,step.Card?.Id.Entry??step.Potion?.Id.Entry??"end",slot,step.Target?.CombatId);
        return tag.Book.Branches.TryGetValue(key,out var b)?Interlocked.Read(ref b.Count):0;
    }
    private static void SampleFork(out long __state)=>__state=(++forkSamples&63)==0 && owner.Value?.Memory!=null?
        GC.GetAllocatedBytesForCurrentThread():0;
    private static void Forked(KernelSession __instance,KernelSession __result,long __state)
    {
        var m=owner.Value;
        if(__state!=0)m?.Memory?.SampleBoard(GC.GetAllocatedBytesForCurrentThread()-__state);
        if(m!=null && m.tags.TryGetValue(__instance,out var tag))m.tags.AddOrUpdate(__result,tag);
    }
    private static Tag? Charge(SimulationMeter m,KernelSession state,CardModel? card,PotionModel? potion,Player? player,Creature? target)
    {
        if(currentLane!=null)Volatile.Write(ref currentLane.Current,state);
        if(!m.tags.TryGetValue(state,out var tag))return null;
        var bucket=tag.Branch;
        if(bucket==null)
        {
            var seat=player??card?.Owner??potion!.Owner;
            var key=new FirstMove(seat.NetId,card?.Id.Entry??potion?.Id.Entry??"end",
                card!=null?tag.Book.Cards.GetValueOrDefault(card,-1):potion!=null?tag.Book.Potions.GetValueOrDefault(potion,-1):-1,target?.CombatId);
            bucket=tag.Book.Branches.GetOrAdd(key,static _=>new());
        }
        Interlocked.Increment(ref bucket.Count);
        if(tag.Branch==bucket)return null;
        m.tags.AddOrUpdate(state,new(tag.Book,bucket));
        return tag;
    }
    private static void FinishAttempt(KernelSession __instance,bool __result,Tag? __state)
    {
        if(!__result && __state!=null)owner.Value?.tags.AddOrUpdate(__instance,__state);
    }
    internal static void Install()
    {
        var harmony=new Harmony("sts2.coopsolver.trial-count");
        harmony.Patch(AccessTools.Method(typeof(KernelSession),"Fork"),prefix:new HarmonyMethod(typeof(SimulationMeter),nameof(SampleFork)),postfix:new HarmonyMethod(typeof(SimulationMeter),nameof(Forked)));
        var finish=new HarmonyMethod(typeof(SimulationMeter),nameof(FinishAttempt));
        harmony.Patch(AccessTools.Method(typeof(KernelSession),"Play"),
            prefix:new HarmonyMethod(typeof(SimulationMeter),nameof(Count)),postfix:finish);
        harmony.Patch(AccessTools.Method(typeof(KernelSession),"UsePotion",new[]{typeof(PotionModel),
                typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),typeof(IReadOnlyList<KernelChoice>),typeof(string).MakeByRefType()}),
            prefix:new HarmonyMethod(typeof(SimulationMeter),nameof(CountPotion)),postfix:finish);
        harmony.Patch(AccessTools.Method(typeof(KernelSession),"EndTurnCore"),
            prefix:new HarmonyMethod(typeof(SimulationMeter),nameof(CountEnd)),postfix:finish);
    }
    private static void Count(KernelSession __instance,CardModel __0,Creature? __1,out Tag? __state)
    {
        ReplayCoverage.ObservePlay(__instance,__0);
        __state=null;if(owner.Value is not {} meter)return;
        ThreadActions++;Interlocked.Increment(ref meter.Fresh.Plays);__state=Charge(meter,__instance,__0,null,null,__1);
    }
    private static void CountPotion(KernelSession __instance,PotionModel __0,Creature? __1,out Tag? __state)
    {
        __state=null;if(owner.Value is not {} meter)return;
        ThreadActions++;Interlocked.Increment(ref meter.Fresh.Potions);__state=Charge(meter,__instance,null,__0,null,__1);
    }
    private static void CountEnd(KernelSession __instance,Player __0,out Tag? __state)
    {
        __state=null;if(owner.Value is not {} meter)return;
        ThreadActions++;Interlocked.Increment(ref meter.Fresh.Ends);__state=Charge(meter,__instance,null,null,__0,null);
    }
    [ThreadStatic] private static SearchTreeMemory.Lane? currentLane;
    internal Scope Enter()=>new(this);
    internal readonly struct Scope : IDisposable
    {
        private readonly SimulationMeter? previous;
        private readonly SearchTreeMemory.Lane? priorLane;
        private readonly KernelSession? priorSession;
        internal Scope(SimulationMeter meter)
        {
            previous=owner.Value;priorLane=currentLane;priorSession=currentLane?.Current;
            owner.Value=meter;currentLane=meter.Memory?.Worker();
        }
        public void Dispose()
        {
            if(currentLane!=null)Volatile.Write(ref currentLane.Current,null);
            currentLane=priorLane;if(priorLane!=null)Volatile.Write(ref priorLane.Current,priorSession);
            owner.Value=previous;
        }
    }
}
