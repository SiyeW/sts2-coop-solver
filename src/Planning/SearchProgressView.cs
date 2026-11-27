using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace CoopSolver;

internal sealed class SearchProgressView
{
    private static readonly Color Grid=new("#8ca2af30"),AxisInk=new("#c0cdd2"),
        Boundary=new("#adbcc6a8"),Trace=new Color(new Color("#fcffff"),.70f),Stripe=new("#a8646850");
    private const int FontSize=HudTypography.Detail;
    private readonly PanelContainer tip=new(){Name="HudSearchProgressTooltip",Visible=false,MouseFilter=Control.MouseFilterEnum.Ignore,ZIndex=12};
    private readonly Func<Color> backdrop;
    private readonly VBoxContainer detail=new(){MouseFilter=Control.MouseFilterEnum.Ignore};
    internal readonly Control Panel=new(){Name="HudSearchProgress",Visible=false,MouseFilter=Control.MouseFilterEnum.Ignore,
        SizeFlagsVertical=Control.SizeFlags.ExpandFill,CustomMinimumSize=new(0,212)};
    internal const float PreferredHeight=385;
    private HudSearchHistory? history;
    private HudSeat[] seats=Array.Empty<HudSeat>();
    private Rect2 resultPlot,memoryPlot;
    private float? cursor;
    private HudSearchHistory? hovered;
    private long hoveredWork;
    private bool hoveredResult;

    private readonly record struct Axis(double Top,double Step,int Count);
    private static Axis Marks(double maximum,int intervals)
    {
        double rough=Math.Max(1,maximum)/Math.Max(1,intervals);
        double magnitude=Math.Pow(10,Math.Floor(Math.Log10(rough)));
        double multiple=rough/magnitude;
        double step=Math.Max(1,magnitude*(multiple<=1.4?1:multiple<=2.8?2:multiple<=7?5:10));
        int count=Math.Max(1,(int)Math.Ceiling(maximum/step));
        return new(step*count,step,count);
    }

    internal static IEnumerable<double> InteriorTicks(double end,double step)
    {
        for(int index=1;index*step<end;index++)yield return index*step;
    }

    internal SearchProgressView(Control overlay,Func<Color> backdrop)
    {
        this.backdrop=backdrop;
        Panel.Draw+=Draw;
        Panel.Resized+=Panel.QueueRedraw;
        tip.AddThemeStyleboxOverride("panel",HudSurface.Box("#17252ffd","#93aab683",9,5));
        detail.AddThemeConstantOverride("separation",2);
        tip.AddChild(detail);overlay.AddChild(tip);
    }

    internal void Set(HudSearchHistory? data,HudSeat[]? party)
    {
        party??=Array.Empty<HudSeat>();
        if(party.Length==0 && data!=null)party=seats;
        if(ReferenceEquals(history,data) && (ReferenceEquals(seats,party) || seats.Length==party.Length &&
            seats.Zip(party,(before,after)=>before.Id==after.Id && before.Name==after.Name && before.Color==after.Color).All(same=>same)))return;
        history=data;seats=party;
        Panel.QueueRedraw();
    }

    private static string Number(double n)=>n.ToString("0",CultureInfo.InvariantCulture);
    private static string Remaining(double n)=>Ui.Text("ui.108",Number(n));
    private static string Loss(double n)=>Ui.Text("ui.109",Number(n));
    private static float TextWidth(Font font,string text)=>font.GetStringSize(text,fontSize:FontSize).X;

