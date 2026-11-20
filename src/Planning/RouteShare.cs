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

internal sealed record HudSeat(ulong Id,string Name,string Role,int? ThisTurn,int? ToEnd,bool DiesThisTurn,bool DiesAtEnd,int? Potions,int? Hp=null,string? Color=null);
internal sealed record HudKill(int Step,string Name,bool IsTarget);
internal sealed record HudRoundHp(int Round,int[] Loss,int[] EndHp,int[]? Damage=null,Dictionary<int,int>? CardDamage=null,int? EnemyHp=null);
internal sealed record HudPlan(int Start,int End,HudSeat[] Party,string Steps,string Result,string Risks,HudKill[]? Kills=null,HudRoundHp[]? Rounds=null);
internal sealed record HudBest(long Visits,long? Pv,long Rate,int Round,int? End,HudSeat[] Party,string Outcome);
internal sealed record HudFrame(long Visits,long? Pv,int Round,string State,string Choice,bool Live,bool Running,HudPlan? Plan,bool Hidden=false,long Rate=0,int? ViewPercent=null,HudBest? Opening=null,HudBest? Realtime=null);
internal sealed record HudHello(string Battle,string Token,long Have);
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
    private long lastRemoteUpdate;
    private string battle="",token="";
    private long serial,version,lastSeen,nextSend,nextHello,have=-1;
    private HudPlan? lastPlan;
    internal HudFrame? Remote {get;private set;}
    internal int Viewers=>viewers.Count;
    internal void Install()
    {
        RitsuLibSidecarTypedMessageRegistry.Register(hello);
        RitsuLibSidecarTypedMessageRegistry.Register(data);
        RitsuLibSidecarTypedMessageRegistry.Subscribe(hello,ctx=>
        {
            if(!ctx.IsHostIngest || ctx.Message.Token?.Length!=32)return;
            lock(gate){if(inbox.Count<16 || inbox.ContainsKey(ctx.SenderNetId))inbox[ctx.SenderNetId]=ctx.Message;}
        });
        RitsuLibSidecarTypedMessageRegistry.Subscribe(data,ctx=>
        {
            if(ctx.IsHostIngest || ctx.Message.Frame is not {State:not null,Choice:not null} f ||
                f.Plan!=null && (f.Plan.Party==null || f.Plan.Steps==null || f.Plan.Result==null || f.Plan.Party.Any(s=>s==null)))return;
            lock(gate)
            {
                if(ctx.Message.Token!=token || ctx.Message.Battle!=battle || ctx.Message.Sequence<=lastSeen ||
                    ctx.SenderNetId!=hostId)return;
                // A later metrics packet must not overwrite a still-pending full plan.
                var message=ctx.Message;
                if(message.Frame.Plan==null && received is {} prev && prev.Data.PlanVersion==message.PlanVersion)
                    message=message with {Frame=message.Frame with {Plan=prev.Data.Frame.Plan}};
                if(received==null || message.Sequence>received.Value.Data.Sequence)received=(ctx.SenderNetId,message);
            }
        });
    }
    internal void Reset(INetGameService? net,string key)
    {
        lock(gate){hostId=(net as NetClientGameService)?.HostNetId;battle=key;token=Guid.NewGuid().ToString("N");lastSeen=0;received=null;inbox.Clear();}
        viewers.Clear();Remote=null;lastPlan=null;version=serial=0;have=-1;nextHello=nextSend=0;
    }
    internal void Guest(NetClientGameService net,long now)
    {
        lock(gate)
        {
            if(received is {} evt)
            {
                received=null;var p=evt.Data;
                if(evt.Sender==net.HostNetId && p.Battle==battle && p.Token==token && p.Sequence>lastSeen)
                {
                    if(p.Frame.Plan!=null || p.PlanVersion==have || p.PlanVersion==0)
                    {
                        lastSeen=p.Sequence;Remote=p.Frame with {Plan=p.Frame.Plan??(p.PlanVersion==0?null:Remote?.Plan)};
                        have=p.PlanVersion;lastRemoteUpdate=now;
                    }
                    else {have=-1;nextHello=0;}
                }
            }
        }
        if(Remote!=null && now-lastRemoteUpdate>6000){Remote=null;have=-1;nextHello=0;}
        if(now<nextHello)return;
        nextHello=now+(Remote==null?1000:5000);
        RitsuLibSidecarTypedMessageRegistry.SendToHost(net,hello,new(battle,token,have));
    }
    internal void Host(NetHostGameService net,HudFrame frame,long now)
    {
        if(!ReferenceEquals(frame.Plan,lastPlan))
        {lastPlan=frame.Plan;version=lastPlan==null?0:++serial;nextSend=0;}
        lock(gate)
        {
            foreach(var (id,h) in inbox)
                if(h.Battle==battle && net.ConnectedPeers.Any(p=>p.peerId==id)){viewers[id]=h;nextSend=0;}
            inbox.Clear();
        }
        if(now<nextSend || viewers.Count==0)return;
        nextSend=now+600;
        foreach(var (id,h) in viewers.ToArray())
        {
            if(!net.ConnectedPeers.Any(p=>p.peerId==id)){viewers.Remove(id);continue;}
            var payload=h.Have==version?frame with {Plan=null}:frame;
            try { if(RitsuLibSidecarTypedMessageRegistry.SendToPeer(net,id,data,new(battle,h.Token,++serial,version,payload)))
                viewers[id]=h with {Have=version}; }
            catch(InvalidDataException){viewers.Remove(id); /* An oversized plan cannot be sent. Local solving continues. */}
        }
    }
}
