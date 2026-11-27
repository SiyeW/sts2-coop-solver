using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace CoopSolver;

// Drive only the visible combat prompt for the pending action. A menu that
// cannot be matched stays open for the player. No global selector is installed.
internal sealed class NativeAutoMenu(Node screen,object identity)
{
    internal readonly object Identity=identity;
    private CardModel[] picks=Array.Empty<CardModel>();
    private int sent;
    private bool confirmed;
    internal static NativeAutoMenu? Current()
    {
        if(NOverlayStack.Instance?.Peek() is Node overlay)
            return overlay is NChooseACardSelectionScreen or NCombatPileCardSelectScreen or NSimpleCardSelectScreen
                ?new(overlay,overlay):null;
        if(NPlayerHand.Instance is {IsInCardSelection:true} hand &&
            Traverse.Create(hand).Field("_selectionCompletionSource").GetValue() is {} prompt)return new(hand,prompt);
        return null;
    }

    internal static int[]? VisibleOrder(KernelChoice choice,ChoiceLayout layout)
    {
        if(choice.Owner!=MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.NetId ||
            NOverlayStack.Instance?.Peek() is not NCombatPileCardSelectScreen screen ||
            Traverse.Create(screen).Field("_pile").GetValue<CardPile>() is not {} pile ||
            AccessTools.Field(typeof(NCardGridSelectionScreen),"_grid").GetValue(screen) is not MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid grid ||
            Traverse.Create(grid).Field("_cards").GetValue<List<CardModel>>() is not {} displayed ||
            displayed.Count!=layout.Source.Length || layout.Source.Any(i=>i<0 || i>=pile.Cards.Count))return null;
        var physical=layout.Source.Select(i=>pile.Cards[i]).ToArray();
        if(!KernelChoice.MenuTokens(physical).SequenceEqual(choice.Options!))return null;
        var order=displayed.Select(c=>Array.IndexOf(physical,c)).ToArray();
        return order.Contains(-1)?null:order;
    }

    internal void Bind(KernelChoice choice,Player actor)
    {
        var fields=Traverse.Create(screen);
        IReadOnlyList<CardModel> options;
        int min,max;
        if(screen is NChooseACardSelectionScreen)
        {
            options=fields.Field("_cards").GetValue<IReadOnlyList<CardModel>>();
            min=fields.Field("_canSkip").GetValue<bool>()?0:1;max=1;
        }
        else
        {
            var prefs=fields.Field("_prefs").GetValue<CardSelectorPrefs>();
            min=prefs.MinSelect;max=prefs.MaxSelect;
            if(screen is NPlayerHand)
            {
                var filter=fields.Field("_currentSelectionFilter").GetValue<Func<CardModel,bool>>();
                options=actor.PlayerCombatState!.Hand.Cards.Where(c=>filter==null || filter(c)).ToArray();
            }
            else if(screen is NCombatPileCardSelectScreen && fields.Field("_pile").GetValue<CardPile>() is {} pile)
            {
                var filter=fields.Field("_filter").GetValue<Func<CardModel,bool>>();
                options=pile.Cards.Where(c=>filter==null || filter(c)).ToArray();
            }
            else options=(IReadOnlyList<CardModel>?)AccessTools.Field(typeof(NCardGridSelectionScreen),"_cards").GetValue(screen)??throw Changed();
        }
        Exception Mismatch()
        {
            GD.PrintErr("[CoopSolver] "+choice.MenuDifference(actor.NetId,options,min,max,screen.GetType().Name));
            return Changed();
        }
        if(choice.Options==null || choice.Owner!=actor.NetId || choice.Minimum!=Math.Min(min,options.Count) ||
            choice.Maximum!=Math.Min(max,options.Count))throw Mismatch();
        var layout=ChoiceLayout.For(choice);
        if(layout==null && choice.Pile==PileType.Draw && ChoiceLayout.Ambiguous(choice) &&
            choice.Cards.Any(c=>choice.Options.Count(o=>o.StateKey==c.StateKey)>1))throw Mismatch();
        if(layout!=null && screen is NCombatPileCardSelectScreen && fields.Field("_pile").GetValue<CardPile>() is {} source)
        {
            var offered=options.ToHashSet();
            if(layout.Source.Any(i=>i<0 || i>=source.Cards.Count || !offered.Contains(source.Cards[i])))throw Mismatch();
            options=layout.Source.Select(i=>source.Cards[i]).ToArray();
        }
        var tokens=KernelChoice.MenuTokens(options);
        if(!tokens.SequenceEqual(choice.Options))
        {
            // Grids can sort the offered cards for display. Bind in recorded order
            // only after matching the entire multiset, including state and copies.
            var byToken=tokens.Select((t,i)=>(t,i)).ToDictionary(x=>x.t,x=>options[x.i]);
            if(choice.Options.Count!=options.Count || choice.Options.Distinct().Count()!=options.Count ||
                choice.Options.Any(t=>!byToken.ContainsKey(t)))throw Mismatch();
            options=choice.Options.Select(t=>byToken[t]).ToArray();tokens=choice.Options;
        }
        picks=choice.Cards.Select(c=>{
            int i=CardPresentation.MenuIndex(choice,c,tokens);
            return i>=0?options[i]:throw Mismatch();
        }).ToArray();
        if(picks.Length<choice.Minimum || picks.Length>choice.Maximum || picks.Distinct().Count()!=picks.Length)throw Changed();
    }
    private static Exception Changed()=>new InvalidOperationException(Ui.Text("ui.087"));
    internal void Advance()
    {
        if(confirmed)return;
        var fields=Traverse.Create(screen);
        if(screen is NChooseACardSelectionScreen)
        {
            if(picks.Length==0)
            {
                var skip=fields.Field("_skipButton").GetValue<NChoiceSelectionSkipButton>();
                if(!skip.IsVisibleInTree() || !skip.IsEnabled)return;
                fields.Method("OnSkipButtonReleased",skip).GetValue();
            }
            else
            {
                var holder=screen.FindChildren("*","",true,false).OfType<NCardHolder>()
                    .FirstOrDefault(h=>h.IsVisibleInTree() && ReferenceEquals(h.CardModel,picks[0]));
                if(holder==null)return;
                fields.Method("SelectHolder",holder).GetValue();
            }
            confirmed=true;return;
        }
        var selected=fields.Field("_selectedCards").GetValue<IEnumerable<CardModel>>().ToArray();
        if(selected.Any(c=>!picks.Take(sent).Contains(c)))throw Changed();
        if(selected.Length<sent)return;
        if(sent<picks.Length)
        {
            var card=picks[sent];
            if(screen is NPlayerHand hand)
            {
                var holder=hand.ActiveHolders.FirstOrDefault(h=>ReferenceEquals(h.CardModel,card));
                if(holder==null)return;
                fields.Method("OnHolderPressed",holder).GetValue();
            }
            else fields.Method("OnCardClicked",card).GetValue();
            sent++;return;
        }
        var confirm=fields.Field(screen is NPlayerHand?"_selectModeConfirmButton":"_confirmButton").GetValue<NConfirmButton>();
        if(confirm==null || !confirm.IsVisibleInTree() || !confirm.IsEnabled)return;
        if(screen is NPlayerHand)fields.Method("OnSelectModeConfirmButtonPressed",confirm).GetValue();
        else fields.Method("CompleteSelection").GetValue();
        confirmed=true;
    }
}
