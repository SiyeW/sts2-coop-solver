using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private readonly Dictionary<Creature,Action<int,int>> hpObservers=new();
    private readonly Dictionary<ulong,int> lostHp=new(),healedHp=new();
    private int PastLoss(ulong seat)=>lostHp.GetValueOrDefault(seat);
    private void WatchHealth(CombatState? room)
    {
        foreach(var (creature,handler) in hpObservers)creature.CurrentHpChanged-=handler;
        hpObservers.Clear();lostHp.Clear();healedHp.Clear();
        if(room==null)return;
        foreach(var player in room.Players)
        {
            void Change(int before,int after)
            {
                if(after<before)lostHp[player.NetId]=PastLoss(player.NetId)+before-after;
                if(after>before)healedHp[player.NetId]=healedHp.GetValueOrDefault(player.NetId)+after-before;
            }
            hpObservers.Add(player.Creature,Change);player.Creature.CurrentHpChanged+=Change;
        }
    }

    private sealed record DisplayedPotion(OpeningResult Result,InteractiveSearch.Line? Line);
    private readonly PotionFront<DisplayedPotion> potions=new();
    private readonly PotionSelection openingPotion=new(),livePotion=new();
    private object? potionRoot,lastOffers,lastPromotions,lastSaved;
    private DisplayedPotion? displayedPotion;
    private static int CompareDisplay(DisplayedPotion a,DisplayedPotion b)=>AnytimeSearch.ComparePlans(
        a.Result.Ending!,a.Result.Steps,b.Result.Ending!,b.Result.Steps,a.Result.Hud?.Rounds,b.Result.Hud?.Rounds);

    private static PotionCost SavedCost(OpeningResult result)
    {
        var plan=result.Hud!;var steps=result.Seed!.Steps;
        var bottles=steps.Where(s=>s.Slot>=0).Select(s=>new SpentPotion(s.Seat,s.Id))
            .Concat(plan.Party.SelectMany(s=>DeathSaves.Events(plan.Rounds,s.Id).Where(e=>e.Potion).Select(e=>new SpentPotion(e.Seat,e.Source))))
            .OrderBy(b=>b.Seat).ThenBy(b=>b.Id,StringComparer.Ordinal).ToArray();
        return new(plan.Party.Sum(s=>s.ToEnd??0),result.Ending!.IrreversibleLoss,result.Ending.OutstandingStolenResource,bottles,result.Ending.Victory && plan.Party.Length>1?plan.Party.Count(s=>s.DiesAtEnd):0,plan.Goal){EnemyHp=result.Ending.Victory?null:result.Ending.RemainingEnemyHp};
    }
    private void SyncPotionRoutes()
    {
        if(!ReferenceEquals(root,potionRoot))
        {potions.Clear();displayedPotion=null;potionRoot=root;lastOffers=lastPromotions=lastSaved=null;}
        if(root==null || board==null)return;
        if(!ReferenceEquals(lastSaved,savedResult))
        {
            lastSaved=savedResult;
            if(savedResult is {ResourceScoreVersion:>=3,Ending:{Verified:true},Hud:not null,Seed:{Terminal:true}} saved)
                potions.Offer(SavedCost(saved),new(saved,shownLine),CompareDisplay);
        }
        void Import(PotionRoute<InteractiveSearch.Line>[]? rows,ref object? seen,bool publish)
        {
            if(rows==null || ReferenceEquals(seen,rows))return;
            seen=rows;
            foreach(var row in rows)
            {
                if(potions.Rows.Any(r=>ReferenceEquals(r.Value.Line,row.Value) || r.Cost.Dominates(row.Cost)))continue;
                var result=RouteRecord(row.Value);
                if(!potions.Offer(row.Cost,new(result,row.Value),CompareDisplay))continue;
                if(publish && owner?.exchange is {} source && livePosition!=null)source.OfferAlternative(livePosition,result.Seed!);
            }
        }
        if(ReferenceEquals(boundRoot,root) && engine?.Latest is {} snapshot && snapshot.Generation==generation)
            Import(snapshot.Options,ref lastOffers,Follow);
        if(!Follow)Import(exchange?.OpeningOptions,ref lastPromotions,false);
    }

    private PotionRoute<DisplayedPotion>[] MenuWithPrefix(PotionRoute<DisplayedPotion>[] rows)
    {
        if(rows.Length==0)return rows;
        var prefix=(exchange?.SpentPotions??Array.Empty<SpentPotion>()).Concat(
            (root?.Party??Array.Empty<MegaCrit.Sts2.Core.Entities.Players.Player>()).SelectMany(p=>
                PrefixFairies(liveAdvisor?.livePosition?.PastRevives,p.NetId))).ToArray();
        int lost=lostHp.Values.Sum();
        int lostMax=root!=null && liveAdvisor?.root is {} current?root.Party.Sum(p=>Math.Max(0,root.MaxHp(p.Creature)-current.MaxHp(p.Creature))):0;
        if(lost==0 && prefix.Length==0 && lostMax==0)return rows;
        return rows.Select(row=>row with {Cost=row.Cost with {Loss=lost+row.Cost.Loss,Goal=row.Cost.Goal is {Kind:CombatGoal.MaxHp} g?g with {Lost=g.Lost+lostMax}:row.Cost.Goal,Bottles=prefix.Concat(row.Cost.Bottles)
            .OrderBy(b=>b.Seat).ThenBy(b=>b.Id,StringComparer.Ordinal).ToArray()}}).ToArray();
    }
    private static IEnumerable<SpentPotion> PrefixFairies(DeathSaves.Entry? entry,ulong id)
    {
        for(;entry!=null;entry=entry.Previous)
            if(entry.Event is {Potion:true} e && e.Seat==id)yield return new(id,e.Source);
    }

    private HudFrame PotionView(HudFrame frame,bool live,PotionSelection first,PotionSelection second,bool saveSelection,PendingHand? choice)
    {
        if(saveSelection)playbackCandidate=null;
        if(frame.Hidden)return frame;
        var side=liveAdvisor!;
        var liveSource=sharedOpening?this:side;
        var opening=frame.Opening;var realtime=frame.Realtime;var plan=frame.Plan;
        if(saveSelection && realtime?.End!=null)playbackCandidate=liveSource.savedResult;
        void Apply(bool current,Advisor source,PotionSelection selection,ref HudBest? row)
        {
            if(row is not {Outcome:"胜" or "败"} || current && !sharedOpening && (source.waitingForStable || source.completedInputs.Count!=0) && choice==null)return;
            var rows=current && choice!=null?choice.PotionRows:source.potions.Rows;
            bool suffix=current && (!sharedOpening || choice!=null);
            if(row.Outcome=="胜" && rows.FirstOrDefault()?.Cost.EnemyHp!=null)return;
            if(suffix)rows=MenuWithPrefix(rows);
            var picked=selection.Pick(rows);
            if(picked==null)return;
            var entry=picked.Value;var hud=entry.Result.Hud!;
            if(saveSelection && current)playbackCandidate=entry.Result;
            if(saveSelection && (!current || !sharedOpening || live))source.displayedPotion=entry;
            var party=suffix?hud.Party.Select(s=>s with {ToEnd=s.ToEnd+PastLoss(s.Id),
                Recovered=s.Recovered+healedHp.GetValueOrDefault(s.Id),
                Potions=s.Potions+(exchange?.UsedPotions(s.Id)??0)+DeathSaves.AutomaticBottles(side.livePosition?.PastRevives,s.Id)}).ToArray():hud.Party;
            bool same=ReferenceEquals(hud,source.savedResult?.Hud) || choice!=null && ReferenceEquals(hud,choice.Display);
            var reference=current && choice!=null?choice.Rendered?.Ending:source.savedResult?.Ending;
            bool comparable=picked.Cost.Goal!=null?row.Exhausted:reference is {Victory:true,Verified:true} &&
                AnytimeSearch.Compare(entry.Result.Ending!,reference)==0;
            row=row with {Outcome=hud.Outcome,Party=party,End=hud.End,Pv=same?row.Pv:entry.Line?.Steps.Length>0 && source.root!=null && choice==null?source.meter.BranchVisits(source.root,entry.Line.Steps[0]):null,Discovered=entry.Line is {FoundAt:>=0}?Compact(entry.Line.FoundAt):same?row.Discovered:null,
                MinimumLossProven=comparable && row.MinimumLossProven,ProofCoverage=comparable?row.ProofCoverage:null,
                ProofPercent=comparable?row.ProofPercent:null,ProofBlocked=comparable && row.ProofBlocked,
                Options=rows.Select(r=>r.Cost with {SeatLoss=r.Value.Result.Hud!.Party.Select(s=>(s.ToEnd??0)+(suffix?PastLoss(s.Id):0)).ToArray()}).ToArray(),PotionKey=selection.Manual?.Key,Goal=picked.Cost.Goal};
            if(current==live)plan=hud;
        }
        Apply(false,this,first,ref opening);Apply(true,liveSource,second,ref realtime);
        if(sharedOpening && choice==null && opening!=null && realtime!=null && first.Manual?.Key==second.Manual?.Key)
            realtime=opening with {LossRevision=realtime.LossRevision};
        var visible=live?realtime:opening;
        return frame with {Opening=opening,Realtime=realtime,Live=live,Plan=plan,Pv=visible?.Pv,Visits=visible?.Visits??frame.Visits,Rate=visible?.Rate??frame.Rate};
    }
    private void SelectPotion(bool live,PotionCost? cost)
    {
        if(FollowingHost){share?.Choose(live,cost);nextHud=0;return;}
        if(live && playback!=null)StopPlayback();
        (live?livePotion:openingPotion).Manual=cost;
        if(showLive!=live){showLive=live;switchRequested=true;}
        nextHud=0;
    }
}
