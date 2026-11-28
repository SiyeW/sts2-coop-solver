using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Cards.OnPlay;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Damage;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopSolver;

internal static class InterceptMirror
{
    private sealed class Coverage : IPredictionStateForkable
    {
        internal readonly List<Creature> Targets;
        internal Coverage(IEnumerable<Creature> targets) => Targets = new(targets);
        public object Fork(PredictionForkContext context) => new Coverage(Targets);
    }

    private static readonly FieldInfo NativeData = AccessTools.Field(typeof(PowerModel), "_internalData");
    private static readonly FieldInfo NativeTargets = AccessTools.Field(
        AccessTools.Inner(typeof(InterceptPower), "Data"), "coveredCreatures");
    private static readonly AccessTools.FieldRef<SimulatedCombatState, IReadOnlyDictionary<Player, string>> PlayerNames =
        AccessTools.FieldRefAccess<SimulatedCombatState, IReadOnlyDictionary<Player, string>>("_playerNames");

    internal static void Install()
    {
        var cards = (MethodMirrorRegistry<CardModel, CardOnPlayMirrorContext>)
            AccessTools.Field(typeof(CardOnPlayMirrors), "Registry").GetValue(null)!;
        cards.Register<Intercept>(Play);
        var damage = (MethodMirrorRegistry<AbstractModel, ModifyDamageMirrorContext, decimal>)
            AccessTools.Field(typeof(ModifyDamageMirrors), "MultiplicativeRegistry").GetValue(null)!;
        damage.Register<InterceptPower>(ProtectorMultiplier);
        damage.Register<CoveredPower>((power, ctx) =>
            ctx.Target == power.Owner && ctx.Props.IsPoweredAttack() ? 0m : 1m);

        var harmony = new Harmony("sts2.coopsolver.intercept");
        harmony.Patch(AccessTools.Method(typeof(KernelSession), nameof(KernelSession.Targets)),
            postfix: new HarmonyMethod(typeof(InterceptMirror), nameof(AllyTargets)));
        harmony.Patch(AccessTools.Method(typeof(PowerPredictionStateSupport), "CaptureRootState"),
            postfix: new HarmonyMethod(typeof(InterceptMirror), nameof(Capture)));
        harmony.Patch(AccessTools.Method(typeof(EndTurnPowerSupport), "TriggerRegular"),
            postfix: new HarmonyMethod(typeof(InterceptMirror), nameof(Expire)));
        harmony.Patch(AccessTools.Method(typeof(SimulatedCombatState), "RemovePowersAfterDeath"),
            prefix: new HarmonyMethod(typeof(InterceptMirror), nameof(ProtectorDied)));
    }

    // The native UI's AnyAlly selector rejects the acting player. The lower
    // level command only checks sides; using it alone could protect oneself.
    private static void AllyTargets(KernelSession __instance, CardModel source, ref IReadOnlyList<Creature?> __result)
    {
        if (!__result.Any(t => t?.Player != null)) return;
        var card = __instance.Simulator.State.FindCard(source);
        if (card != null && __instance.Simulator.GetTargetType(card) == TargetType.AnyAlly)
            __result = __result.Where(c => c != card.Preview.Owner.Creature).ToArray();
    }

    private static void Capture(CombatPredictionSimulator simulator, PowerModel target, PowerModel source)
    {
        if (source is not InterceptPower) return;
        var covered = (List<Creature>)NativeTargets.GetValue(NativeData.GetValue(source))!;
        simulator.StateStore.GetReadOnly(target, () => new Coverage(covered));
    }

    private static void Play(Intercept card, CardOnPlayMirrorContext ctx)
    {
        var combat = (SimulatedCombatState)ctx.CombatState;
        var protector = card.Owner.Creature;
        var target = ctx.Target;
        ctx.GainBlock(protector);
        var previous = combat.EffectivePowers().OfType<CoveredPower>().ToHashSet();
        combat.Apply<CoveredPower>(target, 1, protector);
        var covered = combat.EffectivePowers().OfType<CoveredPower>()
            .SingleOrDefault(p => p.Amount > 0 && p.Owner == target && p.Applier == protector && !previous.Contains(p));
        if (covered == null) return;
        var names = PlayerNames(combat);
        ((StringVar)covered.DynamicVars["Applier"]).StringValue = names[card.Owner];

        bool newlyApplied = combat.GetAmount<InterceptPower>(protector) <= 0;
        if (newlyApplied) combat.Apply<InterceptPower>(protector, 1, target);
        var power = combat.GetMutablePower<InterceptPower>(protector);
        if (power == null || power.Amount <= 0) return;
        var links = ctx.StateStore.Get(power, () => new Coverage(Array.Empty<Creature>())).Targets;
        if (newlyApplied) links.Clear();
        if (!links.Contains(target)) links.Add(target);
        var labels = links.Select(c => names[c.Player!]).ToArray();
        ((StringVar)power.DynamicVars["Covering"]).StringValue = labels.Length == 1 ? labels[0]
            : string.Join(", ", labels.Take(labels.Length - 1)) + ", and " + labels[^1];
    }

    private static decimal ProtectorMultiplier(InterceptPower power, ModifyDamageMirrorContext ctx)
    {
        if (ctx.Target != power.Owner || !ctx.Props.IsPoweredAttack()) return 1m;
        return 1m + ctx.StateStore.Peek<Coverage>(power, () => throw new InvalidOperationException("Missing Intercept roster")).Targets.Count;
    }

    private static void Expire(SimulatedCombatState combat, CombatSide side, bool __result)
    {
        if (!__result || side != CombatSide.Enemy) return;
        foreach (var power in combat.EffectivePowers())
            if (power is InterceptPower or CoveredPower && power.Amount > 0) combat.SetPowerAmount(power, 0);
    }

    private static void ProtectorDied(SimulatedCombatState __instance, Creature creature)
    {
        if (creature.Player == null) return;
        foreach (var power in __instance.EffectivePowers())
            if (power is CoveredPower && power.Applier == creature && power.Amount > 0)
                __instance.SetPowerAmount(power, 0);
    }
}
