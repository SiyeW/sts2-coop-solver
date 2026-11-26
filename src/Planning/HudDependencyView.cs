using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CoopSolver;

internal sealed class HudDependencyView
{
    private const int Gap=12,StandardColumns=4,CellPadding=12;
    private const float ArrowDepth=9,ArrowHalfWidth=5.5f;
    private static float Line=>HudTypography.ActionRowHeight();
    private float SmallLine=>HudTypography.SingleLineHeight(container,true);
    private static readonly Color DependencyColor=new("#e6b86f"),Dim=new("#c5d6dc");
    private readonly VBoxContainer container=new(){Name="HudGraphContainer",Visible=false,SizeFlagsVertical=Control.SizeFlags.ExpandFill};
    private readonly HBoxContainer footer=new(){Name="HudGraphFooter",CustomMinimumSize=new(0,HudTypography.ControlHeight)};
    private readonly Button previous,next;
    private readonly ScrollContainer scroll=new(){Name="HudGraphScroll",HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,
        VerticalScrollMode=ScrollContainer.ScrollMode.Reserve,SizeFlagsVertical=Control.SizeFlags.ExpandFill};
    private readonly GridContainer grid=new(){Name="HudDependencyGrid",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
    private readonly Dictionary<int,Tile> tiles=new();
    private readonly Dictionary<int,WaitCell> waitBoxes=new();
    private HudGraph? shown;
    private HudPlan? shownPlan;
    private IReadOnlyDictionary<uint,string>? targetNames;
    private ulong me;
    private int readyRound;
    private ulong[] ready=Array.Empty<ulong>();
    private readonly List<Tile> headerTiles=new();
    private int firstSeat,columns;
    private int[] visibleSeats=Array.Empty<int>();
    private ColumnLayout[] layouts=Array.Empty<ColumnLayout>();
    private PanelContainer? expansion;
    private Tile? expanded;
    private sealed record Field(string Text,Color Ink,bool Lethal=false);
    private sealed record ActionText(Field[] Fields,Field? Damage,Field? Change,string? Choice=null);
    private readonly record struct ColumnLayout(float Description,float Damage,float Change,float Wait,float Lead)
    {
        internal float MetricsWidth=>Damage>0 || Change>0
            ?1+Lead+HudTypography.FieldGap+(Damage>0?Damage+HudTypography.FieldGap:0)+(Change>0?Change+HudTypography.FieldGap:0)
            :0;
        internal float PreferredWidth=>Math.Max(MinimumWidth,Math.Max(Description+MetricsWidth,Wait)+CellPadding);
        internal float MinimumWidth=>Width(Ui.T("结束回合","End turn"))+1+HudTypography.ExtraGapBeforeMetrics("")+MetricsWidth+CellPadding;
    }
    internal bool HasActions=>tiles.Count>0;
    private sealed record Tile(PanelContainer Box,ActionText Text,ColumnLayout Layout,float Width,bool Even,bool Header=false);
    private sealed record WaitCell(PanelContainer Box,Control Label,bool Right);
    internal Control Panel=>container;
    internal float ContentHeight=>grid.GetCombinedMinimumSize().Y+
        (footer.Visible?footer.GetCombinedMinimumSize().Y+container.GetThemeConstant("separation"):0);
    internal float IdealWidth
    {
        get
        {
            int count=Math.Min(StandardColumns,layouts.Length);
            if(count==0)return 0;
            int start=Math.Clamp(firstSeat,0,layouts.Length-count);
            return layouts.Skip(start).Take(count).Sum(layout=>layout.PreferredWidth)+
                Gap*(count-1)+ScrollbarWidth;
        }
    }
    private float ScrollbarWidth=>scroll.VerticalScrollMode==ScrollContainer.ScrollMode.Disabled?0:
        scroll.GetVScrollBar().GetCombinedMinimumSize().X;

    internal void SetScrollOverflow(bool overflowing)
    {
        var mode=overflowing?ScrollContainer.ScrollMode.Reserve:ScrollContainer.ScrollMode.Disabled;
        if(scroll.VerticalScrollMode==mode)return;
        scroll.VerticalScrollMode=mode;
        FitColumns();
    }

    internal HudDependencyView()
    {
        footer.Alignment=BoxContainer.AlignmentMode.End;footer.Hide();
        previous=Navigation("HudGraphPrevSeat",HudIcons.Previous,-1);
        next=Navigation("HudGraphNextSeat",HudIcons.Next,1);
        footer.AddChild(previous);footer.AddChild(next);
        container.AddChild(scroll);scroll.AddChild(grid);container.AddChild(footer);
        grid.AddThemeConstantOverride("h_separation",Gap);
        grid.AddThemeConstantOverride("v_separation",0);
        grid.Draw+=DrawConnectors;grid.SortChildren+=grid.QueueRedraw;
        scroll.Resized+=FitColumns;
    }

    private Button Navigation(string name,Texture2D icon,int offset)
    {
        var button=new Button{Name=name,Icon=icon,Alignment=HorizontalAlignment.Center,FocusMode=Control.FocusModeEnum.None,Visible=false};
        CombatHud.StyleButton(button,compact:true);
        button.Pressed+=()=>{
            if(shown==null)return;
            firstSeat=Math.Clamp(firstSeat+offset,0,Math.Max(0,shown.Party.Length-columns));
            Render(shown);
        };
        return button;
    }

    internal void Clear()
    {
        shown=null;shownPlan=null;firstSeat=0;columns=0;layouts=Array.Empty<ColumnLayout>();
        ResetGrid();footer.Hide();previous.Hide();next.Hide();
    }

    internal void HideExpansion()
    {
        if(expansion!=null){expansion.Hide();expansion.QueueFree();expansion=null;}
        if(expanded!=null)expanded.Box.Modulate=Colors.White;
        expanded=null;
    }

    private void ResetGrid()
    {
        HideExpansion();tiles.Clear();waitBoxes.Clear();headerTiles.Clear();
        foreach(var child in grid.GetChildren()){grid.RemoveChild(child);child.QueueFree();}
        grid.QueueRedraw();
    }

    private int VisibleColumns(HudGraph graph)
    {
        float width=scroll.Size.X-ScrollbarWidth;
        float minimum=layouts.Select(layout=>layout.MinimumWidth).DefaultIfEmpty(1).Max();
        int fit=width<=0?StandardColumns:Math.Max(1,(int)((width+Gap)/(minimum+Gap)));
        return Math.Min(graph.Party.Length,Math.Min(StandardColumns,fit));
    }

    private void FitColumns()
    {
        if(shown==null)return;
        if(VisibleColumns(shown)!=columns)Render(shown);
        else ApplyColumnWidths();
    }

    private void ApplyColumnWidths()
    {
        if(visibleSeats.Length==0 || layouts.Length==0)return;
        var laneLayouts=visibleSeats.Select(seat=>layouts[seat]).ToArray();
        float preferred=laneLayouts.Sum(layout=>layout.PreferredWidth);
        float minimum=laneLayouts.Sum(layout=>layout.MinimumWidth);
        float budget=scroll.Size.X>0?scroll.Size.X-ScrollbarWidth-Gap*(visibleSeats.Length-1):preferred;
        var widths=laneLayouts.Select(layout=>Mathf.Lerp(layout.MinimumWidth,layout.PreferredWidth,
            preferred>minimum?Math.Clamp((budget-minimum)/(preferred-minimum),0,1):1)).ToArray();
        for(int i=0;i<grid.GetChildCount();i++)
        {
            var cell=grid.GetChild<Control>(i);
            cell.CustomMinimumSize=new(widths[i%widths.Length],cell.CustomMinimumSize.Y);
        }
        grid.QueueRedraw();
    }

    private HudExecuted? executed;
    internal void Update(HudGraph? graph,HudPlan? plan,int selected,ulong local=0,int readyRound=0,ulong[]? ready=null,
        IReadOnlyDictionary<uint,string>? enemyNames=null,HudExecuted? progress=null)
    {
        ready??=Array.Empty<ulong>();
        bool rerender=me!=local || this.readyRound!=readyRound || !this.ready.SequenceEqual(ready) ||
            !Ui.SameTargetNames(targetNames,enemyNames) || !HudExecuted.Same(executed,progress);
        executed=progress;
        targetNames=enemyNames;
        me=local;this.readyRound=readyRound;this.ready=ready;
        if(graph!=null && graph.Round==selected && !ReferenceEquals(graph,shown))
        {
            if(shown!=null && !shown.Party.Select(seat=>seat.Id).SequenceEqual(graph.Party.Select(seat=>seat.Id)))
                firstSeat=0;
            rerender|=shown==null || shown.Round!=graph.Round || !shown.Party.SequenceEqual(graph.Party) ||
                shown.Conservative!=graph.Conservative || !shown.Actions.SequenceEqual(graph.Actions) ||
                !shown.Dependencies.SequenceEqual(graph.Dependencies);
            shown=graph;shownPlan=plan;
        }
        if((graph==null || graph.Round!=selected) && shown!=null)Clear();
        if(rerender && shown!=null)Render(shown);

    }

    private void Render(HudGraph graph)
    {
        ResetGrid();
        int count=graph.Party.Length;
        if(count==0)return;
        float damage=Width(Ui.T("打0","0 dmg"),HudTypography.Detail);
        float change=Width(Ui.T("余0费","0 EN"),HudTypography.Detail);
        layouts=Enumerable.Repeat(new ColumnLayout(0,damage,change,0,0),count).ToArray();
        bool AlreadyEnded(HudGraphAction action)=>graph.Round==readyRound && action.Name=="结束回合" &&
            ready.Contains(graph.Party[action.Seat].Id);
        var moves=graph.Actions.Where(a=>(uint)a.Seat<(uint)count && executed?.Includes(graph.Round,a.Step)!=true).DistinctBy(a=>a.Step).ToArray();
        var actions=moves.Where(a=>!AlreadyEnded(a)).ToDictionary(a=>a.Step);
        var display=actions.Values.OrderBy(a=>a.Step).ToArray();
        var dependencies=graph.Dependencies.Where(e=>actions.ContainsKey(e.Before) && actions.ContainsKey(e.After)).ToLookup(e=>e.After);
        var text=display.ToDictionary(action=>action.Step,Describe);
        foreach(var action in display)
        {
            var description=text[action.Step];
            var layout=layouts[action.Seat];
            layouts[action.Seat]=layout with {
                Description=Math.Max(layout.Description,DescriptionWidth(description)),
                Damage=Math.Max(layout.Damage,MetricWidth(description.Damage)),
                Change=Math.Max(layout.Change,MetricWidth(description.Change)),
                Lead=(description.Damage??description.Change) is {} value
                    ?Math.Max(layout.Lead,HudTypography.ExtraGapAfterMetricRule(value.Text)):layout.Lead
            };
        }
        var level=new Dictionary<int,int>();
        var last=new int[count];
        var cells=new Dictionary<(int Row,int Seat),(HudGraphAction Action,string[]? Wait,bool Right)>();
        foreach(var action in display)
        {
            var wait=dependencies[action.Step].Select(e=>actions[e.Before]).ToArray();
            int row=last[action.Seat]+1;
            if(wait.Length>0)
            {
                int above=wait.Select(a=>level.GetValueOrDefault(a.Step)).Max();
                row=Math.Max(row,above);
                var words=HudRouteView.WaitFields(wait,shownPlan);
                var layout=layouts[action.Seat];
                layouts[action.Seat]=layout with {
                    Wait=Math.Max(layout.Wait,FieldsWidth(words,HudTypography.Detail)+34)
                };
                cells[(row++,action.Seat)]=(action,words,wait[0].Seat>action.Seat);
            }
            level[action.Step]=last[action.Seat]=row;
            cells[(level[action.Step],action.Seat)]=(action,null,false);
        }
        columns=VisibleColumns(graph);
        firstSeat=Math.Clamp(firstSeat,0,Math.Max(0,count-columns));
        visibleSeats=Enumerable.Range(firstSeat,columns).ToArray();
        footer.Visible=previous.Visible=next.Visible=count>columns;
        previous.Disabled=firstSeat==0;next.Disabled=firstSeat+columns>=count;
        grid.Columns=columns;
        foreach(int i in visibleSeats)
        {
            var title=Cell(Line);
            title.AddThemeStyleboxOverride("panel",HudRouteView.SeatBox(graph.Party[i],5,0));
            string[] titleFields={$"P{i+1}",graph.Party[i].Name};
            title.AddChild(TextFields(titleFields,HudTypography.Ink));
            CombatHud.SeatUnderline(title,graph.Party[i].Id==me,HudTypography.Ink);
            grid.AddChild(title);
            var heading=new ActionText(titleFields.Select(value=>new Field(value,HudTypography.Ink)).ToArray(),null,null);
            headerTiles.Add(new(title,heading,default,FieldsWidth(titleFields)+CellPadding,false,true));
        }
        int rows=visibleSeats.Select(seat=>last[seat]).DefaultIfEmpty(0).Max();
        for(int row=1;row<=rows;row++)
            foreach(int seat in visibleSeats)
            {
                if(!cells.TryGetValue((row,seat),out var cell))
                {
                    grid.AddChild(new Control{CustomMinimumSize=new(0,Line),MouseFilter=Control.MouseFilterEnum.Ignore});
                    continue;
                }
                if(cell.Wait is {} words)
                {
                    AddWait(cell.Action.Step,words,cell.Right);
                    continue;
                }
                var description=text[cell.Action.Step];
                var layout=layouts[seat];
                var box=Cell(Line);
                AddFields(box,description,layout,row%2==1);
                grid.AddChild(box);
                tiles.Add(cell.Action.Step,new(box,description,layout,
                    DescriptionWidth(description)+layout.MetricsWidth+CellPadding,row%2==1));
            }
        ApplyColumnWidths();
    }

    private static PanelContainer Cell(float height)=>new(){CustomMinimumSize=new(0,height),
        MouseFilter=Control.MouseFilterEnum.Ignore};

    private void AddWait(int step,string[] words,bool right)
    {
        var box=Cell(Line);
        box.AddThemeStyleboxOverride("panel",new StyleBoxEmpty());
        var layout=new Control{MouseFilter=Control.MouseFilterEnum.Ignore};
        box.AddChild(layout);
        var label=TextFields(words,DependencyColor,small:true,right:right);
        layout.AddChild(label);
        float textWidth=FieldsWidth(words,HudTypography.Detail);
        void Align()
        {
            float width=Math.Min(textWidth,Math.Max(0,layout.Size.X-34));
            label.Size=new(width,Line);
            label.Position=new(right?layout.Size.X-width:0,(layout.Size.Y-Line)/2);
        }
        layout.Resized+=Align;
        Align();
        grid.AddChild(box);
        waitBoxes.Add(step,new(box,label,right));
    }

    private Label Text(string value,Color ink,bool small=false)
    {
        var label=CombatHud.Caption(value,ink,small);
        label.CustomMinimumSize=new(0,small?SmallLine:Line);
        label.ClipText=true;label.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;
        label.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        return label;
    }
    private Control TextFields(IReadOnlyList<string> fields,Color ink,bool small=false,bool right=false)
    {
        var row=new Control{CustomMinimumSize=new(0,small?SmallLine:Line),ClipContents=true,
            SizeFlagsVertical=Control.SizeFlags.ShrinkBegin,MouseFilter=Control.MouseFilterEnum.Ignore};
        var group=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
        group.AddThemeConstantOverride("separation",HudTypography.FieldGap);
        for(int i=0;i<fields.Count;i++)
        {
            var label=Text(fields[i],ink,small);
            label.SizeFlagsHorizontal=Control.SizeFlags.Fill;
            if(i==fields.Count-1)
            {
                label.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
                label.HorizontalAlignment=right?HorizontalAlignment.Right:HorizontalAlignment.Left;
            }
            else
            {
                label.ClipText=false;
                label.TextOverrunBehavior=TextServer.OverrunBehavior.NoTrimming;
            }
            group.AddChild(label);
        }
        row.AddChild(group);
        group.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        if(right)
        {
            float width=FieldsWidth(fields,small?HudTypography.Detail:HudTypography.Body);
            void Align()=>group.OffsetLeft=Math.Max(0,row.Size.X-width);
            row.Resized+=Align;Align();
        }
        return row;
    }

    private static float FieldsWidth(IReadOnlyList<string> fields,int size=HudTypography.Body)=>
        fields.Sum(value=>Width(value,size))+HudTypography.FieldGap*Math.Max(0,fields.Count-1);

    private static float Width(string value,int size=HudTypography.Body)=>
        Mathf.Ceil(HudTypography.Face().GetStringSize(value,fontSize:size).X);

    private ActionText Describe(HudGraphAction action)
    {
        bool potion=action.Name.StartsWith("用药 ",StringComparison.Ordinal);
        string name=Ui.Route(potion?action.Name[3..]:action.Name,shownPlan);
        var fields=new List<Field>{new(name,potion?HudTypography.Damage:HudTypography.Ink)};
        if(action.Target is {Length:>0} target)
        {
            fields.Add(new("→",Dim));
            fields.Add(new(Ui.Route(Ui.EnemyTarget(target,action.EnemyTargetId,targetNames),shownPlan),
                action.TargetKilled?HudTypography.Kill:Dim,action.TargetKilled));
        }
        if(action.OtherKills is {Length:>0} others)
            fields.Add(new(Ui.Route(others,shownPlan),HudTypography.Kill,true));
        string? damageValue=action.DamageAlternatives??(action.Damage>0?$"{action.Damage}":null);
        Field? damage=damageValue!=null
            ?new(Ui.T($"打{damageValue}",$"{damageValue} dmg"),HudTypography.Damage):null;
        Field? change=null;
        if(action.EndLoss is int lost && lost!=0)
            change=lost<0?new(Ui.T($"回{-lost}血",$"+{-lost} HP"),HudTypography.Heal):
                new(Ui.T($"扣{lost}血",$"Loss {lost}"),HudTypography.Loss);
        else if(action.Energy is int left)
        {
            string amount=action.EnergyAlternatives??$"{left}";
            change=new(Ui.T($"余{amount}费",$"{amount} EN"),Dim);
        }
        return new(fields.ToArray(),damage,change,
            action.Choices is {Length:>0} choices?Ui.Route(choices,shownPlan):null);
    }

    private static float FieldWidth(Field field)=>Width(field.Text)+(field.Lethal?9:1);
    private static float FirstLineWidth(Field[] fields)=>
        fields.Sum(FieldWidth)+HudTypography.FieldGap*Math.Max(0,fields.Length-1)+
        HudTypography.ExtraGapBeforeMetrics(fields.LastOrDefault() is {Lethal:false} last?last.Text:"");
    private static float DescriptionWidth(ActionText text)=>Math.Max(FirstLineWidth(text.Fields),
        text.Choice is {} choice?Width(choice,HudTypography.Detail)+HudTypography.ExtraGapBeforeMetrics(choice,HudTypography.Detail):0);
    private static float MetricWidth(Field? field)=>field==null?0:Width(field.Text,HudTypography.Detail);

    private void AddFields(PanelContainer box,ActionText text,ColumnLayout layout,bool even)
    {
        box.AddThemeStyleboxOverride("panel",HudTypography.Row(even,verticalPadding:0));
        var row=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
        row.AddThemeConstantOverride("separation",HudTypography.FieldGap);
        box.AddChild(row);
        var description=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,MouseFilter=Control.MouseFilterEnum.Ignore};
        description.AddThemeConstantOverride("separation",0);row.AddChild(description);
        var main=new Control{CustomMinimumSize=new(0,Line),ClipContents=true,MouseFilter=Control.MouseFilterEnum.Ignore};
        description.AddChild(main);
        float natural=FirstLineWidth(text.Fields);
        float offset=0;
        foreach(var field in text.Fields)
        {
            var label=Text(field.Text,field.Ink);
            Control control=label;
            if(field.Lethal)
            {
                label.CustomMinimumSize=Vector2.Zero;
                control=CombatHud.Outline(label,Ui.T("击杀","Kill"));
            }
            main.AddChild(control);
            control.AnchorBottom=1;
            control.OffsetTop=field.Lethal?1:0;
            control.OffsetBottom=field.Lethal?-1:0;
            float left=offset,end=offset+FieldWidth(field);
            control.OffsetLeft=left;
            void Fit()
            {
                float limit=main.Size.X>=natural?main.Size.X:main.Size.X-HudTypography.ExtraGapBeforeMetrics("");
                control.OffsetRight=Math.Max(left,Math.Min(limit,end));
                control.Visible=limit>left+(field.Lethal?9:0);
            }
            main.Resized+=Fit;
            Fit();
            offset=end+HudTypography.FieldGap;
        }
        if(text.Choice is {} choice)
        {
            var inset=new MarginContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
            var label=Text(choice,HudTypography.Damage,small:true);
            description.AddChild(inset);inset.AddChild(label);
            float gap=HudTypography.ExtraGapBeforeMetrics(choice,HudTypography.Detail);
            float preferred=Width(choice,HudTypography.Detail)+gap;
            void FitChoice()
            {
                int padding=(int)(preferred<=inset.Size.X?gap:HudTypography.ExtraGapBeforeMetrics("",HudTypography.Detail));
                if(!inset.HasThemeConstantOverride("margin_right") || inset.GetThemeConstant("margin_right")!=padding)
                    inset.AddThemeConstantOverride("margin_right",padding);
            }
            inset.Resized+=FitChoice;FitChoice();
        }
        if(layout.MetricsWidth==0)return;
        var divider=HudTypography.ActionMetricRule(text.Damage!=null || text.Change!=null,continuous:true);
        divider.Name="HudGraphMetricRule";
        divider.CustomMinimumSize=new(1+layout.Lead,0);
        row.AddChild(divider);
        void Metric(string name,Field? field,float width)
        {
            if(width<=0)return;
            var label=Text(field?.Text??"",field?.Ink??Dim,small:true);
            label.Name=name;
            label.SizeFlagsHorizontal=Control.SizeFlags.Fill;
            label.SizeFlagsVertical=Control.SizeFlags.ShrinkBegin;
            label.CustomMinimumSize=new(width,Line);
            row.AddChild(label);
        }
        Metric("HudGraphDamage",text.Damage,layout.Damage);
        Metric("HudGraphResource",text.Change,layout.Change);
    }

