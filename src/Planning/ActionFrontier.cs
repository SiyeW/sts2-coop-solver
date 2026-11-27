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
    private long digitBytes;
    internal void Enqueue(int[] digits)
    {
        pending.Enqueue(digits);digitBytes+=SearchTreeMemory.Survey.Buffer(digits.Length,4);
    }
    internal long RetainedBytes=>80+SearchTreeMemory.Survey.Buffer(pending.EnsureCapacity(0),8)+digitBytes;
    internal void Measure(SearchTreeMemory.Survey size)
    {
        size.Owned(RetainedBytes);
        size.Move(move);
    }
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
        for(int i=0;i<count;i++)family.Enqueue(RoundAgenda.ReadDigits(reader));
        return family;
    }
    internal bool Expand(KernelSession state,out TreeStep? step,out KernelTeamSearch.Action? shown,out string reason,ActionFrontier? proof=null)
    {
        var digits=pending.Dequeue();digitBytes-=SearchTreeMemory.Survey.Buffer(digits.Length,4);
        bool ok=ActionFrontier.Apply(state,move,digits,out shown,out var evidence,this,out reason,proof);
        step=ok?new TreeStep(move,digits,evidence):null;
        return ok;
    }
}

// One queue entry per physical action/target. Taking one branch rotates that
// action to the back so a large multi-select cannot monopolize this position.
internal sealed class ActionFrontier
{
    private readonly Queue<MoveFamily> queue = new();
    private readonly bool measureCoverage;
    private AnytimeSearch? owner;
    private AnytimeSearch.Node? node;
    private int readIndex;
    private bool bookmark;
    internal int ReadIndex=>readIndex;
    internal ActionFrontier(AnytimeSearch owner,AnytimeSearch.Node node,int index)
    {this.owner=owner;this.node=node;readIndex=index;}
    internal int FamilyCount=>node?.Families??queue.Count;
    internal double ChoiceShare {get;private set;}
    internal bool Finished => node!=null?readIndex>=node.Legal!.Count && node.Frontier!.Finished:queue.Count==0;
    internal IEnumerable<MoveFamily> Families => queue;

    internal long RetainedBytes=>80+SearchTreeMemory.Survey.Buffer(queue.EnsureCapacity(0),8)+
        System.Linq.Enumerable.Sum(queue,family=>family.RetainedBytes);
    internal void Measure(SearchTreeMemory.Survey size)
    {
        size.Owned(80+SearchTreeMemory.Survey.Buffer(queue.EnsureCapacity(0),8));
        foreach(var family in queue)family.Measure(size);
    }
    private ActionFrontier() {}
    internal ActionFrontier(KernelSession state,Move forced)
    {
        if(forced.Kind==MoveKind.Potion && forced.PotionChoice<0 &&
            CapturedBelt.At(state,state.Party[forced.Seat],forced.Slot) is {} bottle && KernelSession.RequiresPotionChoiceContract(bottle))
        {
            var targets=state.PotionTargets(bottle);
            if(!TryTarget(targets,forced.Target,out var target))return;
            int count=state.PotionActions(bottle,target,int.MaxValue,state.Party).Count;
            for(int i=0;i<count;i++)queue.Enqueue(new MoveFamily(forced with {PotionChoice=i}));
        }
        else queue.Enqueue(new MoveFamily(forced));
    }
    internal void Save(BinaryWriter writer)
    {
        if(node!=null || bookmark){writer.Write(-1);writer.Write(readIndex);return;}
        writer.Write(queue.Count);
        foreach(var family in queue)family.Save(writer);
    }
    internal static ActionFrontier Load(BinaryReader reader)
    {
        var frontier=new ActionFrontier();
        int count=reader.ReadInt32();
        if(count==-1){frontier.bookmark=true;frontier.readIndex=reader.ReadInt32();return frontier;}
        for(int i=0;i<count;i++)frontier.queue.Enqueue(MoveFamily.Load(reader));
        return frontier;
    }


