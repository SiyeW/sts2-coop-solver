using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace CoopSolver;

internal sealed class RouteWindow
{
    private const string LayoutFile="user://coop-solver-window.cfg";
    private static readonly Color Ink=new("#edf2ee"), Muted=new("#a5b6af"), Mint=new("#8edfc0");
    private static readonly Color[] Seats={new("#d4afff"),new("#94dcb7"),new("#efc282")};
    private static readonly Regex RoundHeading=new(@"^第 (\d+) 回合(.*)$");
    private readonly Window window=new() { Name="CoopSolverRouteWindow", Title="全队战斗路线", Visible=false, ForceNative=true,
        Transient=false, Exclusive=false, Borderless=false, Unresizable=false,
        InitialPosition=Window.WindowInitialPosition.Absolute, MinSize=new(500,390) };
    private readonly Label status=Text("等待求解",24,Ink);
    private readonly Label metrics=Text("",14,Muted);
    private readonly Label choiceHint=Text("",18,Mint);
    private readonly Label outcome=Text("",16,Ink);
    private readonly Label warning=Text("",13,new Color("#f3c989"));
    private readonly RichTextLabel actions=new() {Name="RoundActions",SelectionEnabled=true,
        SizeFlagsVertical=Control.SizeFlags.ExpandFill,AutowrapMode=TextServer.AutowrapMode.WordSmart};
    private readonly OptionButton selector=new() {Name="RoundSelect",FitToLongestItem=false,CustomMinimumSize=new(122,38)};
    private readonly Button previous=ButtonFor("上一回合"), next=ButtonFor("下一回合"), control=ButtonFor("开始求解",true);
    private readonly HBoxContainer navigation=new();
    private readonly List<(int Number,string Choices,List<string> Actions)> rounds=new();
    private readonly Button guardButton=ButtonFor("看保血起手");
    private string mainRoute="",mainSummary="",guardRoute="",guardSummary="";
    internal bool ShowingGuard {get;private set;}
    private HudPlan? mainPlan,guardPlan,displayPlan;
    private readonly HudRouteView playerView=new();
    private readonly OptionButton searchPolicy=new(){Name="WindowSearchPolicy",Flat=true};
    private readonly Button selfFilter=ButtonFor("只看自己");
    private bool ownOnly;
    private int selected;
    private string lastRoute="";
    private Rect2I stored;
    private long geometryCheck;
    private bool shown;
    internal bool IsShown=>window.Visible;
    internal bool HasRoute=>rounds.Count>0;

