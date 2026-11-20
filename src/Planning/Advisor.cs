using System;
using Environment = System.Environment;
using System.Linq;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Godot;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CoopSolver;

internal sealed partial class Advisor(SceneTree tree)
{
    public static void Attach()
    {
        SearchCapacity.Load();
        SimulationMeter.Install();
        ChaosPotionChoices.Install();
        ExactMenus.Install();
        ReplayCoverage.Install();
        PotionOpenings.Install();
        RetrievalChoices.Install();
        InterceptMirror.Install();
        var tree=(SceneTree)Engine.GetMainLoop();
        var advisor=new Advisor(tree);
        advisor.liveAdvisor=new Advisor(tree){owner=advisor};
        advisor.share=new RouteShare();advisor.share.Install();
        tree.ProcessFrame+=advisor.Tick;
        GD.Print("[CoopSolver] Ready");
    }
    private RouteWindow? window;
    private Advisor? owner,liveAdvisor;
    private bool resultsHidden,searchEnabled,manuallyPaused;
    private bool showLive,switchRequested,followWanted,openingWanted;
    private bool Follow=>owner!=null;
    private Advisor Selected=>showLive?liveAdvisor!:this;
    private RouteWindow? routeWindow=>Follow ? (owner!.showLive?owner.window:null) : (!showLive?window:null);
    private string buttonLabel="求解 (F8)";
    private void SetButton(string text)
    {
        buttonLabel=text;

    }
    private bool canSolve;
    private bool NativeUi=>DisplayServer.GetName()!="headless";

    private void OpenRoute(bool automatic=false)
    {
        if(owner!=null){owner.OpenRoute(automatic);return;}
        if(!NativeUi || resultsHidden)return;
        window??=new RouteWindow(tree,()=>requested=true,value=>{if(showLive!=value){showLive=value;switchRequested=true;}});
        bool keepFocus=automatic && tree.Root.HasFocus();
        if(!automatic || !window.IsShown)window.Open();
        if(keepFocus)tree.Root.GrabFocus();
        Selected.Paint();
        Selected.SyncWindowControl();
    }

    private int ViewPercent=>showLive?100-SearchCapacity.OpeningPercent:SearchCapacity.OpeningPercent;
    private string ToggleCaption()
    {
        return searchEnabled?"暂停搜索":SearchCapacity.OpeningPercent==100?"开始搜开局":"开始搜实战";
    }
    private void SyncWindowControl()
    {
        if(UseWorker)
        {
            var host=owner??this;
            routeWindow?.SetControl(host.ToggleCaption(),host.searchEnabled || canSolve || host.root!=null);return;
        }
        bool legacyBusy=legacyMode && (search!=null || replay!=null);
        routeWindow?.SetControl(legacyBusy?"计算中":running?"暂停计算":(planner!=null || engine!=null)?"继续计算":"开始求解",(canSolve || !Follow && root!=null) && !legacyBusy);
    }
    private bool requested, holdingKey, caseStarted;
    private CombatState? board;
    private TournamentRun? search;
    private bool baselinePhase;
    private Player[] participants = Array.Empty<Player>();
    private Player[] actors = Array.Empty<Player>();
    private KernelSession? root;
    private KernelScriptReplayRun? replay;
    private string stamp = "";
    private readonly string? caseOutput = Environment.GetEnvironmentVariable("STS2_SOLVER_CASE_RESULT");
    private long nextCheck;

    public void Tick()
    {
        try { Update(); }
        catch (Exception e)
        {
            search?.Dispose();
            search = null;
            replay = null;
            ClearTree();
            liveAdvisor?.ClearTree();
            GD.PrintErr("[CoopSolver] " + e);
            Report("求解未完成：" + e.GetBaseException().Message);
        }
    }

