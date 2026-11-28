using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopSolver;

// One action may open several dependent menus. Digits address individual
// choices, never a pre-ranked list of combinations. Each attempt runs on a fork.
internal sealed class ExactMenus : IDisposable
{
    private readonly KernelSession session;
    private readonly int[] digits;
    private readonly List<int> radices = new();
    private readonly List<KernelChoice> observed = new();
    internal ExactMenus(KernelSession session,int[] digits)
    {
        if(session.MenuSelector!=null) throw new InvalidOperationException("Overlapping menu enumeration");
        this.session=session;
        this.digits=digits;
        session.MenuSelector=Select;
    }
    public void Dispose()=>session.MenuSelector=null;
    internal IReadOnlyList<KernelChoice> Observed => observed;
    // Each dependent menu divides its incoming share among its legal answers.
    internal double ChoiceShare
    {
        get
        {
            double share=1;
            foreach(int radix in radices)share/=radix;
            return share;
        }
    }
    internal bool Consumed => digits.Length<=radices.Count;

    private int Digit(int count)
    {
        if(count<=0) throw new InvalidOperationException("Invalid choice range");
        int pos=radices.Count;
        radices.Add(count);
        int value=pos<digits.Length?digits[pos]:0;
        if(value<0 || value>=count) throw new InvalidOperationException("Choice no longer exists");
        return value;
    }
    private PlanCardChoice Select(TurnStartChoiceRequest request,Player? owner,KernelMenuKind kind)
    {
        if(kind==KernelMenuKind.TurnStart && owner==null && request.Spec?.Options.Count!=0)
            throw new InvalidOperationException("Unknown turn-start menu owner");
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
        var tokens=CardChoiceSupport.ToTokens(selection,options,spec.SourceCards,c=>c.Id.Entry);
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
            ChoiceLayout.Record(spec,choice.Options);
            observed.Add(choice);
            if(kind==KernelMenuKind.Action)session.RecordActionChoice(choice);
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