    private void Draw()
    {
        var size=Panel.Size;
        resultPlot=memoryPlot=default;
        var font=Panel.GetThemeFont("font","Label");
        float line=Mathf.Ceil(font.GetHeight(FontSize)),gap=line+4;
        float top=line/2+2,bottom=line+FontSize+10;
        float usable=size.Y-top-bottom-gap;
        if(usable<12 || size.X<125)return;
        float main=usable*2/3f,mini=usable-main;
        var output=history?.Results??Array.Empty<SearchResultPoint>();
        var bytes=history?.Memory??Array.Empty<SearchMemoryPoint>();
        bool hasLoss=output.Any(p=>p.Loss.HasValue),hasEnemy=output.Any(p=>p.EnemyHp.HasValue);
        double largestLoss=output.Select(p=>p.Loss.GetValueOrDefault()).DefaultIfEmpty(0).Max();
        double largestEnemy=output.Select(p=>p.EnemyHp.GetValueOrDefault()).DefaultIfEmpty(0).Max();
        double cap=history?.LimitMb??0;
        double largestTree=cap>0?cap:bytes.Select(p=>p.Bytes/1_000_000d).DefaultIfEmpty(0).Max();
        int slots=Math.Clamp((int)(main/(line+7)),hasEnemy && hasLoss?4:1,10);
        int enemySlots=slots;
        if(hasEnemy && hasLoss)
        {
            double defeatWeight=Math.Sqrt(Math.Max(1,largestEnemy)),winWeight=Math.Sqrt(Math.Max(1,largestLoss));
            int minimum=Math.Max(2,(slots+2)/3);
            enemySlots=Math.Clamp((int)Math.Round(slots*defeatWeight/(defeatWeight+winWeight)),minimum,slots-minimum);
        }
        var enemy=Marks(largestEnemy,enemySlots);
        var loss=Marks(largestLoss,hasEnemy?slots-enemySlots:slots);
        var tree=Marks(largestTree,Math.Clamp((int)(mini/(line+4)),1,5));
        double treeTop=cap>0?cap:tree.Top;
        float memoryLabels=TextWidth(font,Number(treeTop));
        float resultLabels=Math.Max(hasEnemy?TextWidth(font,Remaining(enemy.Top)):0,hasLoss?TextWidth(font,Loss(loss.Top)):0);
        float labels=Math.Max(memoryLabels,resultLabels);
        string resultTitle=Ui.Text("ui.110");
        string memoryTitle=Ui.Text("ui.111");
        float resultTitleWidth=TextWidth(font,resultTitle),memoryTitleWidth=TextWidth(font,memoryTitle);
        float titleGap=line*.72f;
        bool separateTitles=resultTitleWidth+memoryTitleWidth+titleGap+4>size.Y;
        float left=(separateTitles?2*line:line)+9,right=size.X-labels-13;
        if(right-left<25)return;
        resultPlot=new(left,top,right-left,main);
        memoryPlot=new(left,top+main+gap,right-left,mini);

        float separator=resultPlot.End.Y,winTop=top;
        if(hasEnemy && hasLoss)
        {
            // Both ranges use a common visual tick spacing. Leave the
            // divider itself unlabeled, then start the loss range below it.
            float space=main/(enemy.Count+loss.Count+.62f);
            separator=top+space*enemy.Count;
            winTop=separator+.62f*space;
        }
        float EnemyY(double n)=>separator-(separator-top)*(float)Math.Clamp(n/enemy.Top,0,1);
        float LossY(double n)=>resultPlot.End.Y-(resultPlot.End.Y-winTop)*(float)Math.Clamp(n/loss.Top,0,1);
        float MemoryY(double n)=>memoryPlot.End.Y-mini*(float)Math.Clamp(n/treeTop,0,1);
        float X(double n)=>left+(right-left)*(float)Math.Clamp(n/Math.Max(1,history?.Visits??0),0,1);

        if(hasEnemy)Hatch(new(left,top,right-left,separator-top));
        for(int i=0;hasEnemy && i<=enemy.Count;i++)
        {
            var value=(enemy.Count-i)*enemy.Step;
            if(value==0 && hasLoss)continue;
            Tick(font,resultPlot,EnemyY(value),Remaining(value));
        }
        for(int i=0;hasLoss && i<=loss.Count;i++)
        {
            var value=(loss.Count-i)*loss.Step;
            Tick(font,resultPlot,LossY(value),Loss(value),boundary:value==0);
        }
        Tick(font,memoryPlot,MemoryY(treeTop),Number(treeTop),boundary:cap>0);
        Tick(font,memoryPlot,MemoryY(0),Number(0),boundary:true);
        foreach(double value in InteriorTicks(treeTop,tree.Step))
        {
            float y=MemoryY(value);
            string? label=y-memoryPlot.Position.Y>line*.9f?Number(value):null;
            Tick(font,memoryPlot,y,label);
        }
        if(hasEnemy && hasLoss)Horizontal(resultPlot,separator,Boundary);

        Panel.DrawLine(new(right,resultPlot.Position.Y),resultPlot.End,Boundary,1,true);
        Panel.DrawLine(new(right,memoryPlot.Position.Y),memoryPlot.End,Boundary,1,true);
        float resultEnd=resultPlot.GetCenter().Y+resultTitleWidth/2;
        float memoryEnd=memoryPlot.GetCenter().Y+memoryTitleWidth/2;
        if(separateTitles)
        {
            resultEnd=Math.Min(size.Y-2,Math.Max(2+resultTitleWidth,resultEnd));
            memoryEnd=Math.Min(size.Y-2,Math.Max(2+memoryTitleWidth,memoryEnd));
        }
        else
        {
            memoryEnd=Math.Clamp(memoryEnd,2+resultTitleWidth+titleGap+memoryTitleWidth,size.Y-2);
            resultEnd=Math.Clamp(resultEnd,2+resultTitleWidth,memoryEnd-memoryTitleWidth-titleGap);
        }
        float titleX=font.GetAscent(FontSize)+3;
        VerticalTitle(font,resultTitle,titleX,resultEnd);
        VerticalTitle(font,memoryTitle,titleX+(separateTitles?line:0),memoryEnd);

        string xTitle=Ui.Text("ui.112");
        float xLabelY=memoryPlot.End.Y+font.GetAscent(FontSize)+4;
        float titleY=xLabelY+FontSize+5;
        Panel.DrawString(font,new((left+right-TextWidth(font,xTitle))/2,titleY),xTitle,fontSize:FontSize,modulate:AxisInk);
        string Work(long n)=>n==0?"0":Advisor.Compact(n);
        var max=history?.Visits??0;
        Panel.DrawString(font,new(left,xLabelY),"0",fontSize:FontSize,modulate:AxisInk);
        if(max>0)
        {
            string end=Work(max);
            float endWidth=TextWidth(font,end);
            Panel.DrawString(font,new(right-endWidth,xLabelY),end,fontSize:FontSize,modulate:AxisInk);
            var ticks=Marks(max,Math.Clamp((int)((right-left)/105),1,8));
            foreach(double at in InteriorTicks(max,ticks.Step))
            {
                float x=X(at);
                Panel.DrawLine(new(x,resultPlot.Position.Y),new(x,resultPlot.End.Y),Grid,1,true);
                Panel.DrawLine(new(x,memoryPlot.Position.Y),new(x,memoryPlot.End.Y),Grid,1,true);
                string label=Work((long)at);
                float width=TextWidth(font,label);
                if(x-width/2<left+TextWidth(font,"0")+8 || x+width/2>right-endWidth-8)continue;
                Panel.DrawString(font,new(x-width/2,xLabelY),label,fontSize:FontSize,modulate:AxisInk);
            }
        }

        // Character traces sit below the white published recommendation.
        if(hasLoss && seats.Length>1)
        {
            var background=backdrop();
            for(int seat=0;seat<seats.Length;seat++)
            {
                int index=seat;
                var color=SeatTraceInk(HudRouteView.SeatColor(seats[seat]),background);
                DrawSteps(output,p=>p.Visits,p=>p.SeatLoss.ElementAtOrDefault(index) is int n?LossY(n):null,X,max,color,1.15f);
            }
        }
        DrawSteps(output,p=>p.Visits,p=>p.Loss is int lost?LossY(lost):p.EnemyHp is int hp?EnemyY(hp):null,X,max,Trace,2.35f);
        DrawMemory(bytes,X,MemoryY,treeTop*1_000_000d);
        if(cursor is float guide)
        {
            DashedGuide(resultPlot,guide);
            DashedGuide(memoryPlot,guide);
        }
    }