    internal RouteWindow(SceneTree tree,Action toggle,Action<bool> changeMode)
    {
        var usable=DisplayServer.ScreenGetUsableRect(tree.Root.CurrentScreen);
        var defaultSize=new Vector2I(Math.Min(740,usable.Size.X),Math.Min(690,usable.Size.Y));
        var bounds=new Rect2I(usable.Position+(usable.Size-defaultSize)/2,defaultSize);
        var cfg=new ConfigFile();
        if(cfg.Load(LayoutFile)==Error.Ok)
        {
            var saved=cfg.GetValue("window","rect",bounds).AsRect2I();
            for(int i=0;i<DisplayServer.GetScreenCount();i++)
            {
                var screen=DisplayServer.ScreenGetUsableRect(i);
                // Restore only where the user can still reach the title bar.
                if(!screen.HasPoint(saved.Position+new Vector2I(50,1)))continue;
                var size=new Vector2I(Math.Clamp(saved.Size.X,500,Math.Max(500,screen.Size.X)),
                    Math.Clamp(saved.Size.Y,390,Math.Max(390,screen.Size.Y)));
                bounds=new Rect2I(saved.Position.Clamp(screen.Position, (screen.End-size).Max(screen.Position)),size);
                break;
            }
        }
        SearchCapacity.Limit=cfg.GetValue("search","cores",1).AsInt32();
        stored=bounds;window.Position=bounds.Position;window.Size=bounds.Size;
        window.Theme=new Theme {DefaultFont=new SystemFont {FontNames=new[]{"Microsoft YaHei UI","Microsoft YaHei","Segoe UI"}},DefaultFontSize=16};
        tree.Root.AddChild(window);
        var bg=new ColorRect {Color=new Color("#131c1a"),MouseFilter=Control.MouseFilterEnum.Ignore};
        window.AddChild(bg);bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var margins=new MarginContainer();window.AddChild(margins);margins.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach(var side in new[]{"left","right","top","bottom"})margins.AddThemeConstantOverride("margin_"+side,20);
        var content=new VBoxContainer();margins.AddChild(content);content.AddThemeConstantOverride("separation",10);
        var header=new HBoxContainer();content.AddChild(header);
        status.Name="SearchStatus";status.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        header.AddChild(status);control.Name="RouteSolve";control.Pressed+=toggle;header.AddChild(control);
        metrics.Name="SearchMetrics";
        metrics.MouseFilter=Control.MouseFilterEnum.Stop;
        metrics.TooltipText="模拟量按实际执行入口累计：出牌、喝药和单席结束回合，包括试打与复算。k 是千，M 是百万。";
        var modes=new HBoxContainer();content.AddChild(modes);
        var group=new ButtonGroup();
        Button Mode(string title,string name,bool live)
        {
            var b=ButtonFor(title);b.Name=name;b.ToggleMode=true;b.ButtonGroup=group;
            b.AddThemeStyleboxOverride("pressed",Box("#345d4e",12));b.ButtonPressed=!live;
            b.Pressed+=()=>{b.ButtonPressed=true;changeMode(live);};modes.AddChild(b);return b;
        }
        Mode("战斗开局","OpeningMode",false).TooltipText="保留本场开局，继续实战或读档后仍可对照开局路线";
        Mode("跟随实战","LiveMode",true).TooltipText="实际行动结算后匹配后续分支，从当前盘面自动续算";
        searchPolicy.AddItem("只搜开局");searchPolicy.AddItem("搜实战");
        searchPolicy.ItemSelected+=i=>SearchCapacity.SetOpeningPercent(i==0?100:SearchCapacity.LiveOpeningPercent);
        modes.AddChild(searchPolicy);
        modes.AddChild(new Control{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill});
        var cpu=Text("",14,Muted);cpu.Name="CpuLimit";cpu.AutowrapMode=TextServer.AutowrapMode.Off;
        var minus=ButtonFor("−");var plus=ButtonFor("+");minus.Name="CpuLess";plus.Name="CpuMore";
        void Cpu(int delta)
        {
            SearchCapacity.Limit+=delta;
            cpu.Text=$"CPU {SearchCapacity.Limit}/{SearchCapacity.Available}";
            cpu.TooltipText="两种模式合计使用的搜索线程上限，分母为可用逻辑核心数。";
            minus.Disabled=SearchCapacity.Limit==1;plus.Disabled=SearchCapacity.Limit==SearchCapacity.Available;
            if(delta!=0){var settings=new ConfigFile();settings.Load(LayoutFile);settings.SetValue("search","cores",SearchCapacity.Limit);settings.Save(LayoutFile);}
        }
        minus.Pressed+=()=>Cpu(-1);plus.Pressed+=()=>Cpu(1);
        modes.AddChild(minus);modes.AddChild(cpu);modes.AddChild(plus);Cpu(0);
        choiceHint.Name="PendingChoice";choiceHint.Hide();content.AddChild(choiceHint);
        outcome.Name="RouteOutcome";content.AddChild(outcome);content.AddChild(metrics);content.AddChild(warning);
        var divider=new ColorRect {Color=new Color("#3a4a44"),CustomMinimumSize=new(0,1)};content.AddChild(divider);
        content.AddChild(navigation);
        previous.Name="PreviousRound";next.Name="NextRound";
        navigation.AddChild(selector);
        var prefs=new ConfigFile();prefs.Load(LayoutFile);ownOnly=prefs.GetValue("window","own_only",false).AsBool();playerView.Filter(ownOnly);
        selfFilter.Pressed+=()=>{
            ownOnly=!ownOnly;playerView.Filter(ownOnly);selfFilter.Text=ownOnly?"显示全队":"只看自己";
            var cfg=new ConfigFile();cfg.Load(LayoutFile);cfg.SetValue("window","own_only",ownOnly);cfg.Save(LayoutFile);
        };
        selfFilter.Text=ownOnly?"显示全队":"只看自己";navigation.AddChild(selfFilter);
        guardButton.Name="GuardPlan";guardButton.Hide();navigation.AddChild(guardButton);
        guardButton.Pressed+=()=>
        {
            ShowingGuard=!ShowingGuard;
            guardButton.Text=ShowingGuard?"看当前路线":"看保血起手";
            DrawRoute(ShowingGuard?guardRoute:mainRoute,ShowingGuard?guardSummary:mainSummary,ShowingGuard?guardPlan:mainPlan);
        };
        var spacer=new Control {SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};navigation.AddChild(spacer);
        navigation.AddChild(previous);navigation.AddChild(next);
        selector.ItemSelected+=i=>SelectRound((int)i);previous.Pressed+=()=>SelectRound(selected-1);next.Pressed+=()=>SelectRound(selected+1);
        actions.AddThemeStyleboxOverride("normal",Box("#1c2824",14));
        actions.AddThemeConstantOverride("line_separation",5);
        actions.AddThemeFontSizeOverride("normal_font_size",17);
        content.AddChild(playerView.Roster);content.AddChild(playerView.Scroll);
        // The native window already has round navigation.
        playerView.Round.Visible=false;window.AddChild(playerView.Round);
        content.AddChild(actions);
        window.CloseRequested+=()=>{SaveGeometry();window.Hide();};
        window.TreeExiting+=SaveGeometry;
        Reset();
    }

