using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed record HudTerm(string Source,string Table,string Key);
internal static class Ui
{
    internal static string Missing=>T("…","—");
    internal static string Language=>LocManager.Instance?.Language??"zhs";
    internal static bool English=>Language is not ("zhs" or "zht");
    internal static string T(string chinese,string english)=>English?english:chinese;
    internal static string Turn(int n)=>T($"第{n}回合",$"Turn {n}");
    internal static string Outcome(string? raw)=>English ? raw switch {"胜"=>"W","败"=>"L",_=>raw??""}:raw??"";

    // Persist the model's localization key with its displayed name. Receivers resolve
    // names in their own game, including installed mods, without changing game locale.
    internal static HudTerm[] Terms(InteractiveSearch.Line line)
    {
        var names=new HashSet<HudTerm>();
        var keys=new HashSet<(string Table,string Key)>();
        void Add(LocString? loc)
        {
            if(loc!=null && keys.Add((loc.LocTable,loc.LocEntryKey)) && loc.GetFormattedText() is {Length:>0} name)names.Add(new(name,loc.LocTable,loc.LocEntryKey));
        }
        void Card(string id,string key)
        {
            Add(LocString.GetIfExists("cards",id+".title"));
            var fields=key.Split('|');int i=Array.FindIndex(fields,s=>s.StartsWith("vars=",StringComparison.Ordinal));
            if(i>=0 && i+2<fields.Length)
            {
                var enchant=fields[i+1].Split(':')[0];var affliction=fields[i+2].Split(':')[0];
                if(enchant.Length>0 && enchant!="-")Add(LocString.GetIfExists("enchantments",enchant+".title"));
                if(affliction.Length>0)Add(LocString.GetIfExists("afflictions",affliction+".title"));
            }
        }
        var cards=new HashSet<(string Id,string Key)>();
        foreach(var a in line.Steps)
        {
            Add(a.Player.Character.Title);Add(a.Potion?.Title);Add(a.Target?.Monster?.Title);Add(a.Target?.Player?.Character.Title);
            if(a.Card!=null)cards.Add((a.Card.Id.Entry,a.CardStateKey));
            foreach(var c in (a.Choices??Array.Empty<KernelChoice>()).Concat(a.TurnStartChoices?.Choices??Array.Empty<KernelChoice>()))
                foreach(var selected in c.Cards)cards.Add((selected.Id,selected.StateKey));
        }
        foreach(var card in cards)Card(card.Id,card.Key);
        foreach(var kill in line.Kills??Array.Empty<RouteKill>())Add(kill.Enemy.Monster?.Title);
        return names.ToArray();
    }

