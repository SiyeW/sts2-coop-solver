using Godot;
using System.Linq;

namespace CoopSolver;

internal sealed class RouteOutcomeView
{
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
        var normal=HudSurface.Box("#70959f36","",7,2);
        var highlight=HudSurface.Box("#70959f62","#acc9d28a",7,2);
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
        values.OffsetLeft=7;values.OffsetRight=-7;values.OffsetTop=2;values.OffsetBottom=-2;
        values.AddThemeConstantOverride("separation",6);
        values.MinimumSizeChanged+=SizeButton;
        result=AddValue("ResultValue");enemy=AddValue("EnemyHpValue");
    }

    private Vector2 backSize;
    private void SizeButton()=>Button.CustomMinimumSize=values.Visible?
        values.GetCombinedMinimumSize()+new Vector2(14,4):backSize.Max(new Vector2(0,HudTypography.ControlHeight));

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
        Button.Text=charts?Ui.T("返回","Back"):"";
        values.Visible=!charts;
        if(charts){SizeButton();return;}
        string title=plan?.Steps.Split('\n',2)[0]??"";
        string state=status??(plan==null?Ui.T("暂无数据","No data"):title.Contains("供对照")?Ui.T("开局路线供对照","Opening reference"):title.Contains("待复算")?Ui.T("更新中","Updating"):"");
        bool won=title.Contains("胜利");
        result.Text=state.Length>0?state:won?Ui.T("结果：胜","Win"):title.Contains("倒下")?Ui.T("结果：败","Loss"):Ui.T($"已算到第{plan?.End}回合，胜负未定",$"Through turn {plan?.End} · In progress");
        if(state.Length==0 && won && plan?.Goal is {} goal)result.Text=goal.Label();
        result.Modulate=state.Length==0 && won?new("#a6edbd"):Colors.White;
        var last=plan?.EndEnemyHp;
        if(last==null && plan?.Rounds is {} rounds)
            foreach(var round in rounds)if(round.Round==plan.End && round.EnemyHp.HasValue)last=round.EnemyHp;
        int? loss=plan?.TeamLoss;
        if(loss==null && plan?.Party is {Length:>0} seats && seats.All(s=>s.ToEnd.HasValue))loss=seats.Sum(s=>s.ToEnd!.Value);
        enemy.Text=Ui.English?(won?(loss<0?$"+{-loss} HP":$"HP loss {loss?.ToString()??Ui.Missing}"):$"Enemy HP {last?.ToString()??Ui.Missing}"):won?(loss<0?$"回{-loss}血":$"扣{loss?.ToString()??Ui.Missing}血"):$"敌人剩{last?.ToString()??Ui.Missing}血";
        enemy.AddThemeColorOverride("font_color",HudTypography.LossInk(won?loss:null,HudTypography.Ink));
        enemy.Visible=state.Length==0;
        SizeButton();
    }
}
