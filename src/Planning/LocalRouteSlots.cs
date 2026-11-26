using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Mutex=System.Threading.Mutex;
using Godot;

namespace CoopSolver;

// Each result belongs to the machine that searched it. HUD packets never enter this store.
internal enum LocalRouteRole { Solo, Host, Guest }
internal sealed record LocalBattleRoutes(string Battle,string? OpeningWitness=null,OpeningResult? Opening=null,
    string? LiveWitness=null,OpeningResult? Live=null,string? RunKey=null,double[]? Weights=null)
{
    internal OpeningResult? ForPosition(string? witness)=>witness==null?null:
        witness==OpeningWitness?Opening:witness==LiveWitness?Live:null;
}

internal static class LocalRouteSlots
{
    private sealed record Slots(LocalBattleRoutes? Solo=null,LocalBattleRoutes? Host=null,LocalBattleRoutes? Guest=null)
    {
        internal LocalBattleRoutes? Get(LocalRouteRole role)=>role switch {
            LocalRouteRole.Solo=>Solo,LocalRouteRole.Host=>Host,LocalRouteRole.Guest=>Guest,_=>throw new ArgumentOutOfRangeException(nameof(role))};
        internal Slots With(LocalRouteRole role,LocalBattleRoutes routes)=>role switch {
            LocalRouteRole.Solo=>this with {Solo=routes},LocalRouteRole.Host=>this with {Host=routes},
            LocalRouteRole.Guest=>this with {Guest=routes},_=>throw new ArgumentOutOfRangeException(nameof(role))};
    }

    internal static string FilePath=>ProjectSettings.GlobalizePath("user://coop-solver-routes.json");
    private static Slots Read(string path)=>File.Exists(path)?
        JsonSerializer.Deserialize<Slots>(File.ReadAllBytes(path))??throw new JsonException():new();
    internal static LocalBattleRoutes? Load(string path,LocalRouteRole role,string battle)=>
        Read(path).Get(role) is {} entry && entry.Battle==battle?entry:null;

    internal static LocalBattleRoutes? Last(string path,LocalRouteRole role)=>Read(path).Get(role);

    internal static LocalBattleRoutes Save(string path,LocalRouteRole role,string battle,string witness,OpeningResult result,bool opening,string? runKey=null)
    {
        if(result.Ending is not {Verified:true} || result.Seed is not {Terminal:true})
            throw new ArgumentException("A complete local route is required.",nameof(result));
        return Change(path,role,battle,runKey,entry=>{
            var previous=entry.ForPosition(witness);
            if(previous!=null && !result.Improves(previous) &&
                !AddsRoundGraphs(result,previous))result=previous;
            var updated=opening
                ?entry with {OpeningWitness=witness,Opening=result,LiveWitness=witness,Live=null}
                :entry with {LiveWitness=witness,Live=result};
            if(opening && entry.LiveWitness!=null && entry.LiveWitness!=witness)
                updated=updated with {LiveWitness=entry.LiveWitness,Live=entry.Live};
            if(!opening && witness==entry.OpeningWitness)
                updated=updated with {Opening=result,Live=null};
            return updated;
        });
    }

    private static bool AddsRoundGraphs(OpeningResult result,OpeningResult previous)=>
        RouteGraphBook.VerifiedGraphCount(result.RoundGraphs)>RouteGraphBook.VerifiedGraphCount(previous.RoundGraphs) &&
        result.Route==previous.Route &&
        JsonSerializer.SerializeToUtf8Bytes(result.Seed).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(previous.Seed));

    internal static LocalBattleRoutes ClearRoutes(string path,LocalRouteRole role,string battle,string? runKey,double[] weights)=>
        Change(path,role,battle,runKey,entry=>entry with {
            OpeningWitness=null,Opening=null,LiveWitness=null,Live=null,Weights=weights});

    internal static void SaveWeights(string path,LocalRouteRole role,string battle,string runKey,double[] weights)=>
        Change(path,role,battle,runKey,entry=>entry with {Weights=weights},replaceRoutes:false);

    private static LocalBattleRoutes Change(string path,LocalRouteRole role,string battle,string? runKey,Func<LocalBattleRoutes,LocalBattleRoutes> change,bool replaceRoutes=true)
    {
        // Test clients and two game windows can share a profile. Serialize the whole read-modify-replace.
        using var mutex=new Mutex(false,"CoopSolver.LocalRouteSlots");
        try { if(!mutex.WaitOne(TimeSpan.FromSeconds(1)))throw new IOException("路线文件正在使用"); }
        catch(AbandonedMutexException) {}
        try
        {
            var slots=Read(path);
            var entry=slots.Get(role);
            if(entry!=null && entry.RunKey==null && entry.Battle==battle)
                entry=entry with {RunKey=runKey};
            if(entry==null || entry.RunKey!=runKey || replaceRoutes && entry.Battle!=battle)
                entry=new(battle,RunKey:runKey,Weights:runKey!=null && entry?.RunKey==runKey?entry.Weights:null);
            var updated=change(entry);
            if(updated==entry)return entry;
            var temp=path+".tmp";
            try
            {
                File.WriteAllBytes(temp,JsonSerializer.SerializeToUtf8Bytes(slots.With(role,updated)));
                File.Move(temp,path,true);
            }
            finally {if(File.Exists(temp))File.Delete(temp);}
            return updated;
        }
        finally {mutex.ReleaseMutex();}
    }
}
