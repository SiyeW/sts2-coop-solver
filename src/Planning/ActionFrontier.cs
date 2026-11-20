using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

internal enum MoveKind { Card, Potion, End }
internal sealed record Move(int Seat,MoveKind Kind,int Slot,string Key,uint? Target,int PotionChoice=-1);
internal sealed record TreeStep(Move Move,int[] Digits,string MenuEvidence);

internal sealed class MoveFamily(Move move)
{
    private readonly Queue<int[]> pending=new(new[]{Array.Empty<int>()});
    internal Move Move=>move;
    internal bool Finished=>pending.Count==0;
    internal void Save(BinaryWriter writer)
    {
        RoundAgenda.WriteMove(writer,Move);
        writer.Write(pending.Count);
        foreach(var digits in pending)RoundAgenda.WriteDigits(writer,digits);
    }
    internal static MoveFamily Load(BinaryReader reader)
    {
        var family=new MoveFamily(RoundAgenda.ReadMove(reader));
        family.pending.Clear();
        int count=reader.ReadInt32();
        for(int i=0;i<count;i++)family.pending.Enqueue(RoundAgenda.ReadDigits(reader));
        return family;
    }
    internal bool Expand(KernelSession state,out TreeStep? step,out KernelTeamSearch.Action? shown,out string reason)
    {
        var digits=pending.Dequeue();
        bool ok=ActionFrontier.Apply(state,move,digits,out shown,out var evidence,pending,out reason);
        step=ok?new TreeStep(move,digits,evidence):null;
        return ok;
    }
}

// One queue entry per physical action/target. Taking one branch rotates that
// action to the back so a large multi-select cannot monopolize this position.
internal sealed class ActionFrontier
{
    private readonly Queue<MoveFamily> queue = new();
    internal bool Finished => queue.Count==0;
    internal IEnumerable<MoveFamily> Families => queue;

    private ActionFrontier() {}
    internal void Save(BinaryWriter writer)
    {
        writer.Write(queue.Count);
        foreach(var family in queue)family.Save(writer);
    }
    internal static ActionFrontier Load(BinaryReader reader)
    {
        var frontier=new ActionFrontier();
        int count=reader.ReadInt32();
        for(int i=0;i<count;i++)frontier.queue.Enqueue(MoveFamily.Load(reader));
        return frontier;
    }


    internal ActionFrontier(KernelSession state, Action<string> unsupported)
    {
        var lanes=new List<Queue<Move>>();
        for(int seat=0;seat<state.Party.Count;seat++)
        {
            var p=state.Party[seat];
            if(!state.CanAct(p)) continue;
            var cards=new Queue<Move>();
            var potions=new Queue<Move>();
            var hand=state.Hand(p);
            for(int i=0;i<hand.Count;i++)
            {
                var card=hand[i];
                if(!state.CanPlay(card)) continue;
                foreach(var target in state.Targets(card))
                    cards.Enqueue(new(seat,MoveKind.Card,i,state.CardStateKey(card),target?.CombatId));
            }
            foreach(var bottle in state.UsablePotions(p))
            {
                if(!KernelSession.CanModelPotion(bottle)) { unsupported(bottle.Id.Entry); continue; }
                int slot=CapturedBelt.Slot(state,bottle);
                if(slot<0){unsupported(bottle.Id.Entry+"：缺少实体药水槽");continue;}
                foreach(var target in state.PotionTargets(bottle))
                {
                    var move=new Move(seat,MoveKind.Potion,slot,bottle.Id.Entry,target?.CombatId);
                    if(!KernelSession.RequiresPotionChoiceContract(bottle)) potions.Enqueue(move);
                    else
                    {
                        var picks=state.PotionActions(bottle,target,int.MaxValue,state.Party);
                        if(picks.Count==0) unsupported(bottle.Id.Entry+":menu");
                        for(int i=0;i<picks.Count;i++) potions.Enqueue(move with {PotionChoice=i});
                    }
                }
            }
            lanes.Add(cards);
            lanes.Add(potions);
            lanes.Add(new Queue<Move>(new[]{new Move(seat,MoveKind.End,-1,"",null)}));
        }
        while(lanes.Count>0)
        {
            foreach(var lane in lanes)
                if(lane.TryDequeue(out var move)) queue.Enqueue(new MoveFamily(move));
            lanes.RemoveAll(l=>l.Count==0);
        }
    }

