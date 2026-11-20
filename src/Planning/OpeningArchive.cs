using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using CoopBots.Kernel;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Modding;

namespace CoopSolver;

internal sealed record OpeningResult(TerminalRecord? Ending,int Steps,double PartialScore,string Route,string Summary,string[] Risks,HudPlan? Hud=null,SeedRoute? Seed=null)
{
    internal bool Improves(OpeningResult? previous)
    {
        if(previous==null)return true;
        if(Ending==null && previous.Ending!=null)return false;
        if(Ending!=null && previous.Ending==null)return true;
        int order=Ending==null?PartialScore.CompareTo(previous.PartialScore):
            AnytimeSearch.ComparePlans(Ending,Steps,previous.Ending!,previous.Steps,Hud?.Rounds,previous.Hud?.Rounds);
        return order>0 || order==0 && Seed!=null && (previous.Seed==null || Hud?.Rounds?.Any(r=>r.EnemyHp.HasValue)==true && previous.Hud?.Rounds?.Any(r=>r.EnemyHp.HasValue)!=true || Hud?.Kills!=null && previous.Hud?.Kills==null || Hud?.Rounds!=null && previous.Hud?.Rounds==null || Hud?.Rounds?.Any(r=>r.Damage!=null)==true && previous.Hud?.Rounds?.Any(r=>r.Damage!=null)!=true);
    }
}

internal static class OpeningArchive
{
    internal static string Key(CombatState state,string stamp,string? earlierSolverVersion=null)
    {
        // The HUD-only update reads and writes the existing 0.4.0 replay cache.
        if(earlierSolverVersion==null && ModManager.Mods.Any(m=>m.manifest?.id=="CoopSolver" && m.version?.ToString() is ("v0.4.1" or "v0.5.0" or "v0.5.1" or "v0.5.2" or "v0.5.3" or "v0.5.4" or "v0.5.5" or "v0.6.0")))
            earlierSolverVersion="v0.4.0";
        var run=state.RunState;
        var identity=JsonSerializer.SerializeToUtf8Bytes(new {schema=1,game=typeof(CombatState).Module.ModuleVersionId,
            mods=ModManager.Mods.Select(m=>new {id=m.manifest?.id,version=m.manifest?.id=="CoopSolver" && earlierSolverVersion!=null ? earlierSolverVersion : m.version?.ToString(),state=m.state.ToString()}),
            run.Rng.StringSeed,run.CurrentActIndex,run.TotalFloor,coord=run.CurrentMapCoord?.ToString(),
            run.AscensionLevel,encounter=state.Encounter?.Id.Entry,stamp});
        return Convert.ToHexString(SHA256.HashData(identity));
    }
    private static string PathFor(string key)=>Path.Combine(ProjectSettings.GlobalizePath("user://coop-solver-openings"),key+".json");
    internal static OpeningResult? Load(string key)
    {
        var path=PathFor(key);
        return File.Exists(path)?JsonSerializer.Deserialize<OpeningResult>(File.ReadAllBytes(path)):null;
    }
    internal static OpeningResult SaveBest(string key,OpeningResult result)
    {
        return SaveBestAt(PathFor(key),result);
    }
    internal static OpeningResult SaveBestAt(string path,OpeningResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var gate=new FileStream(path+".lock",FileMode.OpenOrCreate,System.IO.FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
        var previous=File.Exists(path)?JsonSerializer.Deserialize<OpeningResult>(File.ReadAllBytes(path)):null;
        if(previous!=null && !result.Improves(previous))return previous;
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllBytes(temp,JsonSerializer.SerializeToUtf8Bytes(result));
            File.Move(temp,path,true);
            return result;
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
}