    private static Color SeatTraceInk(Color ink,Color background)
    {
        const float previousAlpha=.80f,alpha=.75f;
        // Preserve a single line's composed color on the HUD background.
        float Adjust(float value,float under)=>Math.Clamp(under+(value-under)*(previousAlpha/alpha),0,1);
        return new(Adjust(ink.R,background.R),Adjust(ink.G,background.G),Adjust(ink.B,background.B),alpha);
    }

    private void Tick(Font font,Rect2 plot,float y,string? label,bool boundary=false)
    {
        Horizontal(plot,y,boundary?Boundary:Grid);
        if(label==null)return;
        float baseline=y+(font.GetAscent(FontSize)-font.GetDescent(FontSize))/2;
        Panel.DrawString(font,new(plot.End.X+7,baseline),label,fontSize:FontSize,modulate:AxisInk);
    }

    private void Horizontal(Rect2 plot,float y,Color color)=>
        Panel.DrawLine(new(plot.Position.X,y),new(plot.End.X,y),color,1,true);

    private void VerticalTitle(Font font,string text,float x,float bottom)
    {
        Panel.DrawSetTransform(new(x,bottom),-Mathf.Pi/2);
        Panel.DrawString(font,Vector2.Zero,text,fontSize:FontSize,modulate:AxisInk);
        Panel.DrawSetTransform(Vector2.Zero);
    }

