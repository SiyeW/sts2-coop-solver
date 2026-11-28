using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CoopSolver;

internal readonly record struct HudTargetLabel(string? Prefix,string Name,int Seat=0)
{
    private static readonly Regex player=new(@"^P(?<seat>\d+)(?:\s|$)",RegexOptions.Compiled);
    internal bool HasParenthesizedName=>Prefix!=null && Name.Length>0;

    internal static HudTargetLabel? For(HudTarget? target,string? legacy,HudPlan? plan,string? live=null,HudSeat[]? party=null)
    {
        int seat=target?.Seat??0;
        if(target==null && legacy!=null)
            int.TryParse(player.Match(legacy).Groups["seat"].Value,NumberStyles.None,CultureInfo.InvariantCulture,out seat);
        if(seat>0)
        {
            var players=party??plan?.Party;
            return new($"P{seat}",players!=null && seat<=players.Length?players[seat-1].Name:"",seat);
        }
        if(live!=null || target==null && legacy!=null)
        {
            var position=HudPosition.Read(live??legacy!,out string name);
            const string unconfirmed="（站位待确认）";
            if(name.EndsWith(unconfirmed,StringComparison.Ordinal))name=name[..^unconfirmed.Length];
            return new(position?.Label(),Ui.Route(name,live==null?plan:null));
        }
        return target==null?null:new(target.Position?.Label(),target.Name?.Read()??target.UnknownName??Ui.Missing);
    }

    private string Format(string name)=>Ui.Punctuation(Prefix==null?name:
        name.Length==0?Prefix:Ui.Text("target.named",Prefix,name));
    internal string Minimum=>Format(HasParenthesizedName?"…":Name);
    internal string Text()=>Fit(static value=>HudTypography.Face().GetStringSize(value,fontSize:HudTypography.Body).X);
    internal string Fit(Func<string,float> width,float available=float.MaxValue)
    {
        string full=Format(Name);
        if(!HasParenthesizedName || (width(Name)<=HudTypography.Body*10 && width(full)<=available))return full;
        string original=Name;
        string Short(int length)=>original[..length].TrimEnd()+"…";
        var elements=StringInfo.ParseCombiningCharacters(original);
        int low=0,high=elements.Length-1;
        while(low<high)
        {
            int middle=(low+high+1)/2;
            string candidate=Short(elements[middle]);
            if(width(candidate)<=HudTypography.Body*10 && width(Format(candidate))<=available)low=middle;
            else high=middle-1;
        }
        string shortened=Format(Short(elements[low]));
        return width(shortened)<=available?shortened:Format("");
    }
}
