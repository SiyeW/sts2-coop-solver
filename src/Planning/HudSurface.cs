using System.Collections.Generic;
using Godot;

namespace CoopSolver;

// Shared fill resources keep newly created rows and open menus in sync with the HUD slider.
internal static class HudSurface
{
    private static readonly Dictionary<(string Fill,string Border,int X,int Y),StyleBoxFlat> styles=new();
    private static float opacity=.75f;
    internal static float Opacity=>opacity;
    internal static void SetOpacity(float value)
    {
        opacity=value;
        foreach(var (key,style) in styles)Tint(style,key.Fill,key.Border);
    }
    private static void Tint(StyleBoxFlat style,string fill,string border)
    {
        var bg=new Color(fill);bg.A*=opacity;style.BgColor=bg;
        if(border.Length>0){var edge=new Color(border);edge.A*=opacity;style.BorderColor=edge;}
    }
    internal static StyleBoxFlat Box(string fill,string border,int x,int y)
    {
        var key=(fill,border,x,y);
        if(styles.TryGetValue(key,out var style))return style;
        style=CombatHud.Box(fill,x,y);
        if(border.Length>0)style.SetBorderWidthAll(1);
        Tint(style,fill,border);styles.Add(key,style);return style;
    }
}