    private void Update()
    {
        if(TickHud())return;
        window?.TickGeometry();
        RefreshChoice();
        if (openingChecked && observedBoard!=null && !running && liveAdvisor?.running!=true && search==null && replay==null && !requested && !switchRequested && Environment.TickCount64<nextCheck && !Input.IsKeyPressed(Key.F8))return;
        nextCheck = Environment.TickCount64 + 100;
        var manager = CombatManager.Instance;
        var live = hudRoom; // TickHud already selected the combat and handled remote clients.
        bool playing = live != null && !SelectingHand && !manager.IsPaused && !manager.IsEnding
            && RunManager.Instance.ActionQueueSynchronizer.CombatState == MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase
            && RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null && RunManager.Instance.ActionQueueSet.IsEmpty
            && live.Players.Any(p => p.Creature.IsAlive && p.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
        canSolve=playing;
        if(liveAdvisor!=null)liveAdvisor.canSolve=playing;

        Selected.SyncWindowControl();
        bool pressed = Input.IsKeyPressed(Key.F8);
        bool hotkey=pressed && !holdingKey;
        bool reopen=hotkey && window?.IsShown!=true;
        bool manual=requested || hotkey && !reopen;
        bool start = manual || playing && caseOutput != null && !caseStarted
            && Environment.GetEnvironmentVariable("STS2_CASE_AUDIT_ONLY") != "1";
        holdingKey = pressed;
        requested = false;
        if (!legacyMode)
        {
            if(Environment.GetEnvironmentVariable("STS2_CASE_AUDIT_ONLY")=="1")return;
            WatchNativeInput(live);
            ObserveOpening(live,playing);
            if(playing && seedDirectory!=null && tapeInputs.Count>0)
            {
                var group=new TapeGroup(tapeInputs.ToArray(),SlCache.Witness(live!));exchange?.Append(group);
                try{SlCache.Append(seedDirectory,attempt,group);}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException){archiveError="实战路径未写入："+e.Message;}
                tapeInputs.Clear();
            }
            if(initialWindowPending && playing)initialWindowPending=false;
            if(hotkey && (playing || window!=null))OpenRoute();
            bool changedTab=switchRequested;
            switchRequested=false;
            if(UseWorker)
            {
                if(changedTab)
                {
                    nextHud=0;
                    if(Selected.savedResult==null && Selected.provisionalHud==null && Selected.FallbackPlan()==null)window?.Reset();
                    Selected.Paint();
                    if(Selected.provisionalHud is {} pending)window?.SetRoute(pending.Steps,pending.Result,pending);

                }
                if(start)
                {

                    searchEnabled=!searchEnabled;manuallyPaused=!searchEnabled;
                    if(manuallyPaused)foreach(var slot in openChoices.Values){slot.Probe?.Cancel();slot.Probe=null;}
                }
                bool wantOpening=searchEnabled && SearchCapacity.OpeningPercent>0;
                bool wantLive=searchEnabled && SearchCapacity.OpeningPercent<100;
                InteractiveSearch.Dispatcher.Split(wantOpening && wantLive && playing && root!=null);
                openingWanted=wantOpening;
                if(root!=null && ReferenceEquals(board,live))UpdateTree(live,playing,wantOpening!=running);
                var liveSide=liveAdvisor!;
                if(!wantLive && liveSide.running)
                {
                    liveSide.running=false;liveSide.activeTime.Stop();liveSide.engine?.Pump(false);
                }
                liveSide.followWanted=wantLive;
                if(wantLive || showLive)liveSide.UpdateFollow(live,playing,false,changedTab);
                else liveSide.DropInactiveBattle(live);
                Selected.SyncWindowControl();
                return;
            }
            if(changedTab)
            {
                window?.Reset();Selected.Paint();Selected.SetButton(Selected.buttonLabel);
                if(showLive)liveAdvisor!.followWanted=true;
                else {liveAdvisor!.activeTime.Stop();liveAdvisor.engine?.Pump(false);}
            }
            if(!showLive && start)openingWanted=!running;
            if(showLive && start)liveAdvisor!.followWanted=!liveAdvisor.running;
            if(root!=null && (ReferenceEquals(board,live) || playing))
                UpdateTree(live,playing,!showLive && start || openingWanted && !running && planner==null && engine==null);
            else if(!showLive && (start || changedTab))Paint();
            if(showLive)liveAdvisor!.UpdateFollow(live,playing,start,changedTab);
            else liveAdvisor!.DropInactiveBattle(live);
            Selected.SyncWindowControl();
            return;
        }
        if (search != null || replay != null)
        {
            if (!playing || !ReferenceEquals(board, live) || KernelSession.CaptureLiveStamp(live!) != stamp)
            {
                search?.Dispose();
                search = null;
                replay = null;
                Report("盘面已变化，请重新求解。");
                return;
            }
            if (replay != null)
            {
                replay.Advance(TimeSpan.FromMilliseconds(12));
                if (replay.Done)
                {
                    var verified = replay.Result;
                    replay = null;
                    Report(verified is { Record: { Verified: true, Victory: true } }
                        ? Format(verified) : "候选路线未能从原盘面复算到胜利。");
                }
                return;
            }
            if (search!.Advance(TimeSpan.FromMilliseconds(12)))
            {
                var result = search.Result!;
                search.Dispose();
                search = null;
                if (baselinePhase && result.Record is not { Verified: true, Victory: true })
                {
                    baselinePhase = false;
                    if (caseOutput != null)
                        File.WriteAllText(caseOutput + ".baseline.json", JsonSerializer.Serialize(result.Record));
                    search = NewSearch(includePotions:true, budget:80000, reference:result);
                    return;
                }
                if (caseOutput != null)
                    File.WriteAllText(caseOutput + ".search.json", JsonSerializer.Serialize(new {
                        result.StopReason, result.Rollouts, result.Cutoffs, result.JointNodes, result.JointDiagnostics,
                        best = result.Record, actions = result.Script?.Count,
                        route = result.Script?.Select(a=>new {seat=a.Player.Character.Id.Entry,
                            move=a.Card?.Id.Entry??a.Potion?.Id.Entry??"end",
                            target=a.Target?.Player?.Character.Id.Entry??a.Target?.Monster?.Id.Entry,
                            roundAfter=a.TurnStartChoices?.ToRound, picks=a.Choices?.SelectMany(c=>c.Cards).Select(c=>c.Id)}),
                        usedPotions = result.Script?.Where(a => a.Potion != null).Select(a => new {
                            owner = a.Player.Character.Id.Entry, potion = a.Potion!.Id.Entry,
                            target = a.Target?.Player?.Character.Id.Entry ?? a.Target?.Monster?.Id.Entry
                        })
                    }));
                if (result.Record is { Verified: true, Victory: true } && result.Script is { Count: > 0 })
                    replay = new KernelScriptReplayRun(root!, live!.Players.ToArray(), result, 80);
                else Report(Format(result));
            }
            return;
        }
        if (!start || !playing) return;
        OpenRoute();
        routeWindow?.Reset();
        routeWindow?.SetStatus("正在计算","");
        if (!Capture(live!)) return;
        baselinePhase = true;
        search = NewSearch(includePotions:false, budget:40000, reference:null);
        SetButton("正在计算…");
    }

