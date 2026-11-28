using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed record HudTerm(string Source,string Table,string Key);
internal static partial class Ui
{
    internal static string Missing=>Text("ui.119");
    internal static string Language=>LocManager.Instance?.Language??"zhs";
    internal static bool UsesHanSpacing=>Language is "zhs" or "zht";
    internal static CultureInfo Culture=>UiLocale.Includes(Language)
        ?LocManager.Instance?.CultureInfo??CultureInfo.InvariantCulture:CultureInfo.GetCultureInfo("en");
    internal static string Text(string key,params object?[] values)=>UiLocale.Text(Language,Culture,key,values);
    internal static string Turn(int n)=>Text("ui.120",n);
    internal static string Outcome(string? raw)=>raw switch
    {
        "胜"=>Text("route.outcome_w"),
        "败"=>Text("route.outcome_l"),
        _=>raw??""
    };

    // Persist the model's localization key with its displayed name. Receivers resolve
    // names in their own game, including installed mods, without changing game locale.
    internal static HudTerm[] Terms(InteractiveSearch.Line line)
    {
        var names=new HashSet<HudTerm>();
        var keys=new HashSet<(string Table,string Key)>();
        void Add(LocString? loc)
        {
            if(loc!=null && keys.Add((loc.LocTable,loc.LocEntryKey)) && loc.GetFormattedText() is {Length:>0} name)names.Add(new(name,loc.LocTable,loc.LocEntryKey));
        }
        void Card(string id,string key)
        {
            Add(LocString.GetIfExists("cards",id+".title"));
            var fields=key.Split('|');int i=Array.FindIndex(fields,s=>s.StartsWith("vars=",StringComparison.Ordinal));
            if(i>=0 && i+2<fields.Length)
            {
                var enchant=fields[i+1].Split(':')[0];var affliction=fields[i+2].Split(':')[0];
                if(enchant.Length>0 && enchant!="-")Add(LocString.GetIfExists("enchantments",enchant+".title"));
                if(affliction.Length>0)Add(LocString.GetIfExists("afflictions",affliction+".title"));
            }
        }
        var cards=new HashSet<(string Id,string Key)>();
        foreach(var a in line.Steps)
        {
            Add(a.Player.Character.Title);Add(a.Potion?.Title);Add(a.Target?.Monster?.Title);Add(a.Target?.Player?.Character.Title);
            if(a.Card!=null)cards.Add((a.Card.Id.Entry,a.CardStateKey));
            foreach(var c in (a.Choices??Array.Empty<KernelChoice>()).Concat(a.TurnStartChoices?.Choices??Array.Empty<KernelChoice>()))
                foreach(var selected in c.Cards)cards.Add((selected.Id,selected.StateKey));
        }
        foreach(var card in cards)Card(card.Id,card.Key);
        foreach(var kill in line.Kills??Array.Empty<RouteKill>())Add(kill.Enemy.Monster?.Title);
        return names.ToArray();
    }

    private sealed class LocalNames(HudTerm[] terms)
    {
        private string? language;
        private Regex? pattern;
        private readonly Dictionary<string,string> titles=new();
        internal string Apply(string value)
        {
            if(language!=Language)
            {
                language=Language;titles.Clear();
                foreach(var term in terms)
                    if(LocString.GetIfExists(term.Table,term.Key)?.GetFormattedText() is {} title && title!=term.Source)
                        titles.TryAdd(term.Source,title);
                pattern=titles.Count==0?null:new Regex(@"(?<![a-zA-Z])(?:"+string.Join("|",titles.Keys.OrderByDescending(k=>k.Length).Select(Regex.Escape))+@")(?![a-zA-Z])");
            }
            return pattern?.Replace(value,m=>titles[m.Value])??value;
        }
    }
    private static readonly ConditionalWeakTable<HudTerm[],LocalNames> names=new();
    internal static string Punctuation(string text)=>ChineseSpacing.Format(text);
    internal static bool SameTargetNames(IReadOnlyDictionary<uint,string>? a,IReadOnlyDictionary<uint,string>? b)=>
        ReferenceEquals(a,b) || a!=null && b!=null && a.Count==b.Count &&
        a.All(item=>b.TryGetValue(item.Key,out var name) && name==item.Value);
}
