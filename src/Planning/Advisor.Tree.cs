using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private readonly bool legacyMode=Environment.GetEnvironmentVariable("STS2_LEGACY_SEARCH")=="1";
    private AnytimeSearch? planner;
    private readonly Stopwatch activeTime=new();
    private bool running;
    private long treeTicks,referenceTicks;
    private long autoStopMs, nextStatus, revision, seedCheck, initialHeap;
    private uint initialActionId;
    private TournamentResult? lastSeed;
    private object? lastSeedScript;
    private readonly bool studyAfterWin=Environment.GetEnvironmentVariable("STS2_POST_WIN_STUDY")=="1";
    private long? firstStudyWinMs;
    private readonly int longStudySeconds=int.TryParse(Environment.GetEnvironmentVariable("STS2_LONG_STUDY_SECONDS"),out var seconds) ? seconds : 0;
    private long nextLongSample,lastFrameTick;
    private readonly System.Collections.Generic.List<double> frameGaps=new();
    private int benchIndex;
    private bool benchResume;
    private readonly int[] benchmarks=ReadBenchmarks();

    private static int[] ReadBenchmarks()
    {
        var text=Environment.GetEnvironmentVariable("STS2_TREE_BENCH_SECONDS");
        if(string.IsNullOrWhiteSpace(text))return Array.Empty<int>();
        return text.Split(',').Select(int.Parse).Where(i=>i>0 && i<=3600).Distinct().Order().ToArray();
    }
    private void ClearTree()
    {
        engine?.Release();engine=null;boundRoot=null;shownLine=lastPublication=pendingSuffix=guardLine=null;guardHud=provisionalHud=null;
        displayedPreview=null;
        reuseSource?.Dispose();reuseSource=null;
        running=false; planner?.Dispose(); planner=null; lastSeed=null; lastSeedScript=null; benchResume=false;
        search?.Dispose();search=null;
        root=null;board=null;
        participants=Array.Empty<MegaCrit.Sts2.Core.Entities.Players.Player>();
        actors=participants;
        activeTime.Reset();
        SetButton("求解 (F8)");
    }
    private void UpdateTree(CombatState? live,bool playing,bool start)
    {
        if(root==null || Follow && !playing)return;
        if(UseWorker){UpdateInteractive(start);return;}
        using var counting=meter.Enter();
        if(benchResume){start=true;benchResume=false;}
        if(start)
        {
            if(running){PauseTree("手动暂停");return;}
            caseStarted=true;
            if(planner==null)
            {
                if(savedResult==null)routeWindow?.Reset();
                if(caseOutput!=null && Environment.GetEnvironmentVariable("COOPBOTS_ALLOC_PROBE")=="1")
                {
                    AllocationProbe.RefreshEnabled();
                    AllocationProbe.ReportAndReset();
                }
                int seed=longStudySeconds>0 && caseOutput!=null &&
                    int.TryParse(Environment.GetEnvironmentVariable("STS2_ROUND_SEED"),out var requestedSeed) ? requestedSeed : 171;
                planner=new AnytimeSearch(root!,roundSeed:seed){FollowEnabled=Follow};
                if(reuseSource!=null){planner.InheritCompatibleWork(reuseSource);reuseSource=null;}
                planner.IncumbentChanged+=PublishBest;
                if(caseOutput!=null)planner.IncumbentChanged+=RecordIncumbent;
                planner.TrackPreview=caseOutput!=null || routeWindow!=null;
                if(routeWindow!=null)
                {
                    displayedPreview=null;

                }
                initialHeap=GC.GetGCMemoryInfo().HeapSizeBytes;
                revision=KernelSession.LiveRevision;
                initialActionId=RunManager.Instance.ActionQueueSet.NextActionId;
                benchIndex=0;
                firstStudyWinMs=null;
                if(longStudySeconds>0 && caseOutput!=null)
                    planner.RoundSample=row=>File.AppendAllText(caseOutput+".rounds.jsonl",JsonSerializer.Serialize(new {ms=activeTime.ElapsedMilliseconds,row})+"\n");
                nextLongSample=60000;
                lastFrameTick=0;
                frameGaps.Clear();
                treeTicks=referenceTicks=0;
                baselinePhase=true;
                search=NewSearch(false,40000,null);
            }
            running=true;
            if(savedResult==null && planner.Best==null && planner.Preview==null)routeWindow?.SetNotice("");
            autoStopMs=benchmarks.Length>0 && benchIndex<benchmarks.Length
                ? 1000L*benchmarks[benchIndex] : caseOutput!=null ? activeTime.ElapsedMilliseconds+120000 : long.MaxValue;
            if(studyAfterWin && caseOutput!=null)
                autoStopMs=firstStudyWinMs.HasValue ? firstStudyWinMs.Value+120000 : 360000;
            if(longStudySeconds>0 && caseOutput!=null)autoStopMs=1000L*longStudySeconds;
            activeTime.Start();
            UpdateWindowProgress("正在计算");
            SyncWindowControl();
            nextStatus=0;
        }
        if(!running || planner==null)return;
        bool longStudy=longStudySeconds>0 && caseOutput!=null;
        if(longStudy)
        {
            long now=Stopwatch.GetTimestamp();
            if(lastFrameTick!=0)frameGaps.Add(1000.0*(now-lastFrameTick)/Stopwatch.Frequency);
            lastFrameTick=now;
        }
        bool hadWin=planner.Best?.Record?.Victory==true;
        // TournamentRun accounts its own compute slices. With 3 ms only
        // every third frame the reference often never reached the potion pass
        // in five minutes. Both jobs receive service from the first frame.
        long sliceStart=Stopwatch.GetTimestamp();
        var host=owner??this;
        int share=host.showLive && host.running && host.liveAdvisor?.running==true ? 2 : 1;
        planner.Advance(TimeSpan.FromMilliseconds((search==null?10.0:4.0)/share));
        treeTicks+=Stopwatch.GetTimestamp()-sliceStart;
        if(search!=null)
        {
            sliceStart=Stopwatch.GetTimestamp();
            bool done=search.Advance(TimeSpan.FromMilliseconds(7.0/share));
            if(done || Environment.TickCount64>=seedCheck)
            {
                seedCheck=Environment.TickCount64+750;
                var candidate=done?search.Result:search.BestCompleted;
                if(candidate?.Record is {Verified:true} record && candidate.Script!=null &&
                    !ReferenceEquals(candidate.Script,lastSeedScript))
                {
                    lastSeedScript=candidate.Script;
                    if(lastSeed?.Record is not {} prev || AnytimeSearch.Compare(record,prev)>0) lastSeed=candidate;
                    planner.OfferReference(candidate);
                }
            }
            if(done)
            {
                var saved=search.Result; search.Dispose();search=null;
                if(baselinePhase){baselinePhase=false;search=NewSearch(true,80000,saved);}
            }
            referenceTicks+=Stopwatch.GetTimestamp()-sliceStart;
        }
        if(planner.TrackPreview && planner.Best==null)planner.AdvancePreview();
        if(planner.Best==null && planner.Preview is {} preview && !ReferenceEquals(preview,displayedPreview))
        {
            displayedPreview=preview;
            var health=participants.Zip(preview.Hp,(player,value)=>$"{player.Character.Title.GetFormattedText()} {value}");
            string detail=$"中间路线 · 暂算至第 {preview.Turn} 回合，胜负未定\n"+
                $"队伍生命   {string.Join("  /  ",health)}\n剩 {preview.Enemies} 名敌人，共 {preview.EnemyHp} 生命；剩余药水 {preview.Bottles} 瓶";
            Publish(new(null,preview.Actions.Length,preview.Score,"中间路线\n"+FormatSteps(board!,preview.Actions,rootRound,root),detail,preview.Risks));
        }
        bool newWin=!hadWin && planner.Best?.Record?.Victory==true;
        if(longStudy && (activeTime.ElapsedMilliseconds>=nextLongSample || activeTime.ElapsedMilliseconds>=autoStopMs))
        {
            RecordLongSample(planner);
            nextLongSample=(activeTime.ElapsedMilliseconds/60000+1)*60000;
        }
        if((newWin && !longStudy && caseOutput!=null) || activeTime.ElapsedMilliseconds>=autoStopMs || planner.Finished && search==null)
        {
            PauseTree(newWin?"找到已复算的模拟胜线":"本段计算结束");
            return;
        }
        if(Environment.TickCount64>=nextStatus)
        {
            nextStatus=Environment.TickCount64+1000;
            SetButton($"暂停计算 (F8) · {activeTime.Elapsed.TotalSeconds:F0} 秒 · 普通动作树 {planner.Nodes} 个节点");
            UpdateWindowProgress("正在计算");
            if(RotateIfNeeded())return;
            var memory=GC.GetGCMemoryInfo();
            if(memory.HeapSizeBytes-initialHeap>768L*1024*1024 && memory.HighMemoryLoadThresholdBytes>0 &&
                memory.MemoryLoadBytes>=memory.HighMemoryLoadThresholdBytes)
                PauseTree("内存压力较高，已保留搜索进度");
        }
    }
    private AnytimeSearch.PreviewLine? displayedPreview;

    private void PublishBest()
    {
        var best=planner?.Best;
        if(best?.Record is not {} record)return;
        displayedPreview=null;
        var hp=participants.Zip(record.PostCombatHp,(player,value)=>$"{player.Character.Title.GetFormattedText()} {value}");
        int used=best!.Script!.Count(action=>action.Potion!=null);
        string summary=record.Victory ? "模拟胜利 · 战后生命   "+string.Join("  /  ",hp) :
            $"当前最佳完整路线 · 第 {planner!.BestTurnNumber} 回合全队倒下\n剩 {record.RemainingEnemies} 名敌人，合计 {record.RemainingEnemyHp} 生命";
        Publish(new(record,best.Script!.Count,0,Format(best),summary+$"\n全队用药 {used} 瓶，剩余 {planner!.BestBottlesLeft} 瓶",planner.BestRisks));
    }

    private void SetWindowRisks(string[] risks)
        => routeWindow?.SetNotice(risks.Length>0?ReplayCoverage.Summary(risks)+" 悬停查看详情。":"",string.Join("\n",risks));

    private void UpdateWindowProgress(string title)
    {
        if(routeWindow==null || planner==null)return;
        var elapsed=activeTime.Elapsed;
        string time=$"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
        if(running && savedResult?.Ending?.Victory==true)title="正在优化胜线";
        routeWindow.SetStatus(title,$"累计 {time}   ·   模拟动作 {meter.Actions:N0} 次（出牌 {meter.Plays:N0} / 用药 {meter.Potions:N0} / 结束回合 {meter.Ends:N0}）\n动作树 {planner.Nodes:N0} 个节点   ·   回合评估 {planner.SettledPlans:N0} 次"+TrackingDetail());
    }

    private void RecordLongSample(AnytimeSearch p)
    {
        using var process=Process.GetCurrentProcess();
        var memory=GC.GetGCMemoryInfo();
        frameGaps.Sort();
        var row=new {ms=activeTime.ElapsedMilliseconds,rotations,simulatedActions=meter.Actions,simulatedPlays=meter.Plays,simulatedPotions=meter.Potions,simulatedEnds=meter.Ends,totalNodes=finishedNodes+p.Nodes,totalSettled=finishedSettled+p.SettledPlans,totalReplays=finishedReplays+p.Replays,p.Nodes,p.DeferredRoundJobs,p.RoundFileBytes,p.SettledPlans,p.TreeSeededPasses,p.FarthestRootTurn,p.FarthestTurn,
            p.RootMoves,p.RootOptions,p.AnchorVisits,p.Replays,p.RejectedReplays,p.CompletedRollouts,
            rss=process.WorkingSet64,privateBytes=process.PrivateMemorySize64,heapAtLastGc=memory.HeapSizeBytes,
            initialHeap,systemMemoryLoad=memory.MemoryLoadBytes,systemHighThreshold=memory.HighMemoryLoadThresholdBytes,
            gen2=GC.CollectionCount(2),frames=frameGaps.Count,
            frameP95=frameGaps.Count==0?0:frameGaps[(int)Math.Ceiling(0.95*frameGaps.Count)-1],
            frameMax=frameGaps.Count==0?0:frameGaps[^1],best=p.Best?.Record};
        File.AppendAllText(caseOutput+".long.jsonl",JsonSerializer.Serialize(row)+"\n");
        frameGaps.Clear();
    }
    private void RecordIncumbent()
    {
        var p=planner!;
        var best=p.Best!;
        var route=best.Script!.Select(a=>new {seat=Array.IndexOf(participants,a.Player)+1,
            move=a.Card?.Id.Entry??a.Potion?.Id.Entry??"end",target=a.Target?.CombatId,
            a.CardStateKey,a.CardStateOccurrence,a.ChoiceOrdinals,a.Choices,a.TurnStartChoices}).ToArray();
        File.AppendAllText(caseOutput+".improvements.jsonl",JsonSerializer.Serialize(new {
            ms=activeTime.ElapsedMilliseconds,p.BestOrigin,p.Replays,p.Nodes,record=best.Record,route,risks=p.BestRisks})+"\n");
    }
    private void PauseTree(string reason)
    {
        activeTime.Stop();running=false;
        var p=planner!;
        var best=p.Best;
        string summary=best?.Record is {Verified:true} ? Format(best)
            : p.Preview is {} preview ? $"当前中间路线，暂算至第 {preview.Turn} 回合，胜负未定。\n"+FormatSteps(board!,preview.Actions,rootRound,root)
            : "本段未找到已复算的路线。";
        string stats=$"{reason}。累计 {activeTime.Elapsed.TotalSeconds:F1} 秒，普通动作树已存 {p.Nodes} 个节点，已展开 {p.RootMoves} 种开局动作、含选牌共 {p.RootOptions} 条，最深 {p.MaxDepth} 步。已做 {p.SettledPlans} 次回合结算评估，仍有 {p.DeferredRoundJobs} 项排队，从当前盘面起算最远到第 {Math.Max(board!.RoundNumber,p.FarthestRootTurn)} 回合。";
        if(p.RoundStorageError!=null)stats+="\n回合候选的临时文件不可用，普通动作树仍可搜索："+p.RoundStorageError;
        if(p.BestRisks.Length>0)
        {
            var details=p.BestRisks.Where(x=>!string.IsNullOrWhiteSpace(x)).Take(2).ToArray();
            string warning=ReplayCoverage.Summary(p.BestRisks);
            if(details.Length>0) warning+=" "+string.Join("、",details);
            summary=warning+"\n"+summary;
        }
        if(p.Boundaries.Count>0)stats+="\n部分模拟分支未能结算："+string.Join("、",p.Boundaries.OrderByDescending(k=>k.Value).Take(3).Select(k=>k.Key));
        if(caseOutput!=null)
        {
            double Seconds(long ticks)=>(double)ticks/Stopwatch.Frequency;
            var profile=new {rounds=Seconds(p.RoundTicks),tree=Seconds(treeTicks),reference=Seconds(referenceTicks),walk=Seconds(p.VisitTicks),rollout=Seconds(p.RolloutTicks),verify=Seconds(p.VerifyTicks),potionTrials=Seconds(p.PotionTrialTicks),p.VisitSteps,p.RolloutSteps};
            var kernelWork=AllocationProbe.Enabled ? AllocationProbe.ReportAndReset() : null;
            if(longStudySeconds>0 && frameGaps.Count>0)RecordLongSample(p);
            var metrics=new {reason,p.DeferredRoundJobs,p.RoundFileBytes,p.SettledPlans,p.TreeSeededPasses,p.FarthestRootTurn,p.FarthestTurn,kernelWork,milliseconds=activeTime.ElapsedMilliseconds,p.Nodes,p.RootMoves,p.RootOptions,p.MaxDepth,p.CompletedRollouts,
                p.TruncatedRollouts,p.Replays,p.RejectedReplays,p.AnchorVisits,p.RoundAnchorCount,p.OpeningPotionEdges,p.LaterPotionEdges,p.BestOrigin,risks=p.BestRisks,boundaries=p.Boundaries,
                simulatedActions=meter.Actions,simulatedPlays=meter.Plays,simulatedPotions=meter.Potions,simulatedEnds=meter.Ends,rotations,profile,p.BestBottlesLeft,best=best?.Record,seed=lastSeed?.Record,seedPhase=search==null?"done":baselinePhase?"cards":"potions",revision};
            File.AppendAllText(caseOutput+".tree.jsonl",JsonSerializer.Serialize(metrics)+"\n");
            if(best?.Script is {} line)
                File.WriteAllText(caseOutput+".verified-line.json", JsonSerializer.Serialize(new {
                    best.Record, risks=p.BestRisks,
                    steps=line.Select(a=>new {seat=Array.IndexOf(participants,a.Player)+1, card=a.Card?.Id.Entry,
                        potion=a.Potion?.Id.Entry, target=a.Target?.Player?.Character.Id.Entry??a.Target?.Monster?.Id.Entry,
                        targetCombatId=a.Target?.CombatId, a.CardStateKey, a.CardStateOccurrence, a.EndTurn, a.Choices, a.TurnStartChoices})
                }));
        }
        if(studyAfterWin && caseOutput!=null && firstStudyWinMs==null && best?.Record?.Victory==true && !(p.Finished && search==null))
        {
            firstStudyWinMs=activeTime.ElapsedMilliseconds;
            benchResume=true;
            return;
        }
        if(benchmarks.Length>0 && activeTime.ElapsedMilliseconds>=autoStopMs && ++benchIndex<benchmarks.Length &&
            best?.Record?.Victory!=true && !(p.Finished && search==null))
        {benchResume=true;return;}
        Report(summary+"\n\n"+stats+"\n盘面保持不变时，可继续计算。",false);
        UpdateWindowProgress(best?.Record?.Victory==true && reason=="找到已复算的模拟胜线" ? "已找到模拟胜线" : reason);
        ShowResult();
        if(savedResult==null && best==null && p.Preview==null)routeWindow?.SetNotice(summary);
        SyncWindowControl();
        SetButton("继续计算 (F8)");
    }
}