    private bool Capture(CombatState live)
    {
        caseStarted = true;
        board = live!;
        var players = board.Players.ToArray();
        participants = players;
        actors = players.Where(p => p.Creature.IsAlive && p.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && !CombatManager.Instance.IsPlayerReadyToEndTurn(p)).ToArray();
        if (actors.Length == 0) return false;
        stamp = BoardStamp(board);
        rootRound=board.RoundNumber;
        if (caseOutput != null)
            File.WriteAllText(caseOutput + ".opening.json", JsonSerializer.Serialize(new {
                round = board.RoundNumber, seats = players.Select(p => new { character = p.Character.Id.Entry,
                    hp = p.Creature.CurrentHp, cards = p.PlayerCombatState!.Hand.Cards.Select(c => c.Id.Entry).ToArray() }),
                enemies = board.Enemies.Select(e => new { id = e.Monster?.Id.Entry, hp = e.CurrentHp })
            }));
        root = KernelSession.Capture(board);
        playerNames=RunManager.Instance.NetService.Type==NetGameType.Singleplayer?new[]{"我"}:participants.Select(p=>MegaCrit.Sts2.Core.Platform.PlatformUtil.GetPlayerName(RunManager.Instance.NetService.Platform,p.NetId)).ToArray();
        startHp=participants.Select(p=>root.Hp(p.Creature)).ToArray();
        startBottles=participants.Sum(root.HeldPotionCount);
        CaptureLabels(board);
        if (caseOutput != null)
            File.WriteAllText(caseOutput + ".belts.json", JsonSerializer.Serialize(players.Select(p => new {
                owner = p.Character.Id.Entry, bottles = p.Potions.Select(b => new {
                    slot = p.GetPotionSlotIndex(b), id = b.Id.Entry,
                    held = root.UsablePotions(p).Contains(b), modeled = KernelSession.CanModelPotion(b),
                    targets = root.PotionTargets(b).Select(t => t?.Player?.Character.Id.Entry ?? t?.Monster?.Id.Entry)
                })
            })));
        if (root.ProjectionDifferences.Count != 0)
        {
            Report("当前盘面与模拟器不一致，无法确认路线。\n" + string.Join("\n", root.ProjectionDifferences));
            return false;
        }
        return true;
    }

    private TournamentRun NewSearch(bool includePotions, int budget, TournamentResult? reference)
    {
        var options = new TournamentOptions(EnableJointSearch:true, IncludePotions:includePotions,
            ContinuationActors:participants, MaxDegreeOfParallelism:2) {ReferenceResult=reference};
        return new TournamentRun(root!, participants, actors, options, budget);
    }
    private string Format(TournamentResult r)
        => FormatRoute(board!,r,ReferenceEquals(r,planner?.Best) ? planner!.BestBottlesLeft : null,rootRound,root);