    internal ActionFrontier(KernelSession state, Action<string> unsupported,bool measureCoverage=false)
    {
        this.measureCoverage=measureCoverage;
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
                if(!KernelSession.CanModelPotion(bottle) && !ExactPotionChoices.Supports(bottle))
                { unsupported(bottle.Id.Entry); continue; }
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
        if(node!=null)
        {
            AnytimeSearch.Node? child;
            if(readIndex<node.Legal!.Count)
            {
                child=node.Legal[readIndex++];
                step=child.Step!.Exact!;
                if(!Replay(state,step,out shown,out reason)){owner!.RejectState(child);return false;}
                owner!.ReadState(child,state);
            }
            else
            {
                if(!owner!.ExpandMenu(node,state,out child,out step,out shown,out reason))return false;
                readIndex++;
            }
            ChoiceShare=child!.Share*node.Families;
            return true;
        }
        var family=queue.Dequeue();
        bool ok=family.Expand(state,out step,out shown,out reason,measureCoverage?this:null);
        if(!family.Finished) queue.Enqueue(family);
        return ok;
    }
    internal static bool Replay(KernelSession state,TreeStep step,out KernelTeamSearch.Action? shown,out string reason)
    {
        if(step.Move is not {} move || (uint)move.Seat>=(uint)state.Party.Count || step.Digits==null ||
            move.Kind==MoveKind.Card && move.Slot<0 || move.Kind is not (MoveKind.Card or MoveKind.Potion or MoveKind.End))
        {shown=null;reason="动作记录无效";return false;}
        if(!Apply(state,step.Move,step.Digits,out shown,out var evidence,null,out reason)) return false;
        if(evidence==step.MenuEvidence) return true;
        reason="选牌菜单或选择已变化";
        return false;
    }
    // Preserve the result of an already playable recording. During import, a
    // native key can replace it only when it reproduces both the input and state.
    internal static bool ReplayRecorded(KernelSession parent,ref KernelSession child,KernelTeamSearch.Action? input,
        ref TreeStep? recipe,out KernelTeamSearch.Action? resolved,out string reason,bool recoverKey=false)
    {
        resolved=null;
        if(recipe==null && input==null){reason="保存的动作与当前盘面不符";return false;}
        bool ok;
        ReplayCoverage.Begin(child);
        try
        {
            if(recipe!=null)return Replay(child,recipe,out resolved,out reason);
            ok=child.ReplayPlanned(input!,int.MaxValue,out reason,out resolved);
        }
        finally{ReplayCoverage.End(resolved);}
        if(ok && !SameLayouts(input!,resolved!)){reason="选牌在原牌堆中的位置已变化";return false;}
        if((ok && !recoverKey) || (!ok && !Menus(input!).Any()))return ok;
        if(PortableMove(parent,input!) is not {} move || MenuDigits(input!) is not {} digits)return ok;

        var native=parent.ForkForReplay();
        KernelTeamSearch.Action? played=null;
        ReplayCoverage.Begin(native);
        try
        {
            if(!Apply(native,move,digits,out played,out var evidence,null,out _) ||
                !AnytimeSearch.Edge.SameAction(input,played) || !SameLayouts(input!,played!))return ok;
            if(ok)
            {
                var seats=parent.Party.Select(p=>p.NetId).ToArray();
                var expected=child.PlanningState(seats);var actual=native.PlanningState(seats);
                if(!expected.Matches(actual) || expected.SearchMetadata!=actual.SearchMetadata)return true;
            }
            child=native;recipe=new(move,digits,evidence);resolved=played;reason="";return true;
        }
        finally{ReplayCoverage.End(played);}
    }

    private static IEnumerable<KernelChoice> Menus(KernelTeamSearch.Action action)=>
        (action.Choices??Array.Empty<KernelChoice>()).Concat(action.TurnStartChoices?.Choices??Array.Empty<KernelChoice>());

    private static bool SameLayouts(KernelTeamSearch.Action input,KernelTeamSearch.Action resolved)
    {
        using var observed=Menus(resolved).GetEnumerator();
        foreach(var menu in Menus(input))
        {
            bool present=observed.MoveNext();
            if(ChoiceLayout.For(menu) is not {} saved || menu.Options is not {} options)continue;
            foreach(var card in menu.Cards)
            {
                int at=CardPresentation.MenuIndex(menu,card,options);
                if(at<0)return false;
                bool positional=Enumerable.Range(0,options.Count).Any(i=>options[i].Id==card.Id &&
                    options[i].Upgrade==card.Upgrade && options[i].StateKey==card.StateKey && saved.Runs[i]!=saved.Runs[at]);
                if(!positional)continue;
                if(!present || observed.Current.Options is not {} actualOptions ||
                    ChoiceLayout.For(observed.Current) is not {} actualLayout)return false;
                int actual=CardPresentation.MenuIndex(observed.Current,card,actualOptions);
                if(actual<0 || actualLayout.Source[actual]!=saved.Source[at])return false;
            }
        }
        return true;
    }

