using System.Linq;
using System.Runtime.CompilerServices;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace CoopSolver;

internal enum CombatGoal { None, Card, Gold, Training, MaxHp }

// Kept alongside the combat ending: escaping can settle the battle without
// meeting its objective. Labels are translated on the viewer's game client.
internal sealed record GoalResult(CombatGoal Kind,int Lost,bool Escaped=false)
{
    internal string Label()=>Kind switch
    {
        CombatGoal.Card=>Lost>0?Ui.T($"丢{Lost}张牌",$"Lost {Lost} card(s)"):Escaped?Ui.T("保住卡牌 · 逃走","Card safe · escaped"):Ui.T("保住卡牌","Card safe"),
        CombatGoal.Gold=>Lost>0?Ui.T($"丢{Lost}金",$"Lost {Lost} gold"):Escaped?Ui.T("保住金币 · 逃走","Gold safe · escaped"):Ui.T("保住金币","Gold safe"),
        CombatGoal.Training=>Escaped?Ui.T("超时 · 无奖励","Timed out · no prize"):Ui.T("限时击破 · 有奖励","Beat timer · prize"),
        CombatGoal.MaxHp=>Lost>0?Ui.T($"上限−{Lost}",$"Max HP −{Lost}"):Ui.T("保住血量上限","Max HP intact"),
        _=>""
    };
    internal int CompareLoss(GoalResult other)=>Lost!=other.Lost?Lost.CompareTo(other.Lost):Escaped.CompareTo(other.Escaped);
    private static readonly ConditionalWeakTable<KernelSession,Box> kinds=new();
    private sealed record Box(CombatGoal Kind);
    internal static CombatGoal KindAt(KernelSession root)=>kinds.GetValue(root,s=>new(((SimulatedCombatState)s.Simulator.State.CombatState).Encounter switch
    {
        MegaCrit.Sts2.Core.Models.Encounters.ThievingHopperWeak=>CombatGoal.Card,
        MegaCrit.Sts2.Core.Models.Encounters.GremlinMercNormal=>CombatGoal.Gold,
        MegaCrit.Sts2.Core.Models.Encounters.BattlewornDummyEventEncounter=>CombatGoal.Training,
        MegaCrit.Sts2.Core.Models.Encounters.ScrollsOfBitingNormal or MegaCrit.Sts2.Core.Models.Encounters.ScrollsOfBitingWeak=>CombatGoal.MaxHp,
        _=>CombatGoal.None
    })).Kind;
    private static readonly ConditionalWeakTable<TerminalRecord,GoalResult> endings=new();
    internal static GoalResult? For(TerminalRecord? end)=>end!=null && endings.TryGetValue(end,out var g)?g:null;
    internal static TerminalRecord Tag(TerminalRecord end,GoalResult? goal)
    {if(goal!=null)endings.AddOrUpdate(end,goal);return end;}
    internal static GoalResult? Capture(KernelSession root,KernelSession state)
    {
        var kind=KindAt(root);
        if(kind==CombatGoal.None || !state.HasWon)return null;
        var c=(SimulatedCombatState)state.Simulator.State.CombatState;
        return kind switch {
            CombatGoal.Card=>new(kind,c.UnrecoveredLoot(state.Simulator).Cards,c.EscapedCreatures.Any(x=>x.Monster is ThievingHopper)),
            CombatGoal.Gold=>new(kind,c.UnrecoveredLoot(state.Simulator).Gold,c.EscapedCreatures.Any(x=>x.Monster is FatGremlin)),
            CombatGoal.Training=>new(kind,0,c.EscapedCreatures.Any(x=>IsDummy(x.Monster))),
            CombatGoal.MaxHp=>new(kind,root.Party.Sum(p=>System.Math.Max(0,root.MaxHp(p.Creature)-state.MaxHp(p.Creature)))),
            _=>null};
    }
    private static bool IsDummy(MegaCrit.Sts2.Core.Models.MonsterModel? m)=>m is BattleFriendV1 or BattleFriendV2 or BattleFriendV3;
    internal static void Install()=>new Harmony("coopsolver.training-ending").Patch(
        AccessTools.Method(typeof(SimulatedCombatState),nameof(SimulatedCombatState.MarkBattlewornDummyTimedOut)),
        prefix:new HarmonyMethod(typeof(GoalResult),nameof(Timeout)));
    private static bool Timeout(SimulatedCombatState __instance)
    {
        var dummy=__instance.Enemies.SingleOrDefault(x=>IsDummy(x.Monster));
        if(dummy==null)return true;
        __instance.CreatureEscaped(dummy);return false;
    }
}