    internal void UpdateHover(Control overlay,Vector2 mouse,bool active)
    {
        if(!active || !Panel.IsVisibleInTree()){HideExpansion();return;}
        var local=overlay.GetGlobalTransform().AffineInverse();
        if(!(local*scroll.GetGlobalRect()).HasPoint(mouse)){HideExpansion();return;}
        var tile=tiles.Values.Concat(headerTiles).FirstOrDefault(t=>t.Width>t.Box.Size.X+1 && (local*t.Box.GetGlobalRect()).HasPoint(mouse));
        if(tile==null){HideExpansion();return;}
        if(!ReferenceEquals(tile,expanded))
        {
            HideExpansion();expanded=tile;
            expansion=Cell(Line);
            expansion.Name="HudGraphExpandedAction";expansion.ZIndex=10;
            if(tile.Header)
            {
                expansion.AddThemeStyleboxOverride("panel",tile.Box.GetThemeStylebox("panel"));
                expansion.AddChild(TextFields(tile.Text.Fields.Select(field=>field.Text).ToArray(),HudTypography.Ink));
                CombatHud.SeatUnderline(expansion,headerTiles.IndexOf(tile) is int index &&
                    index>=0 && shown?.Party[visibleSeats[index]].Id==me,HudTypography.Ink);
            }
            else AddFields(expansion,tile.Text,tile.Layout,tile.Even);
            tile.Box.Modulate=Colors.Transparent;
            overlay.AddChild(expansion);
        }
        var rect=local*tile.Box.GetGlobalRect();
        float width=Math.Min(overlay.Size.X,Math.Max(rect.Size.X,tile.Width));
        expansion!.Size=new(width,rect.Size.Y);
        expansion.Position=new(Math.Clamp(rect.Position.X,0,Math.Max(0,overlay.Size.X-width)),rect.Position.Y);
    }

