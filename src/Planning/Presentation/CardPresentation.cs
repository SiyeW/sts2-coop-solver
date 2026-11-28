using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Models;

namespace CoopSolver;

internal static class CardPresentation
{
    private sealed record Fee(string Text);
    private static readonly ConditionalWeakTable<KernelTeamSearch.Action,Fee> fees=new();
    internal static string FeeFor(KernelTeamSearch.Action action)=>fees.TryGetValue(action,out var fee)?fee.Text:"";
    internal static void MarkFee(KernelTeamSearch.Action action,string text)=>fees.AddOrUpdate(action,new(text));

    internal static string HandFee(KernelSession state,CardModel source)
    {
        var sim=state.Simulator;var card=sim.State.FindCard(source);
        if(card==null)return "";
        var hand=sim.State.GetPlayerCombatState(source.Owner).Hand.Cards;
        int? cost=null;
        int Price(PredictedCard c)=>c.Preview.EnergyCost.CostsX?int.MinValue:c.GetEnergyCostValueWithModifiers(sim);
        foreach(var other in hand)
        {
            if(other==card || other.Preview.Id!=card.Preview.Id || other.Preview.CurrentUpgradeLevel!=card.Preview.CurrentUpgradeLevel)continue;
            cost??=Price(card);
            if(Price(other)!=cost)return cost==int.MinValue?"X":cost.Value.ToString();
        }
        return "";
    }

    private static string MenuCost(string key)
    {
        int at=key.IndexOf("|energy=",StringComparison.Ordinal);
        if(at<0)return "";
        int end=key.IndexOf('|',at+8);
        var field=key[(at+8)..(end<0?key.Length:end)];
        if(field.StartsWith("True:",StringComparison.Ordinal))return "X";
        return field.StartsWith("False:",StringComparison.Ordinal) && int.TryParse(field[6..],out int n)?n.ToString():"";
    }
    internal static string MenuFee(KernelChoiceCard card,IReadOnlyList<KernelChoiceCard>? options)
    {
        if(options==null)return "";
        string price=MenuCost(card.StateKey);
        return price.Length!=0 && options.Any(o=>o.Id==card.Id && o.Upgrade==card.Upgrade &&
            MenuCost(o.StateKey) is {Length:>0} other && other!=price)?price:"";
    }
    private static int RecordedUpgrade(string key,CardModel card)
    {
        int end=key.IndexOf('|');
        int plus=end>0?key.LastIndexOf('+',end-1):-1;
        return plus>=0 && int.TryParse(key.AsSpan(plus+1,end-plus-1),out int level) && level>=0?level:card.CurrentUpgradeLevel;
    }
    // Read the display fields from the identity captured immediately before the play.
    // Matching always uses the entire unchanged key, including fields with no UI label.
    internal static HudCardLabel Capture(CardModel card,string key,string fee="",int? upgrade=null)
    {
        int level=upgrade??RecordedUpgrade(key,card);
        if(string.IsNullOrEmpty(key))return new(card.Id.Entry,level);
        var fields=key.Split('|');
        var modifiers=new List<HudCardModifier>();
        int vars=Array.FindIndex(fields,f=>f.StartsWith("vars=",StringComparison.Ordinal));
        if(vars>=0 && vars+2<fields.Length)
        {
            AddModifier(fields[vars+1],true);
            AddModifier(fields[vars+2],false);
        }
        string? replayField=fields.FirstOrDefault(field=>field.StartsWith("replay=",StringComparison.Ordinal));
        int replay=replayField!=null && int.TryParse(replayField.AsSpan(7),out int count)?Math.Max(0,count):0;
        return new(card.Id.Entry,level,modifiers.Count==0?null:modifiers.ToArray(),replay,fee);

        void AddModifier(string field,bool enchantment)
        {
            var tokens=field.Split(':');
            string id=tokens[0];
            if(id.Length==0 || id=="-")return;
            int? amount=(enchantment || id!="BOUND") && tokens.Length>1 && int.TryParse(tokens[1],out int n) && n>1?n:null;
            modifiers.Add(new(enchantment,id,amount));
        }
    }

    internal static string Describe(string key,CardModel card,string fee="")=>Capture(card,key,fee).Suffix(legacy:true);

    // Bound.Amount is copied from the applying power. The card rule is the same at every amount.
    internal static bool SameDisplayedVariant(string a,string b)=>
        a==b || (a.Contains("|BOUND:") && b.Contains("|BOUND:") &&
            System.Text.RegularExpressions.Regex.Replace(a,@"\|BOUND:-?\d+(?=\||$)","|BOUND")==
            System.Text.RegularExpressions.Regex.Replace(b,@"\|BOUND:-?\d+(?=\||$)","|BOUND"));

    internal static string PlayTitle(CardModel card,string key,string fee="")
    {
        string title=card.Title;
        int upgrade=RecordedUpgrade(key,card);
        if(upgrade!=card.CurrentUpgradeLevel && ModelDb.GetByIdOrNull<CardModel>(card.Id) is {} canonical)
            title=canonical.Title+(upgrade==0?"":upgrade==1?"+":"+"+upgrade);
        return title+Describe(key,card,fee);
    }

    // Card choices count the occurrence among same-name, same-upgrade options. The
    // recorded state must also match. A potion can supply a token from Options itself.
    internal static int MenuIndex(KernelChoice choice,KernelChoiceCard selected,IReadOnlyList<KernelChoiceCard> options)
    {
        if(selected.Occurrence<0)return -1;
        int occurrence=0,broadIndex=-1,keyIndex=-1;
        for(int i=0;i<options.Count;i++)
        {
            var option=options[i];
            if(option.Id!=selected.Id || option.Upgrade!=selected.Upgrade)continue;
            if(occurrence++==selected.Occurrence && option.StateKey==selected.StateKey)broadIndex=i;
            if(option==selected)keyIndex=i;
            if(choice.Options!=null && i<choice.Options.Count && ReferenceEquals(choice.Options[i],selected) &&
                option==selected)return i;
        }
        // The choice cursor records its timing and counts copies by name/upgrade.
        // Menu-token producers select from Options, whose ordinals count equal keys.
        if(choice.Timing!=null)return broadIndex;
        if(choice.Options!=null && choice.RequestedMinimum.HasValue)return keyIndex;
        if(keyIndex==broadIndex || broadIndex<0)return keyIndex;
        if(keyIndex<0)return broadIndex;
        return -1;
    }
}