    private void Hatch(Rect2 area)
    {
        Panel.DrawRect(area,new("#723f4428"));
        for(float x=area.Position.X-area.Size.Y;x<area.End.X;x+=13)
        {
            float first=Math.Max(0,area.Position.X-x),last=Math.Min(area.Size.Y,area.End.X-x);
            if(last>first)Panel.DrawLine(new(x+first,area.End.Y-first),new(x+last,area.End.Y-last),Stripe,1,true);
        }
    }

    private void DrawSteps<T>(T[] samples,Func<T,long> work,Func<T,float?> value,Func<double,float> x,long end,Color ink,float width)
    {
        float? previous=null;
        float start=0;
        foreach(var sample in samples)
        {
            float at=x(work(sample));
            var y=value(sample);
            if(previous is float last)
            {
                Panel.DrawLine(new(start,last),new(at,last),ink,width,true);
                if(y is float next && next!=last)Panel.DrawLine(new(at,last),new(at,next),ink,width,true);
            }
            previous=y;start=at;
        }
        if(previous is not float final)return;
        float right=x(end);
        float radius=width>=2?3.4f:2.8f;
        if(right-radius>start)Panel.DrawLine(new(start,final),new(right-radius,final),ink,width,true);
        EndMarker(new(right,final),radius,ink,width);
    }

    private void DrawMemory(SearchMemoryPoint[] points,Func<double,float> x,Func<double,float> y,double cap)
    {
        if(points.Length==0)return;
        const float width=1.65f,radius=2.8f;
        Vector2 Position(SearchMemoryPoint p)=>new(x(p.Visits),y(p.Bytes/1_000_000d));
        for(int i=1;i<points.Length;i++)
        {
            var a=points[i-1];var b=points[i];
            var from=Position(a);var to=Position(b);
            if((a.Bytes>cap)!=(b.Bytes>cap))
            {
                // Clip at the actual crossing, so a sample above the visible
                // range is drawn along the cap until the segment drops below.
                double fraction=(cap-a.Bytes)/((double)b.Bytes-a.Bytes);
                var crossing=new Vector2(x(a.Visits+(b.Visits-a.Visits)*fraction),y(cap/1_000_000d));
                if(from!=crossing)Panel.DrawLine(from,crossing,Trace,width,true);
                from=crossing;
            }
            if(i==points.Length-1 && (to-from).Length()>radius)
                to-=(to-from).Normalized()*radius;
            if(from!=to)Panel.DrawLine(from,to,Trace,width,true);
        }
        EndMarker(Position(points[^1]),radius,Trace,width);
    }

    private void EndMarker(Vector2 point,float radius,Color ink,float width)=>
        Panel.DrawArc(point,radius,0,Mathf.Tau,20,ink,Math.Max(1,width*.7f),true);

    private void DashedGuide(Rect2 plot,float x)
    {
        x=Math.Clamp(x,plot.Position.X,plot.End.X);
        for(float y=plot.Position.Y;y<plot.End.Y;y+=9)
            Panel.DrawLine(new(x,y),new(x,Math.Min(y+4,plot.End.Y)),new("#e9e9e5a0"),1,true);
    }

    private static int After<T>(T[] points,Func<T,long> key,long work)
    {
        int low=0,high=points.Length;
        while(low<high)
        {
            int middle=(low+high)/2;
            if(key(points[middle])<=work)low=middle+1;else high=middle;
        }
        return low;
    }

    private static T? At<T>(T[] points,Func<T,long> key,long work) where T:class
    {
        int index=After(points,key,work);
        return index==0?null:points[index-1];
    }