    private void DrawConnectors()
    {
        if(shown==null)return;
        var local=grid.GetGlobalTransform().AffineInverse();
        for(int i=1;i<columns && i<grid.GetChildCount();i++)
        {
            var title=local*grid.GetChild<Control>(i).GetGlobalRect();
            float x=title.Position.X-Gap/2f;
            HudTypography.DrawVerticalRule(grid,x,grid.Size.Y,new Color("#9aafb867"));
        }
        foreach(var (before,after) in shown.Dependencies)
        {
            if(!tiles.TryGetValue(after,out var destination) || !waitBoxes.TryGetValue(after,out var wait))continue;
            var r=local*destination.Box.GetGlobalRect();
            var strip=local*wait.Box.GetGlobalRect();
            var text=local*wait.Label.GetGlobalRect();
            float arrowX=r.GetCenter().X;
            if(arrowX>text.Position.X-6 && arrowX<text.End.X+6)
                arrowX=wait.Right?strip.Position.X+12:strip.End.X-12;
            var end=new Vector2(arrowX,r.Position.Y);
            var headBase=end+new Vector2(0,-ArrowDepth);
            float y=Math.Min(strip.GetCenter().Y,headBase.Y);
            float lineStart=wait.Right?text.Position.X-4:text.End.X+4;
            if(tiles.TryGetValue(before,out var source))
            {
                var from=local*source.Box.GetGlobalRect();
                float side=from.Position.X<r.Position.X?1:-1;
                var start=new Vector2(side>0?from.End.X:from.Position.X,from.GetCenter().Y);
                lineStart=start.X+side*Gap*.35f;
                grid.DrawPolyline(new[]{start,new Vector2(lineStart,start.Y),new Vector2(lineStart,y)},DependencyColor,1.3f,true);
            }
            float left=Math.Min(lineStart,arrowX),right=Math.Max(lineStart,arrowX);
            float beforeText=Math.Min(right,text.Position.X-4),afterText=Math.Max(left,text.End.X+4);
            if(left<beforeText)grid.DrawLine(new(left,y),new(beforeText,y),DependencyColor,1.3f,true);
            if(afterText<right)grid.DrawLine(new(afterText,y),new(right,y),DependencyColor,1.3f,true);
            grid.DrawLine(new Vector2(arrowX,y),headBase,DependencyColor,1.3f,true);
            grid.DrawColoredPolygon(new[]{end,headBase+new Vector2(-ArrowHalfWidth,0),headBase+new Vector2(ArrowHalfWidth,0)},DependencyColor);
        }
    }
}
