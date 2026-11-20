using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;

namespace CoopSolver;

internal sealed partial class AnytimeSearch
{
    private sealed record PreviewCandidate(Edge[] Path,double Score,int Turn,int[] Hp,int Enemies,int EnemyHp,int Bottles);
    internal sealed record PreviewLine(double Score,KernelTeamSearch.Action[] Actions,int Turn,int[] Hp,int Enemies,int EnemyHp,
        int Bottles,string[] Risks,KernelPlanningState[] Trail,int[]? FirstHp,TreeStep?[] Recipes,RouteKill[] Kills,HudRoundHp[] Rounds);
    private PreviewCandidate? pendingPreview, checkingPreview;
    private KernelSession? previewState;
    private RouteDeaths? previewDeaths;
    private readonly List<KernelTeamSearch.Action> previewActions=new();
    private int[]? previewFirstHp;
    private readonly List<KernelPlanningState> previewStates=new();
    private readonly HashSet<string> previewRisks=new();
    private double previewScore=double.NegativeInfinity,acceptedScore=double.NegativeInfinity;
    internal bool TrackPreview {get;set;}
    internal PreviewLine? Preview {get;private set;}

    private void OfferPreview(KernelSession state,double score,Edge[] prefix,RoundPath? path)
    {
        if(!TrackPreview || Best!=null || score<=previewScore)return;
        previewScore=score;
        var opposition=state.RemainingOpposition();
        pendingPreview=new(RoundRoute(prefix,path),score,state.TurnNumber,party.Select(p=>state.Hp(p.Creature)).ToArray(),
            opposition.Count,opposition.Hp,party.Sum(state.HeldPotionCount));
    }

    private void StopPreview()
    {
        TrackPreview=false;pendingPreview=null;checkingPreview=null;previewState=null;Preview=null;
        previewActions.Clear();previewRisks.Clear();previewStates.Clear();
    }

    // The display replay runs one action per tick and cannot install an
    // unfinished line as the solver's best terminal result.
    internal void AdvancePreview()
    {
        if(!TrackPreview || Best!=null)
        {
            StopPreview();return;
        }
        if(checkingPreview==null)
        {
            if(pendingPreview==null)return;
            checkingPreview=pendingPreview;pendingPreview=null;
            previewActions.Clear();previewRisks.Clear();previewStates.Clear();previewFirstHp=null;previewState=root.ForkForReplay();previewDeaths=new(root);
        }
        var candidate=checkingPreview;
        var state=previewState!;
        bool ok;
        KernelTeamSearch.Action? action;
        ReplayCoverage.Begin(state,previewRisks);
        try {ok=candidate.Path[previewActions.Count].Replay(state,out action,out _);}
        finally {ReplayCoverage.End();}
        if(ok)
        {
            ReplayCoverage.Collect(state,previewRisks);
            previewDeaths!.Observe(state,previewActions.Count,action);previewActions.Add(action!);
            if(previewFirstHp==null && (state.HasTerminal || state.RoundsAdvanced!=root.RoundsAdvanced))previewFirstHp=party.Select(p=>state.Hp(p.Creature)).ToArray();
            if(FollowEnabled)previewStates.Add(state.PlanningState(controlled));
            if(previewActions.Count<candidate.Path.Length)return;
            var opposition=state.RemainingOpposition();
            if(!state.HasTerminal && state.TurnNumber==candidate.Turn && opposition==(candidate.Enemies,candidate.EnemyHp)
                && candidate.Hp.SequenceEqual(party.Select(p=>state.Hp(p.Creature))) && party.Sum(state.HeldPotionCount)==candidate.Bottles)
            {
                Preview=new(candidate.Score,previewActions.ToArray(),candidate.Turn,candidate.Hp,candidate.Enemies,candidate.EnemyHp,
                    candidate.Bottles,previewRisks.Order().ToArray(),previewStates.ToArray(),previewFirstHp,candidate.Path.Select(e=>e.Exact).ToArray(),previewDeaths.Freeze(),previewDeaths.Rounds());
                acceptedScore=candidate.Score;
            }
        }
        previewScore=pendingPreview?.Score??acceptedScore;
        checkingPreview=null;previewState=null;
    }
}
