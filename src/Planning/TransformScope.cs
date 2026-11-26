using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Extensions;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace CoopSolver;

internal static class TransformScope
{
    private static readonly Func<CardModel,bool,IEnumerable<CardModel>> Defaults=
        AccessTools.Method(typeof(CardFactory),"GetDefaultTransformationOptions").CreateDelegate<Func<CardModel,bool,IEnumerable<CardModel>>>();
    private static readonly Func<CardModel,IEnumerable<CardModel>,bool,CardModel[]> Filter=
        AccessTools.Method(typeof(CardFactory),"GetFilteredTransformationOptions").CreateDelegate<Func<CardModel,IEnumerable<CardModel>,bool,CardModel[]>>();
    internal static void Install()=>new Harmony("coopsolver.simulated-transform-scope").Patch(
        AccessTools.Method(typeof(CombatCardGenerationExtensions),"CreateRandomCardForTransform"),
        transpiler:new HarmonyMethod(typeof(TransformScope),nameof(Redirect)));

    // Preserve the native option order and single RNG draw. Its final CreateCard
    // must go to the fork, not the real card's combat or run registry.
    private static CardModel Create(CardModel card,IEnumerable<CardModel> pool,bool combat,Rng rng,CombatPredictionSimulator sim)=>
        ((SimulatedCombatState)sim.State.CombatState).CreateCard(rng.NextItem(Filter(card,pool,combat))!,card.Owner);
    private static CardModel CreateDefault(CardModel card,bool combat,Rng rng,CombatPredictionSimulator sim)=>
        ((SimulatedCombatState)sim.State.CombatState).CreateCard(rng.NextItem(Defaults(card,combat))!,card.Owner);
    private static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions)
    {
        int calls=0;
        foreach(var instruction in instructions)
        {
            if(instruction.operand is MethodInfo target && target.DeclaringType==typeof(CardFactory) && target.Name=="CreateRandomCardForTransform")
            {
                calls++;
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(TransformScope),
                    target.GetParameters().Length==4?nameof(Create):nameof(CreateDefault)));
            }
            else yield return instruction;
        }
        if(calls!=2)throw new InvalidOperationException("Unsupported simulated transform factory layout");
    }
}