    private static Label Text(string text,int size,Color color)
    {
        var label=new Label {Text=text,AutowrapMode=TextServer.AutowrapMode.WordSmart};
        label.AddThemeColorOverride("font_color",color);label.AddThemeFontSizeOverride("font_size",size);
        return label;
    }
    private static StyleBoxFlat Box(string color,int padding)
    {
        var box=new StyleBoxFlat {BgColor=new Color(color)};
        box.SetCornerRadiusAll(6);
        foreach(var side in new[]{Side.Left,Side.Right})box.SetContentMargin(side,padding);
        foreach(var side in new[]{Side.Top,Side.Bottom})box.SetContentMargin(side,8);
        return box;
    }
    private static Button ButtonFor(string text,bool primary=false)
    {
        var button=new Button {Text=text,CustomMinimumSize=new(0,38)};
        button.AddThemeStyleboxOverride("normal",Box(primary?"#a3e7c9":"#2c3c35",12));
        button.AddThemeStyleboxOverride("hover",Box(primary?"#c2f5df":"#40564a",12));
        button.AddThemeStyleboxOverride("pressed",Box(primary?"#8dcbb0":"#30493b",12));
        button.AddThemeStyleboxOverride("disabled",Box("#25302a",12));
        foreach(string state in new[]{"font_color","font_hover_color","font_pressed_color","font_focus_color"})
            button.AddThemeColorOverride(state,primary?new Color("#122a20"):Ink);
        return button;
    }

