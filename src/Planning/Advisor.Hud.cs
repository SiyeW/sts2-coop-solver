using System;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using Godot;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private CombatHud? hud;
    private RouteShare? share;
    private CombatState? hudRoom;
    private INetGameService? hudNet;
    private HudFrame? hudFrame;
    private HudPlan? guardHud,provisionalHud;
    private long nextHud;
    private string pinnedChoice="";
    private bool hudGuard;
    private string[] playerNames=Array.Empty<string>();

    private void ToggleResults()
    {
        resultsHidden=!resultsHidden;nextHud=0;
        if(resultsHidden)window?.Conceal();
        else Selected.Paint();
    }
    private void SetPinned(string text)
    {pinnedChoice=text;if(!resultsHidden)window?.SetChoice(text);nextHud=0;}
    internal static string CharacterInk(MegaCrit.Sts2.Core.Models.CharacterModel character)
    {
        var source=character.Id.Entry=="NECROBINDER"?MegaCrit.Sts2.Core.Helpers.StsColors.pink:character.NameColor;
        return source.ToHtml(false);
    }
    private HudPlan MakeHud(InteractiveSearch.Line line,string route,string summary)
    {
        var seats=participants.Select((p,i)=>new HudSeat(p.NetId,playerNames[i],p.Character.Title.GetFormattedText(),
            line.FirstHp==null?null:startHp[i]-line.FirstHp[i],startHp[i]-line.Hp[i],
            line.FirstHp!=null && line.FirstHp[i]<=0,line.Hp[i]<=0,line.Steps.Count(a=>a.Player.NetId==p.NetId && a.Potion!=null),line.Hp[i],CharacterInk(p.Character))).ToArray();
        var kills=(line.Kills??Array.Empty<RouteKill>()).Select(k=>new HudKill(k.Step,
            k.Enemy.CombatId is uint id && targetNames.TryGetValue(id,out var name)?name:k.Enemy.Name.ToString(),
            ReferenceEquals(k.Enemy,line.Steps[k.Step].Target))).ToArray();
        return new(rootRound,line.EndTurn,seats,route,summary,string.Join("\n",line.Risks),kills,line.Rounds);
    }
    private HudPlan? referenceSource,referenceDisplay;
    private BattleExchange.Position? referencePosition;
    private HudPlan? FallbackPlan()
    {
        if(!Follow || owner!.exchange==null || !ReferenceEquals(owner.observedBoard,board) ||
            owner.savedResult?.Hud is not {} plan)return null;
        if(capturedWitness!=null && capturedWitness==owner.openingWitness && !waitingForStable && completedInputs.Count==0)return plan;
        if(ReferenceEquals(referenceSource,plan) && ReferenceEquals(referencePosition,livePosition))return referenceDisplay;
        referencePosition=livePosition;
        referenceSource=plan;
        int used=livePosition!=null && owner.savedResult.Seed is {} seed?owner.exchange.MatchedPrefix(livePosition,seed):-1;
        bool trim=used>0 && used<owner.savedResult.Seed!.Steps.Length;
        string title=trim?"开局余线待复算":"开局路线供对照";
        string steps=plan.Steps;
        if(trim)
        {
            var rows=steps.Split('\n');int index=0;
            while(index<rows.Length && used>0)
                if(rows[index++].StartsWith("P",StringComparison.Ordinal))used--;
            steps=$"第 {rootRound} 回合\n"+string.Join("\n",rows.Skip(index).SkipWhile(s=>s.StartsWith($"第 {rootRound} 回合",StringComparison.Ordinal)));
        }
        referenceDisplay=plan with {Steps=title+"\n"+steps,Result=title,Start=trim?rootRound:plan.Start,
            Rounds=null,Kills=null,Party=plan.Party.Select(s=>s with {Hp=null,ToEnd=null,ThisTurn=null,Potions=null,DiesThisTurn=false,DiesAtEnd=false}).ToArray()};
        return referenceDisplay;
    }
    private HudBest SummaryFor(Advisor side,CombatState room,long now)
    {
        var current=ReferenceEquals(side.board,room);
        if(side.Follow && exchange!=null && (side.capturedWitness==null || exchange.Current(side.capturedWitness)==null))current=false;
        var best=current?side.savedResult:null;
        if(current && side.Follow && side.capturedWitness!=null && side.capturedWitness==openingWitness &&
            !side.waitingForStable && side.completedInputs.Count==0 && savedResult?.Improves(best)==true)best=savedResult;
        var plan=best?.Hud;
        var line=side.shownLine;
        long? pv=current && ReferenceEquals(best,side.savedResult) && line?.Steps.Length>0 && side.root!=null?
            side.meter.BranchVisits(side.root,line.Steps[0]):null;
        return new(current?side.meter.Actions:0,pv,current?side.meter.Rate(now,side.running):0,
            side.Follow?room.RoundNumber:1,plan?.End,plan?.Party??Array.Empty<HudSeat>(),
            best?.Ending is {} end?(end.Victory?"胜":"败"):"");
    }
    private bool TickHud()
    {
        var rm=RunManager.Instance;
        var net=rm?.NetService;
        var room=CombatManager.Instance.IsInProgress?CombatManager.Instance.DebugOnlyGetState():null;
        bool eligible=net?.Type switch
        {
            NetGameType.Singleplayer=>room?.Players.Count==1,
            NetGameType.Host or NetGameType.Client=>net.IsConnected && room?.Players.Count>=2,
            _=>false
        };
        if(!eligible)room=null;
        if(!ReferenceEquals(room,hudRoom) || !ReferenceEquals(net,hudNet))
        {
            hudRoom=room;hudNet=net;hudFrame=null;hudGuard=false;nextHud=0;hud?.Hide();
            string battle=room==null?"":$"{room.RunState.Rng.StringSeed}/{room.RunState.TotalFloor}/{room.Encounter?.Id.Entry}/{string.Join(",",room.Players.Select(p=>p.NetId))}";
            share?.Reset(net,battle);
        }
        if(room==null || net==null)return false;
        bool localSolver=net.Type!=NetGameType.Client;
        long now=System.Environment.TickCount64;
        if(!localSolver && net is NetClientGameService guest)share?.Guest(guest,now);
        if(hudFrame==null || now>=nextHud)
        {
            nextHud=now+250;
            var a=Selected;
            if(localSolver)
            {
                var isGuard=hudGuard && a.guardHud!=null;
                var plan=isGuard?a.guardHud:a.provisionalHud??a.savedResult?.Hud??a.FallbackPlan();
                if(!isGuard && a.Follow && a.capturedWitness==openingWitness && openingWitness!=null &&
                    ReferenceEquals(a.board,observedBoard) && !a.waitingForStable && a.completedInputs.Count==0 && savedResult?.Improves(a.savedResult)==true)
                    plan=savedResult.Hud;
                var pv=isGuard?a.guardLine:a.shownLine;
                long? branch=(isGuard || ReferenceEquals(plan,a.savedResult?.Hud)) && a.provisionalHud==null && pv?.Steps.Length>0 && a.root!=null?a.meter.BranchVisits(a.root,pv.Steps[0]):null;
                string state=a.waitingForStable?"等待实战结算":a.running?"计算中":a.savedResult!=null?"已保留路线":"等待求解";
                var work=a.engine?.Latest?.Metrics;
                if(work!=null && (work.LoadedSeeds!=0 || work.HumanSteps!=0))
                    state+=$" · 已恢复 {work.LoadedSeeds} 条计算路线、{work.HumanSteps} 步实战，接续 {work.Joined} 段";
                if(a.Follow && a.liveMessage.Length!=0)state+=" · "+a.liveMessage;
                if(plan!=null && ReferenceEquals(plan,a.referenceDisplay))state+=" · 开局路线供对照";
                hudFrame=new(a.meter.Actions,branch,(a.Follow?room.RoundNumber:a.root!=null?a.rootRound:room.RoundNumber),state,pinnedChoice,a.Follow,UseWorker?searchEnabled:a.running,plan,Rate:a.meter.Rate(now,a.running),ViewPercent:ViewPercent);
                var opening=SummaryFor(this,room,now);
                var realtime=SummaryFor(liveAdvisor!,room,now);
                hudFrame=resultsHidden?hudFrame with {Hidden=true,Plan=null,Pv=null,State="",Choice="",Round=0,ViewPercent=null,
                    Visits=opening.Visits+realtime.Visits,Rate=opening.Rate+realtime.Rate}:
                    hudFrame with {Opening=opening,Realtime=realtime};
                if(net is NetHostGameService server)share?.Host(server,hudFrame,now);
            }
            else hudFrame=share?.Remote??new(0,null,room.RoundNumber,"等待房主共享","",false,false,null);
        }
        if(NativeUi && (localSolver || share?.Remote!=null))
        {
            hud??=new CombatHud(tree,()=>requested=true,()=>{showLive=!showLive;switchRequested=true;hudGuard=false;},
                ()=>{hudGuard=!hudGuard;nextHud=0;},()=>OpenRoute(),ToggleResults);
            hud.Update(hudFrame,net.NetId,localSolver,canSolve || root!=null && SearchCapacity.OpeningPercent>0,Selected.guardHud!=null,hudGuard);
        }
        else if(hud?.Visible==true)hud.Hide();
        return !localSolver;
    }
}
