using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using CoopBots.Kernel.Vendor.Engine.Common;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopSolver;

internal sealed class FightEvaluation
{
    // Preserve the six existing means. Fit the remaining six per fight,
    // then average across the ten calibration fights.
    private static readonly double[] Mean={
        -0.1121577034804406,0.19907052078913642,0.5281316292273792,
        0.09315161028416577,-0.018521452629972814,-0.33579565029366426,
        -0.011942644144852296,0.0,0.017051694495604265,
        0.1489326568569169,0.038994333410722976,0.0
    };
    private double[] weights;
    private int pairs;
    private readonly object gate=new();
    internal static FightEvaluation Restore(LocalBattleRoutes? slot,string? runKey)=>
        new(runKey!=null && slot?.RunKey==runKey?slot.Weights:null);
    internal double[] Weights=>Volatile.Read(ref weights);
    internal FightEvaluation(double[]? inherited=null)
    {
        weights=(double[])Mean.Clone();
        if(inherited is {Length:6 or 12} && Array.TrueForAll(inherited,double.IsFinite))
            Array.Copy(inherited,weights,inherited.Length);
    }
    internal static double[] Features(KernelSession state,KernelSession root,int openingEnemies,int openingPrimary)
    {
        double hp=0,block=0,energy=0,hand=0,potions=0,margin=0,nextEnergy=0,nextDraw=0;
        int living=0;
        foreach(var p in state.Party)
        {
            int health=state.Hp(p.Creature);
            hp+=health;potions+=state.HeldPotionCount(p);
            if(health<=0)continue;
            living++;
            var threat=state.Threat(p);
            block+=Math.Min(state.Block(p.Creature),threat.AttackDamage);
            margin+=Math.Clamp((health-threat.HpLoss)/5,-1,1);
            nextEnergy+=state.Power<EnergyNextTurnPower>(p.Creature);
            nextDraw+=state.Power<DrawCardsNextTurnPower>(p.Creature);
            if(!state.CanAct(p))continue;
            int available=Math.Max(0,state.Energy(p)),cost=0;
            foreach(var c in state.Simulator.State.GetPlayerCombatState(p).Hand.Cards)
                cost+=c.Preview.EnergyCost.CostsX?available:Math.Max(0,c.GetEnergyCostValueWithModifiers(state.Simulator));
            energy+=Math.Min(available,cost);hand+=state.UsefulHandCount(p);
        }
        var opposition=state.RemainingOpposition();
        double enemyHealthProportions=0;
        foreach(var enemy in state.Enemies)
            enemyHealthProportions+=Math.Clamp(state.Hp(enemy)/(double)Math.Max(1,state.MaxHp(enemy)),0,1);
        return new[]{-opposition.Hp/10.0,block/5,(hp-DeathSaves.RecoverySince(root,state))/5,energy,hand,potions,
            1-Math.Clamp(enemyHealthProportions/openingEnemies,0,1),living/(double)state.Party.Count,
            (openingPrimary-opposition.Count)/(double)openingPrimary,
            margin/state.Party.Count,nextEnergy,nextDraw};
    }
    internal double Score(double[] state,double[] origin)
    {
        var w=Weights;double value=0;
        for(int i=0;i<w.Length;i++)value+=w[i]*(state[i]-origin[i]);
        return 1/(1+Math.Exp(-value));
    }
    internal void Compare(double[] x,double[] y,int order)
    {
        lock(gate)
        {
            pairs++;
            if(order==0)return;
            int sign=Math.Sign(order);
            var old=weights;double prediction=0,norm=0;
            for(int i=0;i<old.Length;i++)
            {
                double delta=sign*(x[i]-y[i]);
                prediction+=old[i]*delta;norm+=delta*delta;
            }
            double error=1/(1+Math.Exp(Math.Clamp(prediction,-40,40)));
            double rate=0.25/(Math.Sqrt(pairs)*Math.Max(1,Math.Sqrt(norm)));
            var next=new double[old.Length];
            for(int i=0;i<next.Length;i++)
                next[i]=old[i]+rate*(error*sign*(x[i]-y[i])-.10*(old[i]-Mean[i]));
            Volatile.Write(ref weights,next);
        }
    }
}

internal sealed partial class AnytimeSearch
{
    internal FightEvaluation Evaluation {get;init;}=new();
    private sealed record EvaluationOpening(double[] Features,int Enemies,int Primary);
    private EvaluationOpening? evaluationOpening;
    private double[]? rolloutFeatures;
    private RolloutPolicyKind rolloutPolicy;
    private sealed record TrainingSample(Node Child,double[] Features,TerminalRecord Ending);
    // Entries live only as long as this planner and their parent. Never pair across root changes.
    private readonly ConditionalWeakTable<Node,Dictionary<RolloutPolicyKind,TrainingSample>> siblingSamples=new();
    private EvaluationOpening ReadEvaluationOpening()
    {
        var opening=Volatile.Read(ref evaluationOpening);
        if(opening!=null)return opening;
        int enemies=Math.Max(1,root.Enemies.Count);
        int primary=Math.Max(1,root.RemainingOpposition().Count);
        opening=new(FightEvaluation.Features(root,root,enemies,primary),enemies,primary);
        return Interlocked.CompareExchange(ref evaluationOpening,opening,null)??opening;
    }
    private double[] ReadFeatures(KernelSession state)
    {
        var opening=ReadEvaluationOpening();
        return ReferenceEquals(state,root)?opening.Features:FightEvaluation.Features(state,root,opening.Enemies,opening.Primary);
    }
    private double ScoreFeatures(double[] features)=>Evaluation.Score(features,ReadEvaluationOpening().Features);
    private void LearnFromRollout(Node leaf,RolloutPolicyKind policy,double[] features,TerminalRecord ending)
    {
        if(!ending.Verified || leaf.Parent is not {} parent)return;
        var samples=siblingSamples.GetOrCreateValue(parent);
        if(!samples.TryGetValue(policy,out var other))
        {
            samples[policy]=new(leaf,features,ending);return;
        }
        if(other.Child==leaf)return;
        samples.Remove(policy);
        if(samples.Count==0)siblingSamples.Remove(parent);
        Evaluation.Compare(features,other.Features,Compare(ending,other.Ending));
    }
}
