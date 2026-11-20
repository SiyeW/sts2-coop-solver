using System.Linq;
using System.Collections.Generic;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace CoopSolver;
internal sealed partial class Advisor
{
    private sealed record Played(ulong Seat,string Kind,string Id,uint? Target,int Slot,CardModel? Physical)
    {
        internal bool Matches(KernelTeamSearch.Action expected,KernelSession? origin)
        {
            if(expected.Player.NetId!=Seat || expected.Target?.CombatId!=Target)return false;
            // Matching inputs can propose a suffix; the new root must replay it before confirmation.
            if(Kind=="end")return expected.EndTurn;
            if(Kind=="potion")return expected.Potion!=null && origin!=null && CapturedBelt.Slot(origin,expected.Potion)==Slot;
            return expected.Card?.Id.Entry==Id;
        }
    }
    private readonly List<Played> completedInputs=new();
    private ActionExecutor? observedExecutor;
    private void WatchNativeInput(CombatState? combat)
    {
        var executor=combat==null ? null : RunManager.Instance.ActionExecutor;
        if(ReferenceEquals(observedExecutor,executor))return;
        if(observedExecutor!=null)
        {
            observedExecutor.BeforeActionExecuted-=OnNativeBegin;
            observedExecutor.AfterActionExecuted-=OnNativeAction;
        }
        observedExecutor=executor;liveAdvisor!.completedInputs.Clear();foreach(var slot in openChoices.Values)slot.Probe?.Cancel();openChoices.Clear();SetPinned("");
        if(executor!=null)
        {
            executor.BeforeActionExecuted+=OnNativeBegin;
            executor.AfterActionExecuted+=OnNativeAction;
        }
    }
    private sealed class PendingHand(PlayCardAction card,KernelSession? root,Move? move,KernelChoice[] choices)
    {
        internal PlayCardAction Card=card;
        internal KernelSession? Root=root;
        internal Move? Move=move;
        internal KernelChoice[] Choices=choices;
        internal HandChoiceProbe? Probe;
        internal bool Waiting;
        internal string? SavedChoice;
    }
    private readonly Dictionary<GameAction,PendingHand> openChoices=new();
    private bool SelectingHand=>openChoices.Values.Any(s=>s.Waiting) || NPlayerHand.Instance?.IsInCardSelection==true;
    private void OnNativeBegin(GameAction action)
    {
        if(action is not PlayCardAction card || openChoices.ContainsKey(action))return;
        var previous=window?.ShowingGuard==true || hudGuard?Selected.guardLine:Selected.shownLine;
        var planned=previous?.Steps.FirstOrDefault(a=>a.Player.NetId==action.OwnerId);
        bool same=planned?.Card?.Id==card.CardModelId && planned.Target?.CombatId==card.TargetId;
        var source=(showLive || liveAdvisor?.running==true) && liveAdvisor!.completedInputs.Count==0 ? liveAdvisor.root : null;
        Move? move=null;
        if(source!=null)
        {
            int seat=source.Party.ToList().FindIndex(p=>p.NetId==action.OwnerId);
            var physical=card.NetCombatCard.ToCardModelOrNull() ?? AccessTools.Field(typeof(PlayCardAction),"_card").GetValue(card) as CardModel;
            if(seat>=0)
            {
                var hand=source.Hand(source.Party[seat]);
                int slot=physical==null ? -1 : hand.ToList().IndexOf(physical);
                if(slot<0 && hand.Count(c=>c.Id==card.CardModelId)==1)slot=hand.ToList().FindIndex(c=>c.Id==card.CardModelId);
                if(slot>=0)move=new(seat,MoveKind.Card,slot,source.CardStateKey(hand[slot]),card.TargetId);
            }
        }
        var pending=new PendingHand(card,source,move,same?planned!.Choices?.ToArray()??System.Array.Empty<KernelChoice>():System.Array.Empty<KernelChoice>());
        if(window!=null && CombatManager.Instance.DebugOnlyGetState() is {} room)
        {
            int player=room.Players.ToList().FindIndex(p=>p.NetId==card.OwnerId)+1;
            if(player>0)pending.SavedChoice=window.SavedChoiceLine(room.RoundNumber,player,
                (card.NetCombatCard.ToCardModelOrNull()?.Title??ModelDb.GetById<CardModel>(card.CardModelId).Title));
        }
        openChoices.Add(action,pending);
        action.BeforePausedForPlayerChoice+=OnChoiceWaiting;
        action.AfterFinished+=FinishChoice;
        action.BeforeCancelled+=FinishChoice;
    }
    private void OnChoiceWaiting(GameAction action)
    {
        if(!openChoices.TryGetValue(action,out var s))return;
        s.Waiting=true;nextChoicePaint=0;
        if(System.Environment.GetEnvironmentVariable("STS2_UX_TEST")!=null)
            Godot.GD.Print($"[ChoiceCheck] waiting {s.Card.CardModelId.Entry} source={s.Root!=null} move={s.Move!=null} planned={s.Choices.Length}");
        if(!manuallyPaused && s.Choices.Length==0 && s.Root!=null && s.Move!=null && s.Probe==null)
            s.Probe=new(s.Root,s.Move,Selected.meter);
        RefreshChoice();
    }
    private long nextChoicePaint;
    private void RefreshChoice()
    {
        if(openChoices.Count==0 || System.Environment.TickCount64<nextChoicePaint)return;
        nextChoicePaint=System.Environment.TickCount64+100;
        var messages=new List<string>();
        foreach(var s in openChoices.Values)
        {
            if(!s.Waiting)continue;
            var choices=s.Choices.Length>0 ? s.Choices : s.Probe?.Latest?.Choices;
            string left=s.Card.Player.Character.Title.GetFormattedText()+" · ";
            if(choices==null || choices.Length==0){messages.Add(s.SavedChoice!=null?"已存路线供对照："+s.SavedChoice:left+"正在选牌，原路线保留在下方。");continue;}
            foreach(var c in choices)
            {
                if(c.Effect=="Discard" && c.Owner==RunManager.Instance.NetService.NetId &&
                    NPlayerHand.Instance is {IsInCardSelection:true} hand && s.Card.Player.PlayerCombatState is {} pcs)
                {
                    var filter=AccessTools.Field(typeof(NPlayerHand),"_currentSelectionFilter").GetValue(hand) as System.Func<CardModel,bool>;
                    var options=KernelChoice.MenuTokens(pcs.Hand.Cards.Where(card=>filter==null || filter(card)).ToArray());
                    if(!c.Cards.All(options.Contains))
                    {messages.Add(left+"实际可选牌已变化，请对照当前手牌。");continue;}
                }
                string verb=c.Effect switch {"Discard"=>"弃掉","Exhaust"=>"消耗","MoveToHand"=>"取回",_=>"选择"};
                messages.Add(left+(s.Choices.Length>0?"路线建议":"本回合试算建议")+verb+"："+ChoiceNames(c));
            }
        }
        SetPinned(string.Join("\n",messages));
    }
    private void FinishChoice(GameAction action)
    {
        if(!openChoices.Remove(action,out var s))return;
        action.BeforePausedForPlayerChoice-=OnChoiceWaiting;action.AfterFinished-=FinishChoice;action.BeforeCancelled-=FinishChoice;
        s.Probe?.Cancel();
        nextChoicePaint=0;
        if(openChoices.Count==0)SetPinned("");else RefreshChoice();
    }
    private void OnNativeAction(GameAction action)
    {
        if(action.State.ToString() is "GatheringPlayerChoice" or "ReadyToResumeExecuting")return;
        FinishChoice(action);
        if(action.Exception!=null)return;
        Played? observed=action switch
        {
            PlayCardAction c=>new(c.OwnerId,"card",c.CardModelId.Entry,c.TargetId,-1,
                AccessTools.Field(typeof(PlayCardAction),"_card")?.GetValue(c) as CardModel),
            UsePotionAction p=>new(p.OwnerId,"potion","",p.TargetId,checked((int)p.PotionIndex),null),
            EndPlayerTurnAction end=>new(end.OwnerId,"end","",null,-1,null),
            _=>null
        };
        if(observed!=null)
        {
            if(showLive || liveAdvisor?.running==true)liveAdvisor!.completedInputs.Add(observed);
            if(seedDirectory!=null)tapeInputs.Add(new(observed.Seat,observed.Kind,observed.Id,observed.Slot,observed.Target));
        }
    }
}
