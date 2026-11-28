using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Godot;

namespace CoopSolver;

internal sealed class CombatHud
{
    private const string Layout="user://coop-solver-hud.cfg";
    private static readonly Color TextColor=new("#f4f3e9"), Dim=new("#b7c9bd"), Accent=new("#efcc8e");
    private readonly Control root=new(){Name="CoopSolverHUD",MouseFilter=Control.MouseFilterEnum.Ignore};
    private readonly PanelContainer bar=new(){Name="HudBar",MouseFilter=Control.MouseFilterEnum.Stop};
    private readonly HBoxContainer openingRow=new(){Name="HudOpeningBest",MouseFilter=Control.MouseFilterEnum.Ignore};
    private readonly HBoxContainer liveRow=new(){Name="HudLiveBest",MouseFilter=Control.MouseFilterEnum.Ignore};
    private sealed class LossFlash
    {
        internal readonly StyleBoxFlat[] Fields=new StyleBoxFlat[3];
        private long seen=-1,end;
        private bool worsening;
        private Color lastColor;
        internal void Update(long revision,bool visible,long now,bool increased=false)
        {
            if(visible && seen>=0 && revision>seen){end=now+1000;worsening=increased;}
            if(!visible || revision<seen)end=0;
            seen=visible?revision:-1;
            float elapsed=Math.Clamp(1-(end-now)/1000f,0,1);
            float alpha=.42f*(1-elapsed*elapsed*elapsed);
            Color color=worsening?new(.93f,.36f,.36f,alpha):new(.48f,.85f,.68f,alpha);
            if(color==lastColor)return;
            lastColor=color;
            foreach(var field in Fields)field.BgColor=color;
        }
    }
    private readonly LossFlash openingFlash=new(),liveFlash=new();
    private bool? previousController;

    private readonly Label[] openingNumbers=new Label[7],liveNumbers=new Label[7];
    private readonly HBoxContainer allocation=new(),resources=new();
    private readonly HSlider openingShare=new(){Name="HudOpeningShare",MinValue=0,MaxValue=100,Step=1};
    private readonly Label opacityValue=Caption("",Dim,true);
    private readonly RichTextLabel openingCaption,liveCaption;
    private int displayedOpeningShare=-1;
    private readonly Window? nativeWindow;
    private readonly HudCanvas canvas;
    private readonly SceneTree tree;
    private MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar? topBar;
    private long findTopBarAt;
    private readonly Font bodyFace,boldFace;
    private readonly PanelContainer tip=new(){Name="MetricDetail",Visible=false,MouseFilter=Control.MouseFilterEnum.Ignore};
    private readonly Label tipText=Caption("",TextColor,true);
    private readonly Dictionary<Control,(string Message,Label? Start,Label? Proof)> metricHints=new();
    private readonly PanelContainer settingsPanel=new(){Name="HudSettingsPanel",Visible=false};
    private readonly Button memoryPolicy=new(){Name="HudMemory",Flat=true};



    private readonly Panel pop=new(){Name="HudDetails",ClipContents=true};
    private readonly Dictionary<Label,List<Label>> seatCells=new();
    private readonly Dictionary<Label,Label> proofSlots=new();
    private readonly Label connectionStatus=Caption("",Dim,small:true);
    private readonly HSeparator beforeActions=ActionDivider(),afterActions=ActionDivider();
    private static HSeparator ActionDivider()
    {
        var line=new HSeparator{CustomMinimumSize=new(0,5)};
        line.AddThemeStyleboxOverride("separator",new StyleBoxLine{Thickness=1,Color=new Color("#9aafb83e")});
        return line;
    }
    private readonly RouteOutcomeView outcome=new();
    private readonly SearchProgressView progress;
    private readonly Action? historyChanged;
    private bool chartsOpen;
    private float chartWidth,chartHeaderHeight;
    private Vector2 backSize;
    internal bool WantsHistory {get;private set;}
    private void RequestHistory(bool value)
    {
        if(WantsHistory==value)return;
        WantsHistory=value;historyChanged?.Invoke();
    }
    private VBoxContainer roster=>route.Roster;
    private readonly HudRouteView route=new();
    private readonly HudDependencyView graph=new();
    private readonly Action<int?>? requestGraph;
    private bool graphPreferenceKnown;
    private int? lastGraphRound;
    private readonly VBoxContainer col=new();
    private readonly HBoxContainer head=new();
    private readonly SearchWarnings warnings=new();
    private readonly HBoxContainer actionRow=new(){Name="HudActions",Alignment=BoxContainer.AlignmentMode.End};
    private readonly HBoxContainer settingsActions=new(){Name="HudSettingsActions"};
    private readonly Button mode=new(){Name="HudMode",Flat=true,FocusMode=Control.FocusModeEnum.None,IconAlignment=HorizontalAlignment.Right};
    private readonly InlineMenus menus;
    private readonly Button openingPots=new(){Name="HudOpeningPotions",Flat=true},livePots=new(){Name="HudLivePotions",Flat=true};
    private readonly Button autoplay,restart,solve,less,more,windowButton,settings,sourceButton,broadcastButton;
    private bool broadcastEnabled;
    private readonly Button hideResults,ownOnly,prevRound,nextRound;
    private bool soloPlayback,multiplayer;