    internal bool Expand(KernelSession state,out TreeStep? step,out KernelTeamSearch.Action? shown,out string reason)
    {
        var family=queue.Dequeue();
        bool ok=family.Expand(state,out step,out shown,out reason);
        if(!family.Finished) queue.Enqueue(family);
        return ok;
    }
    internal static bool Replay(KernelSession state,TreeStep step,out KernelTeamSearch.Action? shown,out string reason)
    {
        if(!Apply(state,step.Move,step.Digits,out shown,out var evidence,null,out reason)) return false;
        if(evidence==step.MenuEvidence) return true;
        reason="选牌菜单或选择已变化";
        return false;
    }
    internal static bool Apply(KernelSession state,Move move,int[] digits,out KernelTeamSearch.Action? shown,
        out string evidence,Queue<int[]>? pending,out string reason)
    {
        shown=null; evidence=""; reason="";
        var p=state.Party[move.Seat];
        if(!state.CanAct(p)) { reason="玩家行动状态变化"; return false; }
        KernelTeamSearch.Action action;
        if(move.Kind==MoveKind.End) action=new(p,null,null,EndTurn:true);
        else if(move.Kind==MoveKind.Card)
        {
            var hand=state.Hand(p);
            if(move.Slot>=hand.Count || state.CardStateKey(hand[move.Slot])!=move.Key)
            { reason="手牌身份变化"; return false; }
            var card=hand[move.Slot];
            var targets=state.Targets(card);
            if(!TryTarget(targets,move.Target,out var target)) {reason="卡牌目标变化";return false;}
            action=new(p,card,target);
        }
        else
        {
            var bottle=CapturedBelt.At(state,p,move.Slot);
            if(bottle==null || bottle.Id.Entry!=move.Key || !state.HoldsPotion(bottle))
            {reason="药水槽变化";return false;}
            if(!TryTarget(state.PotionTargets(bottle),move.Target,out var target)) {reason="药水目标变化";return false;}
            if(move.PotionChoice<0) action=new(p,null,target,bottle);
            else
            {
                var picks=state.PotionActions(bottle,target,int.MaxValue,state.Party);
                if(move.PotionChoice>=picks.Count){reason="药水选牌变化";return false;}
                action=picks[move.PotionChoice];
            }
        }
        using var menus=new ExactMenus(state,digits);
        try
        {
            if(!state.ForecastCausal(action,int.MaxValue,out reason,out var resolved)) return false;
            if(!menus.Consumed){reason="选牌步骤消失";return false;}
            shown=resolved;
            if(menus.Observed.Count!=0 || resolved.TurnStartChoices?.Choices.Count>0 || move.PotionChoice>=0)
                evidence=JsonSerializer.Serialize(new {answers=menus.Observed, resolved.Choices, resolved.TurnStartChoices});
            return true;
        }
        catch(Exception e) when(e is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            reason=move.Kind+"/"+(move.Key.Split('|')[0])+"："+e.GetBaseException().Message;
            return false;
        }
        finally
        {
            if(pending!=null)
                foreach(var next in menus.Alternatives()) pending.Enqueue(next);
        }
    }
    private static bool TryTarget(IReadOnlyList<Creature?> targets,uint? id,out Creature? target)
    {
        foreach(var candidate in targets)
            if(candidate?.CombatId==id) {target=candidate;return true;}
        target=null;
        return false;
    }
}
