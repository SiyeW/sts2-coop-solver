using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoopSolver;

// Prefer complete card names and modifier clauses. If a clause exceeds the column,
// break at grapheme boundaries, respecting opening/closing punctuation and digit groups.
internal static class RouteTextWrap
{
    internal static string Fit(string text,float width,Func<string,float> measure)
    {
        if(text.Length==0 || measure(text)<=width)return text;
        // Re-shape each new line so a boundary cannot leave a negative advance at either end.
        var plain=ChineseSpacing.WithoutAdvances(text);
        bool spaced=plain!=text;text=plain;
        string Render(string line)=>spaced?Ui.Punctuation(line):line;
        var offsets=StringInfo.ParseCombiningCharacters(text);
        int n=offsets.Length;
        int At(int k)=>k==n?text.Length:offsets[k];
        const string opening=ChineseSpacing.Opening+"([";
        const string closing=ChineseSpacing.Closing+")]、，。！？：；,.!?:;%､｡";
        var cost=new double[n+1];var next=new int[n];
        for(int i=n-1;i>=0;i--)
        {
            cost[i]=double.PositiveInfinity;
            for(int j=i+1;j<=n;j++)
            {
                var line=text[At(i)..At(j)].Trim();
                if(line.Length==0)continue;
                float used=measure(Render(line));
                if(used>width && !double.IsPositiveInfinity(cost[i]))break;
                char last=line[^1];
                int k=j;while(k<n && char.IsWhiteSpace(text[At(k)]))k++;
                char first=k==n?'\0':text[At(k)];
                if(opening.Contains(last) && k<n || closing.Contains(first))continue;
                bool primary=k==n || opening.Contains(first) || "、,，､".Contains(last) ||
                    (k>j || char.IsWhiteSpace(text[At(j)-1])) && !char.IsDigit(first);
                double score=cost[k]+1+(primary?0:10000)+(line.Length==1 && n>1?1000:0)
                    +(k<n && char.IsAsciiLetterOrDigit(last) && char.IsAsciiLetterOrDigit(first)?10000:0)
                    +Math.Pow(Math.Max(0,width-used)/Math.Max(1,width),2)
                    +Math.Max(0,used-width)*100000;
                if(score<cost[i]){cost[i]=score;next[i]=k;}
            }
            if(next[i]==0){next[i]=n;cost[i]=1e12;}
        }
        var lines=new List<string>();
        for(int i=0;i<n;i=next[i])lines.Add(Render(text[At(i)..At(next[i])].Trim()));
        return string.Join("\n",lines);
    }
}
