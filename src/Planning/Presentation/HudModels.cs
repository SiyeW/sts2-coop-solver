using System;
using System.Collections.Generic;
using System.Linq;

namespace CoopSolver;

internal sealed record HudSeat(ulong Id,string Name,string Role,int? ThisTurn,int? ToEnd,bool DiesThisTurn,bool DiesAtEnd,int? Potions,int? Hp=null,string? Color=null,int RevivedHp=0,int Recovered=0);
internal enum TurnKillEffect { None, Orb, Poison, Doom }
internal sealed record HudKill(int Step,string Name,bool IsTarget,ulong? CreditedSeat=null,TurnKillEffect Effect=TurnKillEffect.None,
    uint? EnemyId=null,HudTarget? Target=null)
{
    internal string Caption(HudPlan? plan,IReadOnlyDictionary<uint,string>? names=null)=>
        HudTargetLabel.For(Target,Name,plan,EnemyId is uint id && names?.TryGetValue(id,out var live)==true?live:null)?.Text()??"";
}
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

        foreach(var (step,_,end) in moves)
        {
            if(!StepDamage.TryGetValue(step,out var credited))continue;
            if(!end)
            {
                // The action owns the combined impact; round totals still
                // report each hit under the player who dealt it.
                damage[step]=credited.Sum();
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
internal sealed record HudPlan(int Start,int End,HudSeat[] Party,string Steps,string Result,string Risks,HudKill[]? Kills=null,HudRoundHp[]? Rounds=null,int? EndEnemyHp=null,int? TeamLoss=null,HudTerm[]? Terms=null,string? Language=null,GoalResult? Goal=null,Dictionary<int,uint>? EnemyTargets=null,
    HudMove[]? Moves=null,HudOutcome? ResultKind=null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string Outcome
    {
        get
        {
            if(ResultKind is {} result)return result switch {HudOutcome.Win=>"胜",HudOutcome.Loss=>"败",_=>""};
            string title=Steps.Split('\n',2)[0];
            return title.Contains("胜利")?"胜":title.Contains("倒下")?"败":"";
        }
    }
}
internal sealed record HudBest(long Visits,long? Pv,long Rate,int Round,int? End,HudSeat[] Party,string Outcome,string? Discovered=null,bool Checking=false,bool Exhausted=false,bool MinimumLossProven=false,int? ProofPercent=null,bool MemoryWaiting=false,int ProofDone=0,int ProofTotal=0,bool ProofBlocked=false,double? ProofCoverage=null,long LossRevision=0,PotionCost[]? Options=null,string? PotionKey=null,GoalResult? Goal=null,bool ZeroLossProven=false)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool LossIncreased {get;init;}
    internal HudBest WithoutRoute()=>this with {
        Party=System.Array.Empty<HudSeat>(),End=null,Outcome="",Discovered=null,Options=null,Pv=null,
        MinimumLossProven=false,ZeroLossProven=false,ProofPercent=null,ProofCoverage=null,ProofBlocked=false
    };
}
internal sealed record HudFrame(long Visits,long? Pv,int Round,string State,string Choice,bool Live,bool Running,HudPlan? Plan,bool Hidden=false,long Rate=0,int? ViewPercent=null,HudBest? Opening=null,HudBest? Realtime=null,int? OpeningShare=null,string? PauseReason=null,bool MemoryWaiting=false,long? TreeBytes=null,string? SearchError=null,string[]? Warnings=null,HudRoundGraph[]? Graphs=null,int ReadyRound=0,ulong[]? ReadyEnds=null,HudSearchHistory? History=null,bool RouteDiverged=false,long LiveHistoryRevision=0,HudExecuted? Executed=null,bool LiveSettling=false,HudSharedPhase Phase=HudSharedPhase.Unknown,HudPotionMenu? PotionMenu=null);
internal sealed record HudPotionPrompt(ulong Actor,ulong Chooser,HudName Potion,int Slot,HudCardLabel[]? Cards,bool Discard,
    bool UseCompleted=false)
{
    internal string? Text()=>Cards switch {
        null=>null, {Length:0}=>Ui.Text("format.025"),
        _=>Ui.Text(Discard?"format.023":"route.choose")+string.Join(Ui.Text("ui.044"),Cards.Select(card=>card.Text()))
    };

    internal bool Matches(HudMove? move,HudSeat[] party)=>move is {IsPotion:true} && move.Potion==Potion &&
        (move.PotionSlot==null || move.PotionSlot==Slot+1) &&
        (uint)move.Seat<(uint)party.Length && party[move.Seat].Id==Actor &&
        move.InlinePicks().Any(pick=>pick.Seat>0 && pick.Seat<=party.Length && party[pick.Seat-1].Id==Chooser);
}
internal sealed record HudPotionMenu(int Round,HudSeat[] Party,HudPotionPrompt[] Prompts)
{
    internal bool HasHint(ulong chooser)=>Prompts.Any(prompt=>prompt.Chooser==chooser && prompt.Cards!=null);
    internal static bool Same(HudPotionMenu? left,HudPotionMenu? right)=>ReferenceEquals(left,right) ||
        left!=null && right!=null && left.Round==right.Round && left.Party.SequenceEqual(right.Party) &&
        left.Prompts.Length==right.Prompts.Length && left.Prompts.Zip(right.Prompts).All(pair=>
            (pair.First with {Cards=null})==(pair.Second with {Cards=null}) && pair.First.Text()==pair.Second.Text());

    internal Dictionary<int,int> StartedSteps(IEnumerable<(int Step,HudMove? Move)> moves,HudSeat[] seats,
        HudExecuted? progress)
    {
        var actions=moves.ToArray();
        var steps=new Dictionary<int,int>();
        for(int i=0;i<Prompts.Length;i++)
        {
            var prompt=Prompts[i];
            var matched=actions.Where(action=>!steps.ContainsKey(action.Step) && prompt.Matches(action.Move,seats)).ToArray();
            if(prompt.UseCompleted && progress?.Round==Round)
            {
                int last=progress.Steps.LastOrDefault(step=>matched.Any(action=>action.Step==step),-1);
                if(last>=0){steps.Add(last,i);continue;}
            }
            foreach(var (step,_) in matched)
                if(progress?.Includes(Round,step)!=true){steps.Add(step,i);break;}
        }
        return steps;
    }
}
internal sealed record HudExecuted(int Round,int[] Steps)
{
    internal bool Includes(int round,int step)=>Round==round && Array.IndexOf(Steps,step)>=0;
    internal static bool Same(HudExecuted? a,HudExecuted? b)=>ReferenceEquals(a,b) ||
        a!=null && b!=null && a.Round==b.Round && System.Linq.Enumerable.SequenceEqual(a.Steps,b.Steps);
}
internal sealed record HudGraphAction(int Step,int Seat,string Name,string? Target=null,string? Choices=null,
    int Damage=0,int? Energy=null,int? EndLoss=null,bool TargetKilled=false,string? OtherKills=null,uint? EnemyTargetId=null,
    string? EnergyAlternatives=null,string? DamageAlternatives=null,HudMove? Move=null,HudTarget[]? Defeated=null,
    int[]? EnergyValues=null,int[]? DamageValues=null)
{
    internal string? DamageText()=>DamageValues is {Length:>0}
        ?string.Join("|",DamageValues.Select(value=>value.ToString(Ui.Culture)))
        :DamageAlternatives??(Damage>0?Damage.ToString(Ui.Culture):null);
    internal bool EndTurn=>Move?.EndTurn??Name=="结束回合";
    internal bool IsPotion=>Move?.IsPotion??Name.StartsWith("用药 ",System.StringComparison.Ordinal);
    internal string Caption(HudPlan? plan)=>Move?.Text()??Ui.Route(IsPotion?Name[3..]:Name,plan);
}
internal sealed record HudDamageOptions(int[] Values,bool More=false)
{
    internal string? Text()=>Values.Length<=1?null:
        string.Join("|",Values.Select(value=>value.ToString(Ui.Culture)))+(More?"|…":"");
}
internal sealed record HudGraph(int Round,HudSeat[] Party,HudGraphAction[] Actions,RouteDependencies.Edge[] Dependencies,
    bool Conservative=false,int VerificationVersion=0,HudDamageOptions[]? RoundDamageValues=null);
internal sealed record HudRoundGraph(int Round,HudGraph? Graph,bool Updating,string? Error=null,bool OriginalOrderVerified=false);
