using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Modding;

namespace CoopSolver;

internal sealed record OpeningResult(TerminalRecord? Ending,int Steps,double PartialScore,string Route,string Summary,string[] Risks,HudPlan? Hud=null,SeedRoute? Seed=null,int ResourceScoreVersion=0,HudGraph[]? RoundGraphs=null)
{
    internal bool Improves(OpeningResult? previous)
    {
        if(previous==null)return true;
        if(Ending==null && previous.Ending!=null)return false;
        if(Ending!=null && previous.Ending==null)return true;
        int order=Ending==null?PartialScore.CompareTo(previous.PartialScore):
            AnytimeSearch.ComparePlans(Ending,Steps,previous.Ending!,previous.Steps,Hud?.Rounds,previous.Hud?.Rounds);
        if(order!=0)return order>0;
        if(Seed==null)return false;
        if(previous.Seed==null)return true;
        if(Hud?.Kills!=null && previous.Hud?.Kills==null)return true;

        var rounds=Hud?.Rounds;
        if(rounds==null)return false;
        var prior=previous.Hud?.Rounds;
        if(prior==null)return true;
        bool Adds(Func<HudRoundHp,bool> hasData)=>rounds.Any(hasData) && !prior.Any(hasData);
        return Adds(r=>r.IncludesBlockDamage) || Adds(r=>r.UnassignedDamage!=null) || Adds(r=>r.StepDamage!=null) ||
            Adds(r=>r.EnergyAfter!=null) || Adds(r=>r.EnemyHp.HasValue) || Adds(r=>r.Damage!=null);
    }
}

internal static class OpeningArchive
{
    // Keep the identity used by existing route archives. Their record format
    // is versioned independently from the installed mod's display version.
    private const string ArchiveSolverVersion="v0.4.0";

    internal static string Key(CombatState state,string stamp)
    {
        var run=state.RunState;
        var identity=JsonSerializer.SerializeToUtf8Bytes(new {schema=1,game=typeof(CombatState).Module.ModuleVersionId,
            mods=ModManager.Mods.Select(m=>new {id=m.manifest?.id,version=m.manifest?.id=="CoopSolver"?ArchiveSolverVersion:m.version?.ToString(),state=m.state.ToString()}),
            run.Rng.StringSeed,run.CurrentActIndex,run.TotalFloor,coord=run.CurrentMapCoord?.ToString(),
            run.AscensionLevel,encounter=state.Encounter?.Id.Entry,stamp});
        return Convert.ToHexString(SHA256.HashData(identity));
    }
}
