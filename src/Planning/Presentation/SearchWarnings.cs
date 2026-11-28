using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CoopSolver;

internal sealed class SearchWarnings
{
    internal readonly ScrollContainer Panel=new(){Name="SearchWarnings",Visible=false,
        HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,VerticalScrollMode=ScrollContainer.ScrollMode.Reserve};
    private readonly VBoxContainer rows=new(){SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
    private static readonly Color red=new("#ff9691");
    private string[] shown=Array.Empty<string>();

    internal SearchWarnings()
    {
        Panel.AddChild(rows);
        rows.MinimumSizeChanged+=Fit;
    }

    private void Fit()=>Panel.CustomMinimumSize=new(0,Math.Clamp(rows.GetCombinedMinimumSize().Y,21,104));

    internal void Set(HudFrame frame)
    {
        var messages=new List<string>();
        void Add(string? text)
        {
            if(string.IsNullOrWhiteSpace(text))return;
            text=Ui.Status(text.Trim());
            if(!messages.Contains(text))messages.Add(text);
        }
        if(frame.Warnings!=null)foreach(var item in frame.Warnings)Add(item);
        if(frame.SearchError!=null)Add("当前盘面搜索出错："+frame.SearchError);
        if(!frame.Running)Add(frame.PauseReason);
        if(frame.State=="共享路线过大")Add(frame.State);
        if(!frame.Hidden && frame.Plan?.Risks is {Length:>0} risks)
            foreach(string risk in risks.Split('\n',StringSplitOptions.RemoveEmptyEntries))
                if(ReplayCoverage.HasEffectDetail(risk.Trim()))Add("未覆盖的效果："+risk.Trim());
        Panel.Visible=messages.Count>0;
        if(messages.SequenceEqual(shown))return;
        shown=messages.ToArray();
        foreach(var child in rows.GetChildren()){rows.RemoveChild(child);child.QueueFree();}
        foreach(string text in shown)
        {
            var row=new HBoxContainer();
            row.AddChild(new TextureRect{Texture=HudIcons.Warning,Modulate=red,StretchMode=TextureRect.StretchModeEnum.KeepCentered,
                SizeFlagsVertical=Control.SizeFlags.ShrinkBegin,CustomMinimumSize=new(20,22),MouseFilter=Control.MouseFilterEnum.Ignore});
            var label=CombatHud.Caption(text,red,small:true);
            label.AutowrapMode=TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
            row.AddChild(label);rows.AddChild(row);
        }
        Panel.Visible=shown.Length>0;
        Panel.ScrollVertical=0;Fit();
    }
}
