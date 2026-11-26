using System;
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CoopSolver;

internal static class HudTypography
{
    internal const int FieldGap=5, Body=21, Detail=19, ControlHeight=25, RowHeight=24, BodyHeight=21, DetailHeight=19;
    internal static readonly Color Ink=new("#edf2ef"),Loss=new("#f1a49d"),Damage=new("#efd095"),Heal=new("#a7dfba"),Kill=new("#ffedbf");
    internal static Color LossInk(int? value,Color neutral)=>value switch {>0=>Loss,<0=>Heal,_=>neutral};
    internal static StyleBoxFlat Row(bool alternate,int verticalPadding=1)=>HudSurface.Box(alternate?"#92a7b420":"#23344148","",6,verticalPadding);
    internal static float SingleLineHeight(Control context,bool small)
    {
        int size=small?Detail:Body;
        return Mathf.Max(small?DetailHeight:RowHeight,Mathf.Ceil(context.GetThemeFont("font","Label").GetHeight(size))+2);
    }

    internal static float ActionRowHeight()=>Mathf.Max(RowHeight,Mathf.Ceil(Face().GetHeight(Body))+2);
    internal static float ExtraGapBeforeMetrics(string text,int size=Body)=>
        ExtraMetricGap(text,size,false);
    internal static float ExtraGapAfterMetricRule(string text)=>ExtraMetricGap(text,Detail,true);
    private static float ExtraMetricGap(string text,int size,bool leading)=>
        Math.Max(0,Mathf.Ceil(size*.5f-ChineseSpacing.EdgeBlank(text,size,leading))-FieldGap);

    internal static Control ActionMetricRule(bool visible=true,bool continuous=false)
    {
        var rule=new Control{CustomMinimumSize=new(1,0),MouseFilter=Control.MouseFilterEnum.Ignore};
        if(visible)
        {
            float inset=continuous?0:1;
            rule.Draw+=()=>DrawVerticalRule(rule,0,rule.Size.Y-2*inset,new Color("#9aafb867"),inset);
            rule.Resized+=rule.QueueRedraw;
        }
        return rule;
    }

    internal static void DrawVerticalRule(Control canvas,float x,float height,Color ink,float top=0)
    {
        if(height<=0)return;
        var viewport=canvas.GetViewport();
        if(viewport==null)return;
        var transform=HudCanvas.DeviceTransform(viewport)*canvas.GetGlobalTransformWithCanvas();
        float scale=transform.X.X;
        if(scale<=0)return;
        float origin=transform.Origin.X;
        float left=(Mathf.Floor(origin+x*scale)-origin)/scale;
        canvas.DrawRect(new Rect2(left,top,1/scale,height),ink);
    }
    private static string? language;
    private static Font? regular,bold;

    internal static Font Face(bool heavy=false)
    {
        if(language!=Ui.Language){regular=bold=null;ChineseSpacing.Clear();language=Ui.Language;}
        if(heavy)return bold??=Load(true);
        return regular??=Load(false);
    }

    private static Font Load(bool heavy)
    {
        var native=FontManager.GetSubstituteFont(Ui.Language,heavy?FontType.Bold:FontType.Regular)
            ??GD.Load<Font>(heavy?"res://themes/kreon_bold_shared.tres":"res://themes/kreon_regular_shared.tres");
        if(Ui.English)return native;
        var face=native is FontVariation variation?(FontVariation)variation.Duplicate():new FontVariation{BaseFont=native};
        ChineseSpacing.Bind(face);
        return face;
    }

    internal static Theme Theme(Font? regular=null)
    {
        var theme=new Theme{DefaultFont=regular??Face(),DefaultFontSize=Body};
        foreach(var type in new[]{"Label","Button","RichTextLabel"})
            theme.SetConstant("outline_size",type,0);
        theme.SetColor("font_shadow_color","Label",new Color("#100e0bc4"));
        theme.SetConstant("shadow_offset_x","Label",2);theme.SetConstant("shadow_offset_y","Label",1);
        return theme;
    }
}
