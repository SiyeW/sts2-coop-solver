using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

internal sealed record SpentPotion(ulong Seat,string Id);
internal sealed record PotionCost(int Loss,int Deaths,int Theft,SpentPotion[] Bottles,int VictoryReturn=0,GoalResult? Goal=null)
{
    public int[]? SeatLoss {get;init;}
    public int? EnemyHp {get;init;}
    internal int Metric=>EnemyHp??Cost;
    public int Cost=>Loss-VictoryReturn;
    internal int Count=>Bottles.Length;
    internal string Key=>(Goal==null?"":$"goal:{Goal.Kind}:{Goal.Lost}:{Goal.Escaped}/")+string.Join("/",Bottles.Select(b=>$"{b.Seat}:{b.Id}"));
    internal static PotionCost From(KernelSession root,InteractiveSearch.Line line)
    {
        var bottles=line.Steps.Where(a=>a.Potion!=null).Select(a=>new SpentPotion(a.Player.NetId,a.Potion!.Id.Entry)).ToList();
        int restored=0;
        foreach(var p in root.Party)
        {
            var events=DeathSaves.Events(line.Rounds,p.NetId);
            restored+=events.Sum(e=>e.Hp);
            bottles.AddRange(events.Where(e=>e.Potion).Select(e=>new SpentPotion(p.NetId,e.Source)));
        }
        var end=line.Ending!;
        while(bottles.Count<(int)end.ResourcesConsumed)bottles.Add(new(0,""));
        return new(line.Rounds is {Length:>0}?line.Rounds.Sum(r=>r.Loss.Sum()):root.Party.Sum(p=>root.Hp(p.Creature))-end.PostCombatHp.Sum()+restored,end.IrreversibleLoss,end.OutstandingStolenResource,
            bottles.OrderBy(b=>b.Seat).ThenBy(b=>b.Id,StringComparer.Ordinal).ToArray(),end.Victory && root.Party.Count>1?end.PostCombatHp.Count(h=>h==0):0,GoalResult.For(end)){EnemyHp=end.Victory?null:end.RemainingEnemyHp};
    }
    internal int Tier(PotionCost other)=>Deaths!=other.Deaths?Deaths.CompareTo(other.Deaths):Theft.CompareTo(other.Theft);
    // Keep distinct bottle combinations of equal size. A strictly cheaper
    // combination eliminates one with equal or greater loss.
    internal bool Dominates(PotionCost other)
    {
        if(EnemyHp.HasValue!=other.EnemyHp.HasValue)return !EnemyHp.HasValue;
        if(EnemyHp.HasValue)return Count<other.Count && Metric<=other.Metric;
        if(Goal==null && other.Goal==null)return Tier(other)<0 || Tier(other)==0 && Count<other.Count && Cost<=other.Cost;
        if(Goal==null || other.Goal==null || Goal.Kind!=other.Goal.Kind)return false;
        int objective=Goal.CompareLoss(other.Goal);
        return Deaths<other.Deaths || Deaths==other.Deaths && objective<=0 && Count<=other.Count && Cost<=other.Cost &&
            (Count<other.Count || objective<0 && Bottles.SequenceEqual(other.Bottles));
    }
}
internal sealed record PotionRoute<T>(PotionCost Cost,T Value);
internal sealed class PotionFront<T>
{
    internal PotionRoute<T>[] Rows {get;private set;}=Array.Empty<PotionRoute<T>>();
    internal void ReplaceDisplay(Func<T,T> replace)=>Rows=Rows.Select(r=>r with {Value=replace(r.Value)}).ToArray();
    internal void Clear()=>Rows=Array.Empty<PotionRoute<T>>();
    internal bool Offer(PotionCost cost,T value,Comparison<T> compare)
    {
        string key=cost.Key;
        foreach(var row in Rows)
        {
            if(row.Cost.Dominates(cost))return false;
            if(row.Cost.Key==key && row.Cost.EnemyHp.HasValue==cost.EnemyHp.HasValue && (cost.EnemyHp.HasValue || row.Cost.Tier(cost)==0) &&
                (row.Cost.Metric<cost.Metric || row.Cost.Metric==cost.Metric && compare(value,row.Value)<=0))return false;
        }
        Rows=Rows.Where(r=>r.Cost.Key!=key && !cost.Dominates(r.Cost)).Append(new(cost,value))
            .OrderBy(r=>r.Cost.Metric).ThenBy(r=>r.Cost.Count).ThenBy(r=>r.Cost.Goal?.Lost??0).ThenBy(r=>r.Cost.Goal?.Escaped??false).ThenBy(r=>r.Cost.Key,StringComparer.Ordinal).ToArray();
        return true;
    }
}
internal sealed class PotionSelection
{
    internal PotionCost? Manual;
    internal PotionRoute<T>? Pick<T>(PotionRoute<T>[] rows)
    {
        if(rows.Length==0)return null;
        if(Manual?.EnemyHp!=null && rows[0].Cost.EnemyHp==null)Manual=null;
        if(Manual==null)return rows[0];
        var same=rows.FirstOrDefault(r=>r.Cost.Key==Manual.Key);
        if(same!=null){Manual=same.Cost;return same;}
        var next=rows.FirstOrDefault(r=>r.Cost.Dominates(Manual));
        if(next==null && Manual.Goal!=null)next=rows.FirstOrDefault(r=>r.Cost.Goal==Manual.Goal);
        if(next!=null)Manual=next.Cost;return next??rows[0];
    }
}