    private static Move? PortableMove(KernelSession state,KernelTeamSearch.Action action)
    {
        int seat=0;
        while(seat<state.Party.Count && !ReferenceEquals(state.Party[seat],action.Player))seat++;
        if(seat==state.Party.Count)return null;
        if(action.Card is {} card)
        {
            var hand=state.Hand(action.Player);int slot=-1;
            for(int i=0;i<hand.Count;i++)
                if(ReferenceEquals(hand[i],card)){if(slot>=0)return null;slot=i;}
            return slot<0 || state.CardStateKey(card)!=action.CardStateKey?null:new(seat,MoveKind.Card,slot,action.CardStateKey,action.Target?.CombatId);
        }
        if(action.Potion is {} potion)
        {
            int slot=CapturedBelt.Slot(state,potion);
            if(slot<0 || !state.HoldsPotion(potion) || KernelSession.RequiresPotionChoiceContract(potion))return null;
            return new(seat,MoveKind.Potion,slot,potion.Id.Entry,action.Target?.CombatId);
        }
        return action.EndTurn?new(seat,MoveKind.End,-1,"",null):null;
    }

    private static int[]? MenuDigits(KernelTeamSearch.Action action)
    {
        var digits=new List<int>();
        foreach(var menu in Menus(action))
        {
            if(menu.Options==null || menu.Cards.Count<menu.Minimum || menu.Cards.Count>menu.Maximum)return null;
            digits.Add(menu.Maximum-menu.Cards.Count);
            var remaining=Enumerable.Range(0,menu.Options.Count).ToList();
            foreach(var selected in menu.Cards)
            {
                int option=CardPresentation.MenuIndex(menu,selected,menu.Options);
                int at=remaining.IndexOf(option);
                if(at<0)return null;
                digits.Add(at);remaining.RemoveAt(at);
            }
        }
        int last=digits.FindLastIndex(i=>i!=0);
        return last<0?Array.Empty<int>():digits.GetRange(0,last+1).ToArray();
    }

    internal static TreeStep RebindEquivalent(KernelSession state,TreeStep step,int occurrence)
    {
        var move=step.Move;
        if(move==null || move.Kind!=MoveKind.Card || (uint)move.Seat>=(uint)state.Party.Count)return step;
        var hand=state.Hand(state.Party[move.Seat]);
        if(move.Slot>=0 && move.Slot<hand.Count && state.CardStateKey(hand[move.Slot])==move.Key)return step;
        int first=-1;
        for(int i=0;i<hand.Count;i++)
        {
            if(state.CardStateKey(hand[i])!=move.Key)continue;
            if(first<0)first=i;
            if(occurrence--==0)return step with {Move=move with {Slot=i}};
        }
        return first<0?step:step with {Move=move with {Slot=first}};
    }
    internal static bool Apply(KernelSession state,Move move,int[] digits,out KernelTeamSearch.Action? shown,
        out string evidence,MoveFamily? pending,out string reason,ActionFrontier? proof=null)
    {
        shown=null; evidence=""; reason="";
        if((uint)move.Seat>=(uint)state.Party.Count){reason="行动者已变化";return false;}
        var p=state.Party[move.Seat];
        if(!state.CanAct(p)) { reason="玩家行动状态变化"; return false; }
        KernelTeamSearch.Action action;
        if(move.Kind==MoveKind.End) action=new(p,null,null,EndTurn:true);
        else if(move.Kind==MoveKind.Card)
        {
            var hand=state.Hand(p);
            if((uint)move.Slot>=(uint)hand.Count || state.CardStateKey(hand[move.Slot])!=move.Key)
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
            if(proof!=null)proof.ChoiceShare=menus.ChoiceShare;
            if(resolved.Potion is {} potion && ExactPotionChoices.Supports(potion))
                resolved=resolved with {Choices=menus.Observed.ToArray(),ChoiceOrdinals=digits};
            shown=resolved;
            if(menus.Observed.Count!=0 || resolved.TurnStartChoices?.Choices.Count>0 || move.PotionChoice>=0)
                evidence=JsonSerializer.Serialize(new {answers=menus.Observed, resolved.Choices, resolved.TurnStartChoices});
            return true;
        }
        catch(Exception e) when(e is InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
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
