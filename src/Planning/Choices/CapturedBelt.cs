using System.Linq;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

internal static class CapturedBelt
{
    internal static int Slot(KernelSession state, PotionModel potion) => state.RecordedPotionSlot(potion);

    internal static bool SameGenerated(PotionModel? a, PotionModel? b) =>
        a != null && b != null && a.Id == b.Id &&
        KernelSession.GeneratedPotionSlot(a) is >= 0 and var slot && KernelSession.GeneratedPotionSlot(b) == slot;

    internal static int? DisplaySlot(KernelSession? state,PotionModel potion)
    {
        // The opening belt cannot tell us whether a potion generated later will have a duplicate.
        if(KernelSession.GeneratedPotionSlot(potion) is >=0 and var generated)return generated+1;
        int copies=state==null?potion.Owner.Potions.Count(bottle=>bottle.Id==potion.Id):
            state.CapturedPotions(potion.Owner).Count(bottle=>bottle.Id==potion.Id);
        if(copies<=1)return null;
        int slot=state==null?potion.Owner.GetPotionSlotIndex(potion):Slot(state,potion);
        return slot>=0?slot+1:null;
    }
    internal static PotionModel? At(KernelSession state, Player player, int slot) => state.PotionAtSlot(player, slot);
}