internal sealed partial class AnytimeSearch
{
    internal readonly PotionFront<InteractiveSearch.Line> PotionRoutes=new();
    private readonly Dictionary<string,Proposal> waitingPotions=new();
    private IEnumerator<bool>? savingTrial;
    private KernelSession? savingState;
    private void StartPotionSavings(IReadOnlyList<KernelTeamSearch.Action> verified)
    {
        savingTrial?.Dispose();savingState=null;
        savingTrial=verified.Any(a=>a.Potion!=null)?TryOmittingPotions(verified).GetEnumerator():null;
    }
    private void AdvancePotionSavings()
    {
        try{if(savingTrial!.MoveNext())return;}
        catch(Exception e) when(e is InvalidOperationException or NotSupportedException or ArgumentException){Boundary(e.GetBaseException().Message);}
        savingTrial!.Dispose();savingTrial=null;savingState=null;
    }
    private IEnumerable<bool> TryOmittingPotions(IReadOnlyList<KernelTeamSearch.Action> line)
    {
        for(int omit=0;omit<line.Count;omit++)
        {
            if(line[omit].Potion==null || omit==0 && RequiredFirst!=null)continue;
            var state=savingState=root.ForkForReplay();
            var actual=new List<KernelTeamSearch.Action>(line.Count-1);
            for(int i=0;i<line.Count && !state.HasTerminal;i++)
            {
                if(i==omit)continue;
                if(!state.ReplayPlanned(line[i],int.MaxValue,out _,out var action))break;
                actual.Add(action!);yield return true;
            }
            if(state.HasWon)
                Submit(new(Array.Empty<Edge>(),actual,WithInventory(TerminalRecord.Capture(state,party,true,""),state),"potion-omission"));
            savingState=null;yield return true;
        }
    }
    private bool CollectPotions=>GoalResult.KindAt(root)!=CombatGoal.None || root.Party.Any(p=>root.CapturedPotions(p).Count!=0);
    internal static int CompareLines(InteractiveSearch.Line a,InteractiveSearch.Line b)=>
        ComparePlans(a.Ending!,a.Steps.Length,b.Ending!,b.Steps.Length,a.Rounds,b.Rounds);

