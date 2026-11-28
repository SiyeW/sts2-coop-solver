using System.Linq;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CoopSolver;

// A closed set of effects: these cards, monsters and hooks cannot heal the
// player, procure a potion, or introduce a source of healing. Chomper can add
// Dazed; the other monster effects add block, strength, frail or thorns.
internal static class NoHealingBound
{
    private static bool Covered(AbstractModel m)=>m.GetType().Assembly==typeof(AbstractModel).Assembly && m is
        CccComboModel or DebufferModel or Defect or StrikeDefect or DefendDefect or Zap or Dualcast or AscendersBane or Dazed or
        CrackedCore or BoomingConch or LightningOrb or FrostOrb or
        Toadpole or Chomper or Nibbit or Stabbot or StrengthPower or FrailPower or ThornsPower;

    internal static bool Applies(KernelSession root)
    {
        var combat=(SimulatedCombatState)root.Simulator.State.CombatState;
        if(root.Party.Count!=1 || combat.Allies.Count!=1 || combat.Modifiers.Count!=0)return false;
        var player=root.Party[0];var seat=root.Simulator.State.GetPlayerCombatState(player);
        if(!Covered(player.Character) || root.HeldPotionCount(player)!=0 ||
            combat.Enemies.Any(c=>c.Monster==null || !Covered(c.Monster)) ||
            !combat.RelicsOf(player).All(Covered) || !combat.EffectivePowers().All(Covered) ||
            !seat.OrbQueue.Orbs.All(Covered))return false;
        foreach(var card in seat.AllCards)
            if(!Covered(card.Preview) || card.Preview.Enchantment!=null || card.Preview.Affliction!=null ||
                !CardChoiceSupport.ChoiceCardKey(card).EndsWith("|baselib=-",System.StringComparison.Ordinal))return false;
        return ((ICombatPredictionHookListenerSource)combat).RunHookListeners
            .All(model=>ReferenceEquals(model,combat.MultiplayerScalingModel) || Covered(model));
    }
}
