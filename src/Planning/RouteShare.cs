using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using STS2RitsuLib.Networking.Sidecar;

namespace CoopSolver;

internal sealed record HudHello(string Battle,string Token,long Have,bool? View=null,PotionCost? Opening=null,PotionCost? Live=null,int[]? GraphRounds=null,bool History=false);
internal sealed record HudPacket(string Battle,string Token,long Sequence,long PlanVersion,HudFrame Frame);

internal sealed class RouteShare
{
    private const int MaxBytes=262144;
    private static byte[] Pack<T>(T value)
    {
        var raw=JsonSerializer.SerializeToUtf8Bytes(value);
        if(raw.Length>MaxBytes)throw new InvalidDataException("路线超过共享容量");
        using var output=new MemoryStream();
        using(var stream=new DeflateStream(output,CompressionLevel.Fastest,true))stream.Write(raw);
        return output.ToArray();
    }
    private static T Unpack<T>(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length>MaxBytes)throw new InvalidDataException();
        using var input=new MemoryStream(bytes.ToArray());
        using var stream=new DeflateStream(input,CompressionMode.Decompress);
        using var output=new MemoryStream();var buffer=new byte[4096];
        int n;
        while((n=stream.Read(buffer))!=0)
        {
            if(output.Length+n>MaxBytes)throw new InvalidDataException();
            output.Write(buffer,0,n);
        }
        return JsonSerializer.Deserialize<T>(output.ToArray())??throw new InvalidDataException();
    }
    private readonly RitsuLibSidecarMessageDescriptor<HudHello> hello=new("CoopSolver","hud-hello-v1",Pack,Unpack<HudHello>);
    private readonly RitsuLibSidecarMessageDescriptor<HudPacket> data=new("CoopSolver","hud-route-v1",Pack,Unpack<HudPacket>);
    private readonly object gate=new();
    private readonly Dictionary<ulong,HudHello> inbox=new();
    private readonly Dictionary<ulong,HudHello> viewers=new();
    private (ulong Sender,HudPacket Data)? received;
    private ulong? hostId;
    private long lastRemoteUpdate,missingSequence;
    private string battle="",token="";
    private long serial,version,lastSeen,nextSend,nextHello,have=-1;
    private HudPlan? lastPlan;
    private bool broadcasting=true;
    private bool? view;
    private PotionCost? openingChoice,liveChoice;
    private readonly Dictionary<ulong,(HudPlan? Plan,long Version)> peerPlans=new();
    private int[] graphRounds=Array.Empty<int>();
    private bool wantsHistory;
    internal void ChooseHistory(bool value)
    {
        if(wantsHistory==value)return;
        wantsHistory=value;nextHello=0;
    }
    internal void ChooseGraphs(int[] rounds)
    {
        if(graphRounds.SequenceEqual(rounds))return;
        graphRounds=rounds;nextHello=0;
    }
    internal void Choose(bool live,PotionCost? cost)
    {
        view=live;if(live)liveChoice=cost;else openingChoice=cost;nextHello=0;
    }
    internal HudFrame? Remote {get;private set;}
    internal void Install()
    {
        RitsuLibSidecarTypedMessageRegistry.Register(hello);
        RitsuLibSidecarTypedMessageRegistry.Register(data);
        RitsuLibSidecarTypedMessageRegistry.Subscribe(hello,ctx=>
        {
            static bool Valid(PotionCost? cost)=>cost==null || cost.Bottles!=null && cost.Bottles.All(b=>b?.Id!=null);
            if(!ctx.IsHostIngest || ctx.Message.Token?.Length!=32 || !Valid(ctx.Message.Opening) || !Valid(ctx.Message.Live) || ctx.Message.GraphRounds is {Length:>2})return;
            lock(gate){if(inbox.Count<16 || inbox.ContainsKey(ctx.SenderNetId))inbox[ctx.SenderNetId]=ctx.Message;}
        });
        RitsuLibSidecarTypedMessageRegistry.Subscribe(data,ctx=>
        {
            if(ctx.IsHostIngest || ctx.Message.Frame is not {State:not null,Choice:not null} f ||
                f.Plan!=null && (f.Plan.Party==null || f.Plan.Steps==null || f.Plan.Result==null || f.Plan.Party.Any(s=>s==null)) ||
                f.Graphs is {Length:>2} || f.Graphs?.Any(r=>r==null || r.Graph is {} g &&
                    (g.Round!=r.Round || g.Party==null || g.Actions==null || g.Dependencies==null ||
                     g.Party.Any(s=>s==null) || g.Actions.Any(a=>a?.Name==null)))==true)return;
            var packet=ctx.Message;
            if(f.History is {} history && !ValidHistory(history))
                packet=packet with {Frame=f with {History=null}};
            lock(gate)
            {
                if(ctx.Message.Token!=token || ctx.Message.Battle!=battle || ctx.Message.Sequence<=lastSeen ||
                    ctx.SenderNetId!=hostId)return;
                received=(ctx.SenderNetId,MergePending(received?.Data,packet));
            }
        });
    }
    private static bool ValidHistory(HudSearchHistory history)
    {
        if(history.Visits<0 || history.LimitMb<0 ||
            history.Results is not {Length:<=HudSearchHistory.ResultLimit} ||
            history.Memory is not {Length:<=HudSearchHistory.MemoryLimit})return false;
        long prior=0;
        foreach(var result in history.Results)
        {
            if(result==null || result.Visits<prior || result.Visits>history.Visits ||
                result.EnemyHp is <0 || result.SeatLoss is not {Length:<=64} || result.Potions==null ||
                result.Potions.Length!=result.SeatLoss.Length)return false;
            prior=result.Visits;
        }
        prior=0;
        foreach(var point in history.Memory)
        {
            if(point==null || point.Visits<prior || point.Visits>history.Visits || point.Bytes<0)return false;
            prior=point.Visits;
        }
        return true;
    }

    // Metrics can arrive before the older full payload they refer to. Use the
    // newest status and borrow a plan only within the same session and version.
    internal static HudPacket MergePending(HudPacket? pending,HudPacket incoming)
    {
        if(pending==null)return incoming;
        var newest=incoming.Sequence>pending.Sequence?incoming:pending;
        var other=ReferenceEquals(newest,incoming)?pending:incoming;
        if(newest.Frame.Hidden || newest.PlanVersion==0 || newest.Frame.Plan!=null || other.Frame.Plan==null ||
            newest.PlanVersion!=other.PlanVersion || newest.Battle!=other.Battle || newest.Token!=other.Token)return newest;
        return newest with {Frame=newest.Frame with {Plan=other.Frame.Plan}};
    }
    internal void Reset(INetGameService? net,string key)
    {
        lock(gate){hostId=(net as NetClientGameService)?.HostNetId;battle=key;token=Guid.NewGuid().ToString("N");lastSeen=0;missingSequence=0;received=null;inbox.Clear();}
        peerPlans.Clear();view=null;openingChoice=liveChoice=null;viewers.Clear();Remote=null;lastPlan=null;version=serial=0;have=-1;wantsHistory=false;nextHello=nextSend=0;
    }
    internal void ConsumePending(ulong expectedHost,long now)
    {
        lock(gate)
        {
            if(received is {} evt)
            {
                var p=evt.Data;
                if(evt.Sender==expectedHost && p.Battle==battle && p.Token==token && p.Sequence>lastSeen)
                {
                    if(p.Frame.Plan!=null || p.PlanVersion==have || p.PlanVersion==0)
                    {
                        received=null;lastSeen=p.Sequence;Remote=p.Frame with {Plan=p.PlanVersion==0?null:p.PlanVersion==have && Remote?.Plan!=null?Remote.Plan:p.Frame.Plan};
                        if(view.HasValue && openingChoice!=null && Remote.Opening?.Options is {} oo)
                            openingChoice=oo.FirstOrDefault(o=>o.Key==Remote.Opening.PotionKey);
                        if(view.HasValue && liveChoice!=null && Remote.Realtime?.Options is {} lo)
                            liveChoice=lo.FirstOrDefault(o=>o.Key==Remote.Realtime.PotionKey);
                        if(Remote.Plan is {} plan && plan.Outcome is not ("胜" or "败"))Remote=Remote with {Plan=null,Choice="",Pv=null};
                        if(have!=p.PlanVersion)nextHello=0;
                        have=p.PlanVersion;lastRemoteUpdate=now;
                    }
                    else
                    {
                        // Keep the latest counters and the pending version. An older
                        // full packet for this version can still complete it.
                        if(p.Sequence!=missingSequence)
                        {
                            lastRemoteUpdate=now;missingSequence=p.Sequence;
                            Remote=p.Frame with {Plan=null,Choice="",Pv=null,State="等待主机路线"};
                        }
                        if(have!=-1){have=-1;nextHello=0;}
                    }
                }
            }
        }
        if(Remote!=null && now-lastRemoteUpdate>6000)
        {
            // A restarted legacy host can begin its sequence at one in the same
            // room. Negotiate a fresh token; delayed packets from the old session
            // cannot restore its route or keep the new sequence behind lastSeen.
            lock(gate){token=Guid.NewGuid().ToString("N");lastSeen=missingSequence=0;received=null;}
            Remote=null;have=-1;nextHello=0;
        }
    }
    internal void Guest(NetClientGameService net,long now)
    {
        ConsumePending(net.HostNetId,now);
        if(now<nextHello)return;
        nextHello=now+(Remote==null || have<0?1000:5000);
        RitsuLibSidecarTypedMessageRegistry.SendToHost(net,hello,new(battle,token,have,view,openingChoice,liveChoice,graphRounds.Length==0?null:graphRounds,wantsHistory));
    }
    internal void RefreshGraph()=>nextSend=0;
    internal void Host(NetHostGameService net,HudFrame frame,long now,Func<ulong,HudHello,HudFrame>? project=null,bool broadcast=true)
    {
        if(broadcast!=broadcasting)
        {
            broadcasting=broadcast;nextSend=0;peerPlans.Clear();
        }
        if(broadcast && !ReferenceEquals(frame.Plan,lastPlan))
        {lastPlan=frame.Plan;version=lastPlan==null?0:++serial;nextSend=0;}
        lock(gate)
        {
            foreach(var (id,h) in inbox)
                if(h.Battle==battle && net.ConnectedPeers.Any(p=>p.peerId==id)){viewers[id]=h;nextSend=0;}
            inbox.Clear();
        }
        if(now<nextSend || viewers.Count==0)return;
        nextSend=broadcast?now+600:long.MaxValue;
        foreach(var (id,h) in viewers.ToArray())
        {
            if(!net.ConnectedPeers.Any(p=>p.peerId==id)){viewers.Remove(id);continue;}
            var shown=!broadcast?new HudFrame(0,null,frame.Round,"主机已关闭广播","",true,false,null):
                project!=null?project(id,h):frame;
            var peer=peerPlans.GetValueOrDefault(id);
            if(!ReferenceEquals(peer.Plan,shown.Plan)){peer=(shown.Plan,shown.Plan==null?0:++serial);peerPlans[id]=peer;}
            long peerVersion=peer.Version;
            var payload=h.Have==peerVersion?shown with {Plan=null}:shown;
            try { if(RitsuLibSidecarTypedMessageRegistry.SendToPeer(net,id,data,new(battle,h.Token,++serial,peerVersion,payload)))
                viewers[id]=h; } // Only a guest heartbeat acknowledges receipt.
            catch(InvalidDataException)
            {
                if(payload.History!=null)
                {
                    try
                    {
                        RitsuLibSidecarTypedMessageRegistry.SendToPeer(net,id,data,
                            new(battle,h.Token,++serial,peerVersion,payload with {History=null}));
                        continue;
                    }
                    catch(InvalidDataException) {}
                }
                // Still deliver the current position and counters. Retry the full
                // plan when it changes or the client requests it again.
                var summary=shown with {Plan=null,Choice="",Pv=null,State="共享路线过大",Warnings=null,SearchError=null,Graphs=null,History=null,
                    Opening=shown.Opening is {} o?o with {Options=null}:null,Realtime=shown.Realtime is {} r?r with {Options=null}:null};
                RitsuLibSidecarTypedMessageRegistry.SendToPeer(net,id,data,new(battle,h.Token,++serial,0,summary));
            }
        }
    }
}
