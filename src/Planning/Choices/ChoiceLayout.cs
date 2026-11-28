using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Localization;

namespace CoopSolver;

// Indices are into the complete source pile, not the shuffled/filtered list of offers.
// Keep the presentation evidence outside KernelChoice, whose serialized form is a replay key.
internal sealed record ChoiceLayout(int[] Source,int[] Runs)
{
    private static readonly ConditionalWeakTable<IReadOnlyList<KernelChoiceCard>,ChoiceLayout> layouts=new();
    internal static ChoiceLayout? For(KernelChoice c)=>c.Options!=null && layouts.TryGetValue(c.Options,out var layout)?layout:null;
    internal static void Attach(KernelChoice c,ChoiceLayout? layout)
    {
        if(layout!=null && c.Options is {} options && layout.Source.Length==options.Count && layout.Runs.Length==options.Count)
            layouts.AddOrUpdate(options,layout);
    }
    internal static void Install()=>new Harmony("coopsolver.choice-pile-order").Patch(
        AccessTools.Method(typeof(KernelSession),"MenuTokensForSpec"),postfix:new HarmonyMethod(typeof(ChoiceLayout),nameof(Record)));

    internal static void Record(CardChoiceSpec spec,IReadOnlyList<KernelChoiceCard> __result)
    {
        if(ReplayCoverage.Tracked is not {} simulator || __result.Count<2 || spec.IsImplicitAllSelection ||
            spec.Effect is PlanChoiceEffect.GenerateToHand or PlanChoiceEffect.Duplicate ||
            !__result.GroupBy(c=>(c.Id,c.Upgrade)).Any(g=>g.Count()>1))return;
        var pos=spec.Options.Select(option=>{
            for(int i=0;i<spec.SourceCards.Count;i++)if(ReferenceEquals(spec.SourceCards[i],option))return i;
            return -1;
        }).ToArray();
        if(pos.Contains(-1))return;
        var runs=new int[spec.SourceCards.Count];string? previous=null;int run=0;
        bool move=spec.Effect is PlanChoiceEffect.MoveToHand or PlanChoiceEffect.MoveToHandFreeThisTurn or
            PlanChoiceEffect.Exhaust or PlanChoiceEffect.MoveToDrawTop or PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw;
        for(int i=0;i<runs.Length;i++)
        {
            var key=CardChoiceSupport.ChoiceCardKey(spec.SourceCards[i]);
            if(!move || previous==null || !CardPresentation.SameDisplayedVariant(key,previous))run++;
            runs[i]=run;previous=key;
        }
        var classes=pos.Select(i=>runs[i]).ToArray();
        if(spec.SourcePile!=PileType.Draw)
        {
            Array.Fill(classes,0);
            if(move && spec.SourcePile is PileType.Discard or PileType.Hand)
            {
                // StableShuffle sorts by card ID/upgrade before applying the RNG permutation.
                // Ordinary copies then collapse; unequal modifiers in a tied sort group can
                // still make two removals produce different future draws.
                var seat=simulator.State.GetPlayerCombatState(spec.Options[0].Preview.Owner);
                var hand=seat.Hand.Cards.ToArray();var discard=seat.DiscardPile.Cards.ToArray();
                var draw=seat.DrawPile.Cards.ToArray();
                var relevant=discard.Concat(draw);
                if(spec.SourcePile==PileType.Hand)relevant=relevant.Concat(hand);
                bool hasTiedVariants=relevant.GroupBy(c=>(c.Preview.Id,c.Preview.CurrentUpgradeLevel))
                    .Any(g=>g.Skip(1).Any(c=>!CardPresentation.SameDisplayedVariant(CardChoiceSupport.ChoiceCardKey(g.First()),CardChoiceSupport.ChoiceCardKey(c))));
                if(hasTiedVariants)
                {
                    foreach(var group in Enumerable.Range(0,pos.Length).GroupBy(i=>__result[i].StateKey).Where(g=>g.Count()>1))
                    {
                        var signatures=new List<string[]>();
                        foreach(int i in group)
                        {
                            var card=spec.Options[i];
                            var pile=discard.Where(c=>!ReferenceEquals(c,card)).ToList();
                            if(spec.Effect is PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw)pile.Add(card);
                            if(spec.SourcePile==PileType.Hand)pile.AddRange(hand.Where(c=>!ReferenceEquals(c,card)));
                            if(spec.Effect==PlanChoiceEffect.MoveToDrawTop)pile.Add(card);
                            pile.AddRange(draw);pile.Sort();
                            var signature=pile.Select(c=>CardChoiceSupport.ChoiceCardKey(c)).ToArray();
                            int eq=signatures.FindIndex(s=>s.SequenceEqual(signature));
                            if(eq<0){eq=signatures.Count;signatures.Add(signature);}
                            classes[i]=eq;
                        }
                    }
                }
            }
        }
        layouts.AddOrUpdate(__result,new(pos,classes));
    }

    // Native combat-pile grids preserve discard/exhaust order. The draw picker sorts its filtered
    // pile by rarity then localized title using List.Sort, which can reorder equal keys in large grids.
    internal int[] DisplayOrder(KernelChoice choice)
    {
        var options=choice.Options!;
        var order=Enumerable.Range(0,Source.Length).OrderBy(i=>Source[i]).ToArray();
        if(choice.Pile!=PileType.Draw || order.Length<=16)return order; // insertion sort preserves ties within one displayed variant
        var models=options.Select(c=>ModelDb.GetById<CardModel>(new ModelId("CARD",c.Id))).ToArray();
        int Rarity(int i)=>(int)models[i].Rarity switch {8=>6,9=>7,6=>8,10=>9,7=>10,var other=>other};
        var titles=options.Select((c,i)=>models[i].Title+(c.Upgrade>0?"+":"")+(c.Upgrade>1?c.Upgrade.ToString():"")).ToArray();
        Array.Sort(order,(a,b)=>{int cmp=Rarity(a).CompareTo(Rarity(b));return cmp!=0?cmp:
            string.Compare(titles[a],titles[b],false,LocManager.Instance.CultureInfo);});
        return order;
    }

    internal static bool Ambiguous(KernelChoice c)=>c.Options is {Count:>1} options && c.Cards.Any(p=>
        options.Count(o=>o.Id==p.Id && o.Upgrade==p.Upgrade)>1);
}
