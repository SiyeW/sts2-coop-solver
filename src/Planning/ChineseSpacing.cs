using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace CoopSolver;

// Layout-only negative advances keep the original punctuation outlines. The fallback
// contains no drawing and belongs only to the HUD font, including its measuring copy.
internal static class ChineseSpacing
{
    internal const string Opening="（［｛【〔〈《「『〖“‘";
    internal const string Closing="）］｝】〕〉》」』〗”’";
    private const string Stops="、，。．：；！？";
    private const int Em=64,First=0xf0000;
    private static readonly string[] advances={char.ConvertFromUtf32(First),char.ConvertFromUtf32(First+1),
        char.ConvertFromUtf32(First+2),char.ConvertFromUtf32(First+3)};
    private static readonly Dictionary<(char,char),string> adjustments=new();
    private static readonly Dictionary<(char,bool),float> edgeGaps=new();
    private static FontFile? spacing;
    internal static void Clear(){adjustments.Clear();edgeGaps.Clear();}
    internal static void Bind(FontVariation face)
    {
        var features=face.OpentypeFeatures.Duplicate();
        features[TextServerManager.GetPrimaryInterface().NameToTag("chws")]=1;face.OpentypeFeatures=features;
        if(spacing==null)
        {
            spacing=new FontFile{FixedSize=Em,FixedSizeScaleMode=TextServer.FixedSizeScaleMode.Enabled};
            for(int i=0;i<advances.Length;i++)
            {
                spacing.SetGlyphAdvance(0,Em,First+i,new Vector2(-(i+1)*Em/4,0));
                spacing.SetGlyphSize(0,new Vector2I(Em,0),First+i,Vector2.Zero);
            }
        }
        var fallback=face.Fallbacks.Duplicate();fallback.Insert(0,spacing);face.Fallbacks=fallback;
    }

    private static bool Close(char c)=>Closing.Contains(c) || Stops.Contains(c);
    private static bool Pair(char left,char right)=>Opening.Contains(right)?Opening.Contains(left) || Close(left):Close(left) && Close(right);
    private static bool Ink(Font face,char c,out float start,out float end)
    {
        var ts=TextServerManager.GetPrimaryInterface();
        foreach(var rid in face.GetRids())
        {
            long glyph=ts.FontGetGlyphIndex(rid,Em,c,0);
            if(glyph==0)continue;
            var outline=ts.FontGetGlyphContours(rid,Em,glyph);
            if(!outline.TryGetValue("points",out var points))continue;
            start=float.PositiveInfinity;end=float.NegativeInfinity;
            foreach(var point in points.AsVector3Array()){start=Math.Min(start,point.X);end=Math.Max(end,point.X);}
            return end>start;
        }
        start=end=0;return false;
    }
    internal static float EdgeBlank(string text,int fontSize,bool leading)
    {
        if(Ui.English || text.Length==0)return 0;
        char edge=leading?text[0]:text[^1];
        if(leading?!Opening.Contains(edge):!Close(edge))return 0;
        var face=HudTypography.Face();
        if(!edgeGaps.TryGetValue((edge,leading),out float gap))
        {
            gap=Ink(face,edge,out float start,out float end)
                ?Math.Max(0,leading?start:face.GetStringSize(edge.ToString(),fontSize:Em).X-end):0;
            edgeGaps[(edge,leading)]=gap;
        }
        return gap*fontSize/Em;
    }
    private static string Adjustment(char left,char right)
    {
        if(adjustments.TryGetValue((left,right),out var saved))return saved;
        var face=HudTypography.Face();
        string pair=string.Concat(left,right);
        float Width(string s)=>face.GetStringSize(s,fontSize:Em).X;
        float leftWidth=Width(left.ToString());
        string adjustment="";
        // A font that already positions this pair supplies its own spacing. For
        // other pairs remove only measured blank space, keeping a small ink gap.
        if(Width(pair)>=leftWidth+Width(right.ToString())-2 && Ink(face,left,out _,out float end) && Ink(face,right,out float start,out _))
        {
            float gap=leftWidth-end+start;
            int quarters=Math.Clamp((int)Math.Floor((gap-Em*.09f)/(Em/4)),0,advances.Length);
            if(quarters>0)adjustment=advances[quarters-1];
        }
        return adjustments[(left,right)]=adjustment;
    }

    internal static string Format(string text)
    {
        if(Ui.English)return text;
        StringBuilder? result=null;int last=0;
        for(int i=0;i<text.Length-1;i++)
        {
            if(!Close(text[i]) && !Opening.Contains(text[i]))continue;
            int next=i+1;
            while(next<text.Length && (text[next]==' ' || text[next]=='　'))next++;
            if(next==text.Length || !Pair(text[i],text[next]))continue;
            string adjustment=Adjustment(text[i],text[next]);
            if(next==i+1 && adjustment.Length==0)continue;
            result??=new StringBuilder(text.Length+8);
            result.Append(text,last,i+1-last).Append(adjustment);
            last=next;i=next-1;
        }
        return result==null?text:result.Append(text,last,text.Length-last).ToString();
    }

    internal static string WithoutAdvances(string text)
    {
        if(!text.Contains(advances[0][0]))return text;
        foreach(var advance in advances)text=text.Replace(advance,"");
        return text;
    }
}