    private readonly Label cpuValue=Caption("",Dim,small:true);
    private readonly TextureRect indicator=new(){MouseFilter=Control.MouseFilterEnum.Ignore,StretchMode=TextureRect.StretchModeEnum.KeepCentered};
    private readonly StyleBoxFlat panelStyle,shell;
    private bool pinned,held,ignoreHover,options;
    private float opacity=.75f;
    private readonly HSlider opacitySlider=new(){Name="HudOpacity",MinValue=50,MaxValue=100,Step=1,CustomMinimumSize=new(78,20)};
    private readonly Label opacityCaption=Caption("",TextColor,small:true);
    private bool adjustingSlider;
    private Vector2 pressAt;
    private HudPlan? lastDetail;
    private HudGraph? lastDetailGraph;
    private HudExecuted? lastDetailExecuted;
    private bool detailInitialized,lastDetailLive;
    private HudFrame? last;
    private HudBest? stableLiveNumbers,displayedLiveNumbers;
    internal void ResetLiveDisplay()
    {
        displayedLiveNumbers=stableLiveNumbers=null;last=null;lastDetail=null;lastDetailGraph=null;lastDetailExecuted=null;detailInitialized=false;
        route.Reset();graph.Clear();outcome.Set(null);graphPreferenceKnown=false;
        chartsOpen=false;outcome.Button.SetPressedNoSignal(false);progress.HideTip();progress.Set(null,null);RequestHistory(false);
    }
    internal void SuspendGraphRequest()
    {
        if(lastGraphRound==null)return;
        lastGraphRound=null;graphPreferenceKnown=false;requestGraph?.Invoke(null);
    }
    private int currentCombatRound;
    private const float DetailsWidth=475,PanelGap=9;
    // Keep the pin in the same viewport-relative position as numbers and plans change.
    // Reserve room to its right for a four-seat strip; this is not the rendered panel width.
    private const float PinRightMargin=720;
    private Vector2 PinTravel=>(root.Size-new Vector2(PinRightMargin,68)).Max(Vector2.Zero);
    private int fontSize=17;
    private Vector2? anchor;
    private Vector2 dragDelta;
    private bool dragging;
    private ulong localId;
    internal bool Visible=>root.Visible;
    internal bool Expanded=>pop.Visible;
    internal CombatHud(SceneTree tree,Action start,Action switchMode,Action window,Action hide,Action<bool>? selectLocal=null,Action? restartSearch=null,Action<bool,PotionCost?>? choosePotion=null,Action? playRoute=null,Window? nativeWindow=null,Action<bool>? setBroadcast=null,Action<int?>? requestGraph=null,Action? historyChanged=null)
    {
        this.requestGraph=requestGraph;this.historyChanged=historyChanged;
        this.nativeWindow=nativeWindow;this.tree=tree;
        var config=new ConfigFile();
        if(config.Load(Layout)==Error.Ok)
        {fontSize=Math.Clamp(config.GetValue("hud","font",17).AsInt32(),11,30);var savedAnchor=config.GetValue("hud","anchor",new Vector2(.98f,.12f)).AsVector2();
            if(!savedAnchor.IsEqualApprox(new(.98f,.12f)))anchor=savedAnchor.Clamp(Vector2.Zero,Vector2.One);pinned=config.GetValue("hud","pinned",false).AsBool();float legacy=config.GetValue("hud","opacity",.68f).AsSingle();
            int initial=Mathf.IsEqualApprox(legacy,.68f)?75:Mathf.RoundToInt(legacy*100);
            opacity=Math.Clamp(config.GetValue("hud","opacity_percent",initial).AsInt32(),50,100)/100f;}
        canvas=new HudCanvas(nativeWindow??tree.Root,root,bar,pop);root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        bodyFace=HudTypography.Face();boldFace=HudTypography.Face(true);
        root.Theme=HudTypography.Theme(bodyFace);root.Hide();root.AddChild(bar);
        shell=Box("#101c23ce",7,2);
        shell.SetCornerRadiusAll(7);shell.SetBorderWidthAll(1);shell.BorderColor=new("#839a9c66");
        bar.AddThemeStyleboxOverride("panel",shell);
        var band=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
        band.AddThemeConstantOverride("separation",5);bar.AddChild(band);
        var rows=new VBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
        rows.AddThemeConstantOverride("separation",1);band.AddChild(rows);
        rows.AddChild(openingRow);rows.AddChild(liveRow);
        BuildRow(openingRow,openingNumbers,openingFlash,Ui.Text("ui.013"));
        BuildRow(liveRow,liveNumbers,liveFlash,Ui.Text("ui.014"));
        var pinPanel=new PanelContainer{Name="HudPin",MouseFilter=Control.MouseFilterEnum.Ignore};
        pinPanel.AddThemeStyleboxOverride("panel",Box("#00000000",4,1));
        indicator.Texture=HudIcons.Pin;indicator.CustomMinimumSize=new(24,0);
        pinPanel.AddChild(indicator);band.AddChild(pinPanel);band.MoveChild(pinPanel,0);
        var ruleInk=new Color("#9aafb867");
        band.Draw+=()=>HudTypography.DrawVerticalRule(band,
            (pinPanel.GetRect().End.X+rows.Position.X)/2,band.Size.Y,ruleInk);
        band.SortChildren+=band.QueueRedraw;
        rows.Draw+=()=>{
            var first=openingRow.Visible?openingRow:liveRow;
            var totals=first.GetChild<Control>(4);
            if(totals.Visible)HudTypography.DrawVerticalRule(rows,totals.Position.X+2,rows.Size.Y,ruleInk);
        };
        openingRow.SortChildren+=rows.QueueRedraw;liveRow.SortChildren+=rows.QueueRedraw;
        if(nativeWindow==null)bar.GuiInput+=BarInput;
        else
        {
            pinPanel.GuiInput+=e=>{
                if(e is InputEventMouseButton {ButtonIndex:MouseButton.Left,Pressed:false})
                {
                    nativeWindow.AlwaysOnTop=!nativeWindow.AlwaysOnTop;
                    var cfg=new ConfigFile();cfg.Load("user://coop-solver-window.cfg");
                    cfg.SetValue("window","always_on_top",nativeWindow.AlwaysOnTop);cfg.Save("user://coop-solver-window.cfg");
                }
            };
            pinPanel.MouseFilter=Control.MouseFilterEnum.Stop;pinPanel.MouseDefaultCursorShape=Control.CursorShape.PointingHand;
        }
        root.AddChild(pop);panelStyle=Framed("#111c28","#657985",8,5);ApplyOpacity();
        progress=new SearchProgressView(root,()=>panelStyle.BgColor);
        pop.AddThemeStyleboxOverride("panel",panelStyle);pop.Hide();
        // The layout pass owns the shell rectangle. A queued minimum-size update
        // from new content must not resize the shell between two layout passes.
        pop.AddChild(col);col.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        col.OffsetLeft=panelStyle.GetMargin(Side.Left);col.OffsetTop=panelStyle.GetMargin(Side.Top);
        col.OffsetRight=-panelStyle.GetMargin(Side.Right);col.OffsetBottom=-panelStyle.GetMargin(Side.Bottom);
        col.AddThemeConstantOverride("separation",3);
        col.AddChild(head);
        solve=Btn("HudSolve","",start,head);solve.Icon=HudIcons.Start;solve.IconAlignment=HorizontalAlignment.Center;solve.CustomMinimumSize=new(30,HudTypography.ControlHeight);
        head.AddChild(mode);
        menus=new InlineMenus(root);
        void BindPots(Button button,bool live)
        {
            HudBest? Row()=>live?displayedLiveNumbers:last?.Opening;
            var row=live?liveRow:openingRow;
            menus.BindOptions(button,row.GetChild<Control>(1),row.GetChild<Control>(2),bar,()=>Array.FindIndex(Row()?.Options??Array.Empty<PotionCost>(),o=>o.Key==Row()?.PotionKey),
                item=>{pinned=true;choosePotion?.Invoke(live,Row()?.Options?.FirstOrDefault(o=>o.Key==item.Key));});
        }
        BindPots(openingPots,false);BindPots(livePots,true);
        menus.Bind(mode,"HudViewMenu",new InlineMenus.Item[]{
            new(0,Ui.Text("ui.015"),"HudViewOpening"),new(1,Ui.Text("ui.016"),"HudViewLive")},
            ()=>last?.Live==true?1:0,id=>{if(last!=null && (id==1)!=last.Live)switchMode();});
        var roundSlot=new Control{CustomMinimumSize=new(114,30),MouseFilter=Control.MouseFilterEnum.Ignore};
        head.AddChild(roundSlot);roundSlot.AddChild(route.Round);route.Round.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        prevRound=Btn("HudPrevRound","",()=>route.MoveRound(-1),head);prevRound.Icon=HudIcons.Previous;prevRound.IconAlignment=HorizontalAlignment.Center;
        nextRound=Btn("HudNextRound","",()=>route.MoveRound(1),head);nextRound.Icon=HudIcons.Next;nextRound.IconAlignment=HorizontalAlignment.Center;
        prevRound.CustomMinimumSize=nextRound.CustomMinimumSize=new(30,HudTypography.ControlHeight);
        route.Picked+=_=>{
            if(lastDetail?.Party.Length<=1)return;
            lastDetailGraph=null;
            graph.Panel.Hide();
            graph.Clear();
            route.Scroll.Hide();
        };
        head.AddChild(outcome.Container);outcome.Container.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        outcome.Container.Alignment=BoxContainer.AlignmentMode.End;
        outcome.Button.Pressed+=()=>{
            chartsOpen=outcome.Button.ButtonPressed;
            if(chartsOpen)
            {
                chartWidth=pop.Size.X;
                chartHeaderHeight=head.Size.Y;
                backSize=outcome.Button.Size;
            }
            route.CloseOverview();
            RequestHistory(chartsOpen);
        };
        autoplay=Btn("HudAutoplay",Ui.Text("ui.017"),()=>playRoute?.Invoke(),actionRow,HudIcons.Play);
        autoplay.Hide();
        ownOnly=Btn("HudOwnOnly",Ui.Text("ui.018"),()=>{route.Filter(!route.OwnOnly);Save(filter:true);},actionRow,HudIcons.Filter);
        ownOnly.ToggleMode=true;
        route.Filter(config.GetValue("hud","own_only",false).AsBool());
        hideResults=Btn("HudHide",Ui.Text("ui.019"),hide,actionRow,HudIcons.EyeClosed);
        col.AddChild(roster);
        col.AddChild(route.Overview.Panel);
        col.AddChild(connectionStatus);col.AddChild(warnings.Panel);
        col.AddChild(beforeActions);col.AddChild(route.Scroll);col.AddChild(graph.Panel);col.AddChild(progress.Panel);
        col.AddChild(afterActions);col.AddChild(actionRow);
        settings=Btn("HudOptions",Ui.Text("ui.025"),()=>options=!options,actionRow,HudIcons.Settings);settings.ToggleMode=true;
        col.AddChild(settingsPanel);settingsPanel.AddThemeStyleboxOverride("panel",HudSurface.Box("#242c3240","#839da755",7,3));
        var optionsList=new VBoxContainer();optionsList.AddThemeConstantOverride("separation",2);
        settingsPanel.AddChild(optionsList);optionsList.AddChild(settingsActions);
        optionsList.AddChild(resources);optionsList.AddChild(allocation);
        sourceButton=new Button{Name="HudSource"};
        settingsActions.AddChild(sourceButton);sourceButton.AddThemeFontSizeOverride("font_size",HudTypography.Detail);
        menus.Bind(sourceButton,"HudSourceMenu",new InlineMenus.Item[]{
            new(0,Ui.Text("ui.020"),"HudSourceHost"),
            new(1,Ui.Text("ui.021"),"HudSourceLocal")},
            ()=>previousController==true?1:0,id=>{
                bool local=id==1;
                if(local!=previousController)selectLocal?.Invoke(local);
            });
        broadcastButton=new Button{Name="HudBroadcast"};
        settingsActions.AddChild(broadcastButton);broadcastButton.AddThemeFontSizeOverride("font_size",HudTypography.Detail);
        menus.Bind(broadcastButton,"HudBroadcastMenu",new InlineMenus.Item[]{
            new(1,Ui.Text("ui.022"),"HudBroadcastOn"),
            new(0,Ui.Text("ui.023"),"HudBroadcastOff")},
            ()=>broadcastEnabled?1:0,id=>setBroadcast?.Invoke(id==1));
        restart=Btn("HudRestart",Ui.Text("ui.026"),()=>restartSearch?.Invoke(),settingsActions,HudIcons.Restart);
        windowButton=Btn("HudWindow",Ui.Text("ui.024"),window,settingsActions,HudIcons.External);
        foreach(var row in new[]{actionRow,settingsActions})
        {
            row.AddThemeConstantOverride("separation",4);
            foreach(var button in row.GetChildren().OfType<Button>())button.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        }
        allocation.AddChild(Caption(Ui.Text("ui.027"),TextColor,true));
        opacityCaption.Text=Ui.Text("ui.028");

        openingCaption=AllocationCaption(Ui.Text("ui.029"));
        liveCaption=AllocationCaption(Ui.Text("ui.030"));
        allocation.AddChild(openingCaption);allocation.AddChild(openingShare);allocation.AddChild(liveCaption);
        StyleSlider(openingShare);
        openingShare.ValueChanged+=value=>SearchCapacity.SetOpeningPercent((int)value,persist:!adjustingSlider);
        openingShare.DragStarted+=()=>adjustingSlider=true;
        openingShare.DragEnded+=changed=>{adjustingSlider=false;if(changed)SearchCapacity.SetOpeningPercent((int)openingShare.Value);};
        resources.AddChild(Caption(Ui.Text("ui.031"),TextColor,true));resources.AddChild(memoryPolicy);
        memoryPolicy.AddThemeFontSizeOverride("font_size",HudTypography.Detail);
        memoryPolicy.AddThemeColorOverride("font_color",Dim);
        SearchCapacity.BindMemory(menus,memoryPolicy);
        resources.AddSpacer(false);

        var cpuGroup=new HBoxContainer();
        resources.AddChild(cpuGroup);
        cpuGroup.AddChild(Caption("CPU",TextColor,true));cpuGroup.AddChild(cpuValue);
        less=Btn("HudCpuLess","",()=>SearchCapacity.SetAndSave(SearchCapacity.Limit-1),cpuGroup);
        more=Btn("HudCpuMore","",()=>SearchCapacity.SetAndSave(SearchCapacity.Limit+1),cpuGroup);
        less.Icon=HudIcons.Minus;more.Icon=HudIcons.Plus;
        less.IconAlignment=more.IconAlignment=HorizontalAlignment.Center;
        less.CustomMinimumSize=more.CustomMinimumSize=new(HudTypography.ControlHeight,HudTypography.ControlHeight);
        var opacityGroup=new HBoxContainer();optionsList.AddChild(opacityGroup);
        opacityGroup.AddChild(opacityCaption);opacityGroup.AddChild(opacityValue);
        ReserveCaption(opacityValue,"100%");
        foreach(var row in new[]{allocation,resources,cpuGroup,opacityGroup})
            row.AddThemeConstantOverride("separation",4);
        opacityGroup.AddChild(opacitySlider);StyleSlider(opacitySlider);
        tip.AddThemeStyleboxOverride("panel",HudSurface.Box("#101c23ff","#839a9c88",7,3));
        tip.AddChild(tipText);root.AddChild(tip);
        opacitySlider.SetValueNoSignal(Math.Round(opacity*100));
        opacitySlider.DragStarted+=()=>adjustingSlider=true;
        opacitySlider.DragEnded+=changed=>{adjustingSlider=false;if(changed)Save(tone:true);};
        opacitySlider.ValueChanged+=value=>{
            opacity=(float)value/100;ApplyOpacity();if(!adjustingSlider)Save(tone:true);
        };
    }
    private static void AlignFooter(HBoxContainer row)
    {
        float width=0;
        foreach(Button button in row.GetChildren())
            if(button.Visible)width=Math.Max(width,button.GetMinimumSize().X);
        foreach(Button button in row.GetChildren())
        {
            var size=new Vector2(width,button.CustomMinimumSize.Y);
            if(button.Visible && button.CustomMinimumSize!=size)button.CustomMinimumSize=size;
        }
    }