    internal void Conceal(){window.Hide();Reset();SetChoice("");}
    internal void Open()
    {
        if(window.Visible){if(window.Mode==Window.ModeEnum.Minimized)window.Mode=Window.ModeEnum.Windowed;window.GrabFocus();return;}
        window.Show();
        window.Size=stored.Size;window.Position=stored.Position;shown=true;
    }
    internal void TickGeometry()
    {
        if(!window.Visible || System.Environment.TickCount64<geometryCheck)return;
        geometryCheck=System.Environment.TickCount64+1000;SaveGeometry();
    }
    private void SaveGeometry()
    {
        if(!shown || !window.Visible || window.Mode!=Window.ModeEnum.Windowed)return;
        var rect=new Rect2I(window.Position,window.Size);
        if(rect==stored)return;
        var cfg=new ConfigFile();cfg.Load(LayoutFile);cfg.SetValue("window","rect",rect);
        if(cfg.Save(LayoutFile)==Error.Ok)stored=rect;
    }
    internal void SetChoice(string message){if(choiceHint.Text!=message)choiceHint.Text=message;choiceHint.Visible=message.Length>0;}
    internal void SetControl(string caption,bool enabled)
    {
        searchPolicy.Select(SearchCapacity.OpeningPercent==100?0:1);
        if(control.Text!=caption)control.Text=caption;
        control.Disabled=!enabled;
    }
    internal void SetStatus(string title,string detail)
    {
        status.Text=title;metrics.Text=detail;metrics.Visible=detail.Length>0;
    }
    internal void Reset()
    {
        mainPlan=guardPlan=displayPlan=null;playerView.Reset();playerView.Roster.Hide();actions.Show();mainRoute=mainSummary=guardRoute=guardSummary="";ShowingGuard=false;guardButton.Text="看保血起手";guardButton.Hide();
        lastRoute="";rounds.Clear();selector.Clear();selected=0;
        outcome.Hide();warning.Hide();navigation.Hide();actions.Clear();
        actions.PushColor(Muted);actions.AddText("开始求解后，这里会按回合列出当前可复算的路线。");actions.Pop();
        SetStatus("等待求解","");
    }
    internal void Invalidate()
    {
        SetStatus("盘面已变化",HasRoute?"下方是上一次的路线，可继续翻看。重新求解会读取当前盘面。":"等待当前盘面可以求解。");
    }
    internal void SetNotice(string text,string details="")
    {
        warning.Text=text;warning.TooltipText=details;
        warning.MouseFilter=details.Length>0?Control.MouseFilterEnum.Stop:Control.MouseFilterEnum.Ignore;
        warning.Visible=text.Length>0;
    }
    internal void SetGuard(string route,string summary,HudPlan? plan=null)
    {
        guardRoute=route;guardSummary=summary;guardPlan=plan;
        guardButton.Visible=guardRoute!=mainRoute && guardRoute.Length>0;
        if(ShowingGuard)DrawRoute(route,summary,plan);
    }
    internal void ClearGuard()
    {
        if(ShowingGuard && mainRoute.Length!=0)DrawRoute(mainRoute,mainSummary,mainPlan);
        ShowingGuard=false;guardRoute=guardSummary="";guardButton.Text="看保血起手";guardButton.Hide();
    }
    internal void SetRoute(string route,string? resultSummary=null,HudPlan? plan=null)
    {
        mainPlan=plan;mainRoute=route;mainSummary=resultSummary??route.Split('\n')[0];
        guardButton.Visible=guardRoute.Length!=0 && (ShowingGuard || guardRoute!=mainRoute);
        if(!ShowingGuard)DrawRoute(route,mainSummary,plan);
    }
    private void DrawRoute(string route,string? resultSummary,HudPlan? plan)
    {
        resultSummary=resultSummary?.Replace("模拟胜利","胜利").Replace("发现时模拟","发现时计算");
        if(route==lastRoute && ReferenceEquals(displayPlan,plan)){if(resultSummary!=null)outcome.Text=resultSummary;return;}
        int wanted=rounds.Count>0?rounds[selected].Number:-1;
        displayPlan=plan;playerView.Set(plan,plan?.Start??0,MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.NetId);
        playerView.Round.Hide();playerView.Roster.Visible=plan!=null;playerView.Scroll.Visible=plan!=null;selfFilter.Visible=plan?.Party.Length>1;actions.Visible=plan==null;
        lastRoute=route;rounds.Clear();selector.Clear();
        var lines=route.Split('\n');
        outcome.Text=resultSummary??lines[0].Replace("模拟找到胜线。","");outcome.Show();
        foreach(var line in lines.Skip(1))
        {
            var match=RoundHeading.Match(line);
            if(match.Success)
            {
                int number=int.Parse(match.Groups[1].Value);
                rounds.Add((number,match.Groups[2].Value,new List<string>()));selector.AddItem($"第 {number} 回合");
            }
            else if(rounds.Count>0 && line.Length>0)rounds[^1].Actions.Add(line);
        }
        navigation.Visible=HasRoute;
        if(HasRoute)SelectRound(Math.Max(0,rounds.FindIndex(r=>r.Number==wanted)));
    }
    internal string? SavedChoiceLine(int number,int seat,string title)
    {
        var turn=rounds.Find(r=>r.Number==number);
        if(turn.Actions==null)return null;
        string who=$"P{seat} ";
        var choices=turn.Actions.Where(s=>s.StartsWith(who,StringComparison.Ordinal) && s.Contains("〔P"))
            .Where(s=>s.Split('：').ElementAtOrDefault(1)?.Split(' ', '〔')[0]==title).Distinct().Take(2).ToArray();
        return choices.Length==1?choices[0]:null;
    }

