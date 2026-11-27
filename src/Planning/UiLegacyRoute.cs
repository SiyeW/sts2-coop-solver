using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CoopSolver;

internal static partial class Ui
{
    private static readonly Dictionary<string,string> legacyRouteWords=new(StringComparer.Ordinal)
    {
        ["选牌中 · "]="format.029",
        ["正在计算当前选牌"]="format.028",
        ["正在核对可选牌"]="format.027",
        ["本回合建议"]="format.026",
        ["跳过选择"]="format.025",
        ["添加重放 "]="format.024",
        ["弃掉 "]="format.023",
        ["取回 "]="format.022",
        ["消耗 "]="format.021",
        ["选择 "]="format.020",
        [" 选 "]="format.019",
        ["已离场"]="format.007",
        ["历史站位未记录"]="format.008",
        ["站位待确认"]="format.009",
    };
    private static readonly Regex legacyRoutePattern=new(
        @"药水槽 (?<slot>\d+)|手牌(?<hand>\d+)|第(?<copy>\d+)张|选项(?<option>\d+)|(?<energy>-?\d+|X)费|(?<replay>[（、])重放|"+
        string.Join("|",legacyRouteWords.Keys.OrderByDescending(word=>word.Length).Select(Regex.Escape)),
        RegexOptions.Compiled);

    internal static string Route(string raw,HudPlan? plan)
    {
        if(raw.Contains("手牌"))raw=Regex.Replace(raw,@"（手牌\d+）|、手牌\d+(?=）)","");
        if(raw.Contains("魂缚") || raw.Contains("Bound"))
            raw=Regex.Replace(raw,@"(?<=[（、])(魂缚|Bound) -?\d+(?=[、）])","$1");
        if(plan?.Terms is {Length:>0} terms && plan.Language!=Language)
            raw=names.GetValue(terms,t=>new(t)).Apply(raw);
        if(raw=="结束回合")return Text("route.end_turn");
        if(raw=="选择")return Text("route.choice");
        string prefix="";
        if(raw.StartsWith("选 ",StringComparison.Ordinal)){prefix=Text("route.choose");raw=raw[2..];}
        if(raw.StartsWith("用药 ",StringComparison.Ordinal)){prefix=Text("route.use");raw=raw[3..];}
        if(!UsesHanSpacing)
            raw=Regex.Replace(raw,@"（([左右])(\d+)(?:上(\d+))?）",m=>
                " ("+(m.Groups[1].Value=="左"?"L":"R")+m.Groups[2].Value+(m.Groups[3].Success?" · "+m.Groups[3].Value:"")+")");
        raw=legacyRoutePattern.Replace(raw,match=>
        {
            foreach(var (name,key) in LegacyArguments)
                if(match.Groups[name].Success)return Text(key,match.Groups[name].Value);
            if(match.Groups["replay"].Success)
            {
                string bracket=match.Groups["replay"].Value;
                return bracket+Text(bracket=="（"?"format.018":"format.017");
            }
            return Text(legacyRouteWords[match.Value]);
        });
        return prefix+(UsesHanSpacing?Punctuation(raw):raw.Replace("（"," (").Replace("）",")").Replace("、",Text("ui.044")));
    }

    private static readonly (string Group,string Key)[] LegacyArguments=
    {
        ("slot","format.006"),("hand","format.010"),("copy","format.011"),("option","format.012"),("energy","format.013")
    };
}