    private static void ReserveCaption(Label label,string widest)
    {
        var font=HudTypography.Face();
        label.CustomMinimumSize=new(Mathf.Ceil(font.GetStringSize(widest,fontSize:HudTypography.Detail).X),0);
    }
    private RichTextLabel AllocationCaption(string widest)
    {
        var label=new RichTextLabel
        {
            FitContent=true,ScrollActive=false,AutowrapMode=TextServer.AutowrapMode.Off,
            MouseFilter=Control.MouseFilterEnum.Ignore,SizeFlagsVertical=Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize=new(Mathf.Ceil(bodyFace.GetStringSize(widest,fontSize:HudTypography.Detail).X),0)
        };
        label.AddThemeFontOverride("normal_font",bodyFace);
        label.AddThemeFontSizeOverride("normal_font_size",HudTypography.Detail);
        label.AddThemeColorOverride("default_color",TextColor);
        label.AddThemeColorOverride("font_shadow_color",new Color("#100e0bc4"));
        label.AddThemeConstantOverride("shadow_offset_x",2);
        label.AddThemeConstantOverride("shadow_offset_y",1);
        return label;
    }
    private static void SetAllocationCaption(RichTextLabel label,string text)
    {
        var percentage=Regex.Match(text,@"%\s*[0-9]+|[0-9]+\s*%");
        label.Clear();
        label.AddText(text[..percentage.Index]);
        label.PushColor(Dim);
        label.AddText(percentage.Value);
        label.Pop();
        label.AddText(text[(percentage.Index+percentage.Length)..]);
    }
    private static void StyleSlider(HSlider slider)
    {
        slider.SizeFlagsVertical=Control.SizeFlags.ShrinkCenter;
        slider.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        slider.CustomMinimumSize=new(65,18);
        slider.AddThemeIconOverride("grabber",HudIcons.Thumb);slider.AddThemeIconOverride("grabber_highlight",HudIcons.Thumb);
        foreach(var (state,color) in new[]{("slider","#79919cab"),("grabber_area","#92bfb5e8"),("grabber_area_highlight","#afd2c9ff")})
        {
            var track=Box(color,0,2);track.SetCornerRadiusAll(2);
            slider.AddThemeStyleboxOverride(state,track);
        }
    }
    internal void SetPlayback(bool solo,bool active,bool available)
    {
        soloPlayback=solo;
        autoplay.Visible=soloPlayback && !multiplayer;autoplay.Disabled=!active && !available;
        autoplay.Text=active?Ui.Text("ui.032"):Ui.Text("ui.033");
        autoplay.Icon=active?HudIcons.Stop:HudIcons.Play;
    }
    private void ApplyOpacity()
    {
        panelStyle.BgColor=new Color(.054f,.083f,.11f,opacity);
        shell.BgColor=new Color(.063f,.11f,.137f,opacity);
        HudSurface.SetOpacity(opacity);
        opacityValue.Text=Math.Round(opacity*100)+"%";
    }
    internal static Label Caption(string text,Color color,bool small=false)
    {
        var label=new Label{Text=Ui.Punctuation(text),MouseFilter=Control.MouseFilterEnum.Ignore};
        label.CustomMinimumSize=new(0,small?HudTypography.DetailHeight:HudTypography.BodyHeight);label.VerticalAlignment=VerticalAlignment.Center;
        label.AddThemeColorOverride("font_color",color);
        label.AddThemeFontSizeOverride("font_size",small?HudTypography.Detail:HudTypography.Body);
        return label;
    }
    private static Button Btn(string name,string text,Action act,Control parent,Texture2D? icon=null)
    {
        var b=new Button{Name=name,Text=text,Icon=icon,IconAlignment=HorizontalAlignment.Right,Flat=true,FocusMode=Control.FocusModeEnum.None};
        b.AddThemeFontSizeOverride("font_size",HudTypography.Detail);StyleButton(b);
        b.ClipText=false;b.TextOverrunBehavior=TextServer.OverrunBehavior.NoTrimming;
        b.Pressed+=act;parent.AddChild(b);return b;
    }
    internal static StyleBoxFlat Framed(string fill,string border,int x,int y)
    {
        var box=Box(fill,x,y);box.SetBorderWidthAll(1);box.BorderColor=new(border);return box;
    }
    internal static void StyleButton(Button button,bool compact=false)
    {
        int x=compact?3:6,y=1;
        button.Flat=false;button.CustomMinimumSize=new(button.CustomMinimumSize.X,compact?20:HudTypography.ControlHeight);
        button.AddThemeStyleboxOverride("normal",HudSurface.Box("#80979b25","#93a9b087",x,y));
        button.AddThemeStyleboxOverride("hover",HudSurface.Box("#91b4bc48","#aec6cdbe",x,y));
        var selected=HudSurface.Box("#72bba758","#a4d2c3c4",x,y);
        button.AddThemeStyleboxOverride("pressed",selected);button.AddThemeStyleboxOverride("hover_pressed",selected);
        button.AddThemeStyleboxOverride("disabled",HudSurface.Box("#5d6c7318","#82939855",x,y));
    }
    private static StyleBoxFlat Stroke()
    {
        var s=Box("#28201a36",4,0);s.SetBorderWidthAll(1);s.BorderColor=new("#ffe0a5");s.SetCornerRadiusAll(3);return s;
    }
    internal static PanelContainer Outline(Label label,string tip)
    {
        var p=new PanelContainer{Name="HudKill",MouseFilter=Control.MouseFilterEnum.Pass};
        p.AddThemeStyleboxOverride("panel",Stroke());p.AddChild(label);return p;
    }
    internal static StyleBoxFlat Box(string color,int x,int y)
    {
        var s=new StyleBoxFlat{BgColor=new Color(color),AntiAliasing=true,AntiAliasingSize=1.0f,CornerDetail=12};s.SetCornerRadiusAll(5);
        s.SetContentMargin(Side.Left,x);s.SetContentMargin(Side.Right,x);s.SetContentMargin(Side.Top,y);s.SetContentMargin(Side.Bottom,y);return s;
    }
    private void Save(bool filter=false,bool tone=false)
    {
        var cfg=new ConfigFile();cfg.Load(Layout);
        if(filter)cfg.SetValue("hud","own_only",route.OwnOnly);
        else if(tone)cfg.SetValue("hud","opacity_percent",Mathf.RoundToInt(opacity*100));
        else
        {
            cfg.SetValue("hud","font",fontSize);
            if(anchor is {} preferred)cfg.SetValue("hud","anchor",preferred);
            cfg.SetValue("hud","pinned",pinned);
        }
        cfg.Save(Layout);
    }
    private Vector2 DefaultAnchor()
    {
        long now=System.Environment.TickCount64;
        if((topBar==null || !GodotObject.IsInstanceValid(topBar) || !topBar.IsVisibleInTree()) && now>=findTopBarAt)
        {
            findTopBarAt=now+1000;
            topBar=tree.Root.FindChildren("*","",true,false)
                .OfType<MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar>().FirstOrDefault(node=>node.IsVisibleInTree());
        }
        if(topBar!=null && GodotObject.IsInstanceValid(topBar) && topBar.BossIcon is { } boss && boss.IsVisibleInTree())
        {
            var rect=root.GetGlobalTransform().AffineInverse()*boss.GetGlobalRect();
            return new(rect.End.X+12,Math.Max(0,rect.GetCenter().Y-bar.Size.Y/2));
        }
        return new(root.Size.X*.444f,9);
    }

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
                    if(!dragging){pinned=!pinned;ignoreHover=!pinned;pop.Visible=pinned;}
                    held=dragging=false;Save();
                }
                bar.AcceptEvent();
            }
        }
        if(e is InputEventMouseMotion motion && held)
        {
            if((motion.GlobalPosition/root.Scale).DistanceTo(pressAt)>=5)dragging=true;
            if(!dragging)return;
            var area=PinTravel.Max(Vector2.One);
            anchor=((motion.GlobalPosition/root.Scale-dragDelta)/area).Clamp(Vector2.Zero,Vector2.One);bar.AcceptEvent();
        }
    }
    private void BuildRow(HBoxContainer container,Label[] numbers,LossFlash flash,string title)
    {
        container.AddThemeConstantOverride("separation",2);
        for(int i=0;i<numbers.Length;i++)
        {
            var box=new PanelContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
            var field=Box("#00000000",4,0);
            if(i is >=1 and <=3)
            {
                flash.Fields[i-1]=field;
                if(i<3){field.ExpandMarginRight=2;field.CornerRadiusTopRight=field.CornerRadiusBottomRight=0;}
                if(i>1)field.CornerRadiusTopLeft=field.CornerRadiusBottomLeft=0;
            }
            if(i==4)field.SetContentMargin(Side.Left,10);
            box.AddThemeStyleboxOverride("panel",field);container.AddChild(box);
            var label=Caption(i==0?title:"",TextColor);
            numbers[i]=label;
            if(i>=4)
            {
                // Keep the caption beside its value. Spare space belongs at the end of the cell.
                var strip=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
                strip.AddThemeConstantOverride("separation",HudTypography.FieldGap);box.AddChild(strip);
                label.HorizontalAlignment=HorizontalAlignment.Right;
                Label? marker=null,caption=null;
                if(i==4)
                {
                    marker=Caption("",Dim,small:true);
                    marker.Name="LossProof";marker.MouseFilter=Control.MouseFilterEnum.Pass;
                    marker.Hide();strip.AddChild(marker);proofSlots.Add(numbers[0],marker);
                }
                if(i<6)
                {
                    caption=Caption(i==4?Ui.Text("ui.034"):Ui.Text("ui.035"),Dim,small:true);
                    strip.AddChild(caption);
                }
                strip.AddChild(label);
                if(i==6)
                {
                    label.HorizontalAlignment=HorizontalAlignment.Right;
                    strip.AddChild(Caption(Ui.Text("ui.036"),Dim,small:true));
                }
                metricHints[strip]=(i==4?Ui.Text("ui.037"):i==5?Ui.Text("ui.038"):Ui.Text("ui.039"),i==4?caption:null,marker);
            }
            else if(i==1)
            {
                var strip=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};strip.AddThemeConstantOverride("separation",3);
                box.AddChild(strip);strip.AddChild(label);seatCells.Add(label,new());
            }
            else if(i==2)
            {
                var strip=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};strip.AddThemeConstantOverride("separation",3);
                box.AddChild(strip);strip.AddChild(label);seatCells.Add(label,new());strip.AddChild(container==openingRow?openingPots:livePots);
            }
            else box.AddChild(label);
        }
    }
    private void FormatRow(Label[] row,HudBest? best,string name,bool hidden,int? round=null)
    {
        row[0].Text=name;
        // Older peers may include a preview endpoint even with no terminal outcome.
        if(best!=null && best.Outcome is not ("胜" or "败"))
            best=best with {Outcome="",End=null,Party=Array.Empty<HudSeat>(),Discovered=null,MinimumLossProven=false,ZeroLossProven=false,ProofPercent=null};
        var marker=proofSlots[row[0]];
        bool proven=!hidden && best is {MinimumLossProven:true} or {ZeroLossProven:true};
        int? percent=hidden?null:best?.ProofPercent;
        double? coverage=!hidden && best?.Outcome=="胜"?best.ProofCoverage:null;
        string checkedText=coverage switch
        {
            null or <=0.001=>"",
            _=>(Math.Min(999,Math.Floor(coverage.Value*1000))/10).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"%"
        };
        marker.Text=proven?"✓":percent is >0 and <=99?$"≈{percent}%":percent==0?"":checkedText;
        marker.AddThemeColorOverride("font_color",proven?new Color("#a6edbd"):Dim);
        row[4].Text=best==null?Ui.Missing:Advisor.Compact(best.Visits);
        row[5].Text=string.IsNullOrEmpty(best?.Discovered)?Ui.Missing:best.Discovered;
        row[6].Text=Advisor.Compact(best?.Rate??0);
        for(int i=1;i<4;i++)
            ((Control)(i==1?row[i].GetParent().GetParent():row[i].GetParent())).Visible=!hidden;
        var button=row==openingNumbers?openingPots:livePots;
        var options=best?.Options??Array.Empty<PotionCost>();
        button.Visible=!hidden && options.Length>0;
        if(button.Visible)
        {
            var items=options.Select((o,i)=>{
                var seats=best!.Party;
                InlineMenus.Value Chip(string value,int i)=>new(value,i<0 || seats.Length<2?null:HudRouteView.SeatColor(seats[i]).ToHtml(false),
                    i>=0 && seats.Length>1 && seats[i].Id==localId,
                    Prefix:seats.Length<2?null:i>=0?$"P{i+1}":Ui.Text("ui.040"));
                var losses=new List<InlineMenus.Value>();
                if(o.EnemyHp is int hp)losses.Add(new(Ui.Text("ui.041",hp)));
                else
                {
                    losses.Add(new(Ui.Text("ui.042"),Ink:TextColor,Padding:0));
                    if(o.SeatLoss is {} perSeat && perSeat.Length==seats.Length && seats.Length>0)
                        for(int seat=0;seat<seats.Length;seat++)
                        {
                            int loss=perSeat[seat];
                            losses.Add(new(loss.ToString(),seats.Length>1?HudRouteView.SeatColor(seats[seat]).ToHtml(false):null,
                                seats.Length>1 && seats[seat].Id==localId,HudTypography.LossInk(loss,HudTypography.Ink),Padding:6));
                        }
                    else losses.Add(new(o.Loss.ToString(),Ink:HudTypography.LossInk(o.Loss,HudTypography.Ink)));
                }
                var drugs=new List<InlineMenus.Value>();
                string BottleName(IGrouping<string,SpentPotion> group)
                {
                    var name=MegaCrit.Sts2.Core.Models.ModelDb.GetByIdOrNull<MegaCrit.Sts2.Core.Models.PotionModel>(
                        new MegaCrit.Sts2.Core.Models.ModelId("POTION",group.Key.Length==0?"UNKNOWN":group.Key))?.Title.GetFormattedText()??group.Key;
                    return (string.IsNullOrEmpty(name)?Ui.Text("ui.043"):name)+(group.Count()>1?" ×"+group.Count():"");
                }
                var byOwner=o.Bottles.ToLookup(b=>b.Id.Length==0?-1:Array.FindIndex(seats,s=>s.Id==b.Seat));
                foreach(int owner in Enumerable.Range(0,seats.Length).Append(-1))
                {
                    var names=byOwner[owner].GroupBy(b=>b.Id).Select(BottleName).ToArray();
                    if(names.Length==0)continue;
                    var text=string.Join(Ui.Text("ui.044"),names);
                    drugs.Add(Chip(text,owner));
                }
                if(drugs.Count==0)drugs.Add(new(Ui.Text("ui.045")));
                if(o.Goal!=null)drugs.Add(new(o.Goal.Label()));
                if(o.VictoryReturn>0)drugs.Add(new(Ui.Text("ui.046",o.VictoryReturn)));
                return new InlineMenus.Item(i,"",Key:o.Key,Left:losses.ToArray(),Right:drugs.ToArray());
            }).OrderBy(item=>options[item.Id].EnemyHp??options[item.Id].Loss).Prepend(new(-1,options[0].EnemyHp.HasValue?Ui.Text("ui.047"):Ui.Text("ui.048"),row==openingNumbers?"HudPotionAutoOpening":"HudPotionAutoLive",
                Left:null,Right:null)).ToArray();
            menus.Items(button,items);
        }
        if(hidden)return;
        var party=best?.Party??Array.Empty<HudSeat>();
        row[1].Text=party.Length==0?Ui.Text("ui.049"):Ui.Text("ui.050");
        SetSeatValues(row[1],party,false);
        row[2].Text=party.Length==0?Ui.Text("ui.051"):Ui.Text("ui.052");
        SetSeatValues(row[2],party,true);
        row[3].Text=Ui.Text("route.turn_ratio",round??currentCombatRound,best?.End?.ToString(Ui.Culture)??Ui.Missing)
            +" "+(best?.Goal is {Kind:CombatGoal.Training,Escaped:true}?Ui.Text("ui.054"):Ui.Outcome(best?.Outcome));
    }
    private void SetSeatValues(Label heading,HudSeat[] party,bool potions)
    {
        var labels=seatCells[heading];
        var strip=heading.GetParent<HBoxContainer>();
        for(int i=0;i<Math.Max(labels.Count,party.Length);i++)
        {
            if(i==labels.Count)
            {
                var cell=Caption("",TextColor);cell.HorizontalAlignment=HorizontalAlignment.Center;
                var panel=new PanelContainer{MouseFilter=Control.MouseFilterEnum.Ignore,SizeFlagsVertical=Control.SizeFlags.ShrinkCenter};
                strip.AddChild(panel);strip.MoveChild(panel,i+1);panel.AddChild(cell);labels.Add(cell);
            }
            var value=labels[i];var wrap=value.GetParent<PanelContainer>();
            wrap.Visible=i<party.Length;
            if(!wrap.Visible)continue;
            var seat=party[i];value.Text=(potions?seat.Potions:seat.ToEnd)?.ToString()??Ui.Missing;
            bool lethal=!potions && seat.DiesAtEnd;
            wrap.AddThemeStyleboxOverride("panel",party.Length>1?HudRouteView.SeatBox(seat,6,0,lethal):
                HudSurface.Box("#00000000",lethal?"#ffe0a5":"",6,0));
            value.AddThemeFontOverride("font",bodyFace);
            var ink=potions?HudTypography.Ink:HudTypography.LossInk(seat.ToEnd,HudTypography.Ink);
            value.AddThemeColorOverride("font_color",ink);
            SeatUnderline(wrap,party.Length>1 && seat.Id==localId,ink);
        }
    }
    internal static void SeatUnderline(PanelContainer box,bool local,Color ink)
    {
        const string key="coop_local_underline";
        if(!box.HasMeta(key))
        {
            if(!local)return;
            box.Draw+=()=>{
                if(!box.GetMeta(key).AsBool())return;
                float inset=box.GetThemeStylebox("panel") is StyleBoxFlat style?
                    Math.Max(style.CornerRadiusBottomLeft,style.CornerRadiusBottomRight):4;
                if(box.Size.X>2*inset)
                    box.DrawLine(new(inset,box.Size.Y-1),new(box.Size.X-inset,box.Size.Y-1),
                        box.GetMeta("coop_local_edge_ink").AsColor(),1);
            };
        }
        box.SetMeta(key,local);box.SetMeta("coop_local_edge_ink",ink);box.QueueRedraw();
    }
    private void Format(HudFrame f)
    {
        var single=f.State=="等待房主共享"?null:new HudBest(f.Visits,f.Pv,f.Rate,f.Round,f.Plan?.End,f.Plan?.Party??Array.Empty<HudSeat>(),f.Plan?.Outcome??"");
        FormatRow(openingNumbers,f.Hidden?single:f.Opening??(!f.Live?single:null),f.Hidden?Ui.Text("ui.055"):Ui.Text("ui.056"),f.Hidden);
        int percent=f.OpeningShare??SearchCapacity.OpeningPercent;
        openingRow.Visible=f.Hidden || percent!=0;
        liveRow.Visible=!f.Hidden && percent!=100;
        if(!f.Hidden)
        {
            var realtime=f.Realtime??(f.Live?single:null);
            if(f.RouteDiverged)realtime=realtime?.WithoutRoute();
            if(f.RouteDiverged || f.State is "主机已关闭广播" or "等待房主共享")stableLiveNumbers=null;
            int? frozenRound=null;
            if(realtime?.End!=null && realtime.Outcome is "胜" or "败")
            {
                if(f.LiveSettling && realtime.Options==null && stableLiveNumbers is {Options:{} retained} prior)
                    realtime=realtime with {Options=retained,PotionKey=prior.PotionKey};
                stableLiveNumbers=realtime;
            }
            else if(stableLiveNumbers is {} previous)
            {
                frozenRound=previous.Round;
                realtime=previous with {Rate=realtime?.Rate??0,Visits=realtime?.Visits is >0 and var visits?visits:previous.Visits,
                    Checking=false};
            }
            displayedLiveNumbers=realtime;
            FormatRow(liveNumbers,realtime,Ui.Text("ui.057"),false,frozenRound);
        }
        bool dual=openingRow.Visible && liveRow.Visible;
        var openingProof=proofSlots[openingNumbers[0]];
        var liveProof=proofSlots[liveNumbers[0]];
        float proofWidth=Math.Max(openingRow.Visible && openingProof.Text.Length>0?openingProof.GetMinimumSize().X:0,
            liveRow.Visible && liveProof.Text.Length>0?liveProof.GetMinimumSize().X:0);
        foreach(var marker in new[]{openingProof,liveProof})
        {
            marker.CustomMinimumSize=new(proofWidth,0);
            marker.Visible=proofWidth>0;
            marker.MouseFilter=marker.Text.Length==0?Control.MouseFilterEnum.Ignore:Control.MouseFilterEnum.Pass;
        }
        for(int field=1;field<=2;field++)
        {
            var a=seatCells[openingNumbers[field]];var b=seatCells[liveNumbers[field]];
            int count=Math.Max(a.Count,b.Count);
            for(int seat=0;seat<count;seat++)
            {
                var left=seat<a.Count && a[seat].GetParent<PanelContainer>().Visible?a[seat]:null;
                var right=seat<b.Count && b[seat].GetParent<PanelContainer>().Visible?b[seat]:null;
                float width=dual?Math.Max(left?.GetMinimumSize().X??0,right?.GetMinimumSize().X??0):0;
                if(left!=null)left.CustomMinimumSize=new(width,0);
                if(right!=null)right.CustomMinimumSize=new(width,0);
            }
        }
        for(int i=0;i<openingNumbers.Length;i++)
        {
            var a=openingRow.GetChild<Control>(i);var b=liveRow.GetChild<Control>(i);
            float width=dual?Math.Max(a.GetMinimumSize().X,b.GetMinimumSize().X):0;
            a.CustomMinimumSize=b.CustomMinimumSize=new(width,0);
        }
        float height=Math.Max(openingRow.GetMinimumSize().Y,liveRow.GetMinimumSize().Y);
        openingRow.CustomMinimumSize=liveRow.CustomMinimumSize=new(0,height);
        bar.ResetSize();
    }
    internal void TrackHover(Vector2 mouse,bool hidden,bool choosing=false)
    {
        var strip=bar.GetRect();var details=pop.GetRect();
        bool over=strip.HasPoint(mouse);
        if(!over)ignoreHover=false;
        bool within=over || pop.Visible && (details.HasPoint(mouse) || InlineMenus.InGap(mouse,strip,details) || menus.Contains(mouse));
        bool expand=nativeWindow!=null || !dragging && (pinned || adjustingSlider || choosing || !ignoreHover && within);
        pop.Visible=expand;
        bool resized=false;
        for(int i=4;i<7;i++)
        {
            bool visible=expand && (i!=5 || !hidden);
            var opening=openingRow.GetChild<Control>(i);
            var live=liveRow.GetChild<Control>(i);
            if(opening.Visible==visible && live.Visible==visible)continue;
            opening.Visible=live.Visible=visible;resized=true;
        }
        if(resized)bar.ResetSize();
    }
    internal void Remove(){RequestHistory(false);root.Hide();canvas.Remove();}
    internal void HideOverlay()
    {
        root.Hide();canvas.Hide();pop.Hide();menus.Close();tip.Hide();
        held=dragging=ignoreHover=false;
    }
    internal void Hide()
    {
        HideOverlay();previousController=null;ResetLiveDisplay();
    }
    internal void Update(HudFrame f,ulong me,bool host,bool canStart,int combatRound,bool guest=false,bool? broadcasting=null,
        IReadOnlyDictionary<uint,string>? enemyNames=null)
    {
        root.Show();localId=me;
        if(opacity!=HudSurface.Opacity){opacity=HudSurface.Opacity;opacitySlider.SetValueNoSignal(Math.Round(opacity*100));ApplyOpacity();}
        root.Scale=Vector2.One*(fontSize/17f);
        var viewport=root.GetViewport();
        var logical=root.GetViewportRect().Size;
        var screenScale=root.Scale*HudCanvas.DeviceTransform(viewport).Scale;
        Vector2 PixelPosition(Vector2 point)=>screenScale.X>0 && screenScale.Y>0?(point*screenScale).Round()/screenScale:point;
        root.Size=logical/root.Scale;
        if(previousController!=host)
        {
            long resetAt=System.Environment.TickCount64;
            openingFlash.Update(0,false,resetAt);liveFlash.Update(0,false,resetAt);
            menus.SetReadOnly(openingPots,!host);menus.SetReadOnly(livePots,!host);
            previousController=host;ResetLiveDisplay();
        }
        if(f.Hidden && last?.Hidden!=true){route.CloseOverview();lastDetail=null;detailInitialized=false;route.Reset();outcome.Set(null);chartsOpen=false;outcome.Button.SetPressedNoSignal(false);}
        if(last!=f || combatRound!=currentCombatRound)
        {
            currentCombatRound=combatRound;
            Format(f);warnings.Set(f);
            last=f;
        }
        bar.Position=nativeWindow!=null?Vector2.Zero:PixelPosition(anchor is {} position?position*PinTravel:DefaultAnchor());
        var mouse=root.GetLocalMousePosition();long now=System.Environment.TickCount64;
        openingFlash.Update(f.Opening?.LossRevision??0,!f.Hidden && openingRow.Visible && f.Opening!=null,now);
        liveFlash.Update(f.Realtime?.LossRevision??0,!f.Hidden && liveRow.Visible && f.Realtime!=null,now,
            f.Realtime?.LossIncreased==true);
        var menu=f.Hidden || f.PotionMenu?.Round!=combatRound?null:f.PotionMenu;
        bool choosing=menu?.HasHint(me)==true;
        TrackHover(mouse,f.Hidden,choosing);
        indicator.Modulate=(nativeWindow?.AlwaysOnTop??pinned)?Accent:new Color(.69f,.75f,.77f,.75f);
        if(!pop.Visible)menus.Close();
        if(!pop.Visible){tip.Hide();progress.HideTip();RequestHistory(false);graph.HideExpansion();SuspendGraphRequest();canvas.Update(root,menus);return;}
        bool departed=f.Live && !f.Hidden && f.RouteDiverged;
        bool hostUnavailable=f.State is "主机已关闭广播" or "等待房主共享";
        // Older hosts omit their plan during native action settlement.
        bool keepLegacy=f.Live && !departed && !f.Hidden && !hostUnavailable && f.Plan==null &&
            (f.Realtime?.Checking==true || f.State.StartsWith("等待实战结算",StringComparison.Ordinal));
        var p=departed?null:f.Plan??(keepLegacy && lastDetailLive?lastDetail:null);
        if(departed && lastDetail!=null)
        {
            route.CloseOverview();graph.Clear();
        }
        bool showCharts=chartsOpen && !f.Hidden && !choosing;
        head.CustomMinimumSize=new(0,showCharts?chartHeaderHeight:0);
        string? status=departed?Ui.Text("ui.058"):null;
        RequestHistory(showCharts);
        progress.Panel.Visible=showCharts;
        progress.Set(showCharts?f.History:null,p?.Party);
        multiplayer=guest || broadcasting.HasValue || (p?.Party.Length??menu?.Party.Length??0)>1;
        int graphRound=route.RoundFor(p,f.Round);
        int? wantedGraph=multiplayer && p!=null && !f.Hidden?graphRound:null;
        if(!graphPreferenceKnown || wantedGraph!=lastGraphRound)
        {
            graphPreferenceKnown=true;lastGraphRound=wantedGraph;requestGraph?.Invoke(wantedGraph);
        }
        var report=wantedGraph.HasValue?f.Graphs?.FirstOrDefault(entry=>entry.Round==graphRound):null;
        bool building=report?.Updating==true && f.Plan!=null;
        bool graphPending=wantedGraph.HasValue && f.Graphs!=null && report==null;
        var certified=report?.Graph is {Conservative:false} proof?proof:null;
        bool waitForGraph=building || graphPending;
        if(certified==null && waitForGraph && lastDetail!=null && lastDetailLive==f.Live && lastDetailGraph?.Round==graphRound)
        {
            // Keep the plan and its verified graph together while replacing this round.
            p=lastDetail;
            certified=lastDetailGraph;
        }
        outcome.Set(p,status,showCharts,backSize);
        outcome.Button.Disabled=p==null && !showCharts;
        var executed=f.Live?(ReferenceEquals(p,f.Plan)?f.Executed:
            ReferenceEquals(p,lastDetail) && lastDetailLive?lastDetailExecuted:null):null;
        lastDetailExecuted=executed;
        var liveNames=f.Live?enemyNames:null;
        route.SetTargetNames(liveNames);
        if(!ReferenceEquals(p,lastDetail) || !detailInitialized || lastDetailLive!=f.Live)
        {
            detailInitialized=true;lastDetail=p;lastDetailLive=f.Live;
        }
        bool orderedOnly=certified==null && report?.OriginalOrderVerified==true;
        route.ShowOriginalOrder(orderedOnly);
        route.Set(p,f.Round,me);
        var shownMenu=menu?.Round==graphRound?menu:null;
        route.SetPotionMenu(shownMenu,me);
        lastDetailGraph=certified;
        ulong[] ended=f.Live?f.ReadyEnds??Array.Empty<ulong>():Array.Empty<ulong>();
        route.SetReadyEnds(f.Live?f.ReadyRound:0,ended,executed);
        route.SetWaits(certified);
        if(choosing)route.CloseOverview();
        bool overview=route.Overview.Panel.Visible;
        bool details=!showCharts && !f.Hidden && !departed;
        bool navigation=details && route.RoundCount>0;
        prevRound.Disabled=route.RoundIndex<=0;
        nextRound.Disabled=route.RoundIndex+1>=route.RoundCount;
        autoplay.Visible=soloPlayback && !multiplayer;
        ownOnly.Visible=multiplayer;
        ownOnly.Disabled=!details || p==null || orderedOnly;
        ownOnly.SetPressedNoSignal(route.OwnOnly && !orderedOnly);
        head.Visible=!f.Hidden || host;
        prevRound.Visible=nextRound.Visible=route.Round.GetParent<Control>().Visible=navigation;
        outcome.Container.Visible=!f.Hidden;
        restart.Visible=host;hideResults.Visible=host;
        hideResults.Text=f.Hidden?Ui.Text("ui.060"):Ui.Text("ui.061");
        hideResults.Icon=f.Hidden?HudIcons.EyeOpen:HudIcons.EyeClosed;
        roster.Visible=details && !overview && roster.GetChildCount()>0;
        route.Scroll.Visible=details && !overview && route.Count>0 &&
            (!multiplayer || orderedOnly || route.OwnOnly && (certified!=null || shownMenu!=null));
        var visibleGraph=certified??(multiplayer && !orderedOnly && shownMenu!=null?
            new HudGraph(graphRound,shownMenu.Party,Array.Empty<HudGraphAction>(),Array.Empty<RouteDependencies.Edge>()):null);
        bool showGraph=details && !route.OwnOnly && !overview && visibleGraph!=null;
        graph.Update(visibleGraph,p,graphRound,me,readyRound:f.Live?f.ReadyRound:0,ready:ended,enemyNames:liveNames,progress:executed,menu:shownMenu);
        graph.Panel.Visible=showGraph && graph.HasActions;
        bool endedTurn=f.Live && f.ReadyRound==graphRound && p is {Party.Length:>0} && p.Party.All(seat=>ended.Contains(seat.Id));
        beforeActions.Visible=afterActions.Visible=showCharts || route.Scroll.Visible || graph.Panel.Visible;
        solve.Visible=host;mode.Visible=!f.Hidden;mode.Disabled=!host;
        solve.Icon=f.Running?HudIcons.Pause:HudIcons.Start;
        solve.Disabled=!canStart && !f.Running;
        sourceButton.Visible=guest;broadcastButton.Visible=broadcasting.HasValue;broadcastEnabled=broadcasting==true;
        connectionStatus.Text=f.State=="主机已关闭广播"?Ui.Text("ui.062"):"";
        connectionStatus.Visible=connectionStatus.Text.Length>0;
        settingsPanel.Visible=options;settings.SetPressedNoSignal(options);
        allocation.Visible=resources.Visible=host;
        windowButton.Visible=nativeWindow==null;
        AlignFooter(actionRow);
        if(options)AlignFooter(settingsActions);
        int opening=SearchCapacity.OpeningPercent;openingShare.SetValueNoSignal(opening);
        if(displayedOpeningShare!=opening)
        {
            displayedOpeningShare=opening;
            SetAllocationCaption(openingCaption,Ui.Text("ui.064",opening));
            SetAllocationCaption(liveCaption,Ui.Text("ui.065",100-opening));
        }
        cpuValue.Text=$"{SearchCapacity.Limit}/{SearchCapacity.Available}";
        less.Disabled=SearchCapacity.Limit<=1;more.Disabled=SearchCapacity.Limit>=SearchCapacity.Available;
        if(nativeWindow!=null)
        {
            var usable=DisplayServer.ScreenGetUsableRect(nativeWindow.CurrentScreen);
            var need=Mathf.CeilToInt(Math.Max(bar.Size.X,col.GetCombinedMinimumSize().X+20)*root.Scale.X);
            var minimum=new Vector2I(Math.Min(usable.Size.X,Math.Max(500,need)),Math.Min(usable.Size.Y,390));
            if(!showCharts && nativeWindow.MinSize!=minimum)nativeWindow.MinSize=minimum;
            pop.Position=new(0,bar.GetRect().End.Y+PanelGap);
            route.SizeForWidth(root.Size.X-panelStyle.GetMinimumSize().X);
            pop.Size=new(showCharts?Math.Min(root.Size.X,chartWidth):root.Size.X,Math.Max(0,root.Size.Y-pop.Position.Y));
        }
        else
        {
            float above=bar.Position.Y,below=root.Size.Y-bar.GetRect().End.Y;
            float available=Math.Max(0,Math.Max(above,below)-PanelGap);
            float contentWidth=graph.Panel.Visible?graph.IdealWidth:route.IdealWidth;
            float desiredWidth=Math.Max(DetailsWidth,Math.Max(col.GetCombinedMinimumSize().X,contentWidth)+20);
            float roomToRight=root.Size.X-bar.Position.X;
            float width=Math.Min(root.Size.X,showCharts?chartWidth:Math.Min(desiredWidth,Math.Max(DetailsWidth,roomToRight)));
            route.SizeForWidth(width-panelStyle.GetMinimumSize().X);
            var items=col.GetChildren().OfType<Control>().Where(control=>control.Visible).ToArray();
            float desired=items.Sum(control=>ReferenceEquals(control,graph.Panel)?graph.ContentHeight:
                ReferenceEquals(control,route.Scroll)?route.ContentHeight:ReferenceEquals(control,progress.Panel)?
                SearchProgressView.PreferredHeight:control.GetCombinedMinimumSize().Y)
                +Math.Max(0,items.Length-1)*col.GetThemeConstant("separation")+panelStyle.GetMinimumSize().Y;
            if(waitForGraph && details && !overview && !endedTurn)
                desired=Math.Max(desired,pop.Size.Y);
            if(graph.Panel.Visible)graph.SetScrollOverflow(desired>available);
            if(route.Scroll.Visible)route.Scroll.VerticalScrollMode=desired>available?
                ScrollContainer.ScrollMode.Reserve:ScrollContainer.ScrollMode.Disabled;
            float fullHeight=screenScale.Y>0?Mathf.Ceil(desired*screenScale.Y)/screenScale.Y:desired;
            pop.Size=new(PixelPosition(new(width,0)).X,Math.Min(available,fullHeight));
            bool belowBar=below>=above;
            int headerIndex=belowBar?0:col.GetChildCount()-1;
            if(head.GetIndex()!=headerIndex)col.MoveChild(head,headerIndex);
            pop.Position=PixelPosition(new(Math.Clamp(bar.Position.X,0,Math.Max(0,root.Size.X-pop.Size.X)),
                belowBar?bar.GetRect().End.Y+PanelGap:Math.Max(0,above-pop.Size.Y-PanelGap)));
        }
        menus.Refresh();graph.UpdateHover(root,mouse,graph.Panel.Visible && !menus.Visible);progress.Hover(root,mouse,showCharts && !menus.Visible);UpdateHint(mouse);canvas.Update(root,menus);
    }
    internal void UpdateHint(Vector2 mouse)
    {
        if(menus.Visible || !pop.Visible){tip.Hide();return;}
        Rect2 Rect(Control c)=>root.GetGlobalTransform().AffineInverse()*c.GetGlobalRect();
        foreach(var (strip,metric) in metricHints)
        {
            if(!strip.IsVisibleInTree() || !Rect(strip).HasPoint(mouse))continue;
            string hint=metric.Message;
            if(metric.Start is {} caption && mouse.X<Rect(caption).Position.X)
            {
                if(metric.Proof is not {Visible:true,Text:"✓"} proof || !Rect(proof).HasPoint(mouse))continue;
                hint=Ui.Text("proof.minimum_loss");
            }
            if(!tip.Visible || tipText.Text!=hint){tipText.Text=hint;tip.Show();tip.ResetSize();tip.MoveToFront();}
            var r=bar.GetRect();
            float above=r.Position.Y-tip.Size.Y-2;
            tip.Position=new(Math.Clamp(Rect(strip).Position.X,0,Math.Max(0,root.Size.X-tip.Size.X)),
                Math.Clamp(above>=0?above:r.End.Y+2,0,Math.Max(0,root.Size.Y-tip.Size.Y)));
            return;
        }
        tip.Hide();
    }
}
