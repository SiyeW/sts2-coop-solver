using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CoopSolver;

internal static partial class Ui
{
    // The inputs of this table remain stable for old cache files and old hosts.
    private static readonly Dictionary<string,string> legacyStatuses=new(StringComparer.Ordinal)
    {
        ["进入战斗时捕获第一回合；中途可切换跟随实战。"]="status.001",
        ["按本场第一回合计算，实战操作不会改写开局结果。"]="status.002",
        ["结算后自动跟随当前盘面。"]="status.003",
        ["实战偏离原路线 · 下方旧路线仅供对照，正在重算"]="status.004",
        ["余线与现盘不符 · 下方仅供对照，正在重算"]="status.005",
        ["正在重算 · 上一盘面路线"]="status.006",
        ["已复算可继续执行的步骤"]="status.007",
        ["盘面匹配，复核余线"]="status.008",
        ["动作匹配，复核余线"]="status.009",
        ["正在从现盘试接余线"]="status.010",
        ["计算已暂停"]="status.011",
        ["手动暂停"]="status.012",
        ["开始搜索"]="status.013",
        ["暂停搜索"]="status.014",
        ["暂停计算"]="status.015",
        ["继续计算"]="status.016",
        ["开始求解"]="status.017",
        ["正在优化胜线"]="status.018",
        ["等待实战结算"]="status.019",
        ["等待房主共享"]="status.020",
        ["正在计算当前盘面"]="status.021",
        ["当前盘面已复算"]="status.022",
        ["核对实战盘面"]="status.023",
        ["正在评估当前选牌"]="status.024",
        ["已保留开局路线"]="status.025",
        ["已保留路线"]="status.026",
        ["等待求解"]="status.027",
        ["等待实战盘面"]="status.028",
        ["等待战斗开局"]="status.029",
        ["从战斗开局计算"]="status.030",
        ["已续用缓存"]="status.031",
        ["正在接续搜索"]="status.032",
        ["开局路线供对照"]="status.033",
        ["下方保留上一次路线，结算后核对已执行的步骤。"]="status.034",
        ["正在结算实战动作"]="status.035",
        ["当前选牌"]="status.036",
        ["正在计算"]="status.037",
        ["计算中"]="status.038",
        ["开局搜索出错："]="status.039",
        ["实战搜索出错："]="status.040",
        ["自动打牌已停止："]="status.041",
        ["检测到其他操作"]="status.042",
        ["扣血超过起始路线"]="status.043",
        ["无法核实当前局面"]="status.044",
        ["无法复算起始路线"]="status.045",
        ["当前局面无法沿原损失路线继续"]="status.046",
        ["动作未完成"]="status.047",
        ["选牌未完成"]="status.048",
        ["无法核对当前选择"]="status.049",
        ["选牌与路线不符"]="status.050",
        ["药水槽已变化"]="status.051",
        ["目标已变化"]="status.052",
        ["手牌已变化"]="status.053",
        ["当前盘面搜索出错："]="status.054",
        ["模组规则无法模拟："]="status.055",
        ["盘面与模拟不一致："]="status.056",
        ["未覆盖的效果："]="status.057",
        ["开局路线读取失败："]="status.058",
        ["开局结果仅保留在内存："]="status.059",
        ["实战续线未写入："]="status.060",
        ["实战路径未写入："]="status.061",
        ["共享路线过大"]="status.062",
        ["路线中有敌方招式按攻击意图结算。"]="status.063",
        ["部分战斗效果的实机结算仍待核对。"]="status.064",
        [" 悬停查看详情。"]="status.065",
        ["（按攻击意图结算）"]="status.066",
        ["内存已达到搜索上限，计算暂停。"]="status.067",
        ["核对中"]="extra.checking",
        ["等待搜索"]="extra.waiting_search",
        ["主机已关闭广播"]="extra.host_disabled",
        ["已验证队友动作的换序 · 正在核对盘面"]="extra.reordered_verified",
        ["正在核对选牌后的余线"]="extra.checking_after_choice",
        ["提前结束回合 · 正在复核余线"]="extra.early_end",
        ["本机旧路线未清除："]="extra.old_routes_not_removed",
        ["模拟胜利"]="extra.sim_win",
        ["全队倒下"]="extra.party_down",
        ["完整路线 · 全队倒下"]="extra.complete_loss",
        ["胜负未定"]="extra.undecided",
        ["当前盘面没有已核实的路线"]="extra.no_verified",
        ["动作执行前战斗已结束"]="extra.combat_ended",
        ["达到验证预算"]="extra.validation_budget",
        ["原路线未通过复算："]="extra.original_replay_failed",
        ["动作进度组合超出验证范围"]="extra.progress_combinations",
        ["原路线终点缺少可比较的盘面"]="extra.original_end_missing",
        ["达到换序验证预算"]="extra.reorder_budget",
        ["已验证，箭头简化达到预算"]="extra.simplify_budget",
        ["换序后缺少可比较的盘面"]="extra.reorder_state_missing",
        ["同一进度的盘面不同"]="extra.same_progress_diff",
        ["重排后的段末与原路线不同"]="extra.reorder_end_diff",
        ["无法恢复该回合起点："]="extra.restore_round_failed",
        ["没有找到与这条路线对应的模拟盘面"]="extra.missing_sim_state",
        ["路线缺少该回合的动作"]="extra.missing_round_actions",
        ["无法复算原行动顺序"]="extra.original_order_failed",
        ["盘面已变化，请重新求解。"]="extra.state_changed_restart",
        ["候选路线未能从原盘面复算到胜利。"]="extra.candidate_win_failed",
        ["本次搜索未找到完整胜线。"]="extra.no_complete_win",
        ["已到搜索时间上限。"]="extra.time_limit",
        ["发现时搜索量未记录"]="extra.found_unrecorded",
        ["供对照"]="extra.reference",
        ["待复算"]="extra.pending_verify",
        ["发现时"]="extra.found_at",
        ["胜后复活，余 1 血"]="extra.victory_revive_one",
        ["搜索中"]="status.037",
        ["正在搜索当前盘面"]="status.021",
        ["等待主机路线"]="status.020",
        ["盘面计算出错："]="status.054",
        ["开局计算出错："]="status.039",
        ["实战计算出错："]="status.040"
    };
    private static readonly Regex legacyStatusPattern=new(
        @"已执行 (?<played>\d+) 步|提前执行第 (?<early>\d+) 步|累计模拟 (?<visits>[\d.kMB]+) 次|"+
        string.Join("|",legacyStatuses.Keys.OrderByDescending(value=>value.Length).Select(Regex.Escape)),RegexOptions.Compiled);
    private static readonly Regex legacyGraphError=new(@"^第(?<round>\d+)回合(?<status>分列图生成失败|仅支持原顺序)：(?<reason>.*)$",RegexOptions.Compiled);
    private static readonly Regex graphEffectState=new(
        @"facts-unavailable:(?:missing-captured-power-state|unqualified-internal-power-state):(?<type>[\w.]+)",
        RegexOptions.Compiled);

    internal static string Status(string raw)
    {
        var graph=legacyGraphError.Match(raw);
        if(graph.Success)
        {
            string reason=Status(graph.Groups["reason"].Value);
            reason=graphEffectState.Replace(reason,match=>
            {
                string type=match.Groups["type"].Value.Split('.')[^1];
                var power=MegaCrit.Sts2.Core.Models.ModelDb.All.OfType<MegaCrit.Sts2.Core.Models.PowerModel>()
                    .FirstOrDefault(model=>model.GetType().Name==type);
                return power?.Title.GetFormattedText() is {Length:>0} name?name+" ("+type+")":type;
            });
            string key=graph.Groups["status"].Value=="仅支持原顺序"?"extra.round_original_order":"extra.round_graph_error";
            return Text(key,graph.Groups["round"].Value,reason);
        }
        // Match against the original string. Translated text is never parsed again.
        return legacyStatusPattern.Replace(raw,match=>
        {
            var played=match.Groups["played"];
            var early=match.Groups["early"];
            var visits=match.Groups["visits"];
            if(played.Success)return Text("format.034",played.Value);
            if(early.Success)return Text("format.035",early.Value);
            return visits.Success?Text("format.036",visits.Value):Text(legacyStatuses[match.Value]);
        });
    }
}
