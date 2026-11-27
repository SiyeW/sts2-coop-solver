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
        ContinuationHistory.Install();
        DamageAttribution.Install();
        DeathSaves.Install();
        TransformScope.Install();
        GoalResult.Install();
        PotionOpenings.Install();
        RetrievalChoices.Install();
        InterceptMirror.Install();
        var tree=(SceneTree)Engine.GetMainLoop();
        var advisor=new Advisor(tree);
        advisor.liveAdvisor=new Advisor(tree){owner=advisor};
        advisor.share=new RouteShare();advisor.share.Install();
        advisor.InstallNativeMenus();
        tree.Root.TreeExiting+=()=>advisor.SaveLearnedWeights(force:true);
        tree.ProcessFrame+=advisor.Tick;
        GD.Print("[CoopSolver] Ready");
    }
    private RouteWindow? window;
    private Advisor? owner,liveAdvisor;
    private bool resultsHidden,searchEnabled,manuallyPaused;
    private bool showLive=true,switchRequested,followWanted,openingWanted;
    private bool Follow=>owner!=null;
    private bool sharedOpening;
    private Advisor Selected=>showLive && !sharedOpening?liveAdvisor!:this;
    private bool canSolve;
    private bool NativeUi=>DisplayServer.GetName()!="headless";

    private void OpenRoute(bool automatic=false)
    {
        if(owner!=null){owner.OpenRoute(automatic);return;}
        if(!NativeUi)return;
        window??=new RouteWindow(tree,CreateHud);
        bool keepFocus=automatic && tree.Root.HasFocus();
        if(!automatic || !window.IsShown)window.Open();
        if(window.Panel is {} detached && hudFrame!=null && hudRoom!=null && hudNet!=null)
            detached.Update(hudFrame,hudNet.NetId,!FollowingHost,canSolve,hudRoom.RoundNumber,
                hudNet.Type==MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client,
                hudNet.Type==MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Host?broadcastRoutes:null,targetNames);
        if(keepFocus)tree.Root.GrabFocus();
    }

    private int ViewPercent=>showLive?100-SearchCapacity.OpeningPercent:SearchCapacity.OpeningPercent;
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
        try
        {
            Update();SaveLearnedWeights();AdvanceArchivedChoices();
            FlushRouteArchive();liveAdvisor?.FlushRouteArchive();AdvancePlayback();
            if(tickError!=null && Environment.TickCount64-tickErrorAt>2000){tickError=null;nextHud=0;}
        }
        catch (Exception e)
        {
            StopPlayback();search?.Dispose();
            search = null;
            replay = null;
            ClearTree();
            liveAdvisor?.ClearTree();
            var message="当前盘面搜索出错："+e.GetBaseException().Message;
            if(tickError!=message)GD.PrintErr("[CoopSolver] "+e);
            tickError=message;tickErrorAt=Environment.TickCount64;nextHud=0;
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
        if(playing)CloseSettledTurnStart(live!);
        canSolve=playing;
        if(liveAdvisor!=null)liveAdvisor.canSolve=playing;

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
            if(sharedOpening && playing && live!=null && (tapeInputs.Count>0 || Environment.TickCount64>=nextStampCheck))
            {
                nextStampCheck=Environment.TickCount64+300;
                if(tapeInputs.Count>0 || BoardStamp(live)!=stamp)
                {
                    sharedOpening=false;nextHud=0;
                    var side=liveAdvisor!;
                    side.board=live;side.root=root;side.shownLine=shownLine;
                    side.ClearRecommendations();
                    side.previousRecommendations.AddRange(previousRecommendations);
                    side.displayedRounds.AddRange(displayedRounds);
                    side.recommendationsRoot=recommendationsRoot;
                    side.displayedPotion=displayedPotion;side.potionRoot=potionRoot;
                    ClearRecommendations();
                    side.savedResult=savedResult;side.meter=meter;
                    // Force capture even if the visible stamp happened to cycle back.
                    side.stamp="";
                }
            }
            if(playing && exchange!=null && tapeInputs.Count>0)
            {
                RecordInputGroup(SlCache.CaptureGroup(live!,tapeInputs.ToArray()));
            }
            if(live!=null && beforeReady.Count>0)
                foreach(var seat in beforeReady.Where(pair=>pair.Value.Root.TurnNumber!=live.RoundNumber).Select(pair=>pair.Key).ToArray())beforeReady.Remove(seat);
            // Keep the opening and actual moves available for a later local search.
            if(hotkey && (playing || hudRoom!=null || window!=null))OpenRoute();
            if(FollowingHost)
            {
                if(playing && rejectedPosition.HasValue)CaptureStable(live!);
                return;
            }
            if(System.Threading.Volatile.Read(ref restarting)!=0)return;
            bool changedTab=switchRequested;
            switchRequested=false;
            if(UseWorker)
            {
                if(changedTab)nextHud=0;
                if(start)
                {
                    searchEnabled=!searchEnabled;manuallyPaused=!searchEnabled;
                    if(searchEnabled){ClearWorkerError();liveAdvisor!.ClearWorkerError();}
                    pauseReason=null;
                    if(manuallyPaused)foreach(var slot in PendingChoices){slot.Probe?.Cancel();slot.Probe=null;}
                }
                bool wantOpening=searchEnabled && (sharedOpening || SearchCapacity.OpeningPercent>0);
                bool wantLive=searchEnabled && !sharedOpening && SearchCapacity.OpeningPercent<100;
                InteractiveSearch.Dispatcher.Split(wantOpening && root!=null && engine?.IsExhausted!=true && !Memory.Waiting() &&
                     (wantLive && playing && liveAdvisor?.engine?.IsExhausted!=true || SelectingHand && SearchCapacity.OpeningPercent<100));
                openingWanted=wantOpening;
                if(searchEnabled)exchange?.PumpPromotions(meter);
                if(root!=null && exchange?.TakeLine() is {} promoted)RenderLine(promoted with {FoundAt=meter.Actions+exchange.ConnectedWork});
                if(root!=null && ReferenceEquals(board,live))UpdateTree(live,playing,wantOpening!=running);
                wantLive &= searchEnabled;
                var liveSide=liveAdvisor!;
                if(!wantLive && liveSide.running)
                {
                    liveSide.running=false;liveSide.activeTime.Stop();liveSide.engine?.Pump(false);
                }
                liveSide.followWanted=wantLive;
                if(!sharedOpening && (wantLive || showLive || manuallyPaused && SearchCapacity.OpeningPercent<100))liveSide.UpdateFollow(live,playing,false,changedTab);
                else liveSide.DropInactiveBattle(live);
                return;
            }
            if(changedTab)
            {
                nextHud=0;
                if(showLive)liveAdvisor!.followWanted=true;
                else {liveAdvisor!.activeTime.Stop();liveAdvisor.engine?.Pump(false);}
            }
            if(!showLive && start)openingWanted=!running;
            if(showLive && start)liveAdvisor!.followWanted=!liveAdvisor.running;
            if(root!=null && (ReferenceEquals(board,live) || playing))
                UpdateTree(live,playing,!showLive && start || openingWanted && !running && planner==null && engine==null);
            if(showLive)liveAdvisor!.UpdateFollow(live,playing,start,changedTab);
            else liveAdvisor!.DropInactiveBattle(live);
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
        if (!Capture(live!)) return;
        baselinePhase = true;
        search = NewSearch(includePotions:false, budget:40000, reference:null);
    }

    private bool captureDeferred;
    private string[] captureWarnings=Array.Empty<string>();
    private (CombatState Room,uint Action,int Round)? rejectedPosition;
    private string? tickError;
    private long tickErrorAt;

    private void ClearCaptureWarnings()
    {
        rejectedPosition=null;captureWarnings=Array.Empty<string>();
    }
    private KernelSession? CaptureStable(CombatState live)
    {
        var host=owner??this;
        var position=(live,RunManager.Instance.ActionQueueSet.NextActionId,live.RoundNumber);
        if(host.rejectedPosition==position)return null;
        if(host.rejectedPosition.HasValue)host.ClearCaptureWarnings();
        try
        {
            var captured=KernelSession.Capture(live);
            if(captured.ProjectionDifferences.Count==0){host.ClearCaptureWarnings();return captured;}
            host.captureWarnings=captured.ProjectionDifferences.Select(s=>"盘面与模拟不一致："+s).ToArray();
            GD.PrintErr("[CoopSolver] "+string.Join("\n",host.captureWarnings));
        }
        catch(CoopBots.Kernel.Vendor.IncompatibleGameplayModException e)
        {
            // The full patch signature is for the log. Show the affected game method to the player.
            const string mirrored=" on mirrored ";
            int at=e.Subject.IndexOf(mirrored,StringComparison.Ordinal);
            string effect=at<0?e.Subject:e.Subject[(at+mirrored.Length)..];
            if(at>=0)effect=string.Join(".",effect.Split('.').TakeLast(2));
            host.captureWarnings=new[]{"模组规则无法模拟："+e.PlayerFacingModName+" · "+effect};
            GD.PrintErr("[CoopSolver] "+e.Message);
        }
        catch(Exception e) when(e is IndexOutOfRangeException or ArgumentOutOfRangeException ||
            e is InvalidOperationException && e.Message.Contains("Collection was modified",StringComparison.Ordinal))
        {return null;} // Retry after the native transition settles.
        host.rejectedPosition=position;host.nextHud=0;
        return null;
    }

    private bool Capture(CombatState live)
    {

        var captured=CaptureStable(live);
        captureDeferred=captured==null && (owner??this).rejectedPosition==null;
        if(captured==null)return false;
        var players=captured.Party.ToArray();
        var active=players.Where(p=>p.Creature.IsAlive && p.PlayerCombatState?.Phase==PlayerTurnPhase.Play &&
            !CombatManager.Instance.IsPlayerReadyToEndTurn(p)).ToArray();
        if(active.Length==0)return false;
        var capturedStamp=BoardStamp(live);
        caseStarted=true;board=live;participants=players;actors=active;root=captured;
        stamp=capturedStamp;rootRound=live.RoundNumber;
        if (caseOutput != null)
            File.WriteAllText(caseOutput + ".opening.json", JsonSerializer.Serialize(new {
                round = board.RoundNumber, seats = players.Select(p => new { character = p.Character.Id.Entry,
                    hp = p.Creature.CurrentHp, cards = p.PlayerCombatState!.Hand.Cards.Select(c => c.Id.Entry).ToArray() }),
                enemies = board.Enemies.Select(e => new { id = e.Monster?.Id.Entry, hp = e.CurrentHp })
            }));
        playerNames=RunManager.Instance.NetService.Type==NetGameType.Singleplayer?new[]{Ui.Text("ui.001")}:participants.Select(p=>MegaCrit.Sts2.Core.Platform.PlatformUtil.GetPlayerName(RunManager.Instance.NetService.Platform,p.NetId)).ToArray();
        startHp=participants.Select(p=>root.Hp(p.Creature)).ToArray();
        startBottles=participants.Sum(root.HeldPotionCount);
        CaptureLabels(board);
        if (caseOutput != null)
            File.WriteAllText(caseOutput + ".belts.json", JsonSerializer.Serialize(players.Select(p => new {
                owner = p.Character.Id.Entry, bottles = p.Potions.Select(b => new {
                    slot = p.GetPotionSlotIndex(b), id = b.Id.Entry,
                    held = root.UsablePotions(p).Contains(b), modeled = KernelSession.CanModelPotion(b) || ExactPotionChoices.Supports(b),
                    targets = root.PotionTargets(b).Select(t => t?.Player?.Character.Id.Entry ?? t?.Monster?.Id.Entry)
                })
            })));
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

    internal static string ChoiceNames(KernelChoice choice,bool live=false)=>
        choice.Cards.Count==0?"（跳过）":string.Join("、",ChoiceCards(choice,live).Select(card=>card.Text(legacy:true)));

    private static HudCardLabel[] ChoiceCards(KernelChoice choice,bool live=false)
    {
        return choice.Cards.Select(c=>{
            var model=ModelDb.GetById<CardModel>(new ModelId("CARD",c.Id));
            var label=CardPresentation.Capture(model,c.StateKey,CardPresentation.MenuFee(c,choice.Options),c.Upgrade);
            if(choice.Options is {} options)
            {
                var same=Enumerable.Range(0,options.Count).Where(i=>options[i].Id==c.Id && options[i].Upgrade==c.Upgrade &&
                    CardPresentation.Describe(options[i].StateKey,model,CardPresentation.MenuFee(options[i],choice.Options))==CardPresentation.Describe(c.StateKey,model,CardPresentation.MenuFee(c,choice.Options))).ToArray();
                int at=CardPresentation.MenuIndex(choice,c,options);
                var layout=ChoiceLayout.For(choice);
                bool stateDiff=same.Any(i=>!CardPresentation.SameDisplayedVariant(options[i].StateKey,c.StateKey));
                bool positionDiff=layout!=null && choice.Cards.Count<options.Count &&
                    same.Select(i=>layout.Runs[i]).Distinct().Count()>1;
                if(at>=0 && same.Length>1 && (stateDiff || positionDiff))
                {
                    var visible=live && layout!=null?NativeAutoMenu.VisibleOrder(choice,layout):null;
                    var order=(visible??layout?.DisplayOrder(choice)??Enumerable.Range(0,options.Count).ToArray()).Where(same.Contains).ToArray();
                    int ordinal=Array.IndexOf(order,at)+1;
                    if(ordinal>0)label=label with {Copy=ordinal};
                }
            }
            return label;
        }).ToArray();
    }

    private static string FormatSteps(CombatState board,System.Collections.Generic.IReadOnlyList<KernelTeamSearch.Action> route,int? startRound=null,KernelSession? frozen=null,System.Collections.Generic.IReadOnlyDictionary<uint,string>? positions=null)
    {
        var s=new StringBuilder("第 ").Append(startRound??board.RoundNumber).Append(" 回合");
        void AppendChoice(KernelChoice choice)
        {
            int seat=board.Players.ToList().FindIndex(p=>p.NetId==choice.Owner)+1;
            s.Append("〔P").Append(seat).Append(choice.Cards.Count==0?" 跳过选择":" 选 "+ChoiceNames(choice)).Append("〕");
        }
        for(int index=0;index<route.Count;index++)
        {
            var action=route[index];
            var who = $"P{board!.Players.ToList().FindIndex(p => p.NetId == action.Player.NetId) + 1} {action.Player.Character.Title.GetFormattedText()}";
            var move = action.EndTurn ? "结束回合" : action.Potion is {} used?"用药 "+used.Title.GetFormattedText():action.Card?.Title??"选择";
            if(action.Card is {} card)
                move=CardPresentation.PlayTitle(card,action.CardStateKey,CardPresentation.FeeFor(action));
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

    private void Report(string message)
    {
        if(caseOutput!=null)File.WriteAllText(caseOutput,message);
        else GD.Print("[CoopSolver] "+message);
    }
}
