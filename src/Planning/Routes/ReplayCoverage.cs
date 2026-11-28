using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopSolver;

// Capture only the frozen simulator currently replaying a proposed route.
// Warnings from speculative branches do not describe the selected route.
internal static class ReplayCoverage
{
    // Older saved routes and older hosts can still carry observer-only notes.
    internal static bool HasEffectDetail(string note)=>!string.IsNullOrWhiteSpace(note) &&
        !note.EndsWith("（按攻击意图结算）",StringComparison.Ordinal) &&
        !note.EndsWith("（按攻击意图估算）",StringComparison.Ordinal) &&
        note!="内核标记了估算，未提供具体效果名";

    // CardPlay records a pending picker when its options are generated. A verified replay
    // can subsequently answer it. Keep warnings for other methods, effects and incomplete menus.
    internal static string[] RouteDetails(string[] notes,IEnumerable<KernelTeamSearch.Action> plays)=>
        RouteDetails(notes,plays.Select(a=>(a.Card?.Id.Entry,a.Player.NetId,a.Choices)));

    private static string[] RouteDetails(string[] notes,
        IEnumerable<(string? Card,ulong Seat,IReadOnlyList<KernelChoice>? Choices)> plays)
    {
        if(notes.Length==0)return notes;
        const string suffix=".CardPlay:UnresolvedPlayerChoice";
        bool Answered(string note)
        {
            if(!note.EndsWith(suffix,StringComparison.Ordinal))return false;
            string source=note[..^suffix.Length];
            bool found=false;
            foreach(var play in plays.Where(p=>p.Card==source))
            {
                var menus=play.Choices?.Where(c=>c!=null && c.Owner==play.Seat && (c.MenuSource==source || c.Source==source)).ToArray();
                if(menus is not {Length:>0})return false;
                foreach(var menu in menus)
                {
                    if(menu.Options is not {} options || menu.Cards==null || menu.Minimum<0 || menu.Maximum<menu.Minimum ||
                        menu.Cards.Count<menu.Minimum || menu.Cards.Count>menu.Maximum)return false;
                    if(options.Count==0 && (menu.RequestedMinimum is not >=0 || menu.RequestedMaximum is not >=0 ||
                        menu.RequestedMinimum>menu.RequestedMaximum || menu.Maximum!=0))return false;
                    var selected=new HashSet<int>();
                    foreach(var card in menu.Cards)
                    {
                        int i=CardPresentation.MenuIndex(menu,card,options);
                        if(i<0 || !selected.Add(i))return false;
                    }
                }
                found=true;
            }
            return found;
        }
        return notes.SelectMany(n=>n.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
            .Where(n=>HasEffectDetail(n) && !Answered(n)).Distinct().ToArray();
    }

    internal static OpeningResult? Restore(OpeningResult? saved)
    {
        if(saved?.Ending?.Verified!=true || saved.Seed is not {Terminal:true,Steps:not null} seed)return saved;
        var plays=seed.Steps.Where(s=>s!=null).Select(s=>((string?)(s.Slot<0?s.Id:null),s.Seat,(IReadOnlyList<KernelChoice>?)s.Choices));
        var risks=RouteDetails(saved.Risks??Array.Empty<string>(),plays);
        var hud=saved.Hud;
        if(hud?.Risks is {Length:>0} text)
            hud=hud with {Risks=string.Join("\n",RouteDetails(text.Split('\n'),plays))};
        if(hud?.Rounds is {} rounds && rounds.Any(r=>r.Potions==null))
        {
            int round=hud.Start;
            var counts=rounds.ToDictionary(r=>r.Round,r=>hud.Party.Select(s=>DeathSaves.Events(new[]{r},s.Id).Count(e=>e.Potion)).ToArray());
            foreach(var step in seed.Steps)
            {
                int seat=Array.FindIndex(hud.Party,s=>s.Id==step.Seat);
                if(step.Slot>=0 && seat>=0 && counts.TryGetValue(round,out var uses))uses[seat]++;
                if(step.Turn!=null)round=step.Turn.ToRound;
            }
            hud=hud with {Rounds=rounds.Select(r=>r.Potions!=null?r:r with {Potions=counts[r.Round]}).ToArray()};
        }
        return saved with {Risks=risks,Hud=hud};
    }

    [ThreadStatic] private static CombatPredictionSimulator? tracked;
    internal static CombatPredictionSimulator? Tracked=>tracked;
    internal static bool IsTracking(CombatPredictionSimulator simulator)=>ReferenceEquals(simulator,tracked);
    [ThreadStatic] private static CardModel? played;
    [ThreadStatic] private static string? fee;
    internal static void Begin(KernelSession state){tracked=state.Simulator;played=null;fee=null;}
    internal static void ObservePlay(KernelSession state,CardModel source)
    {
        if(played!=null || !IsTracking(state.Simulator))return;
        played=source;fee=CardPresentation.HandFee(state,source);
    }
    internal static void End(KernelTeamSearch.Action? resolved=null)
    {
        if(resolved?.Card!=null && ReferenceEquals(resolved.Card,played) && !string.IsNullOrEmpty(fee))
            CardPresentation.MarkFee(resolved,fee);
        tracked=null;played=null;fee=null;
    }

    internal static void Collect(KernelSession state,HashSet<string> output)
    {
        // A plain attack can set the kernel flag without reporting any missing effect.
        // Only the concrete gaps recorded by the action belong to this route.
        if(state.LastActionHadEnvironmentalRisk && !string.IsNullOrWhiteSpace(state.LastActionRiskDetail))
            output.Add(state.LastActionRiskDetail);
    }
}