    private void SelectRound(int index)
    {
        if(index<0 || index>=rounds.Count)return;
        selected=index;selector.Select(index);previous.Disabled=index==0;next.Disabled=index==rounds.Count-1;
        var round=rounds[index];playerView.SelectNumber(round.Number);playerView.Round.Hide();actions.Clear();
        if(round.Choices.Length>0){actions.PushColor(Mint);actions.AddText("回合开始 "+round.Choices+"\n");actions.Pop();}
        if(round.Actions.Count==0)actions.AddText("路线在本回合开始时结束。");
        int ordinal=rounds.Take(index).Sum(r=>r.Actions.Count(a=>a.StartsWith("P",StringComparison.Ordinal)));
        void FrameEnemy(string name)
        {
            actions.PushTable(1);actions.PushCell();actions.SetCellBorderColor(new Color("#ffe0a5"));
            actions.SetCellPadding(new Rect2(3,0,3,0));actions.AddText(name);actions.Pop();actions.Pop();
        }
        foreach(string line in round.Actions)
        {
            int split=line.IndexOf('：');
            if(split>1 && line[0]=='P')
            {
                int seat=line[1]-'1';
                actions.PushColor(displayPlan!=null && seat>=0 && seat<displayPlan.Party.Length?HudRouteView.SeatColor(displayPlan.Party[seat]):
                    seat>=0 && seat<Seats.Length?Seats[seat]:Mint);
                actions.AddText(line.Substring(0,split)+"  ");actions.Pop();
                var body=line.Substring(split+1);
                var kills=displayPlan?.Kills?.Where(k=>k.Step==ordinal).ToArray()??Array.Empty<HudKill>();ordinal++;
                actions.PushColor(Ink);
                var target=kills.FirstOrDefault(k=>k.IsTarget);
                int at=target==null?-1:body.IndexOf(" → "+target.Name,StringComparison.Ordinal);
                if(at<0)actions.AddText(body);
                else {actions.AddText(body[..(at+3)]);FrameEnemy(target!.Name);actions.AddText(body[(at+3+target.Name.Length)..]);}
                foreach(var k in kills)if(!k.IsTarget || at<0){actions.AddText("  ");FrameEnemy(k.Name);}
                actions.AddText("\n");actions.Pop();
            }
            else actions.AddText(line+"\n");
        }
        actions.ScrollToLine(0);
    }
}

