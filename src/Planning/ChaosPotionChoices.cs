using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CoopSolver;

// The pinned kernel supplies a choice cursor to cards, but ordinary potion
// branches never arm it. Distilled Chaos can auto-play multiple chooser cards.
// Reuse the same physical-card choices and bounded enumeration as card plays.
internal static class ChaosPotionChoices
{
    private static readonly AccessTools.FieldRef<KernelSession,int[]?> Vector =
        AccessTools.FieldRefAccess<KernelSession,int[]?>("choiceOrdinals");
    private static readonly AccessTools.FieldRef<KernelSession,int> Position =
        AccessTools.FieldRefAccess<KernelSession,int>("choicePosition");
    private static readonly AccessTools.FieldRef<KernelSession,List<int>> Counts =
        AccessTools.FieldRefAccess<KernelSession,List<int>>("choiceCounts");
    private static readonly AccessTools.FieldRef<KernelSession,List<KernelChoice>> Answers =
        AccessTools.FieldRefAccess<KernelSession,List<KernelChoice>>("resolvedChoices");
    private delegate bool UseCore(KernelSession session,PotionModel potion,Creature? target,IReadOnlyList<KernelChoice>? choices,out string boundary);
    private static readonly UseCore Use = AccessTools.Method(typeof(KernelSession),"UsePotion",
        new[]{typeof(PotionModel),typeof(Creature),typeof(IReadOnlyList<KernelChoice>),typeof(string).MakeByRefType()})
        .CreateDelegate<UseCore>();
    private static readonly System.Action<Queue<int[]>,HashSet<string>,IReadOnlyList<int>,IReadOnlyList<int>> Enqueue =
        AccessTools.Method(typeof(KernelSession).Assembly.GetType("CoopBots.Kernel.KernelChoiceContinuation"), "EnqueueAlternatives")
            .CreateDelegate<System.Action<Queue<int[]>,HashSet<string>,IReadOnlyList<int>,IReadOnlyList<int>>>();

    internal static void Install()
    {
        var h = new Harmony("sts2.coopsolver.chaos-choice");
        HarmonyMethod Hook(string name) => new(typeof(ChaosPotionChoices),name);
        h.Patch(AccessTools.Method(typeof(KernelSession),"PotionBranches"),prefix:Hook(nameof(Branches)));
        h.Patch(AccessTools.Method(typeof(KernelSession),"PotionActions"),prefix:Hook(nameof(Actions)));
        h.Patch(AccessTools.Method(typeof(KernelSession),"ApplyCausal"),prefix:Hook(nameof(BeforeReplay)),postfix:Hook(nameof(AfterReplay)));
        h.Patch(AccessTools.Method(typeof(KernelSession),"UsePotion",
            new[]{typeof(PotionModel),typeof(Creature),typeof(string).MakeByRefType()}),prefix:Hook(nameof(BeforeDirectUse)));
    }

    private static void BeforeDirectUse(KernelSession __instance, PotionModel potion)
    {
        if (potion is not DistilledChaos) return;
        Vector(__instance)=Array.Empty<int>();
        Position(__instance)=0;
    }

    private static bool Branches(KernelSession __instance, PotionModel potion, Creature? target, int maximumBranches,
        ref IEnumerable<KernelSession.CardBranch> __result)
    {
        if (potion is not DistilledChaos) return true;
        __result = Enumerate(__instance,potion,target,maximumBranches);
        return false;
    }

    private static bool Actions(KernelSession __instance, PotionModel potion, Creature? target, int maximumChoices,
        ref IReadOnlyList<KernelTeamSearch.Action> __result)
    {
        if (potion is not DistilledChaos) return true;
        __result = Enumerate(__instance,potion,target,maximumChoices).Where(b=>b.Boundary.Length==0)
            .Select(b=>new KernelTeamSearch.Action(potion.Owner,null,target,potion,b.Choices,ChoiceOrdinals:b.ChoiceOrdinals)).ToArray();
        return false;
    }

    private static IEnumerable<KernelSession.CardBranch> Enumerate(KernelSession parent,PotionModel bottle,Creature? target,int limit)
    {
        var queue=new Queue<int[]>();
        queue.Enqueue(Array.Empty<int>());
        var seen=new HashSet<string>{""};
        for(int i=0;i<limit && queue.TryDequeue(out var vector);i++)
        {
            var child=parent.Fork();
            Vector(child)=vector;
            Position(child)=0;
            bool ok=Use(child,bottle,target,null,out var reason);
            var counts=Counts(child);
            int[] used=Enumerable.Range(0,counts.Count).Select(j=>j<vector.Length?vector[j]:0).ToArray();
            Enqueue(queue,seen,used,counts);
            yield return new(child,Answers(child).ToArray(),ok?"":reason,used);
        }
    }

    private static void BeforeReplay(KernelSession __instance,ref KernelTeamSearch.Action action,out int __state)
    {
        __state=-1;
        if(action.Potion is not DistilledChaos) return;
        __state=Answers(__instance).Count;
        if(action.ChoiceOrdinals==null) action=action with { ChoiceOrdinals=Array.Empty<int>() };
    }

    private static bool SameMenu(KernelChoice a, KernelChoice b) =>
        a.Owner==b.Owner && a.Source==b.Source && a.Context==b.Context && a.Effect==b.Effect && a.Pile==b.Pile &&
        a.Timing==b.Timing && a.NativeRound==b.NativeRound && a.PlayerTurnNumber==b.PlayerTurnNumber &&
        a.Minimum==b.Minimum && a.Maximum==b.Maximum && a.RequestedMinimum==b.RequestedMinimum &&
        a.RequestedMaximum==b.RequestedMaximum && a.MenuSource==b.MenuSource &&
        a.Options!=null && b.Options!=null && a.Options.SequenceEqual(b.Options) && a.Cards.SequenceEqual(b.Cards);

    private static void AfterReplay(KernelSession __instance,KernelTeamSearch.Action action,int __state,
        ref KernelTeamSearch.Action resolvedAction,ref string boundary,ref bool __result)
    {
        if(__state<0 || !__result) return;
        var actual=Answers(__instance).Skip(__state).ToArray();
        if(action.Choices is {} planned && (planned.Count!=actual.Length || !planned.Zip(actual,SameMenu).All(x=>x)))
        {
            boundary="distilled-chaos-choice-changed";
            __result=false;
        }
        else resolvedAction=resolvedAction with { Choices=actual };
    }
}
