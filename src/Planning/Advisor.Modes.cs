using System;
using System.IO;
using Environment=System.Environment;
using System.Linq;
using CoopBots.Kernel;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Runs;

namespace CoopSolver;

internal sealed partial class Advisor
{
    private CombatState? observedBoard;
    private bool openingChecked,waitingForStable;
    private int rootRound,rotations;
    private long nextStampCheck,nextRotation,finishedNodes,finishedSettled,finishedReplays;
    private string? openingKey,seedDirectory;
    private BattleExchange? exchange;
    private BattleExchange.Position? livePosition;
    private string attempt="";
    private string? openingWitness,capturedWitness;
    private readonly System.Collections.Generic.List<TapeAction> tapeInputs=new();
    private string? archiveError;
    private OpeningResult? savedResult;
    private AnytimeSearch? reuseSource;
    private SimulationMeter meter=new();

    private static string BoardStamp(CombatState state)=>KernelSession.CaptureLiveStamp(state)+"|ready:"+
        string.Join(",",state.Players.Where(CombatManager.Instance.IsPlayerReadyToEndTurn).Select(p=>p.NetId));

    private void ObserveOpening(CombatState? live,bool stable)
    {
        if(!ReferenceEquals(live,observedBoard))
        {
            ClearTree();
            observedBoard=live;savedResult=null;openingKey=null;openingChecked=false;seedDirectory=null;exchange=null;openingWitness=null;tapeInputs.Clear();attempt=Guid.NewGuid().ToString("N");initialWindowPending=live!=null;
            if(live==null){if(!showLive)Paint();return;}
        }
        if(live==null || !stable || openingChecked)return;
        if(live.RoundNumber==1 && live.Players.Any(p=>p.Creature.IsAlive &&
            p.PlayerCombatState?.Phase!=PlayerTurnPhase.Play))return;
        openingChecked=true;
        if(live.RoundNumber!=1 || live.Players.Any(CombatManager.Instance.IsPlayerReadyToEndTurn) ||
            CombatManager.Instance.History.Entries.Any(e=>e is PotionUsedEntry ||
                e is CardPlayStartedEntry card && !card.CardPlay.IsAutoPlay))
        {
            openingKey=null;savedResult=null;Paint();return;
        }
        if(!Capture(live)){root=null;board=null;return;}
        // Capture itself must not consume the automatic test's start request.
        if(caseOutput!=null && !openingWanted)caseStarted=false;
        string key=OpeningArchive.Key(live,stamp);
        seedDirectory=SlCache.DirectoryFor(key);
        openingWitness=SlCache.Witness(live);exchange=new(openingWitness);
        if(key!=openingKey){savedResult=null;openingKey=key;}
        archiveError=null;
        try
        {
            var disk=OpeningArchive.Load(key);
            if(disk==null && NativeUi)
            {
                var old=OpeningArchive.Load(OpeningArchive.Key(live,stamp,"v0.3.0"))??OpeningArchive.Load(OpeningArchive.Key(live,stamp,"v0.2.0"));
                if(old!=null)disk=OpeningArchive.SaveBest(key,old);
            }
            if(disk!=null)disk=RefreshArchivedDisplay(disk);
            if(disk!=null && disk.Improves(savedResult))savedResult=disk;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {archiveError="开局路线读取失败："+e.Message;}
        exchange?.PublishOpening(savedResult?.Seed);
        meter=new();rotations=0;finishedNodes=finishedSettled=finishedReplays=0;
        if(!showLive){routeWindow?.Reset();Paint();}
    }

    private void UpdateFollow(CombatState? live,bool stable,bool start,bool switched)
    {
        if(!ReferenceEquals(board,live) && live!=null || live==null && board!=null)
        {
            ClearTree();reuseSource?.Dispose();reuseSource=null;savedResult=null;meter=new();rotations=0;finishedNodes=finishedSettled=finishedReplays=0;
            routeWindow?.Reset();board=live;
        }
        if(!stable || live==null)
        {
            if(!waitingForStable)
            {
                activeTime.Stop();engine?.Pump(false);
                routeWindow?.SetStatus("正在结算实战动作","下方保留上一次路线，结算后核对已执行的步骤。");
                waitingForStable=true;
            }
            return;
        }
        long now=Environment.TickCount64;
        var actionId=RunManager.Instance.ActionQueueSet.NextActionId;
        bool inspect=root==null || completedInputs.Count!=0 || waitingForStable || start || switched ||
            initialActionId!=actionId || revision!=KernelSession.LiveRevision || now>=nextStampCheck;
        if(inspect)
        {
            nextStampCheck=now+500;
            string next=BoardStamp(live);
            if(root==null || next!=stamp)
            {
                if(UseWorker)
                {
                    engine?.Pump(false);
                    var previousRoot=root;
                    if(!Capture(live)){root=null;return;}
                    meter=new();
                    capturedWitness=SlCache.Witness(live);livePosition=owner?.exchange?.Current(capturedWitness);
                    ReconcileLive(root!,previousRoot);boundRoot=null;
                    if(followWanted && !start){running=true;activeTime.Start();}
                    PaintInteractive("核对实战盘面");
                }
                else
                {
                var old=planner;planner=null;
                // A second native action can arrive while the previous transfer
                // is only half indexed. Do not chain superseded snapshots.
                if(old?.Recovering==true){old.Dispose();old=null;}
                reuseSource?.Dispose();reuseSource=null;
                ClearTree();savedResult=null;routeWindow?.Reset();
                if(!Capture(live)){old?.Dispose();root=null;return;}
                reuseSource=old;
                }
            }
            completedInputs.Clear();initialActionId=actionId;revision=KernelSession.LiveRevision;
        }
        bool wasWaiting=waitingForStable;waitingForStable=false;
        if(running && (wasWaiting || switched)){activeTime.Start();ShowResult();}
        bool begin=followWanted && !running && (planner==null || switched);
        UpdateTree(live,true,start || begin);
    }

    private void DropInactiveBattle(CombatState? current)
    {
        if(board==null || ReferenceEquals(board,current))return;
        ClearTree();reuseSource?.Dispose();reuseSource=null;savedResult=null;
    }

    private void Publish(OpeningResult result)
    {
        if(!result.Improves(savedResult))return;
        savedResult=result;
        if(!Follow && openingKey!=null)
        {
            try
            {
                savedResult=OpeningArchive.SaveBest(openingKey,result);
                exchange?.PublishOpening(savedResult.Seed);
                if(result.Seed is {} seed && result.Ending!=null && seedDirectory!=null)SlCache.SaveSeed(seedDirectory,seed);
                archiveError=null;
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {archiveError="开局结果仅保留在内存："+e.Message;}
        }
        if(Follow && owner?.seedDirectory is {} directory && capturedWitness!=null && result.Seed!=null)
        {
            livePosition?.Offer(result.Seed);
            try
            {
                if(capturedWitness==owner.openingWitness && owner.openingKey!=null)
                {
                    owner.savedResult=OpeningArchive.SaveBest(owner.openingKey,result);
                    owner.exchange?.PublishOpening(owner.savedResult.Seed);
                    if(result.Ending!=null)SlCache.SaveSeed(directory,result.Seed);
                }
                else OpeningArchive.SaveBestAt(Path.Combine(directory,capturedWitness+".continuation"),result);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { archiveError="实战续线未写入："+e.Message; }
        }
        ShowResult();
    }

    private void ShowResult()
    {
        if(routeWindow==null)return;
        if(savedResult==null)
        {
            var reference=FallbackPlan();
            if(reference!=null)routeWindow.SetRoute(reference.Steps,reference.Result,reference);
            return;
        }
        routeWindow.SetRoute(savedResult.Route,savedResult.Summary,savedResult.Hud);
        SetWindowRisks(savedResult.Risks);
        if(archiveError!=null)routeWindow.SetNotice(archiveError);
    }

    private void Paint()
    {
        if(routeWindow==null)return;
        if(UseWorker && engine!=null)PaintInteractive(running?"正在计算":"计算已暂停");
        else if(planner!=null)UpdateWindowProgress(running?"正在计算":"计算已暂停");
        else routeWindow.SetStatus(Follow?"等待实战盘面":savedResult!=null?"已保留开局路线":
            root!=null?"等待求解":"等待战斗开局",Follow?"结算后自动跟随当前盘面。":
            root==null && savedResult==null?"进入战斗时捕获第一回合；中途可切换跟随实战。":
            "按本场第一回合计算，实战操作不会改写开局结果。");
        ShowResult();
        if(guardLine!=null && UseWorker)RenderLine(guardLine,false,asGuard:true);
        if(archiveError!=null)routeWindow.SetNotice(archiveError);
        SyncWindowControl();
    }

    private string TrackingDetail()=>Follow && planner!=null ?
        $"\n实战第 {rootRound} 回合 · 沿用 {planner.ReusedNodes:N0} 个节点、{planner.ReusedRoundRows:N0} 条路径"+
        (planner.Recovering?"（核对中）":"") : $"\n从战斗开局计算 · 已释放 {rotations} 轮搜索缓存";

    private bool RotateIfNeeded()
    {
        var p=planner!;
        if(p.Recovering || Environment.TickCount64<nextRotation)return false;
        var gc=GC.GetGCMemoryInfo();
        bool pressure=gc.HighMemoryLoadThresholdBytes>0 && gc.MemoryLoadBytes>=gc.HighMemoryLoadThresholdBytes &&
            gc.HeapSizeBytes-initialHeap>192L*1024*1024;
        if(p.Nodes<24000 && p.RoundFileBytes<48L*1024*1024 && !(pressure && p.Nodes>=4000))return false;
        finishedNodes+=p.Nodes;finishedSettled+=p.SettledPlans;finishedReplays+=p.Replays;
        p.Dispose();rotations++;nextRotation=Environment.TickCount64+5000;
        planner=new AnytimeSearch(root!,roundSeed:171+rotations){FollowEnabled=Follow,TrackPreview=savedResult?.Ending==null};
        planner.IncumbentChanged+=PublishBest;
        if(caseOutput!=null)planner.IncumbentChanged+=RecordIncumbent;
        planner.KeepBestFromSameRoot(p);
        if(pressure)GC.Collect(2,GCCollectionMode.Optimized,blocking:false,compacting:false);
        return true;
    }
}
