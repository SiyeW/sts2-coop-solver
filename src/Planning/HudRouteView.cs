using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace CoopSolver;

// The persisted and shared route uses the same text as the second-screen view.
// Keep unknown lines verbatim, including menus attached to a round boundary.
internal sealed class HudRouteView
{
    private sealed record Page(int Number,List<(string Text,int Step)> Lines);
    private static readonly Regex RoundHeading=new(@"^第 (\d+) 回合(.*)$");
    private static readonly Regex PlayerMove=new(@"^P(\d+) [^：]+：(.*)$");
    private HudSeat[] seats=Array.Empty<HudSeat>();
    private HudKill[] kills=Array.Empty<HudKill>();
    private HudPlan? lastPlan;
    private readonly List<Page> pages=new();
    private readonly VBoxContainer rows=new(){Name="HudRows",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
    internal readonly ScrollContainer Scroll=new(){Name="HudSteps",HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,SizeFlagsVertical=Control.SizeFlags.ExpandFill};
    internal readonly OptionButton Round=new(){Name="HudRound",Flat=true,FocusMode=Control.FocusModeEnum.None};
    private int selected=-1;
    private int localSeat;
    internal bool OwnOnly {get;private set;}
    private readonly HashSet<int> expanded=new();
    internal readonly HFlowContainer Roster=new(){Name="HudRoster"};
    private bool manual;
    internal int Count=>rows.GetChildCount();
    internal float ContentHeight=>rows.GetCombinedMinimumSize().Y;

    internal HudRouteView()
    {
        rows.AddThemeConstantOverride("separation",2);
        Scroll.AddChild(rows);Scroll.Visible=Round.Visible=false;
        Round.ItemSelected+=i=>{manual=true;Show((int)i);};
    }
    internal static Color SeatColor(HudSeat? seat)
    {
        if(seat?.Color is not {} css || !Godot.Color.HtmlIsValid(css))return new("#cbd6df");
        var color=new Color(css);
        // Preserve hue while keeping identity colors quieter than the action text.
        // Fix relative luminance too: a green at red's HSV value looks much brighter.
        var tint=Color.FromHsv(color.H,Math.Min(.235f,color.S*.34f),1f);
        float scale=.75f/Math.Max(.01f,tint.Luminance);
        return new Color(tint.R*scale,tint.G*scale,tint.B*scale);
    }
    internal void Reset(){manual=false;selected=-1;Set(null,0);}
    internal void Set(HudPlan? plan,int current,ulong me=0)
    {
        if(ReferenceEquals(plan,lastPlan))return;
        if(lastPlan?.Steps!=plan?.Steps)expanded.Clear();
        lastPlan=plan;
        seats=plan?.Party??Array.Empty<HudSeat>();kills=plan?.Kills??Array.Empty<HudKill>();
        localSeat=Array.FindIndex(seats,s=>s.Id==me)+1;
        string text=plan?.Steps??"";
        int step=-1;
        pages.Clear();Round.Clear();
        foreach(var line in text.Split('\n'))
        {
            var heading=RoundHeading.Match(line);
            if(heading.Success)
            {
                int n=int.Parse(heading.Groups[1].Value);
                pages.Add(new(n,new()));Round.AddItem($"第 {n} 回合",n);
                string extra=heading.Groups[2].Value.Trim();
                if(extra.Length>0)pages[^1].Lines.Add((extra,-1));
            }
            else if(pages.Count!=0 && !string.IsNullOrWhiteSpace(line))pages[^1].Lines.Add((line,PlayerMove.IsMatch(line)?++step:-1));
        }
        int index=pages.FindIndex(p=>p.Number==(manual?selected:current));
        if(index<0){manual=false;index=Math.Max(0,pages.FindIndex(p=>p.Number==current));}
        Show(index);
    }
    internal void Filter(bool own)
    {
        OwnOnly=own;expanded.Clear();
        if(Round.Selected>=0)Show(Round.Selected);
    }
    internal void SelectNumber(int number)
    {
        int i=pages.FindIndex(p=>p.Number==number);
        if(i>=0 && number!=selected){manual=true;Show(i);}
    }
    private bool Other(string text)
    {
        var m=PlayerMove.Match(text);
        return localSeat!=0 && m.Success && int.Parse(m.Groups[1].Value)!=localSeat &&
            !text.Contains($"〔P{localSeat} ",StringComparison.Ordinal);
    }
    private void PlayerTotals()
    {
        foreach(var c in Roster.GetChildren()){Roster.RemoveChild(c);c.QueueFree();}
        var hp=lastPlan?.Rounds?.FirstOrDefault(r=>r.Round==selected);
        for(int i=0;i<seats.Length;i++)
        {
            var s=seats[i];var color=SeatColor(s);
            int? loss=hp?.Loss?.Length==seats.Length?hp.Loss[i]:selected==lastPlan?.Start?s.ThisTurn:null;
            int? end=hp?.EndHp?.Length==seats.Length?hp.EndHp[i]:
                selected==lastPlan?.Start && s.Hp.HasValue && s.ToEnd.HasValue && loss.HasValue?s.Hp+s.ToEnd-loss:null;
            var item=new HBoxContainer();item.AddThemeConstantOverride("separation",4);
            if(i>0)item.AddChild(CombatHud.Caption("·  ",new("#a5afb6"),small:true));
            item.AddChild(CombatHud.Caption($"P{i+1} {s.Name}",color,small:true));
            var pair=CombatHud.Caption($"扣{loss?.ToString()??"—"}/{end?.ToString()??"—"}",color,small:true);
            if(end==0)item.AddChild(CombatHud.Outline(pair,"回合末倒下"));else item.AddChild(pair);
            var dealt=hp?.Damage?.Length==seats.Length?hp.Damage[i].ToString():"—";
            item.AddChild(CombatHud.Caption("打"+dealt,color,small:true));
            item.TooltipText=s.Role+(i+1==localSeat?"（我）":"");Roster.AddChild(item);
        }
    }
    private void Show(int index)
    {
        int oldScroll=Scroll.ScrollVertical;
        foreach(var child in rows.GetChildren()){rows.RemoveChild(child);child.QueueFree();}
        Round.Visible=pages.Count>0;
        Scroll.Visible=pages.Count>0;
        if(index>=pages.Count){PlayerTotals();return;}
        Round.Select(index);
        var page=pages[index];
        bool same=selected==page.Number;
        selected=page.Number;
        PlayerTotals();
        for(int i=0;i<page.Lines.Count;)
        {
            var line=page.Lines[i++];
            if(!OwnOnly || !Other(line.Text)){AddRow(line.Text,line.Step);continue;}
            int first=i-1;
            while(i<page.Lines.Count && Other(page.Lines[i].Text))i++;
            int key=line.Step;
            var fold=new Button{Name="FoldPeers"+key,Flat=true,Text=$"{(expanded.Contains(key)?"▴":"▸")}  队友 {i-first} 步",FocusMode=Control.FocusModeEnum.None};
            fold.AddThemeFontSizeOverride("font_size",11);fold.AddThemeColorOverride("font_color",new Color("#aab9c1"));
            fold.AddThemeStyleboxOverride("normal",CombatHud.Box("#8799a811",2,0));
            fold.Pressed+=()=>{if(!expanded.Remove(key))expanded.Add(key);Show(index);};rows.AddChild(fold);
            if(expanded.Contains(key))for(int j=first;j<i;j++)AddRow(page.Lines[j].Text,page.Lines[j].Step);
        }
        Scroll.ScrollVertical=same?oldScroll:0;
    }
    private void AddRow(string text,int step)
    {
        var match=PlayerMove.Match(text);
        int seat=match.Success?int.Parse(match.Groups[1].Value):0;
        string body=match.Success?match.Groups[2].Value:text;
        int at=body.IndexOf('〔');
        string detail=at>=0?body[at..]:"";
        if(at>=0)body=body[..at];
        string[] move=body.Split(" → ",2);
        var bg=new PanelContainer{MouseFilter=Control.MouseFilterEnum.Pass};
        bg.AddThemeStyleboxOverride("panel",CombatHud.Box(rows.GetChildCount()%2==0?"#bbcbe312":"#050e191c",7,4));
        if(Other(text))bg.Modulate=new Color(.78f,.8f,.83f,.82f);
        rows.AddChild(bg);
        var line=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
        line.AddThemeConstantOverride("separation",9);bg.AddChild(line);
        var who=CombatHud.Caption(seat>0?"P"+seat:"",SeatColor(seat>0 && seat<=seats.Length?seats[seat-1]:null),small:true);
        who.CustomMinimumSize=new(27,0);line.AddChild(who);
        var content=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,MouseFilter=Control.MouseFilterEnum.Pass};
        content.AddThemeConstantOverride("separation",1);line.AddChild(content);
        if(move[0].Length!=0)
        {
            var action=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
            content.AddChild(action);
            var hits=lastPlan?.Rounds?.FirstOrDefault(r=>r.Round==selected)?.CardDamage;
            var title=CombatHud.Caption(hits!=null && hits.TryGetValue(step,out int amount)?move[0]+"  打"+amount:move[0],move[0]=="结束回合"?new("#bdcbd1"):new("#fffcf4"));
            title.AutowrapMode=TextServer.AutowrapMode.WordSmart;
            title.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;action.AddChild(title);
            if(move.Length==2)
            {
                var target=CombatHud.Caption("→ "+move[1],new("#c5d6dc"),small:true);
                target.HorizontalAlignment=HorizontalAlignment.Right;
                target.AutowrapMode=TextServer.AutowrapMode.WordSmart;
                if(kills.Any(k=>k.Step==step && k.IsTarget))
                {
                    action.AddChild(CombatHud.Caption("→",new("#c5d6dc"),small:true));
                    target.Text=move[1];
                    target.CustomMinimumSize=new(Math.Min(180,target.GetThemeFont("font").GetStringSize(move[1],fontSize:14).X+2),0);
                    action.AddChild(CombatHud.Outline(target,"击杀"));
                }
                else {target.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;action.AddChild(target);}
            }
        }
        foreach(var kill in kills.Where(k=>k.Step==step && (!k.IsTarget || move.Length==1)))
        {
            var victim=CombatHud.Outline(CombatHud.Caption(kill.Name,new("#ffedbf"),small:true),"击杀");
            victim.SizeFlagsHorizontal=Control.SizeFlags.ShrinkBegin;content.AddChild(victim);
        }
        if(detail.Length==0)return;
        var pick=CombatHud.Caption(detail.Replace("〕〔","  ·  ").Trim('〔','〕'),new("#f3d19a"),small:true);
        pick.AutowrapMode=TextServer.AutowrapMode.WordSmart;content.AddChild(pick);
    }
}
