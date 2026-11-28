using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CoopSolver;

// The normal tournament admits a single target per non-chooser potion, often
// the lowest-HP ally. Keep its openings and also compare the other recipients
// of an admitted ally-targeting potion whose benefit depends on the deck/hand.
internal static class PotionOpenings
{
    internal static void Install()
    {
        new Harmony("sts2.coopsolver.potion-targets").Patch(
            AccessTools.Method(typeof(KernelTournament),"FirstActions"),
            postfix:new HarmonyMethod(typeof(PotionOpenings),nameof(IncludeRecipients)));
    }

    private static void IncludeRecipients(KernelSession root, IReadOnlyList<Player> actors,
        TournamentOptions o, Func<bool>? stopRequested, List<KernelTeamSearch.Action> __result)
    {
        // End turn is legal when no affordable play or useful potion exists.
        // Otherwise the tournament would stop before evaluating the enemy turn.
        if (__result.Count == 0 && o.IncludeEndTurns && !o.PotionsOnly && root.CanFork && !root.HasTerminal
            && o.Request?.IsStopped != true && stopRequested?.Invoke() != true)
        {
            var next = actors.FirstOrDefault(root.CanAct);
            if (next != null) __result.Add(new(next, null, null, EndTurn:true));
            return;
        }
        var alternatives=new List<KernelTeamSearch.Action>();
        foreach(var action in __result)
        {
            var potion=action.Potion;
            if(potion is not (DistilledChaos or Clarity or Fortifier)) continue;
            foreach(var target in root.PotionTargets(potion))
            {
                if(target?.Player==null || __result.Any(a=>a.Potion==potion && a.Target==target)
                    || alternatives.Any(a=>a.Potion==potion && a.Target==target)) continue;
                alternatives.Add(new(action.Player,null,target,potion));
            }
        }
        __result.AddRange(alternatives);
    }
}
