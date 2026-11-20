using System;
using System.Collections.Generic;
using System.Linq;

using Godot;

namespace CoopSolver;

internal sealed class CombatHud
{
    private const string Layout="user://coop-solver-hud.cfg";
    private static readonly Color TextColor=new("#f4f3e9"), Dim=new("#b7c9bd"), Accent=new("#efcc8e");
    private readonly Control root=new(){Name="CoopSolverHUD",MouseFilter=Control.MouseFilterEnum.Ignore};
    private readonly VBoxContainer bar=new(){Name="HudBar",MouseFilter=Control.MouseFilterEnum.Stop};
    private readonly HBoxContainer openingRow=new(){Name="HudOpeningBest",MouseFilter=Control.MouseFilterEnum.Ignore};
    private readonly HBoxContainer liveRow=new(){Name="HudLiveBest",MouseFilter=Control.MouseFilterEnum.Ignore};
    private readonly Label[] openingNumbers=new Label[4],liveNumbers=new Label[4];
    private readonly HFlowContainer searchOptions=new();
    private readonly OptionButton policy=new(){Name="HudPolicy",Flat=true};


    private readonly PanelContainer pop=new(){Name="HudDetails"};
    private readonly Dictionary<Label,List<Label>> lossCells=new();
    private readonly Dictionary<Label,Label[]> counters=new();
    private readonly SystemFont[] fonts=new SystemFont[4];
    private readonly Label status=Caption("",Dim,small:true), outcome=Caption("",TextColor),choice=Caption("",Accent);
    private HFlowContainer roster=>route.Roster;
    private readonly HudRouteView route=new();
    private readonly VBoxContainer col=new();
    private readonly HBoxContainer head=new();
    private readonly HFlowContainer footer=new(),extras=new();
    private readonly Button solve,mode,less,more,guard,windowButton,settings,opacityButton;
    private readonly Button hideResults,ownOnly;
    private readonly OptionButton openingShare=new(){Name="HudOpeningShare",Flat=true};
    private readonly Label cpu=Caption("",Dim,small:true),indicator=Caption("▾",Dim,small:true);
    private readonly StyleBoxFlat panelStyle,deathBorder,emptyBorder;
    private bool pinned,held,ignoreHover,options;
    private float opacity=.68f;
    private Vector2 pressAt;
    private HudPlan? lastDetail;
    private bool detailInitialized;
    private HudFrame? last;
    private int fontSize=17;
    private long hoverUntil;
    private Vector2 anchor=new(.98f,.12f),dragDelta;
    private bool dragging;
    private ulong localId;
    internal bool Visible=>root.Visible;
    internal bool Expanded=>pop.Visible;
    internal CombatHud(SceneTree tree,Action start,Action switchMode,Action alternate,Action window,Action hide)
    {
        var config=new ConfigFile();
        if(config.Load(Layout)==Error.Ok)
        {fontSize=Math.Clamp(config.GetValue("hud","font",17).AsInt32(),11,30);anchor=config.GetValue("hud","anchor",anchor).AsVector2().Clamp(Vector2.Zero,Vector2.One);pinned=config.GetValue("hud","pinned",false).AsBool();opacity=Math.Clamp(config.GetValue("hud","opacity",.68f).AsSingle(),.4f,.9f);}
        for(int i=0;i<4;i++)fonts[i]=new SystemFont{FontNames=new[]{"Microsoft YaHei UI","Microsoft YaHei","Segoe UI"},FontItalic=(i&1)!=0,FontWeight=(i&2)!=0?750:500};
        var layer=new CanvasLayer{Layer=92};tree.Root.AddChild(layer);layer.AddChild(root);root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        root.Theme=new Theme{DefaultFont=fonts[0],DefaultFontSize=16};root.Hide();root.AddChild(bar);
        bar.AddThemeConstantOverride("separation",2);
        deathBorder=Stroke();emptyBorder=Box("#00000000",0,0);
        bar.TooltipText="点击固定或收起　·　拖动移位　·　滚轮缩放\n计算：全部 / 推荐首手分支　　扣血：至路线末累计";
        bar.AddChild(openingRow);bar.AddChild(liveRow);
        BuildRow(openingRow,openingNumbers,"开局最优");
        BuildRow(liveRow,liveNumbers,"实战最优");
        indicator.CustomMinimumSize=new(28,0);indicator.HorizontalAlignment=HorizontalAlignment.Center;
        indicator.AddThemeFontSizeOverride("font_size",23);indicator.AddThemeStyleboxOverride("normal",Box("#182838da",3,0));
        openingRow.AddChild(indicator);bar.GuiInput+=BarInput;
        bar.MouseEntered+=()=>{if(!ignoreHover)hoverUntil=System.Environment.TickCount64+650;};
        root.AddChild(pop);panelStyle=Box("#111c28",10,7);ApplyOpacity();
        pop.AddThemeStyleboxOverride("panel",panelStyle);pop.Hide();
        pop.AddChild(col);col.AddThemeConstantOverride("separation",5);
        col.AddChild(head);head.AddChild(route.Round);head.AddChild(outcome);outcome.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        outcome.HorizontalAlignment=HorizontalAlignment.Right;outcome.ClipText=true;
        ownOnly=Btn("HudOwnOnly","只看自己",()=>{route.Filter(!route.OwnOnly);Save();},head);
        route.Filter(config.GetValue("hud","own_only",false).AsBool());
        col.AddChild(roster);
        col.AddChild(choice);choice.AutowrapMode=TextServer.AutowrapMode.WordSmart;
        col.AddChild(route.Scroll);col.AddChild(footer);col.AddChild(searchOptions);col.AddChild(extras);
        solve=Btn("HudSolve","开始",start,footer);
        mode=Btn("HudMode","开局",switchMode,footer);
        mode.TooltipText="切换查看开局或实战路线，计算比例保持不变";
        status.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;status.ClipText=true;footer.AddChild(status);
        hideResults=Btn("HudHide","隐藏结果",hide,footer);
        settings=Btn("HudOptions","设置",()=>options=!options,footer);
        less=Btn("HudCpuLess","−",()=>SearchCapacity.SetAndSave(SearchCapacity.Limit-1),extras);
        extras.AddChild(cpu);more=Btn("HudCpuMore","+",()=>SearchCapacity.SetAndSave(SearchCapacity.Limit+1),extras);
        guard=Btn("HudGuard","保血起手",alternate,extras);windowButton=Btn("HudWindow","副屏",window,extras);
        searchOptions.AddChild(policy);policy.AddItem("只搜开局",1);policy.AddItem("搜实战",0);
        policy.ItemSelected+=i=>{
            SearchCapacity.SetOpeningPercent(i==0?100:SearchCapacity.LiveOpeningPercent);
        };
        searchOptions.AddChild(openingShare);
        foreach(int value in new[]{0,10,20,30,50,75})openingShare.AddItem($"开局 {value}%",value);
        openingShare.Select(openingShare.GetItemIndex(SearchCapacity.OpeningPercent));
        openingShare.TooltipText="分给战斗开局的计算时间，其余用于实战";
        openingShare.ItemSelected+=i=>SearchCapacity.SetOpeningPercent(openingShare.GetItemId((int)i));
        opacityButton=Btn("HudOpacity","",()=>{opacity=opacity<.55f?.68f:opacity<.8f?.86f:.48f;ApplyOpacity();Save();},extras);
    }
    private void ApplyOpacity()=>panelStyle.BgColor=new Color(.054f,.083f,.11f,opacity);
    internal static Label Caption(string text,Color color,bool small=false)
    {
        var label=new Label{Text=text,MouseFilter=Control.MouseFilterEnum.Ignore};
        label.AddThemeColorOverride("font_color",color);label.AddThemeColorOverride("font_shadow_color",new Color(0,0,0,.7f));
        label.AddThemeConstantOverride("shadow_offset_y",1);
        if(small)label.AddThemeFontSizeOverride("font_size",14);
        return label;
    }
    private static Button Btn(string name,string text,Action act,Control parent)
    {
        var b=new Button{Name=name,Text=text,Flat=true,FocusMode=Control.FocusModeEnum.None};
        b.AddThemeFontSizeOverride("font_size",14);foreach(string state in new[]{"normal","hover","pressed","hover_pressed","disabled"})
            b.AddThemeStyleboxOverride(state,Box(state=="hover"?"#385266b0":"#1c2b3640",8,3));
        b.ClipText=false;b.TextOverrunBehavior=TextServer.OverrunBehavior.NoTrimming;
        b.Pressed+=act;parent.AddChild(b);return b;
    }
    private static StyleBoxFlat Stroke()
    {
        var s=Box("#28201a36",4,0);s.SetBorderWidthAll(1);s.BorderColor=new("#ffe0a5");s.SetCornerRadiusAll(3);return s;
    }
    internal static PanelContainer Outline(Label label,string tip)
    {
        var p=new PanelContainer{Name="HudKill",TooltipText=tip,MouseFilter=Control.MouseFilterEnum.Pass};
        p.AddThemeStyleboxOverride("panel",Stroke());p.AddChild(label);return p;
    }
    internal static StyleBoxFlat Box(string color,int x,int y)
    {
        var s=new StyleBoxFlat{BgColor=new Color(color)};s.SetCornerRadiusAll(5);
        s.SetContentMargin(Side.Left,x);s.SetContentMargin(Side.Right,x);s.SetContentMargin(Side.Top,y);s.SetContentMargin(Side.Bottom,y);return s;
    }
    private void Save()
    {var cfg=new ConfigFile();cfg.SetValue("hud","font",fontSize);cfg.SetValue("hud","anchor",anchor);cfg.SetValue("hud","pinned",pinned);cfg.SetValue("hud","opacity",opacity);cfg.SetValue("hud","own_only",route.OwnOnly);cfg.Save(Layout);}
    private void BarInput(InputEvent e)
    {
        if(e is InputEventMouseButton b)
        {
            if(b.Pressed && b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {fontSize=Math.Clamp(fontSize+(b.ButtonIndex==MouseButton.WheelUp?1:-1),11,30);Save();if(last!=null)Format(last);bar.AcceptEvent();}
            else if(b.ButtonIndex==MouseButton.Left)
            {
                if(b.Pressed){held=true;dragging=false;pressAt=b.GlobalPosition/root.Scale;dragDelta=pressAt-bar.Position;}
                else if(held)
                {
                    if(!dragging){pinned=!pinned;ignoreHover=!pinned;hoverUntil=0;pop.Visible=pinned;}
                    held=dragging=false;Save();
                }
                bar.AcceptEvent();
            }
        }
        if(e is InputEventMouseMotion motion && held)
        {
            if((motion.GlobalPosition/root.Scale).DistanceTo(pressAt)>=5)dragging=true;
            if(!dragging)return;
            var area=(root.Size-bar.Size).Max(Vector2.One);
            anchor=((motion.GlobalPosition/root.Scale-dragDelta)/area).Clamp(Vector2.Zero,Vector2.One);bar.AcceptEvent();
        }
    }
    private void BuildRow(HBoxContainer container,Label[] numbers,string title)
    {
        container.AddThemeConstantOverride("separation",2);
        for(int i=0;i<numbers.Length;i++)
        {
            var box=new PanelContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
            box.AddThemeStyleboxOverride("panel",Box("#111c28b8",6,2));container.AddChild(box);
            var label=Caption(i==0?title:"",TextColor);
            label.AddThemeFontSizeOverride("font_size",16);numbers[i]=label;
            if(i==0)
            {
                var strip=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
                strip.AddThemeConstantOverride("separation",2);box.AddChild(strip);strip.AddChild(label);
                var figures=new Label[3];counters[label]=figures;
                for(int n=0;n<figures.Length;n++)
                {
                    var value=Caption("",TextColor);value.HorizontalAlignment=HorizontalAlignment.Right;
                    value.CustomMinimumSize=new(n==2?88:62,0);strip.AddChild(value);figures[n]=value;
                }
            }
            else if(i==1)
            {
                var strip=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};strip.AddThemeConstantOverride("separation",0);
                box.AddChild(strip);strip.AddChild(label);lossCells.Add(label,new());
            }
            else box.AddChild(label);

        }
    }
    private void FormatRow(Label[] row,HudBest? best,string name,bool hidden)
    {
        row[0].Text=name;var figures=counters[row[0]];
        figures[0].Text=Advisor.Compact(best?.Visits??0);
        figures[1].Visible=!hidden;figures[1].Text=best?.Pv is {} pv?Advisor.Compact(pv):"—";
        figures[2].Text=Advisor.Compact(best?.Rate??0)+" v/s";
        for(int i=1;i<4;i++)
            ((Control)(i==1?row[i].GetParent().GetParent():row[i].GetParent())).Visible=!hidden;
        if(hidden)return;
        var party=best?.Party??Array.Empty<HudSeat>();
        row[1].Text=party.Length==0?"扣血 —":"扣血 ";
        var labels=lossCells[row[1]];
        for(int i=0;i<Math.Max(party.Length,labels.Count);i++)
        {
            if(i==labels.Count)
            {
                var label=Caption("",TextColor);
                var wrap=new PanelContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
                row[1].GetParent().AddChild(wrap);wrap.AddChild(label);labels.Add(label);
            }
            var cell=labels[i];var panel=(PanelContainer)cell.GetParent();panel.Visible=i<party.Length;
            if(i>=party.Length)continue;
            var seat=party[i];cell.Text=(i==0?"":"·")+(seat.ToEnd?.ToString()??"—");
            panel.AddThemeStyleboxOverride("panel",seat.DiesAtEnd?deathBorder:emptyBorder);
            cell.AddThemeFontOverride("font",fonts[(seat.Id==localId?0:1)+(seat.DiesAtEnd?2:0)]);
            cell.AddThemeColorOverride("font_color",HudRouteView.SeatColor(seat));
        }
        row[2].Text=$"回合 {best?.Round.ToString()??"—"}/{best?.End?.ToString()??"—"} {best?.Outcome}";
        row[3].Text="药水 "+(party.Length==0?"—":string.Join("·",party.Select(s=>s.Potions?.ToString()??"—")));
    }
    private void Format(HudFrame f)
    {
        var single=new HudBest(f.Visits,f.Pv,f.Rate,f.Round,f.Plan?.End,f.Plan?.Party??Array.Empty<HudSeat>(),"");
        FormatRow(openingNumbers,f.Hidden?single:f.Opening??(!f.Live?single:null),f.Hidden?"计算":"开局最优",f.Hidden);
        liveRow.Visible=!f.Hidden;
        if(!f.Hidden)FormatRow(liveNumbers,f.Realtime??(f.Live?single:null),"实战最优",false);
        bar.ResetSize();
    }
    internal void Hide()
    {
        root.Hide();pop.Hide();held=dragging=ignoreHover=false;hoverUntil=0;last=null;lastDetail=null;detailInitialized=false;route.Reset();
    }
    internal void Update(HudFrame f,ulong me,bool host,bool canStart,bool hasGuard,bool showGuard)
    {
        root.Show();localId=me;
        root.Scale=Vector2.One*(fontSize/17f);
        root.Size=root.GetViewportRect().Size/root.Scale;
        if(f.Hidden && last?.Hidden!=true){route.Round.GetPopup().Hide();lastDetail=null;detailInitialized=false;route.Reset();choice.Text=outcome.TooltipText="";}
        if(last!=f){Format(f);last=f;}
        var area=(root.Size-bar.Size).Max(Vector2.Zero);bar.Position=anchor*area;
        var mouse=root.GetLocalMousePosition();long now=System.Environment.TickCount64;
        bool over=bar.GetRect().HasPoint(mouse);
        if(!over)ignoreHover=false;
        if(!ignoreHover && (over || pop.Visible && (pop.GetRect().HasPoint(mouse) || policy.GetPopup().Visible || openingShare.GetPopup().Visible || route.Round.GetPopup().Visible)))hoverUntil=now+220;
        pop.Visible=!dragging && (pinned || !ignoreHover && now<hoverUntil);
        indicator.Text=pinned?"▲":"▼";indicator.Modulate=pinned?Accent:Dim;
        if(f.Hidden && !host)pop.Hide();
        if(!pop.Visible)return;
        var p=f.Plan;
        if(!ReferenceEquals(p,lastDetail) || !detailInitialized)
        {
            detailInitialized=true;lastDetail=p;
            route.Set(p,f.Round,me);
            string title=p?.Steps.Split('\n',2)[0]??"等待路线";
            string result=title.Contains("供对照")?"开局路线供对照":title.Contains("倒下")?"全队倒下":title.Contains("胜利")?"胜利":title.Contains("保血")?"保血起手":title.Contains("待复算")?"更新中":p==null?"等待路线":"胜负未定";
            outcome.Text=result;outcome.Modulate=result=="胜利"?new("#a6edbd"):Colors.White;
            outcome.TooltipText=(p?.Result??"").Replace("模拟胜利","胜利").Replace("发现时模拟","发现时计算")+(string.IsNullOrEmpty(p?.Risks)?"":"\n"+p.Risks);
            outcome.MouseFilter=Control.MouseFilterEnum.Pass;
        }
        ownOnly.Visible=p?.Party.Length>1;ownOnly.Text=route.OwnOnly?"显示全队":"只看自己";
        head.Visible=!f.Hidden;
        roster.Visible=!f.Hidden && roster.GetChildCount()>0;route.Scroll.Visible=!f.Hidden && route.Count>0;
        hideResults.Visible=host;hideResults.Text=f.Hidden?"显示结果":"隐藏结果";
        choice.Text=f.Choice;choice.Visible=f.Choice.Length>0;
        solve.Visible=host;mode.Visible=host && !f.Hidden;
        solve.Text=f.Running?"暂停搜索":SearchCapacity.OpeningPercent==100?"开始搜开局":"开始搜实战";
        solve.Disabled=!canStart && !f.Running;
        mode.Text=f.Live?"看开局操作":"看实战操作";
        bool changing=f.State.Contains("复核余线") || f.State.Contains("正在重算") || f.State.Contains("试接余线") || f.State.Contains("等待实战结算");
        status.Text=changing?"更新中":f.Choice.Length>0?"选牌中":host?"":f.Live?"房主 · 实战":"房主 · 开局";
        if(!changing && f.State.Contains("已恢复"))status.Text="已接回路线";
        status.TooltipText=f.State;
        searchOptions.Visible=host && (options || !f.Running);
        policy.Select(SearchCapacity.OpeningPercent==100?0:1);
        openingShare.Visible=SearchCapacity.OpeningPercent!=100;
        if(openingShare.Visible && openingShare.GetSelectedId()!=SearchCapacity.OpeningPercent)
            openingShare.Select(openingShare.GetItemIndex(SearchCapacity.OpeningPercent));
        extras.Visible=options;less.Visible=cpu.Visible=more.Visible=host;windowButton.Visible=host && !f.Hidden;
        cpu.Text=$"CPU {SearchCapacity.Limit}/{SearchCapacity.Available}";
        less.Disabled=SearchCapacity.Limit<=1;more.Disabled=SearchCapacity.Limit>=SearchCapacity.Available;
        guard.Visible=host && !f.Hidden && hasGuard;guard.Text=showGuard?"主路线":"保血起手";
        opacityButton.Text=$"底色 {Math.Round(opacity*100)}%";opacityButton.TooltipText="点击调整面板底色深浅";
        float above=bar.Position.Y,below=root.Size.Y-bar.GetRect().End.Y;
        float available=Math.Max(above,below);
        float width=Math.Min(root.Size.X,Math.Max(440,bar.Size.X));
        float desired=col.GetCombinedMinimumSize().Y+(route.Scroll.Visible?route.ContentHeight:0)+17;
        pop.Size=new(width,Math.Min(available,desired));
        float x=anchor.X>.5f?bar.GetRect().End.X-pop.Size.X:bar.Position.X;
        pop.Position=new(Math.Clamp(x,0,Math.Max(0,root.Size.X-pop.Size.X)),below>=pop.Size.Y || below>=above?bar.GetRect().End.Y:Math.Max(0,above-pop.Size.Y));
    }
}
