using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CoopSolver;

internal sealed record RouteKill(int Step,Creature Enemy,ulong? CreditedSeat=null,TurnKillEffect Effect=TurnKillEffect.None);

// Inspect settled replay steps only. Targetability can also change when a
// creature escapes or enters a revival phase, so it cannot establish a kill.
internal sealed class RouteDeaths
{
    private readonly HashSet<Creature> living=new();
    private readonly List<RouteKill> kills=new();
    private readonly List<HudRoundHp> rounds=new();
    private int current,historyIndex;
    private int[] damage,potions;
    private Dictionary<int,int> cardDamage=new();
    private Dictionary<int,int> unassigned=new();
    private Dictionary<int,string> damageNotes=new();
    private Dictionary<int,int[]> stepDamage=new();
    private Dictionary<int,int> energyAfter=new();
    private DeathSaves.Entry? lastRevive;
    private Dictionary<int,Revival[]> revives=new();
    private int[] roundLost,roundRestored,priorLost,priorRestored;
    private int[] startHp,previousHp;
    private Dictionary<int,int[]> endLoss=new();
    internal RouteDeaths(KernelSession root)
    {
        lastRevive=DeathSaves.Read(root);roundRestored=new int[root.Party.Count];roundLost=new int[root.Party.Count];
        var combat=(SimulatedCombatState)root.Simulator.State.CombatState;
        priorLost=root.Party.Select(p=>combat.GetCumulativeHpLost(p.Creature)).ToArray();
        priorRestored=root.Party.Select(p=>combat.GetRecoveredHp(p.Creature)).ToArray();
        potions=new int[root.Party.Count];damage=new int[root.Party.Count];historyIndex=root.Simulator.History.Count();current=root.TurnNumber;startHp=root.Party.Select(p=>root.Hp(p.Creature)).ToArray();previousHp=(int[])startHp.Clone();Observe(root,-1);
    }
    internal HudRoundHp[] Rounds()=>revives.Count==0 && endLoss.Count==0 && unassigned.Count==0 && energyAfter.Count==0 && cardDamage.Count==0 && !damage.Any(n=>n!=0)?rounds.ToArray():
        rounds.Append(new HudRoundHp(current,System.Array.Empty<int>(),System.Array.Empty<int>(),damage.ToArray(),new(cardDamage),EnergyAfter:new(energyAfter),StepDamage:new(stepDamage),DamageNotes:new(damageNotes),UnassignedDamage:new(unassigned),EndLoss:new(endLoss),Revives:new(revives),Recovered:roundRestored.ToArray(),Potions:potions.ToArray(),IncludesBlockDamage:true)).ToArray();
    internal RouteKill[] Freeze()=>kills.ToArray();
    private void Note(int step,string row)=>damageNotes[step]=damageNotes.TryGetValue(step,out var prior)?prior+"\n"+row:row;
    internal void Observe(KernelSession session,int step,KernelTeamSearch.Action? action=null)
    {
        if(action!=null && !action.EndTurn)energyAfter[step]=session.Energy(action.Player);
        var sim=session.Simulator;
        var combat=(SimulatedCombatState)sim.State.CombatState;
        int entries=sim.History.Count();
        var doomKills=DamageAttribution.TakeDoomKills(sim);
        Dictionary<Creature,(ulong? Seat,TurnKillEffect Effect)>? endingHits=null;
        for(;historyIndex<entries;historyIndex++)
        {
            if(sim.History[historyIndex] is not CombatPredictionDamageReceivedEntry hit || !combat.KnownEnemies.Contains(hit.Receiver))continue;
            int amount=DamageAttribution.Amount(hit);
            if(amount<=0)continue;
            var credit=DamageAttribution.Get(hit);
            ulong? ownerId=DamageAttribution.Seat(hit,credit);
            if(action?.EndTurn==true && hit.Result.WasTargetKilled)
            {
                var effect=hit.Source.Kind switch
                {
                    CombatDamageSourceKind.Poison=>TurnKillEffect.Poison,
                    _=>credit?.Effect??TurnKillEffect.None
                };
                (endingHits??=new())[hit.Receiver]=(ownerId,effect);
            }
            if(ownerId==null)
            {
                unassigned[step]=unassigned.GetValueOrDefault(step)+amount;
                Note(step,$"未归属　{hit.Source.Id??"间接效果"}　打{amount}");
                continue;
            }
            for(int i=0;i<session.Party.Count;i++)
            {
                if(session.Party[i].NetId!=ownerId)continue;
                damage[i]+=amount;
                if(!stepDamage.TryGetValue(step,out var seats))stepDamage.Add(step,seats=new int[damage.Length]);
                seats[i]+=amount;
                if(credit!=null || action?.Card==null || ownerId!=action.Player.NetId || hit.Source.Kind!=CombatDamageSourceKind.Card)
                {
                    string source=credit?.Source??hit.Source.Id switch {"MERCURY_HOURGLASS"=>"水银沙漏","ThornsPower"=>"荆棘反伤",_=>hit.Source.Id??"结算伤害"};
                    string row=$"P{i+1} {source}　打{amount}";
                    Note(step,row);
                }
            }
            if(action?.Card!=null && action.Player.NetId==ownerId)cardDamage[step]=cardDamage.GetValueOrDefault(step)+amount;
        }
        var head=DeathSaves.Read(session);
        Revival[] events=System.Array.Empty<Revival>();
        if(step>=0 && !ReferenceEquals(head,lastRevive) && head!=null)
        {
            var added=new Stack<Revival>();
            for(var e=head;e!=null && !ReferenceEquals(e,lastRevive);e=e.Previous)added.Push(e.Event);
            events=added.ToArray();revives[step]=events;
        }
        lastRevive=head;
        if(action!=null)
        {
            for(int i=0;i<potions.Length;i++)
            {
                var seat=session.Party[i].NetId;
                if(action.Potion!=null && action.Player.NetId==seat)potions[i]++;
                potions[i]+=events.Count(e=>e.Potion && e.Seat==seat);
            }
            int[]? losses=action.EndTurn?new int[previousHp.Length]:null;
            for(int i=0;i<previousHp.Length;i++)
            {
                int hp=session.Hp(session.Party[i].Creature);
                var creature=session.Party[i].Creature;
                int currentRecovery=combat.GetRecoveredHp(creature),currentLoss=combat.GetCumulativeHpLost(creature);
                int restored=Math.Max(0,currentRecovery-priorRestored[i]);
                int lost=Math.Max(currentLoss-priorLost[i],previousHp[i]-hp+restored);
                restored=Math.Max(restored,hp-previousHp[i]+lost);
                priorRestored[i]=currentRecovery;priorLost[i]=currentLoss;roundLost[i]+=lost;
                roundRestored[i]+=restored;
                if(losses!=null)losses[i]=lost;
                previousHp[i]=hp;
            }
            if(losses!=null && (session.TurnNumber!=current || session.HasTerminal || losses.Any(n=>n!=0)))
                endLoss[step]=losses;
        }
        if(step>=0 && (session.TurnNumber!=current || session.HasTerminal))
        {
            var end=session.Party.Select(p=>session.Hp(p.Creature)).ToArray();
            rounds.Add(new(current,roundLost,end,damage,cardDamage,session.RemainingOpposition().Hp,energyAfter,stepDamage,damageNotes,unassigned,endLoss,revives,roundRestored,potions,IncludesBlockDamage:true));
            potions=new int[end.Length];damage=new int[end.Length];cardDamage=new();energyAfter=new();stepDamage=new();damageNotes=new();unassigned=new();endLoss=new();
            roundLost=new int[end.Length];roundRestored=new int[end.Length];revives=new();
            startHp=end;current=session.TurnNumber;
        }
        var state=session.Simulator.State;

        foreach(var enemy in combat.KnownEnemies)
        {
            var hp=state.GetCreature(enemy);
            if(combat.EscapedCreatures.Contains(enemy)){living.Remove(enemy);continue;}
            if(combat.EffectiveEnemyHp(enemy,hp)>0){living.Add(enemy);continue;}
            if(!living.Remove(enemy) || hp.CurrentHp>0 || step<0)continue;
            var hit=endingHits?.GetValueOrDefault(enemy)??default;
            if(action?.EndTurn==true && doomKills?.TryGetValue(enemy,out var applier)==true)
                hit=(applier,TurnKillEffect.Doom);
            kills.Add(new(step,enemy,hit.Seat,hit.Effect));
        }
    }
}
