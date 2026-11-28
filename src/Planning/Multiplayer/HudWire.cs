using System;
using System.Linq;

namespace CoopSolver;

internal enum HudSharedPhase
{
    Unknown, Resolving, Searching, Retained, Idle, Choosing, WaitingForHost, SharingDisabled, TooLarge
}

internal static class HudWire
{
    internal static HudFrame Send(HudFrame frame,bool structured)
    {
        if(!structured)return frame with {Plan=Legacy(frame.Plan),Graphs=Graphs(frame.Graphs,false),PotionMenu=null};
        var phase=Identify(frame.State);
        var plan=frame.Plan;
        if(plan?.Moves!=null)
            plan=plan with
            {
                Steps="", Result="", Terms=null, Language=null, Party=Seats(plan.Party),
                Kills=plan.Kills?.Select(kill=>kill.Target!=null?kill with {Name=""}:kill).ToArray(),
            };
        return frame with
        {
            Plan=plan, Graphs=Graphs(frame.Graphs,true), Phase=phase, Choice="",
            PotionMenu=frame.PotionMenu is {} menu?menu with {Party=Seats(menu.Party)}:null,
            State=phase==HudSharedPhase.Unknown?frame.State:"",
            Opening=Best(frame.Opening), Realtime=Best(frame.Realtime),
        };
    }
    internal static HudFrame Receive(HudFrame frame)=>frame.Phase==HudSharedPhase.Unknown?
        frame:frame with {State=LegacyPhase(frame.Phase)};

    private static HudPlan? Legacy(HudPlan? plan)=>plan==null?null:plan with
    {
        Moves=null,ResultKind=null,Kills=plan.Kills?.Select(kill=>kill with {Target=null}).ToArray()
    };
    private static HudSeat[] Seats(HudSeat[] party)=>party.Select(seat=>seat with {Role=""}).ToArray();
    private static HudBest? Best(HudBest? best)=>best==null?null:best with {Party=Seats(best.Party)};
    private static HudRoundGraph[]? Graphs(HudRoundGraph[]? rounds,bool structured)=>
        rounds?.Select(round=>round.Graph is not {} graph?round:round with
        {
            Graph=graph with
            {
                Party=structured?Seats(graph.Party):graph.Party,
                Actions=graph.Actions.Select(action=>structured?Structured(action):
                    action with {Move=null,Defeated=null,EnergyValues=null,DamageValues=null}).ToArray(),
            }
        }).ToArray();

    private static HudGraphAction Structured(HudGraphAction action)=>action with
    {
        Name=action.Move!=null?"":action.Name,
        Target=action.Move!=null?null:action.Target,
        Choices=action.Move!=null?null:action.Choices,
        OtherKills=action.Defeated!=null?null:action.OtherKills,
        EnergyAlternatives=action.EnergyValues!=null?null:action.EnergyAlternatives,
        DamageAlternatives=action.DamageValues!=null?null:action.DamageAlternatives
    };
    private static HudSharedPhase Identify(string state)=>state.Split(" · ",2,StringSplitOptions.None)[0] switch
    {
        "等待实战结算"=>HudSharedPhase.Resolving,
        "搜索中"=>HudSharedPhase.Searching,
        "已保留路线"=>HudSharedPhase.Retained,
        "等待搜索"=>HudSharedPhase.Idle,
        "正在评估当前选牌"=>HudSharedPhase.Choosing,
        "等待房主共享"=>HudSharedPhase.WaitingForHost,
        "主机已关闭广播"=>HudSharedPhase.SharingDisabled,
        "共享路线过大"=>HudSharedPhase.TooLarge,
        _=>HudSharedPhase.Unknown
    };
    private static string LegacyPhase(HudSharedPhase phase)=>phase switch
    {
        HudSharedPhase.Resolving=>"等待实战结算",
        HudSharedPhase.Searching=>"搜索中",
        HudSharedPhase.Retained=>"已保留路线",
        HudSharedPhase.Idle=>"等待搜索",
        HudSharedPhase.Choosing=>"正在评估当前选牌",
        HudSharedPhase.WaitingForHost=>"等待房主共享",
        HudSharedPhase.SharingDisabled=>"主机已关闭广播",
        HudSharedPhase.TooLarge=>"共享路线过大",
        _=>""
    };
}
