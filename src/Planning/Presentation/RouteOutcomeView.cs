using Godot;
using System.Linq;

namespace CoopSolver;

internal sealed class RouteOutcomeView
{
    internal const int TextInset=7;
    internal readonly HBoxContainer Container=new(){Name="RouteResultBlocks",MouseFilter=Control.MouseFilterEnum.Pass};
    internal readonly Button Button=new(){Name="HudRouteOutcome",ToggleMode=true,FocusMode=Control.FocusModeEnum.None,
        MouseDefaultCursorShape=Control.CursorShape.PointingHand};
    private readonly HBoxContainer values=new(){MouseFilter=Control.MouseFilterEnum.Ignore};
    private readonly Label result,enemy;
    private HudPlan? currentPlan;
    private string? currentStatus;
    private bool currentCharts;
    private string? currentLanguage;

    internal RouteOutcomeView()
    {
        Container.AddChild(Button);
        var normal=HudSurface.Box("#70959f36","",TextInset,2);
        var highlight=HudSurface.Box("#70959f62","#acc9d28a",TextInset,2);
        Button.AddThemeStyleboxOverride("normal",normal);
        Button.AddThemeStyleboxOverride("disabled",normal);
        Button.AddThemeStyleboxOverride("hover",highlight);
        Button.AddThemeStyleboxOverride("pressed",highlight);
        Button.AddThemeStyleboxOverride("hover_pressed",highlight);
        Button.AddThemeStyleboxOverride("focus",new StyleBoxEmpty());
        Button.AddThemeFontSizeOverride("font_size",HudTypography.Body);
        foreach(var color in new[]{"font_color","font_hover_color","font_pressed_color","font_hover_pressed_color"})
            Button.AddThemeColorOverride(color,HudTypography.Ink);
        Button.AddChild(values);
        values.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        values.OffsetLeft=TextInset;values.OffsetRight=-TextInset;values.OffsetTop=2;values.OffsetBottom=-2;
        values.AddThemeConstantOverride("separation",6);
        values.MinimumSizeChanged+=SizeButton;
        result=AddValue("ResultValue");enemy=AddValue("EnemyHpValue");
    }

    private Vector2 backSize;
    private void SizeButton()=>Button.CustomMinimumSize=values.Visible?
        values.GetCombinedMinimumSize()+new Vector2(2*TextInset,4):backSize.Max(new Vector2(0,HudTypography.ControlHeight));

    private Label AddValue(string name)
    {
        var label=CombatHud.Caption("",new("#eef1e9"));label.Name=name;
        values.AddChild(label);return label;
    }

    internal void Set(HudPlan? plan,string? status=null,bool charts=false,Vector2 chartButtonSize=default)
    {
        if(ReferenceEquals(currentPlan,plan) && currentStatus==status && currentCharts==charts &&
            currentLanguage==Ui.Language && backSize==chartButtonSize)return;
        currentPlan=plan;currentStatus=status;currentCharts=charts;currentLanguage=Ui.Language;backSize=chartButtonSize;
        Button.Text=charts?Ui.Text("ui.099"):"";
        string title=plan?.Steps.Split('\n',2)[0]??"";
        string state=status??(title.Contains("供对照")?Ui.Text("ui.101"):"");
        bool hasResult=plan!=null && !title.Contains("待复算");
        Button.Visible=charts || state.Length>0 || hasResult;
        values.Visible=Button.Visible && !charts;
        if(!values.Visible)
        {
            result.Text=enemy.Text="";
            SizeButton();return;
        }
        bool won=plan?.Outcome=="胜";
        result.Text=state.Length>0?state:won?Ui.Text("ui.103"):plan?.Outcome=="败"?Ui.Text("ui.104"):Ui.Text("ui.105",plan?.End);
        if(state.Length==0 && won && plan?.Goal is {} goal)result.Text=goal.Label();
        result.Modulate=state.Length!=0?Colors.White:plan?.Outcome switch
        {
            "胜"=>new Color("#a6edbd"),
            "败"=>HudTypography.Loss,
            _=>Colors.White
        };
        var last=plan?.EndEnemyHp;
        if(last==null && plan?.Rounds is {} rounds)
            foreach(var round in rounds)if(round.Round==plan.End && round.EnemyHp.HasValue)last=round.EnemyHp;
        int? loss=plan?.TeamLoss;
        if(loss==null && plan?.Party is {Length:>0} seats && seats.All(s=>s.ToEnd.HasValue))loss=seats.Sum(s=>s.ToEnd!.Value);
        enemy.Text=won?(loss<0?Ui.Text("ui.078",-loss):Ui.Text("route.loss_value",loss?.ToString(Ui.Culture)??Ui.Missing)):
            Ui.Text("ui.116",last?.ToString(Ui.Culture)??Ui.Missing);
        enemy.AddThemeColorOverride("font_color",HudTypography.LossInk(won?loss:null,HudTypography.Ink));
        enemy.Visible=state.Length==0;
        SizeButton();
    }
}