    private void RememberPotionRoute(Proposal proposal,TerminalRecord record,KernelSession state)
    {
        if(!CollectPotions || !record.Victory && PotionRoutes.Rows.Any(r=>r.Cost.EnemyHp==null))return;
        var rounds=verificationDeaths!.Rounds();
        int loss=rounds.Sum(r=>r.Loss.Sum());
        if(record.Victory && GoalResult.KindAt(root)==CombatGoal.None && PotionRoutes.Rows.Where(r=>r.Cost.EnemyHp==null).Any(r=>r.Cost.Deaths<record.IrreversibleLoss || r.Cost.Deaths==record.IrreversibleLoss &&
            (r.Cost.Theft<record.OutstandingStolenResource || r.Cost.Theft==record.OutstandingStolenResource &&
             r.Cost.Count<record.ResourcesConsumed && r.Cost.Cost<=loss-(party.Length>1?record.PostCombatHp.Count(h=>h==0):0))))return;
        var recipes=new TreeStep?[replayed.Count];
        for(int i=0;i<proposal.Prefix.Length;i++)recipes[i]=proposal.Prefix[i].Exact;
        var candidate=new InteractiveSearch.Line(replayed.ToArray(),verifyingStates.ToArray(),record,state.TurnNumber,record.PostCombatHp.ToArray(),
            record.RemainingEnemies,record.RemainingEnemyHp,party.Sum(state.HeldPotionCount),ReplayCoverage.RouteDetails(replayRisks.ToArray(),replayed),0,HelperMeter?.Actions??0,Nodes,verifyingFirstHp,recipes,
            Kills:verificationDeaths.Freeze(),Rounds:rounds);
        PotionRoutes.Offer(PotionCost.From(root,candidate),candidate,CompareLines);
    }
    private bool QueuePotionAlternative(Proposal proposal)
    {
        var end=proposal.Ending;
        if(!end.Verified || !CollectPotions || !end.Victory && Best?.Record?.Victory==true)return false;
        if(!end.Victory && PotionRoutes.Rows.Any(r=>r.Cost.EnemyHp==null ||
            (r.Cost.Count<end.ResourcesConsumed || r.Cost.Count==0 && end.ResourcesConsumed==0) && r.Cost.Metric<=end.RemainingEnemyHp))return false;
        var drinks=new List<SpentPotion>();
        foreach(var edge in proposal.Prefix)
        {
            if(edge.Exact?.Move is {Kind:MoveKind.Potion} m)drinks.Add(new(party[m.Seat].NetId,m.Key));
            else if(edge.Prior?.Potion is {} bottle)drinks.Add(new(edge.Prior.Player.NetId,bottle.Id.Entry));
            else if(edge.Saved is {Slot:>=0} s)drinks.Add(new(s.Seat,s.Id));
        }
        drinks.AddRange(proposal.Tail.Where(a=>a.Potion!=null).Select(a=>new SpentPotion(a.Player.NetId,a.Potion!.Id.Entry)));
        while(drinks.Count<(int)end.ResourcesConsumed)drinks.Add(new(0,""));
        var optimistic=new PotionCost(root.Party.Sum(p=>root.Hp(p.Creature))-end.PostCombatHp.Sum(),end.IrreversibleLoss,end.OutstandingStolenResource,
            drinks.OrderBy(d=>d.Seat).ThenBy(d=>d.Id,StringComparer.Ordinal).ToArray(),end.Victory && party.Length>1?end.PostCombatHp.Count(h=>h==0):0,GoalResult.For(end)){EnemyHp=end.Victory?null:end.RemainingEnemyHp};
        var key=optimistic.Key;
        int length=proposal.Prefix.Length+proposal.Tail.Count;
        foreach(var row in PotionRoutes.Rows)
        {
            var cost=row.Cost;
            if(cost.Dominates(optimistic))return false;
            if(cost.Key!=key || cost.EnemyHp.HasValue!=optimistic.EnemyHp.HasValue || end.Victory && cost.Tier(optimistic)!=0)continue;
            if(cost.Metric<optimistic.Metric)return false;
            if(cost.Metric==optimistic.Metric && (!end.Victory || row.Value.EndTurn<=root.TurnNumber+end.RoundsSurvived && row.Value.Steps.Length<=length))return false;
        }
        if(waitingPotions.TryGetValue(key,out var old) && ComparePlans(end,length,old.Ending,old.Prefix.Length+old.Tail.Count)<=0)
            return false;
        waitingPotions[key]=proposal;
        return true;
    }
}
