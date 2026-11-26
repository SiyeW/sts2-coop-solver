using System;
using System.Collections.Generic;
using System.Linq;

namespace CoopSolver;

internal sealed record HudSeat(ulong Id,string Name,string Role,int? ThisTurn,int? ToEnd,bool DiesThisTurn,bool DiesAtEnd,int? Potions,int? Hp=null,string? Color=null,int RevivedHp=0,int Recovered=0);
internal enum TurnKillEffect { None, Orb, Poison, Doom }
internal sealed record HudKill(int Step,string Name,bool IsTarget,ulong? CreditedSeat=null,TurnKillEffect Effect=TurnKillEffect.None,uint? EnemyId=null);
internal sealed record HudRoundHp(int Round,int[] Loss,int[] EndHp,int[]? Damage=null,Dictionary<int,int>? CardDamage=null,int? EnemyHp=null,Dictionary<int,int>? EnergyAfter=null,Dictionary<int,int[]>? StepDamage=null,Dictionary<int,string>? DamageNotes=null,Dictionary<int,int>? UnassignedDamage=null,Dictionary<int,int[]>? EndLoss=null,Dictionary<int,Revival[]>? Revives=null,int[]? Recovered=null,int[]? Potions=null,bool IncludesBlockDamage=false)
{
    internal Dictionary<int,int> DamageByAction((int Step,int Seat,bool End)[] moves)
    {
        var damage=new Dictionary<int,int>();
        if(!IncludesBlockDamage)return damage;
        if(StepDamage==null)
        {
            if(CardDamage!=null)
                foreach(var move in moves)if(CardDamage.TryGetValue(move.Step,out int amount))damage[move.Step]=amount;
            return damage;
        }

        foreach(var (step,seat,end) in moves)
        {
            if(!StepDamage.TryGetValue(step,out var credited))continue;
            if(!end)
            {
                damage[step]=damage.GetValueOrDefault(step)+credited.ElementAtOrDefault(seat);
                continue;
            }
            for(int i=0;i<credited.Length;i++)
                if(credited[i]>0)
                {
                    int own=EndStep(moves,i,step)??SettlementStep(i);
                    damage[own]=damage.GetValueOrDefault(own)+credited[i];
                }
        }
        return damage;
    }

    internal Dictionary<int,int>? LossByAction((int Step,int Seat,bool End)[] moves)
    {
        if(EndLoss==null)return null;
        var result=new Dictionary<int,int>();
        foreach(var (settlement,losses) in EndLoss)
            for(int seat=0;seat<losses.Length;seat++)
                if(losses[seat]!=0)
                {
                    int step=EndStep(moves,seat,settlement)??SettlementStep(seat);
                    result[step]=result.GetValueOrDefault(step)+losses[seat];
                }
        return result;
    }

    // Display-only key when the credited player's End Turn is already executed.
    // -1 is reserved for route text that has no action.
    internal static int SettlementStep(int seat)=>-2-seat;

    private static int? EndStep((int Step,int Seat,bool End)[] moves,int seat,int settlement)
    {
        int? first=null,latest=null;
        foreach(var move in moves)
        {
            if(!move.End || move.Seat!=seat)continue;
            first??=move.Step;
            if(move.Step<=settlement)latest=move.Step;
        }
        return latest??first;
    }

    internal static HudKill[] PlaceEndKills(HudKill[] kills,HudSeat[] party,(int Step,int Seat,bool End)[] moves)
    {
        var result=new List<HudKill>(kills.Length);
        foreach(var kill in kills)
        {
            if(kill.CreditedSeat is not ulong player || !moves.Any(m=>m.Step==kill.Step && m.End))
            {result.Add(kill);continue;}
            int seat=Array.FindIndex(party,s=>s.Id==player);
            if(seat>=0)result.Add(kill with {Step=EndStep(moves,seat,kill.Step)??SettlementStep(seat)});
        }
        return result.ToArray();
    }
}
internal sealed record HudPlan(int Start,int End,HudSeat[] Party,string Steps,string Result,string Risks,HudKill[]? Kills=null,HudRoundHp[]? Rounds=null,int? EndEnemyHp=null,int? TeamLoss=null,HudTerm[]? Terms=null,string? Language=null,GoalResult? Goal=null,Dictionary<int,uint>? EnemyTargets=null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string Outcome
    {
        get
        {
            string title=Steps.Split('\n',2)[0];
            return title.Contains("胜利")?"胜":title.Contains("倒下")?"败":"";
        }
    }
}
internal sealed record HudBest(long Visits,long? Pv,long Rate,int Round,int? End,HudSeat[] Party,string Outcome,string? Discovered=null,bool Checking=false,bool Exhausted=false,bool MinimumLossProven=false,int? ProofPercent=null,bool MemoryWaiting=false,int ProofDone=0,int ProofTotal=0,bool ProofBlocked=false,double? ProofCoverage=null,long LossRevision=0,PotionCost[]? Options=null,string? PotionKey=null,GoalResult? Goal=null)
{
    internal HudBest WithoutRoute()=>this with {
        Party=System.Array.Empty<HudSeat>(),End=null,Outcome="",Discovered=null,Options=null,Pv=null,
        MinimumLossProven=false,ProofPercent=null,ProofCoverage=null,ProofBlocked=false
    };
}
internal sealed record HudFrame(long Visits,long? Pv,int Round,string State,string Choice,bool Live,bool Running,HudPlan? Plan,bool Hidden=false,long Rate=0,int? ViewPercent=null,HudBest? Opening=null,HudBest? Realtime=null,int? OpeningShare=null,string? PauseReason=null,bool MemoryWaiting=false,long? TreeBytes=null,string? SearchError=null,string[]? Warnings=null,HudRoundGraph[]? Graphs=null,int ReadyRound=0,ulong[]? ReadyEnds=null,HudSearchHistory? History=null,bool RouteDiverged=false,long LiveHistoryRevision=0,HudExecuted? Executed=null,bool LiveSettling=false);
internal sealed record HudExecuted(int Round,int[] Steps)
{
    internal bool Includes(int round,int step)=>Round==round && Array.IndexOf(Steps,step)>=0;
    internal static bool Same(HudExecuted? a,HudExecuted? b)=>ReferenceEquals(a,b) ||
        a!=null && b!=null && a.Round==b.Round && System.Linq.Enumerable.SequenceEqual(a.Steps,b.Steps);
}
internal sealed record HudGraphAction(int Step,int Seat,string Name,string? Target=null,string? Choices=null,
    int Damage=0,int? Energy=null,int? EndLoss=null,bool TargetKilled=false,string? OtherKills=null,uint? EnemyTargetId=null,
    string? EnergyAlternatives=null,string? DamageAlternatives=null);
internal sealed record HudGraph(int Round,HudSeat[] Party,HudGraphAction[] Actions,RouteDependencies.Edge[] Dependencies,bool Conservative=false,int VerificationVersion=0);
internal sealed record HudRoundGraph(int Round,HudGraph? Graph,bool Updating,string? Error=null);