    internal static string FormatRoute(CombatState board,TournamentResult r,int? bottlesLeft,int? startRound=null,KernelSession? frozen=null)
    {
        if (r.Record is not { Verified: true } || r.Script is not { Count: > 0 })
            return "本次搜索未找到完整胜线。\n" + (r.StopReason == "deadline" ? "已到搜索时间上限。" : r.StopReason);
        var s = new StringBuilder(r.Record.Victory ? "模拟找到胜线。预计各人剩余生命："+string.Join("、",r.Record.PostCombatHp)
            : $"模拟结果：全队倒下（已复算），剩余敌人 {r.Record.RemainingEnemies} 名、合计 {r.Record.RemainingEnemyHp} 生命");
        s.Append("。全队用药 ").Append(r.Script.Count(a=>a.Potion!=null)).Append(" 瓶");
        if(bottlesLeft.HasValue)s.Append("，结束时持有 ").Append(bottlesLeft.Value).Append(" 瓶");
        return s.Append('\n').Append(FormatSteps(board,r.Script,startRound,frozen)).ToString();
    }

    private static string ChoiceNames(KernelChoice choice)
    {
            var names=choice.Cards.Select(c=>{
                var model=ModelDb.GetById<CardModel>(new ModelId("CARD",c.Id));
                string title=model.Title+(c.Upgrade<=0?"":c.Upgrade==1?"+":"+"+c.Upgrade);
                if(choice.Options!=null && choice.Options.Count(t=>t.Id==c.Id && t.Upgrade==c.Upgrade)>1)
                    title+=$"（同名第 {c.Occurrence+1} 张）";
                return title;
            });
        return choice.Cards.Count==0?"（跳过）":string.Join("、",names);
    }

    private static string FormatSteps(CombatState board,System.Collections.Generic.IReadOnlyList<KernelTeamSearch.Action> route,int? startRound=null,KernelSession? frozen=null,System.Collections.Generic.IReadOnlyDictionary<uint,string>? positions=null)
    {
        var s=new StringBuilder("第 ").Append(startRound??board.RoundNumber).Append(" 回合");
        void AppendChoice(KernelChoice choice)
        {
            int seat=board.Players.ToList().FindIndex(p=>p.NetId==choice.Owner)+1;
            s.Append("〔P").Append(seat).Append(choice.Cards.Count==0?" 跳过选择":" 选 "+ChoiceNames(choice)).Append("〕");
        }
        foreach (var action in route)
        {
            var who = $"P{board!.Players.ToList().FindIndex(p => p.NetId == action.Player.NetId) + 1} {action.Player.Character.Title.GetFormattedText()}";
            var move = action.EndTurn ? "结束回合" : action.Card?.Title ?? action.Potion?.Title.GetFormattedText() ?? "选择";
            s.Append("\n").Append(who).Append("：").Append(move);
            if(action.Potion is {} potion && (frozen==null ? action.Player.Potions.Count(b=>b.Id==potion.Id) : frozen.CapturedPotions(action.Player).Count(b=>b.Id==potion.Id))>1)
            {
                int slot=frozen==null ? action.Player.GetPotionSlotIndex(potion) : CapturedBelt.Slot(frozen,potion);
                if(slot>=0)s.Append("（药水槽 ").Append(slot+1).Append("）");
            }
            if(action.Target?.Player is {} teammate)
                s.Append(" → P").Append(board.Players.ToList().FindIndex(p=>p.NetId==teammate.NetId)+1)
                    .Append(' ').Append(teammate.Character.Title.GetFormattedText());
            else if(action.Target!=null)
                s.Append(" → ").Append(action.Target.CombatId is uint id && positions!=null && positions.TryGetValue(id,out var name)
                    ? name : action.Target.Name.ToString());
            if (action.Choices != null)
                foreach (var c in action.Choices) AppendChoice(c);
            if (action.TurnStartChoices is {} t)
            {
                if (t.ToRound != t.FromRound) s.Append("\n第 ").Append(t.ToRound).Append(" 回合");
                foreach (var choice in t.Choices) AppendChoice(choice);
            }
        }
        return s.ToString();
    }

    private void Report(string message,bool display=true)
    {
        SetButton("求解 (F8)");
        if (caseOutput != null) File.WriteAllText(caseOutput, message);
        if(!display || routeWindow==null)return;
        if(message.StartsWith("模拟找到胜线。",StringComparison.Ordinal) || message.StartsWith("模拟结果：",StringComparison.Ordinal))
        {
            routeWindow.SetRoute(message);routeWindow.SetStatus(message.StartsWith("模拟找到胜线。")?"已找到模拟胜线":"当前最佳完整路线","");
        }
        else routeWindow.SetStatus("求解提示",message);
        SyncWindowControl();
    }
}
