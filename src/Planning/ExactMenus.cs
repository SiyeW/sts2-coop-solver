using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

// One action may open several dependent menus. Digits address individual
// choices, never a pre-ranked list of combinations. Each attempt runs on a fork.
internal sealed class ExactMenus : IDisposable
{
    [ThreadStatic] private static ExactMenus? current;
    private readonly KernelSession session;
    private readonly int[] digits;
    private readonly List<int> radices = new();
    private readonly List<KernelChoice> observed = new();
    private static readonly AccessTools.FieldRef<KernelSession,List<KernelChoice>> Answers =
        AccessTools.FieldRefAccess<KernelSession,List<KernelChoice>>("resolvedChoices");
    private delegate IReadOnlyList<PlanCardToken> Tokenizer(IReadOnlyList<PredictedCard> selection,
        IReadOnlyList<PredictedCard> options,IReadOnlyList<PredictedCard> source,Func<CardModel,string> name);
    private static readonly Tokenizer Tokens=AccessTools.Method(typeof(CardChoiceSupport),"ToTokens").CreateDelegate<Tokenizer>();

    internal ExactMenus(KernelSession session,int[] digits)
    {
        if(current!=null) throw new InvalidOperationException("Overlapping menu enumeration");
        this.session=session;
        this.digits=digits;
        current=this;
    }
    public void Dispose() => current=null;
    internal IReadOnlyList<KernelChoice> Observed => observed;
    internal bool Consumed => digits.Length<=radices.Count;

    internal static void Install()
    {
        var harmony=new Harmony("sts2.coopsolver.physical-menus");
        void Patch(string original,string prefix) => harmony.Patch(AccessTools.Method(typeof(KernelSession),original),
            prefix:new HarmonyMethod(typeof(ExactMenus),prefix));
        Patch("ChoiceCursor",nameof(CardCursor));
        Patch("AutomaticTurnStartChoice",nameof(TurnChoice));
        Patch("PreparePartyStartChoices",nameof(UseRecordedMenu));
        Patch("PrepareAfterStartChoices",nameof(UseRecordedMenu));
    }
    private static bool UseRecordedMenu(KernelSession __instance) => current?.session!=__instance;
    private static bool CardCursor(KernelSession __instance,Player owner,ref TurnStartChoiceCursor __result)
    {
        var scope=current;
        if(scope?.session!=__instance) return true;
        __result=TurnStartChoiceCursor.ForAutomaticPolicy(request=>scope.Select(request,owner,true));
        return false;
    }
    private static bool TurnChoice(KernelSession __instance,TurnStartChoiceRequest request,ref PlanCardChoice? __result)
    {
        var scope=current;
        if(scope?.session!=__instance) return true;
        var owner=request.Spec?.Options.FirstOrDefault()?.Preview.Owner;
        if(owner==null && request.Spec?.Options.Count!=0) throw new InvalidOperationException("Unknown turn-start menu owner");
        __result=scope.Select(request,owner,false);
        return false;
    }
    private int Digit(int count)
    {
        if(count<=0) throw new InvalidOperationException("Invalid choice range");
        int pos=radices.Count;
        radices.Add(count);
        int value=pos<digits.Length?digits[pos]:0;
        if(value<0 || value>=count) throw new InvalidOperationException("Choice no longer exists");
        return value;
    }
    private PlanCardChoice Select(TurnStartChoiceRequest request,Player? owner,bool actionMenu)
    {
        var spec=request.Spec??TurnStartChoiceSupport.BuildSpec(session.Simulator,owner!,request);
        var options=spec.Options;
        var min=Math.Min(spec.MinCount,options.Count);
        var max=Math.Min(spec.MaxCount,options.Count);
        IReadOnlyList<PredictedCard> selection;
        if(spec.IsImplicitAllSelection) selection=options;
        else
        {
            // Descending cardinality gives a useful first proposal for retrieval.
            int take=max-Digit(max-min+1);
            var remaining=options.ToList();
            var chosen=new List<PredictedCard>(take);
            for(int i=0;i<take;i++)
            {
                int at=Digit(remaining.Count);
                chosen.Add(remaining[at]);
                remaining.RemoveAt(at);
            }
            selection=chosen;
        }
        var tokens=Tokens(selection,options,spec.SourceCards,c=>c.Id.Entry);
        var plan=new PlanCardChoice(request.Effect,request.SourcePile,tokens,request.SourceId,request.ContextId,request.Timing);
        var actor=options.FirstOrDefault()?.Preview.Owner??owner;
        if(actor!=null)
        {
            var choice=new KernelChoice(actor.NetId,request.SourceId,request.Effect.ToString(),request.SourcePile,
                request.ContextId,min,max,tokens.Select(t=>new KernelChoiceCard(t.CardId,t.UpgradeLevel,t.StateKey,t.OptionOccurrence)).ToArray())
            {
                Options=KernelChoice.MenuTokens(options.Select(c=>c.Preview).ToArray()),
                RequestedMinimum=spec.MinCount,RequestedMaximum=spec.MaxCount,MenuSource=request.MenuSource,Timing=request.Timing.ToString()
            };
            observed.Add(choice);
            if(actionMenu) Answers(session).Add(choice);
        }
        return plan;
    }

    internal IReadOnlyList<int[]> Alternatives()
    {
        // Unique parent: decrement the last nonzero digit and trim trailing zeros.
        // Breadth-first traversal of these vectors revisits EARLIER decisions
        // without exhausting a later discard menu. No duplicate/visited set.
        var children=new List<int[]>();
        for(int i=Math.Max(0,digits.Length-1);i<radices.Count;i++)
        {
            int previous=i<digits.Length?digits[i]:0;
            if(previous+1>=radices[i]) continue;
            var next=new int[i+1];
            Array.Copy(digits,next,Math.Min(i,digits.Length));
            next[i]=previous+1;
            children.Add(next);
        }
        return children;
    }
}
