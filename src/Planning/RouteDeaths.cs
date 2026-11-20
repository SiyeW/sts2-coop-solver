using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CoopSolver;

internal sealed record RouteKill(int Step,Creature Enemy);

// Inspect settled replay steps only. Targetability can also change when a
// creature escapes or enters a revival phase, so it cannot establish a kill.
internal sealed class RouteDeaths
{
    private readonly HashSet<Creature> living=new();
    private readonly List<RouteKill> kills=new();
    private readonly List<HudRoundHp> rounds=new();
    private int current,historyIndex;
    private int[] damage;
    private Dictionary<int,int> cardDamage=new();
    private int[] startHp;
    internal RouteDeaths(KernelSession root)
    {
        damage=new int[root.Party.Count];historyIndex=root.Simulator.History.Count();current=root.TurnNumber;startHp=root.Party.Select(p=>root.Hp(p.Creature)).ToArray();Observe(root,-1);
    }
    internal HudRoundHp[] Rounds()=>cardDamage.Count==0 && !damage.Any(n=>n!=0)?rounds.ToArray():
        rounds.Append(new HudRoundHp(current,System.Array.Empty<int>(),System.Array.Empty<int>(),damage.ToArray(),new(cardDamage))).ToArray();
    internal RouteKill[] Freeze()=>kills.ToArray();
    internal void Observe(KernelSession session,int step,KernelTeamSearch.Action? action=null)
    {
        var sim=session.Simulator;
        var combat=(SimulatedCombatState)sim.State.CombatState;
        int entries=sim.History.Count();
        for(;historyIndex<entries;historyIndex++)
        {
            if(sim.History[historyIndex] is not CombatPredictionDamageReceivedEntry hit || !combat.KnownEnemies.Contains(hit.Receiver))continue;
            var owner=hit.Dealer?.Player??hit.Dealer?.PetOwner??hit.CardSource?.Original?.Owner;
            int amount=hit.Result.UnblockedDamage;
            if(owner==null || amount<=0)continue;
            for(int i=0;i<session.Party.Count;i++)if(session.Party[i].NetId==owner.NetId)damage[i]+=amount;
            if(action?.Card!=null && action.Player.NetId==owner.NetId)cardDamage[step]=cardDamage.GetValueOrDefault(step)+amount;
        }
        if(step>=0 && (session.TurnNumber!=current || session.HasTerminal))
        {
            var end=session.Party.Select(p=>session.Hp(p.Creature)).ToArray();
            rounds.Add(new(current,startHp.Zip(end,(a,b)=>a-b).ToArray(),end,damage,cardDamage,session.RemainingOpposition().Hp));
            damage=new int[end.Length];cardDamage=new();
            startHp=end;current=session.TurnNumber;
        }
        var state=session.Simulator.State;

        foreach(var enemy in combat.KnownEnemies)
        {
            var hp=state.GetCreature(enemy);
            if(combat.EscapedCreatures.Contains(enemy)){living.Remove(enemy);continue;}
            if(combat.EffectiveEnemyHp(enemy,hp)>0){living.Add(enemy);continue;}
            if(living.Remove(enemy) && hp.CurrentHp<=0 && step>=0)kills.Add(new(step,enemy));
        }
    }
}
