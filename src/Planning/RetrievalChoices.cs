using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopSolver;

// For a three-card retrieval, reserve a few choices that can generate energy
// and retrieve the same starter again. The normal simulator prices the actual
// effects, including drawbacks. Only candidate admission changes here.
internal static class RetrievalChoices
{
    private static readonly Assembly Kernel=typeof(KernelSession).Assembly;
    private static readonly Type Spec=Kernel.GetType("CoopBots.Kernel.Vendor.CardChoiceSpec",true)!;
    private static readonly Type Choice=Kernel.GetType("CoopBots.Kernel.Vendor.PlanCardChoice",true)!;
    private static readonly Type Support=Kernel.GetType("CoopBots.Kernel.Vendor.CardChoiceSupport",true)!;
    private static readonly MethodInfo Tokens=AccessTools.Method(Support,"ToTokens");
    private static readonly MethodInfo Clone=AccessTools.Method(Choice,"<Clone>$");
    private static readonly PropertyInfo Cards=Choice.GetProperty("Cards")!;
    private static readonly PropertyInfo SourcePile=Spec.GetProperty("SourcePile")!;
    private static readonly PropertyInfo Minimum=Spec.GetProperty("MinCount")!;
    private static readonly PropertyInfo Maximum=Spec.GetProperty("MaxCount")!;
    private static readonly PropertyInfo Effect=Spec.GetProperty("Effect")!;
    private static readonly PropertyInfo Options=Spec.GetProperty("Options")!;
    private static readonly PropertyInfo Source=Spec.GetProperty("SourceCards")!;
    private static readonly Func<CardModel,string> Name=c=>c.Id.Entry;
    private static readonly Func<CardModel,double> CardValue=AccessTools.Method(Support,"CardValue").CreateDelegate<Func<CardModel,double>>();

    internal static void Install()
    {
        var method=AccessTools.Method(Kernel.GetType("CoopBots.Kernel.KernelRelicChoices"),"CleanupChoices");
        var predicted=Options.PropertyType.GetGenericArguments()[0];
        var postfix=AccessTools.Method(typeof(RetrievalChoices),nameof(IncludeEngines)).MakeGenericMethod(Choice,predicted);
        var harmony=new Harmony("sts2.coopsolver.retrieval");
        harmony.Patch(method,postfix:new HarmonyMethod(postfix));
        // The upstream choice builder already covers every single discard pick.
        // The cursor truncates that list a second time before assigning ordinals.
        var cursor=typeof(KernelSession).GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(t=>t.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public))
            .Single(m=>m.Name.StartsWith("<ChoiceCursor>") && m.GetParameters().Length==1 &&
                m.GetParameters()[0].ParameterType.Name=="TurnStartChoiceRequest");
        harmony.Patch(cursor,transpiler:new HarmonyMethod(typeof(RetrievalChoices),nameof(SingleRetrievalLimit)));
    }

    private static IEnumerable<CodeInstruction> SingleRetrievalLimit(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int specLocal=__originalMethod.GetMethodBody()!.LocalVariables.Single(v=>v.LocalType==Spec).LocalIndex;
        var code=instructions.ToList();
        int index=Enumerable.Range(0,code.Count).Single(i=>code[i].opcode==OpCodes.Ldc_I4_8);
        var original=code[index];
        var loadSpec=CodeInstruction.LoadLocal(specLocal);
        loadSpec.labels.AddRange(original.labels);
        loadSpec.blocks.AddRange(original.blocks);
        code[index]=loadSpec;
        code.Insert(index+1,CodeInstruction.Call(typeof(RetrievalChoices),nameof(LimitFor)));
        return code;
    }

    private static int LimitFor(object spec)
    {
        if((PileType)SourcePile.GetValue(spec)! != PileType.Discard || (int)Maximum.GetValue(spec)! != 1 ||
            Effect.GetValue(spec)!.ToString()!="MoveToHand") return 8;
        var options=(System.Collections.IEnumerable)Options.GetValue(spec)!;
        int count=options is System.Collections.ICollection collection ? collection.Count : options.Cast<object>().Count();
        return Math.Max(8,count+1);
    }

    private static void IncludeEngines<TChoice,TCard>(object spec,int limit,ref IReadOnlyList<TChoice> __result)
    {
        if(limit<3 || __result.Count==0 || (PileType)SourcePile.GetValue(spec)! != PileType.Discard ||
            (int)Minimum.GetValue(spec)! != 3 || (int)Maximum.GetValue(spec)! != 3 ||
            Effect.GetValue(spec)!.ToString()!="MoveToHand") return;
        var options=(IReadOnlyList<TCard>)Options.GetValue(spec)!;
        if(options.Count<=3)return;
        var preview=typeof(TCard).GetProperty("Preview")!;
        var entries=options.Select(card=>(physical:card,model:(CardModel)preview.GetValue(card)!)).ToArray();
        var energy=entries.Where(t=>t.model is BorrowedTime or Alignment).OrderByDescending(t=>t.model.IsUpgraded).Take(1).ToArray();
        var retrieval=entries.Where(t=>t.model is Graveblast or Hologram).OrderByDescending(t=>t.model.IsUpgraded).Take(1).ToArray();
        if(energy.Length==0 || retrieval.Length==0) return;
        var payoffs=entries.Where(t=>t.model.Type==CardType.Attack && t.model is not Graveblast)
            .OrderByDescending(t=>CardValue(t.model)).DistinctBy(t=>(t.model.Id,t.model.CurrentUpgradeLevel)).Take(limit/2).ToArray();
        if(payoffs.Length==0)return;
        var retained=__result.Take(Math.Max(1,limit-payoffs.Length)).ToList();
        foreach(var payoff in payoffs)
        {
            var selection=new[]{energy[0].physical,retrieval[0].physical,payoff.physical};
            var tokens=Tokens.Invoke(null,new object?[]{selection,options,Source.GetValue(spec),Name});
            var choice=(TChoice)Clone.Invoke(__result[0],null)!;
            Cards.SetValue(choice,tokens);
            retained.Add(choice);
        }
        __result=retained;
    }
}
