using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace CoopSolver;

// Capture only the frozen simulator currently replaying a proposed route.
// Warnings from speculative branches do not describe the selected route.
internal static class ReplayCoverage
{
    private const string AttackIntentNote="（按攻击意图结算）";
    internal static string Summary(string[] notes)
    {
        if(notes.Length==0)return "";
        return notes.All(n=>n.EndsWith(AttackIntentNote,StringComparison.Ordinal))
            ? "路线中有敌方招式按攻击意图结算。" : "部分战斗效果的实机结算仍待核对。";
    }

    [ThreadStatic] private static CombatPredictionSimulator? tracked;
    [ThreadStatic] private static HashSet<string>? details;
    internal static void Install() => new Harmony("sts2.coopsolver.replay-coverage").Patch(
        AccessTools.Method(typeof(MonsterMoveSemantics),"ApplyPartyMove"),
        prefix:new HarmonyMethod(typeof(ReplayCoverage),nameof(ObserveMove)));
    internal static void Begin(KernelSession state, HashSet<string> output)
    { tracked=state.Simulator;details=output; }
    internal static void End(){tracked=null;details=null;}
    private static void ObserveMove(CombatPredictionSimulator simulator,ForecastMove move)
    {
        if (!ReferenceEquals(simulator,tracked) || move.Owner.Monster is not {} enemy) return;
        if(move.Move.Intents.Count>0 && move.Move.Intents.All(x=>x is AttackIntent) &&
            !MonsterMoveEffects.Supports(enemy,move.Move.Id))
            details!.Add(enemy.Id.Entry+"."+move.Move.Id+AttackIntentNote);
    }
    internal static void Collect(KernelSession state,HashSet<string> output)
    {
        if(!state.LastActionHadEnvironmentalRisk)return;
        if(!string.IsNullOrEmpty(state.LastActionRiskDetail)){output.Add(state.LastActionRiskDetail);return;}
        foreach(var gap in PredictionCoverage.Collect(state.Simulator).Where(g=>!g.Compensated))
            output.Add($"{gap.SourceId}.{gap.Method}:{gap.Reason}");
        if(output.Count==0)output.Add("内核标记了估算，未提供具体效果名");
    }
}