    private static List<string[]> TooltipRows(HudSearchHistory trace,long work,bool onResult)
    {
        var rows=new List<string[]>{new[]{Ui.Text("ui.113",work)}};
        if(onResult && At(trace.Results,p=>p.Visits,work) is {} point)
        {
            string HpChange(int n)=>n<0?Ui.Text("ui.114",-n):Ui.Text("ui.115",n);
            if(point.EnemyHp is int enemy)rows.Add(new[]{Ui.Text("ui.116",enemy)});
            int? teamLoss=point.Loss;
            if(teamLoss==null && point.SeatLoss.All(v=>v.HasValue))teamLoss=point.SeatLoss.Sum(v=>v.GetValueOrDefault());
            var total=new List<string>(2);
            if(teamLoss is int loss)total.Add(HpChange(loss));
            if(point.Potions.All(v=>v.HasValue))
            {
                int bottles=point.Potions.Sum(v=>v.GetValueOrDefault());
                total.Add(Ui.Text("ui.117",bottles));
            }
            if(total.Count>0)rows.Add(total.ToArray());
            if(point.SeatLoss.Length>1)
                for(int i=0;i<point.SeatLoss.Length;i++)
                {
                    string seat=$"P{i+1}",change=point.SeatLoss[i] is int n?HpChange(n):Ui.Missing;
                    rows.Add(point.Potions[i] is int pots?new[]{seat,change,Ui.Text("ui.118",pots)}:new[]{seat,change});
                }
        }
        if(!onResult && At(trace.Memory,p=>p.Visits,work) is {} sample)
            rows.Add(new[]{$"{sample.Bytes/1_000_000d:0.##} MB"});
        return rows;
    }

    private void SetTooltip(IReadOnlyList<string[]> rows)
    {
        for(int i=0;i<Math.Max(detail.GetChildCount(),rows.Count);i++)
        {
            if(i==detail.GetChildCount())
            {
                var group=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
                group.AddThemeConstantOverride("separation",HudTypography.FieldGap);
                detail.AddChild(group);
            }
            var row=detail.GetChild<HBoxContainer>(i);
            row.Visible=i<rows.Count;
            if(!row.Visible)continue;
            var cells=rows[i];
            for(int j=0;j<Math.Max(cells.Length,row.GetChildCount());j++)
            {
                if(j==row.GetChildCount())row.AddChild(CombatHud.Caption("",HudTypography.Ink,small:true));
                var label=row.GetChild<Label>(j);
                label.Visible=j<cells.Length;
                if(label.Visible)label.Text=cells[j];
            }
        }
        tip.Show();tip.ResetSize();
    }

    internal void Hover(Control overlay,Vector2 mouse,bool active)
    {
        if(!active || history is not {Visits:>0} trace || !Panel.IsVisibleInTree())
        {HideTip();return;}
        var intoPanel=(overlay.GetGlobalTransform().AffineInverse()*Panel.GetGlobalTransform()).AffineInverse();
        Vector2 where=intoPanel*mouse;
        bool onResult=resultPlot.HasPoint(where);
        var plot=onResult?resultPlot:memoryPlot;
        if(!plot.HasPoint(where)){HideTip();return;}
        double fraction=(where.X-plot.Position.X)/(double)plot.Size.X;
        long work=fraction>=1?trace.Visits:(long)Math.Round(trace.Visits*fraction);
        float guide=where.X;
        if(!onResult && trace.Memory.Length>0)
        {
            int after=After(trace.Memory,p=>p.Visits,work);
            if(after==0 || after<trace.Memory.Length && trace.Memory[after].Visits-work<work-trace.Memory[after-1].Visits)
                work=trace.Memory[after].Visits;
            else work=trace.Memory[after-1].Visits;
            guide=plot.Position.X+plot.Size.X*(float)(work/(double)trace.Visits);
        }
        if(cursor!=guide){cursor=guide;Panel.QueueRedraw();}
        if(!tip.Visible || !ReferenceEquals(hovered,trace) || hoveredWork!=work || hoveredResult!=onResult)
        {
            hovered=trace;hoveredWork=work;hoveredResult=onResult;
            SetTooltip(TooltipRows(trace,work,onResult));
        }
        var extent=tip.GetCombinedMinimumSize();
        var chart=overlay.GetGlobalTransform().AffineInverse()*Panel.GetGlobalRect();
        float right=chart.End.X+12,left=chart.Position.X-extent.X-12;
        float tx=right+extent.X<=overlay.Size.X?right:left>=0?left:
            mouse.X+15+extent.X<=overlay.Size.X?mouse.X+15:mouse.X-15-extent.X;
        float ty=mouse.Y+18+extent.Y<=overlay.Size.Y?mouse.Y+18:mouse.Y-18-extent.Y;
        tip.Position=new(Math.Clamp(tx,0,Math.Max(0,overlay.Size.X-extent.X)),
            Math.Clamp(ty,0,Math.Max(0,overlay.Size.Y-extent.Y)));
    }

    internal void HideTip()
    {
        tip.Hide();hovered=null;
        if(cursor.HasValue){cursor=null;Panel.QueueRedraw();}
    }
}