    private sealed class LocalNames(HudTerm[] terms)
    {
        private string? language;
        private Regex? pattern;
        private readonly Dictionary<string,string> titles=new();
        internal string Apply(string value)
        {
            if(language!=Language)
            {
                language=Language;titles.Clear();
                foreach(var term in terms)
                    if(LocString.GetIfExists(term.Table,term.Key)?.GetFormattedText() is {} title && title!=term.Source)
                        titles.TryAdd(term.Source,title);
                pattern=titles.Count==0?null:new Regex(@"(?<![a-zA-Z])(?:"+string.Join("|",titles.Keys.OrderByDescending(k=>k.Length).Select(Regex.Escape))+@")(?![a-zA-Z])");
            }
            return pattern?.Replace(value,m=>titles[m.Value])??value;
        }
    }
    private static readonly ConditionalWeakTable<HudTerm[],LocalNames> names=new();
    internal static string Punctuation(string text)=>ChineseSpacing.Format(text);
    internal static bool SameTargetNames(IReadOnlyDictionary<uint,string>? a,IReadOnlyDictionary<uint,string>? b)=>
        ReferenceEquals(a,b) || a!=null && b!=null && a.Count==b.Count &&
        a.All(item=>b.TryGetValue(item.Key,out var name) && name==item.Value);
    internal static string EnemyTarget(string fallback,uint? id,IReadOnlyDictionary<uint,string>? targetNames)=>
        id is uint target && targetNames?.TryGetValue(target,out var name)==true?name:fallback;
    internal static string Route(string raw,HudPlan? plan)
    {
        // Archived and older host routes can carry positional labels from before 0.10.3.
        if(raw.Contains("手牌"))raw=Regex.Replace(raw,@"（手牌\d+）|、手牌\d+(?=）)","");
        if(raw.Contains("魂缚") || raw.Contains("Bound"))
            raw=Regex.Replace(raw,@"(?<=[（、])(魂缚|Bound) -?\d+(?=[、）])","$1");
        if(plan?.Terms is {Length:>0} terms && plan.Language!=Language)
            raw=names.GetValue(terms,t=>new(t)).Apply(raw);
        if(!English)return Punctuation(Regex.Replace(raw,@"(?<=[\p{IsCJKUnifiedIdeographs}]) +(?=-?\d+(?:费|、|）))",""));
        if(raw=="结束回合")return "End turn";
        if(raw.StartsWith("选 ",StringComparison.Ordinal))raw="Choose "+raw[2..];
        if(raw.StartsWith("用药 ",StringComparison.Ordinal))raw="Use "+raw[3..];
        raw=Regex.Replace(raw,@"（([左右])(\d+)(?:上(\d+))?）",m=>" ("+(m.Groups[1].Value=="左"?"L":"R")+m.Groups[2].Value+(m.Groups[3].Success?" · "+m.Groups[3].Value:"")+")");
        raw=Regex.Replace(raw,@"药水槽 (\d+)","slot $1");
        raw=raw.Replace("站位待确认","position unknown").Replace("历史站位未记录","position unknown").Replace("已离场","off the field");
        raw=Regex.Replace(raw,@"手牌(\d+)","hand $1");
        raw=Regex.Replace(raw,@"第(\d+)张","copy $1");
        raw=Regex.Replace(raw,@"选项(\d+)","option $1");
        raw=Regex.Replace(raw,@"(-?\d+|X)费","$1 EN");
        return raw.Replace("选牌中 · ","Choosing · ").Replace("正在计算当前选牌","Evaluating choices")
            .Replace("正在核对可选牌","Checking options").Replace("本回合建议","Suggested: ")
            .Replace("跳过选择","Skip").Replace("添加重放 ","Add Replay to ").Replace("弃掉 ","Discard ")
            .Replace("取回 ","Retrieve ").Replace("消耗 ","Exhaust ").Replace("选择 ","Choose ").Replace(" 选 "," Choose ")
            .Replace("（重放","（Replay").Replace("、重放","、Replay").Replace("（"," (").Replace("）",")").Replace("、",", ");
    }
    // Saved/shared status messages use the original protocol vocabulary. Convert
    // at the presentation boundary; room and player identifiers stay untouched.
    internal static string Status(string raw)
    {
        if(raw.Length==0)return raw;
        raw=raw.Replace("盘面计算出错：","盘面搜索出错：")
            .Replace("开局计算出错：","开局搜索出错：").Replace("实战计算出错：","实战搜索出错：");
        if(!English)return raw;
        foreach(var (source,translation) in statuses)raw=raw.Replace(source,translation);
        raw=Regex.Replace(raw,@"已执行 (\d+) 步","Played $1");
        raw=Regex.Replace(raw,@"提前执行第 (\d+) 步","Played move $1 early");
        return Regex.Replace(raw,@"累计模拟 ([\d.kMB]+) 次","$1 visits");
    }
    private static readonly (string,string)[] statuses={
        ("进入战斗时捕获第一回合；中途可切换跟随实战。","Waiting for the first turn."),
        ("按本场第一回合计算，实战操作不会改写开局结果。","Searching from the opening."),
        ("结算后自动跟随当前盘面。","Following play."),
        ("实战偏离原路线 · 下方旧路线仅供对照，正在重算","Updating after a different move"),
        ("余线与现盘不符 · 下方仅供对照，正在重算","Position changed · Updating"),
        ("正在重算 · 上一盘面路线","Updating · Previous route"),
        ("已复算可继续执行的步骤","Continuation verified"),("盘面匹配，复核余线","Position matched · Checking route"),
        ("动作匹配，复核余线","Move matched · Checking route"),("正在从现盘试接余线","Checking continuation"),
        ("计算已暂停","Paused"),("手动暂停","Paused"),("开始搜索","Start search"),("暂停搜索","Pause search"),
        ("暂停计算","Pause"),("继续计算","Resume"),("开始求解","Start"),("正在优化胜线","Improving winning route"),
        ("等待实战结算","Resolving move"),("等待房主共享","Waiting for host"),("正在计算当前盘面","Searching position"),
        ("当前盘面已复算","Route verified"),("核对实战盘面","Checking position"),("正在评估当前选牌","Evaluating choices"),
        ("已保留开局路线","Opening saved"),("已保留路线","Route saved"),("等待求解","Ready"),
        ("等待实战盘面","Waiting for play"),("等待战斗开局","Waiting for combat"),("从战斗开局计算","From opening"),
        ("已续用缓存","Cache reused"),("正在接续搜索","Resuming"),("开局路线供对照","Opening reference"),
        ("下方保留上一次路线，结算后核对已执行的步骤。","Keeping the previous route while the move resolves."),
        ("正在结算实战动作","Resolving move"),("当前选牌","Card choice"),("正在计算","Searching"),("计算中","Searching"),
        ("开局搜索出错：","Opening search error: "),("实战搜索出错：","Live search error: "),
        ("自动打牌已停止：","Auto play stopped: "),("检测到其他操作","another action was played"),("扣血超过起始路线","loss exceeds the starting route"),
        ("无法核实当前局面","cannot verify this position"),("无法复算起始路线","cannot verify the starting route"),
        ("当前局面无法沿原损失路线继续","no verified continuation within the loss limit"),("动作未完成","action did not complete"),
        ("选牌未完成","card choice did not complete"),("无法核对当前选择","cannot verify this choice"),("选牌与路线不符","card choices changed"),("药水槽已变化","potion slot changed"),("目标已变化","target changed"),("手牌已变化","hand changed"),
        ("当前盘面搜索出错：","Position error: "),("模组规则无法模拟：","Unsupported mod rule: "),
        ("盘面与模拟不一致：","Simulation mismatch: "),("未覆盖的效果：","Unmodeled effect: "),
        ("开局路线读取失败：","Could not load opening: "),("开局结果仅保留在内存：","Opening saved in memory only: "),
        ("实战续线未写入：","Could not save continuation: "),("实战路径未写入：","Could not save played moves: "),
        ("共享路线过大","Shared route is too large"),
        ("路线中有敌方招式按攻击意图结算。","Some enemy moves use attack intent."),
        ("部分战斗效果的实机结算仍待核对。","Some effects need verification."),(" 悬停查看详情。"," Hover for details."),
        ("（按攻击意图结算）"," (attack intent)"),("内存已达到搜索上限，计算暂停。","Search paused at memory limit.")
    };
}
