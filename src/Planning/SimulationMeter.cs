using System;
using System.Collections.Generic;
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
    private long retained;
    internal void Retain(long count)=>Interlocked.Add(ref retained,count);
    private long plays,potions,ends;
    internal long Plays=>Interlocked.Read(ref plays);
    internal long Potions=>Interlocked.Read(ref potions);
    internal long Ends=>Interlocked.Read(ref ends);
    internal long Actions=>Plays+Potions+Ends+Interlocked.Read(ref retained);
    private readonly record struct FirstMove(ulong Seat,string Id,int Slot,uint? Target);
    private sealed class Bucket {internal long Count;}
    private sealed class Book
    {
        internal readonly ConcurrentDictionary<FirstMove,Bucket> Branches=new();
        internal readonly Dictionary<CardModel,int> Cards=new();
        internal readonly Dictionary<PotionModel,int> Potions=new();
    }
    private sealed record Tag(Book Book,Bucket? Branch);
    private readonly ConditionalWeakTable<KernelSession,Tag> tags=new();
    internal void BeginRoot(KernelSession root)
    {
        var book=new Book();
        foreach(var p in root.Party)
        {
            var hand=root.Hand(p);
            for(int i=0;i<hand.Count;i++)book.Cards[hand[i]]=i;
            foreach(var bottle in root.CapturedPotions(p))book.Potions[bottle]=CapturedBelt.Slot(root,bottle);
        }
        tags.AddOrUpdate(root,new(book,null));
    }
    internal long BranchVisits(KernelSession root,KernelTeamSearch.Action step)
    {
        if(!tags.TryGetValue(root,out var tag))return 0;
        int slot=step.Card!=null?tag.Book.Cards.GetValueOrDefault(step.Card,-1):
            step.Potion!=null?tag.Book.Potions.GetValueOrDefault(step.Potion,-1):-1;
        var key=new FirstMove(step.Player.NetId,step.Card?.Id.Entry??step.Potion?.Id.Entry??"end",slot,step.Target?.CombatId);
        return tag.Book.Branches.TryGetValue(key,out var b)?Interlocked.Read(ref b.Count):0;
    }
    private static void Forked(KernelSession __instance,KernelSession __result)
    {
        var m=owner.Value;
        if(m!=null && m.tags.TryGetValue(__instance,out var tag))m.tags.AddOrUpdate(__result,tag);
    }
    private static Tag? Charge(SimulationMeter m,KernelSession state,CardModel? card,PotionModel? potion,Player? player,Creature? target)
    {
        if(!m.tags.TryGetValue(state,out var tag))return null;
        var bucket=tag.Branch;
        if(bucket!=null){Interlocked.Increment(ref bucket.Count);return null;}
        var seat=player??card?.Owner??potion!.Owner;
        var key=new FirstMove(seat.NetId,card?.Id.Entry??potion?.Id.Entry??"end",
            card!=null?tag.Book.Cards.GetValueOrDefault(card,-1):potion!=null?tag.Book.Potions.GetValueOrDefault(potion,-1):-1,target?.CombatId);
        bucket=tag.Book.Branches.GetOrAdd(key,static _=>new());
        m.tags.AddOrUpdate(state,new(tag.Book,bucket));Interlocked.Increment(ref bucket.Count);
        return tag;
    }
    private static void FinishAttempt(KernelSession __instance,bool __result,Tag? __state)
    {
        if(!__result && __state!=null)owner.Value?.tags.AddOrUpdate(__instance,__state);
    }
    internal static void Install()
    {
        var harmony=new Harmony("sts2.coopsolver.trial-count");
        harmony.Patch(AccessTools.Method(typeof(KernelSession),"Fork"),postfix:new HarmonyMethod(typeof(SimulationMeter),nameof(Forked)));
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
        __state=null;if(owner.Value is not {} meter)return;
        ThreadActions++;Interlocked.Increment(ref meter.plays);__state=Charge(meter,__instance,__0,null,null,__1);
    }
    private static void CountPotion(KernelSession __instance,PotionModel __0,Creature? __1,out Tag? __state)
    {
        __state=null;if(owner.Value is not {} meter)return;
        ThreadActions++;Interlocked.Increment(ref meter.potions);__state=Charge(meter,__instance,null,__0,null,__1);
    }
    private static void CountEnd(KernelSession __instance,Player __0,out Tag? __state)
    {
        __state=null;if(owner.Value is not {} meter)return;
        ThreadActions++;Interlocked.Increment(ref meter.ends);__state=Charge(meter,__instance,null,null,__0,null);
    }
    internal Scope Enter()=>new(this);
    internal readonly struct Scope : IDisposable
    {
        private readonly SimulationMeter? previous;
        internal Scope(SimulationMeter meter){previous=owner.Value;owner.Value=meter;}
        public void Dispose()=>owner.Value=previous;
    }
}

internal static class CapturedBelt
{
    private static readonly AccessTools.FieldRef<KernelSession,IReadOnlyDictionary<PotionModel,(int Slot,PotionModel Captured)>> Slots=
        AccessTools.FieldRefAccess<KernelSession,IReadOnlyDictionary<PotionModel,(int Slot,PotionModel Captured)>>("capturedPotions");
    internal static int Slot(KernelSession state,PotionModel potion)=>Slots(state).TryGetValue(potion,out var item)?item.Slot:-1;
    internal static PotionModel? At(KernelSession state,MegaCrit.Sts2.Core.Entities.Players.Player player,int slot)
    {
        foreach(var potion in state.CapturedPotions(player))if(Slot(state,potion)==slot)return potion;
        return null;
    }
}
