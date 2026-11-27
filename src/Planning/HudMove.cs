using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;

namespace CoopSolver;

internal enum HudMoveKind { EndTurn, Card, Potion, Choice }
internal enum HudOutcome { Unknown, Win, Loss }

internal sealed record HudName(string Table,string Key)
{
    internal static HudName? From(LocString? value)=>value==null?null:new(value.LocTable,value.LocEntryKey);
    internal string Read()=>LocString.GetIfExists(Table,Key)?.GetFormattedText()??Key.Split('.')[0];
}

internal sealed record HudPosition(bool Left,int Column,int? Row)
{
    private static readonly Regex suffix=new(@"（(?<side>[左右])(?<column>\d+)(?:上(?<row>\d+))?）$",RegexOptions.Compiled);
    internal static HudPosition? From(string? label)=>Read(label??"",out _);
    internal static HudPosition? Read(string label,out string name)
    {
        var match=suffix.Match(label);
        name=match.Success?label[..match.Index]:label;
        return match.Success && int.TryParse(match.Groups["column"].Value,NumberStyles.None,CultureInfo.InvariantCulture,out int column)?
            new(match.Groups["side"].Value=="左",column,
            int.TryParse(match.Groups["row"].Value,CultureInfo.InvariantCulture,out int row)?row:null):null;
    }
    internal string Label()=>Ui.Text(Left?"target.left":"target.right",Column)+
        (Row is int row?Ui.Text("target.row",row):"");
}

internal sealed record HudTarget(HudName? Name,int Seat=0,HudPosition? Position=null,string? UnknownName=null)
{
    internal static HudTarget Of(Creature creature,int seat=0,string? position=null)
    {
        var name=HudName.From(creature.Player?.Character.Title??creature.Monster?.Title);
        return new(name,seat,HudPosition.From(position),name==null?creature.Name.ToString():null);
    }
}

internal sealed record HudCardModifier(bool Enchantment,string Id,int? Amount)
{
    internal string Text()
    {
        string table=Enchantment?"enchantments":"afflictions";
        return new HudName(table,Id+".title").Read()+(Amount is int n?" "+n:"");
    }
}

internal sealed record HudCardLabel(string Id,int Upgrade,HudCardModifier[]? Modifiers=null,int Replay=0,string Fee="",int Copy=0)
{
    internal string Text(bool legacy=false)=>
        new HudName("cards",Id+".title").Read()+(Upgrade<=0?"":Upgrade==1?"+":"+"+Upgrade)+Suffix(legacy);

    internal string Suffix(bool legacy)
    {
        bool han=legacy || Ui.UsesHanSpacing;
        var parts=Modifiers?.Select(m=>m.Text()).ToList()??new List<string>();
        if(Replay>0)parts.Add((legacy?"重放":Ui.Text("format.018"))+(Replay>1?" "+Replay:""));
        if(Fee.Length!=0)parts.Add(legacy?Fee+"费":Ui.Text("format.013",Fee));
        string result=parts.Count==0?"":(han?"（":" (")+string.Join(legacy?"、":Ui.Text("ui.044"),parts)+(han?"）":")");
        if(Copy>0)result+=(han?"（":" (")+(legacy?$"第{Copy}张":Ui.Text("format.011",Copy))+(han?"）":")");
        return legacy?result:Ui.Punctuation(result);
    }
}

internal sealed record HudPick(int Seat,HudCardLabel[] Cards)
{
    internal string Text(int actor=0,bool legacy=false)
    {
        string label=Cards.Length==0?(legacy?"跳过选择":Ui.Text("format.025")):
            (legacy?"选 ":Ui.Text("route.choose"))+string.Join(legacy?"、":Ui.Text("ui.044"),Cards.Select(card=>card.Text(legacy)));
        return (Seat!=actor?$"P{Seat} ":"")+label;
    }
}

internal sealed record HudMove(int Seat,HudMoveKind Kind,HudCardLabel? Card=null,HudName? Potion=null,int? PotionSlot=null,
    HudTarget? Target=null,HudPick[]? Picks=null,int? NextRound=null,HudPick[]? NextPicks=null)
{
    internal bool EndTurn=>Kind==HudMoveKind.EndTurn;
    internal bool IsPotion=>Kind==HudMoveKind.Potion;

    internal string Text(bool legacy=false)
    {
        if(EndTurn)return legacy?"结束回合":Ui.Text("route.end_turn");
        if(Card!=null)return Card.Text(legacy);
        if(!IsPotion)return legacy?"选择":Ui.Text("route.choice");
        string title=Potion?.Read()??Ui.Missing;
        string slot=PotionSlot is int n?
            legacy?$"（药水槽 {n}）":Ui.UsesHanSpacing?"（"+Ui.Text("format.006",n)+"）":" ("+Ui.Text("format.006",n)+")":"";
        return (legacy?"用药 ":"")+title+slot;
    }

    internal IEnumerable<HudPick> InlinePicks()=>
        (Picks??Array.Empty<HudPick>()).Concat(NextRound==null?NextPicks??Array.Empty<HudPick>():Array.Empty<HudPick>());

    internal string? Choices()
    {
        string result=string.Join(Ui.Text("ui.044"),InlinePicks().Select(pick=>pick.Text()));
        return result.Length==0?null:result;
    }

}
