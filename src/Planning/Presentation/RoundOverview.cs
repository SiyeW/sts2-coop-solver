using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CoopSolver;

internal sealed class RoundOverview
{
    internal readonly PanelContainer Panel=new(){Name="RoundOverview",Visible=false};
    private readonly ScrollContainer scroll=new(){HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,
        VerticalScrollMode=ScrollContainer.ScrollMode.Reserve};
    private readonly VBoxContainer body=new(){SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
    private readonly Action<int> choose;
    private readonly VBoxContainer table;
    private static readonly Color DefaultInk=new("#dce4e7"),Highlighted=new("#d0f6e4");

    internal RoundOverview(Action<int> choose)
    {
        this.choose=choose;
        Panel.AddThemeStyleboxOverride("panel",HudSurface.Box("#17252c55","#9aafb888",7,5));
        table=new VBoxContainer();
        table.AddThemeConstantOverride("separation",2);
        Panel.AddChild(table);
        scroll.AddChild(body);body.AddThemeConstantOverride("separation",0);
    }

    private static string[] Titles()=>new[]{Ui.Text("ui.088"),Ui.Text("ui.089"),Ui.Text("ui.090"),Ui.Text("ui.091")};
    private static int[]? Values(HudRoundHp? hp,int field)=>field switch
        {0=>hp?.EndHp,1=>hp?.Loss,2 when hp?.IncludesBlockDamage==true=>hp.Damage,_=>null};
    private static Color MetricInk(int field,int? value,Color neutral)=>field switch
    {
        1=>HudTypography.LossInk(value,neutral),
        2 when value>0=>HudTypography.Damage,
        _=>neutral
    };
    private static string Number(int? value)=>value?.ToString()??Ui.Missing;
    private static float TextWidth(string text)=>Mathf.Ceil(HudTypography.Face().GetStringSize(text,fontSize:HudTypography.Body).X);

    private sealed class Layout(HudPlan? plan,bool turns,IReadOnlyDictionary<int,string?[]>? roundDamage)
    {
        internal readonly HudSeat[] Party=plan?.Party??Array.Empty<HudSeat>();
        internal readonly bool Turns=turns;
        internal readonly int Seats=Math.Max(1,plan?.Party.Length??0);
        internal readonly float[][] Cells=Enumerable.Range(0,3).Select(field=>Enumerable.Range(0,Math.Max(1,plan?.Party.Length??0))
            .Select(seat=>Math.Max(TextWidth($"P{seat+1}"),(plan?.Rounds??Array.Empty<HudRoundHp>())
                .Select(hp=>TextWidth(MetricText(hp,field,seat,roundDamage?.GetValueOrDefault(hp.Round)))).DefaultIfEmpty(0).Max())+10)
            .ToArray()).ToArray();

        internal string?[]? DamageAt(int round)=>roundDamage?.GetValueOrDefault(round);

        internal float TurnWidth=>Math.Max(TextWidth(Ui.Text("ui.092")),(plan?.Rounds??Array.Empty<HudRoundHp>())
            .Select(hp=>TextWidth(Ui.Turn(hp.Round))).DefaultIfEmpty(0).Max())+10;
        internal float Width(int field,string title)=>Math.Max(TextWidth(title)+8,field<3
            ?Cells[field].Sum()+Math.Max(0,Seats-1)*4
            :(plan?.Rounds??Array.Empty<HudRoundHp>()).Select(row=>TextWidth(Number(row.EnemyHp))+8).DefaultIfEmpty(0).Max());
    }

    private static string MetricText(HudRoundHp? round,int field,int seat,string?[]? damage)
    {
        if(field==2 && damage?.ElementAtOrDefault(seat) is {} alternatives)return alternatives;
        var values=Values(round,field);
        return Number(values!=null && (uint)seat<(uint)values.Length?values[seat]:null);
    }

    private static HBoxContainer Line()
    {
        var row=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
        row.AddThemeConstantOverride("separation",6);return row;
    }

    private static void Divider(HBoxContainer row,bool spread=false)
    {
        var line=new Control{Name="HudColumnRule",MouseFilter=Control.MouseFilterEnum.Ignore,
            CustomMinimumSize=new(2,0),SizeFlagsHorizontal=spread?Control.SizeFlags.ExpandFill:Control.SizeFlags.Fill};
        line.Draw+=()=>HudTypography.DrawVerticalRule(line,line.Size.X/2,line.Size.Y,new Color("#9aafb867"));
        line.Resized+=line.QueueRedraw;
        row.AddChild(line);
    }

    private static Label Text(string value,Color ink,float width=0)
    {
        var label=CombatHud.Caption(value,ink);
        label.CustomMinimumSize=new(width,HudTypography.RowHeight);
        label.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        label.HorizontalAlignment=HorizontalAlignment.Center;
        return label;
    }

    private static HBoxContainer Fill(Layout layout,HudRoundHp? hp,int round,bool heading,bool selected,int local)
    {
        var row=Line();
        Color ink=selected?Highlighted:DefaultInk;
        if(layout.Turns)row.AddChild(Text(heading?Ui.Text("ui.093"):Ui.Turn(round),ink,layout.TurnWidth));
        var titles=Titles();
        for(int i=0;i<4;i++)
        {
            if(i>0 || layout.Turns)Divider(row);
            float width=layout.Width(i,titles[i]);
            var group=new VBoxContainer{CustomMinimumSize=new(width,0),SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,
                MouseFilter=Control.MouseFilterEnum.Ignore};
            group.AddThemeConstantOverride("separation",2);
            row.AddChild(group);
            if(i==3)
            {
                var label=Text(heading?titles[i]:Number(hp?.EnemyHp),ink);
                label.SizeFlagsVertical=Control.SizeFlags.ExpandFill;
                if(!heading && hp?.EnemyHp==0)
                    group.AddChild(CombatHud.Outline(label,Ui.Text("ui.094")));
                else group.AddChild(label);
                continue;
            }
            if(heading)
            {
                group.AddChild(Text(titles[i],ink));
                if(layout.Seats==1)continue;
            }
            var band=new HBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,MouseFilter=Control.MouseFilterEnum.Ignore};
            band.AddThemeConstantOverride("separation",4);group.AddChild(band);
            var values=Values(hp,i);
            for(int seat=0;seat<layout.Seats;seat++)
            {
                int? value=values!=null && seat<values.Length?values[seat]:null;
                var label=Text(heading?$"P{seat+1}":MetricText(hp,i,seat,layout.DamageAt(round)),heading?ink:MetricInk(i,value,ink),
                    layout.Cells[i][seat]);
                if(!heading)
                {
                    if(i==0 && value==0)
                    {
                        var fallen=CombatHud.Outline(label,Ui.Text("ui.095"));
                        fallen.CustomMinimumSize=new(layout.Cells[i][seat],0);
                        label.CustomMinimumSize=new(0,HudTypography.RowHeight);
                        fallen.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;band.AddChild(fallen);
                    }
                    else band.AddChild(label);
                    continue;
                }
                var box=new PanelContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,MouseFilter=Control.MouseFilterEnum.Ignore};
                box.AddThemeStyleboxOverride("panel",HudRouteView.SeatBox(layout.Party.ElementAtOrDefault(seat),0,0));
                box.AddChild(label);band.AddChild(box);
                CombatHud.SeatUnderline(box,seat+1==local,ink);
            }
        }
        return row;
    }

    internal static void RenderSummary(VBoxContainer target,int number,HudPlan? plan,int localSeat,int leftInset,string?[]? damage=null)
    {
        Clear(target);
        if(plan==null || number<0)return;
        var rounds=plan.Rounds??Array.Empty<HudRoundHp>();
        var hp=rounds.FirstOrDefault(r=>r.Round==number);
        var titles=Titles();
        int seats=Math.Max(1,plan.Party.Length);
        float cellWidth=TextWidth("00");
        foreach(var round in rounds)
            for(int field=0;field<3;field++)
                if(Values(round,field) is {} values)
                    foreach(int value in values)cellWidth=Math.Max(cellWidth,TextWidth(Number(value)));

        var inset=new MarginContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
        inset.AddThemeConstantOverride("margin_left",leftInset);
        inset.AddThemeConstantOverride("margin_right",RouteOutcomeView.TextInset);
        var row=Line();
        target.AddChild(inset);inset.AddChild(row);
        for(int field=0;field<4;field++)
        {
            if(field>0)Divider(row,spread:true);
            var group=Line();group.AddThemeConstantOverride("separation",4);
            row.AddChild(group);
            var title=CombatHud.Caption(titles[field],DefaultInk);
            group.AddChild(title);
            if(field==3)
            {
                var enemy=Text(Number(hp?.EnemyHp),DefaultInk);
                Control enemyValue=hp?.EnemyHp==0?CombatHud.Outline(enemy,Ui.Text("ui.096")):enemy;
                group.AddChild(enemyValue);
                continue;
            }

            var values=Values(hp,field);
            for(int seat=0;seat<seats;seat++)
            {
                int? value=values!=null && seat<values.Length?values[seat]:null;
                Color ink=MetricInk(field,value,DefaultInk);
                bool lethal=field==0 && value==0;
                string text=MetricText(hp,field,seat,damage);
                var label=Text(text,ink,Math.Max(cellWidth,TextWidth(text)));
                PanelContainer badge;
                if(plan.Party.Length<2 && lethal)badge=CombatHud.Outline(label,Ui.Text("ui.097"));
                else
                {
                    badge=new PanelContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
                    var style=plan.Party.Length>=2?HudRouteView.SeatBox(plan.Party[seat],4,0,lethal):
                        HudSurface.Box("#00000000","",4,0);
                    badge.AddThemeStyleboxOverride("panel",style);
                    badge.AddChild(label);
                }
                group.AddChild(badge);
                CombatHud.SeatUnderline(badge,plan.Party.Length>=2 && seat+1==localSeat,ink);
            }
            if(field==2 && hp is {IncludesBlockDamage:true,UnassignedDamage:{} unassigned} &&
                unassigned.Values.Sum() is int other && other>0)
                group.AddChild(CombatHud.Caption(Ui.Text("ui.098",other),HudTypography.Damage,true));
        }
    }

    internal void Render(IReadOnlyList<int> numbers,HudPlan? plan,int selected,int localSeat,IReadOnlyDictionary<int,string?[]>? roundDamage=null)
    {
        int oldScroll=scroll.ScrollVertical;
        if(scroll.GetParent()==table)table.RemoveChild(scroll);
        Clear(table);Clear(body);
        var layout=new Layout(plan,true,roundDamage);
        var header=new MarginContainer();
        header.AddChild(Fill(layout,null,0,true,false,localSeat));table.AddChild(header);
        table.AddChild(scroll);
        foreach(int n in numbers)
        {
            var hp=plan?.Rounds?.FirstOrDefault(r=>r.Round==n);
            if(hp==null)continue;
            var line=Fill(layout,hp,n,false,n==selected,localSeat);
            var button=new Button{Name="RoundEntry"+n,FocusMode=Control.FocusModeEnum.None,
                CustomMinimumSize=new(line.GetCombinedMinimumSize().X,HudTypography.ActionRowHeight())};
            var background=HudSurface.Box(n==selected?"#68a89560":body.GetChildCount()%2==0?"#92a7b420":"#23344148",
                n==selected?"#9dd7bc88":"",0,0);
            button.AddThemeStyleboxOverride("normal",background);
            button.AddThemeStyleboxOverride("hover",HudSurface.Box(n==selected?"#68a89575":"#91b4bc48","",0,0));
            button.AddThemeStyleboxOverride("pressed",background);
            button.AddThemeStyleboxOverride("hover_pressed",background);
            button.AddThemeStyleboxOverride("focus",new StyleBoxEmpty());
            button.AddChild(line);line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            button.Pressed+=()=>choose(n);body.AddChild(button);
            var required=line.GetCombinedMinimumSize();
            button.CustomMinimumSize=new(required.X,Math.Max(HudTypography.ActionRowHeight(),required.Y));
        }
        float content=body.GetCombinedMinimumSize().Y;
        bool overflow=content>240;
        scroll.VerticalScrollMode=overflow?ScrollContainer.ScrollMode.Reserve:ScrollContainer.ScrollMode.Disabled;
        header.AddThemeConstantOverride("margin_right",overflow?(int)Math.Ceiling(scroll.GetVScrollBar().GetCombinedMinimumSize().X):0);
        scroll.CustomMinimumSize=new(0,Math.Min(240,content));
        scroll.ScrollVertical=overflow?oldScroll:0;
    }

    private static void Clear(Container container)
    {
        foreach(var child in container.GetChildren()){container.RemoveChild(child);child.QueueFree();}
    }
}
