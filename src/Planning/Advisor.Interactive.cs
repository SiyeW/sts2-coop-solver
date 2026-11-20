using System;
using System.Collections.Generic;
using System.Linq;
using CoopBots.Kernel;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace CoopSolver;
internal sealed partial class Advisor
{
    private bool UseWorker=>!legacyMode && caseOutput==null;
    private InteractiveSearch? engine;
    private KernelSession? boundRoot;
    private int generation;
    private InteractiveSearch.Line? shownLine,lastPublication,pendingSuffix,guardLine;
    private int[] startHp=Array.Empty<int>();
    private int startBottles;
    private readonly Dictionary<uint,string> targetNames=new();
    private string liveMessage="",lastTrim="",confirmedTrim="";
    private bool pendingCheckVisible;
    private long nextWorkerUi;
    private bool initialWindowPending;

    private void CaptureLabels(CombatState state)
    {
        targetNames.Clear();
        foreach(var group in state.Enemies.GroupBy(e=>e.Name.ToString()))
        {
            var items=group.Select(e=>(e,node:NCombatRoom.Instance?.GetCreatureNode(e))).ToArray();
            if(items.Length==1){if(items[0].e.CombatId is uint id)targetNames[id]=group.Key;continue;}
            var placed=items.Select(x=>(x.e,point:x.node?.Hitbox.GetGlobalRect().GetCenter())).ToArray();
            bool positioned=placed.All(x=>x.point.HasValue);
            var ordered=placed.OrderBy(x=>x.point?.X).ThenBy(x=>x.point?.Y).ToArray();
            for(int i=0;i<ordered.Length;i++)
            {
                var (e,pt)=ordered[i];
                if(e.CombatId is not uint id)continue;
                if(!positioned){targetNames[id]=group.Key+"（站位待确认）";continue;}
                string side=i+1<=ordered.Length-i?$"左{i+1}":$"右{ordered.Length-i}";
                var col=ordered.Where(x=>Math.Abs(x.point!.Value.X-pt!.Value.X)<20).OrderBy(x=>x.point!.Value.Y).ToArray();
                if(col.Length>1)side+=$"上{Array.FindIndex(col,x=>x.e==e)+1}";
                targetNames[id]=state.Enemies.Count==ordered.Length?side:$"{group.Key}（{side}）";
            }
        }
    }

    private void ReconcileLive(KernelSession fresh,KernelSession? previousRoot)
    {
        var priorHud=(routeWindow?.ShowingGuard==true || (owner??this).hudGuard)?guardHud??savedResult?.Hud:provisionalHud??savedResult?.Hud;
        if((routeWindow?.ShowingGuard==true || (owner??this).hudGuard) && guardLine!=null)shownLine=guardLine;
        routeWindow?.ClearGuard();guardLine=null;guardHud=null;provisionalHud=null;(owner??this).hudGuard=false;
        pendingSuffix=null;lastPublication=null;pendingCheckVisible=false;lastTrim=confirmedTrim="";
        if(shownLine==null){savedResult=null;provisionalHud=priorHud;liveMessage="正在计算当前盘面";return;}
        var actual=fresh.PlanningState(fresh.Party.Select(p=>p.NetId));
        int used=0;
        for(int i=0;i<shownLine.Trail.Length && i+1<shownLine.Steps.Length;i++)
            if(shownLine.Trail[i].Matches(actual)){used=i+1;break;}
        bool exact=used>0;
        if(System.Environment.GetEnvironmentVariable("STS2_UX_TEST") is {} trace)
            System.IO.File.AppendAllText(trace+".sync.txt",$"inputs={completedInputs.Count} trail={shownLine.Trail.Length} first={shownLine.Steps[0].Card?.Id.Entry}"+
                $" actual={string.Join(",",completedInputs.Select(a=>a.Kind+"/"+a.Id+"/"+a.Target))} mismatch={shownLine.Trail.FirstOrDefault()?.FirstMismatch(actual)}\n");
        if(!exact && completedInputs.Count>0 && completedInputs.Count<shownLine.Steps.Length)
        {
            bool matches=true;
            for(int i=0;i<completedInputs.Count;i++)
                if(!completedInputs[i].Matches(shownLine.Steps[i],previousRoot)){matches=false;break;}
            if(matches)
            {
                used=completedInputs.Count;
                if(used<=shownLine.Trail.Length && owner?.seedDirectory is {} directory)
                    RecordDrift(directory,shownLine.Trail[used-1].FirstMismatch(actual));
            }
        }
        if(used>0)
        {
            var rest=shownLine with {Kills=null,Rounds=null,Steps=shownLine.Steps.Skip(used).ToArray(),Trail=shownLine.Trail.Skip(used).ToArray(),Recipes=shownLine.Recipes.Skip(used).ToArray(),FirstHp=null};
            savedResult=null;shownLine=rest;pendingSuffix=rest;pendingCheckVisible=true;
            lastTrim=liveMessage=$"已执行 {used} 步 · "+(exact?"盘面匹配，复核余线":"动作匹配，复核余线");
            RenderLine(rest,provisional:true);return;
        }
        // Test one nearby alternative only when the player actually deviates.
        // No end-turn, forced choice or potion is crossed, and an ambiguous
        // duplicate is never removed by guessing. All remaining steps are replayed.
        if(completedInputs.Count==1 && completedInputs[0].Kind=="card")
        {
            int at=-1;
            for(int i=0;i<shownLine.Steps.Length;i++)
            {
                var step=shownLine.Steps[i];
                if(step.Card==null || step.Choices is {Count:>0} || step.TurnStartChoices is { } t &&
                    (t.ToRound!=t.FromRound || t.Choices.Count!=0))break;
                if(!completedInputs[0].Matches(step,previousRoot))continue;
                if(at>=0){at=-1;break;}
                at=i;
            }
            if(at>0)
            {
                var original=shownLine;
                var keep=Enumerable.Range(0,original.Steps.Length).Where(i=>i!=at).ToArray();
                pendingSuffix=original with {Steps=keep.Select(i=>original.Steps[i]).ToArray(),
                    Recipes=keep.Select(i=>i<original.Recipes.Length?original.Recipes[i]:null).ToArray(),
                    Trail=Array.Empty<KernelPlanningState>(),FirstHp=null,BridgeAfter=at,
                    JoinProof=at<original.Trail.Length?original.Trail[at]:null};
                shownLine=pendingSuffix;provisionalHud=priorHud;savedResult=null;pendingCheckVisible=true;
                lastTrim=liveMessage=$"提前执行第 {at+1} 步 · 正在从现盘试接余线";
                // Keep the previous line marked as reference until the bridge
                // succeeds; its old endpoint is not a prediction for the deviation.
                return;
            }
        }
        shownLine=null;savedResult=null;provisionalHud=priorHud;
        liveMessage="实战偏离原路线 · 下方旧路线仅供对照，正在重算";
    }

    private void RecordDrift(string directory,string? difference)
    {
        if(difference==null)return;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path=System.IO.Path.Combine(directory,"replay-differences.jsonl");
            var info=new System.IO.FileInfo(path);
            if(info.Exists && info.Length>=256*1024)return;
            var item=new {attempt=owner!.attempt,round=rootRound,actionId=MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionQueueSet.NextActionId,
                inputs=completedInputs.Select(a=>new {a.Seat,a.Kind,a.Id,a.Target,a.Slot}),difference};
            System.IO.File.AppendAllText(path,System.Text.Json.JsonSerializer.Serialize(item)+"\n");
        }
        catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException){}
    }

    private void UpdateInteractive(bool start)
    {
        if(root==null)return;
        if(start){running=!running;if(running)activeTime.Start();else activeTime.Stop();}
        if(!running){engine?.Pump(false);if(start)PaintInteractive("手动暂停");return;}
        if(!ReferenceEquals(boundRoot,root))
        {
            boundRoot=root;generation++;lastPublication=null;
            engine??=new InteractiveSearch();
            var first=owner??this;
            bool atOpening=!Follow || capturedWitness!=null && capturedWitness==first.openingWitness;
            engine.Bind(generation,root,actors,Follow,meter,pendingSuffix,atOpening?first.seedDirectory:null,first.attempt,atOpening?first.savedResult?.Seed:null,first.exchange,livePosition);pendingSuffix=null;
            nextWorkerUi=0;
        }
        var update=engine!.Latest;
        if(update?.Generation==generation)
        {
            if(update.Error!=null)
            {
                if(update.Result!=null)RenderLine(update.Result,false);
                running=false;openingWanted=followWanted=false;(owner??this).searchEnabled=false;(owner??this).manuallyPaused=true;activeTime.Stop();engine.Release();engine=null;boundRoot=null;
                routeWindow?.SetStatus("计算已暂停",update.Error);GD.PrintErr("[CoopSolver] "+update.Error);return;
            }
            if(update.SuffixFailed && pendingCheckVisible)
            {
                pendingCheckVisible=false;shownLine=null;
                liveMessage="余线与现盘不符 · 下方仅供对照，正在重算";
            }
            if(update.Guard is {} safe && !ReferenceEquals(safe,guardLine))
            {
                guardLine=safe;RenderLine(safe,false,asGuard:true);
            }
            if(update.Result is {} line && !ReferenceEquals(lastPublication,line))
            {
                lastPublication=line;
                RenderLine(line,false);
            }
            if(update.SuffixVerified && pendingCheckVisible && lastTrim.Length>0)
            {
                pendingCheckVisible=false;
                confirmedTrim=liveMessage=lastTrim.Split('·')[0]+"· 已复算可继续执行的步骤";
                PaintInteractive("正在计算");
            }
        }
        // Don't queue work faster than the render loop can consume. Both
        // modes share one serial worker regardless of UI refresh frequency.
        engine.Pump(true);
        if(start || System.Environment.TickCount64>=nextWorkerUi)
        {
            nextWorkerUi=System.Environment.TickCount64+350;
            PaintInteractive("正在计算");
        }
    }

    private void RenderLine(InteractiveSearch.Line value,bool provisional,bool asGuard=false)
    {
        string label=asGuard?"保血起手 · 首轮结算":provisional?"已执行前缀 · 余线待复算":value.Ending==null?"回合方案 · 胜负未定":value.Ending.Victory?"模拟胜利":"完整路线 · 全队倒下";
        int rounds=Math.Max(0,value.EndTurn-rootRound);
        string health=string.Join("\n",participants.Select((p,i)=>
        {
            int delta=startHp[i]-value.Hp[i];
            string change=delta<0?$"回复 {-delta}":$"损失 {delta}";
            int deltaTurn=value.FirstHp==null?0:startHp[i]-value.FirstHp[i];
            string turn=value.FirstHp==null?"":(deltaTurn>=0?$"本轮净掉血 {deltaTurn}":$"本轮净回复 {-deltaTurn}")+"  ·  ";
            return $"{p.Character.Title.GetFormattedText()}   {turn}至路线末{change}  ·  剩 {value.Hp[i]} 血";
        }));
        string summary=$"{label}  ·  第 {value.EndTurn} 回合（距当前 {rounds} 回合）\n{health}\n"+
            $"用药 {value.Steps.Count(a=>a.Potion!=null)} 瓶 · 余 {value.Bottles} 瓶   |   {value.Steps.Length} 步"+
            $"   |   发现时模拟 {Compact(value.FoundAt)} 次";
        if(value.Ending?.Victory!=true)summary+=$"\n剩 {value.EnemyCount} 名敌人，共 {value.EnemyHp} 血";
        string route=label+"\n"+FormatSteps(board!,value.Steps,rootRound,root,targetNames);
        var model=MakeHud(value,route,summary);
        SeedRoute? seed=null;
        if(!asGuard && !provisional)
            seed=new(value.Steps.Select((a,i)=>SavedStep.From(root!,a,i<value.Recipes.Length?value.Recipes[i]:null)).ToArray(),
                value.EndTurn,value.Hp,value.EnemyCount,value.EnemyHp,value.Bottles,value.Ending!=null,value.Ending?.Victory==true);
        var record=new OpeningResult(value.Ending,value.Steps.Length,value.Score,route,summary,value.Risks,model,seed);
        if(asGuard){guardHud=model;routeWindow?.SetGuard(route,summary,model);return;}
        if(provisional){provisionalHud=model;routeWindow?.SetRoute(route,summary,model);SetWindowRisks(value.Risks);return;}
        if(!record.Improves(savedResult))
        {
            if(shownLine==null && savedResult?.Seed!=null && seed!=null &&
                savedResult.Seed.Steps.Length==seed.Steps.Length && record.Route==savedResult.Route)shownLine=value;
            return;
        }
        provisionalHud=null;shownLine=value;(owner??this).nextHud=0;liveMessage=Follow?"当前盘面已复算":"";
        Publish(record);
    }

    private OpeningResult RefreshArchivedDisplay(OpeningResult saved)
    {
        var route=System.Text.RegularExpressions.Regex.Replace(saved.Route,@"(?<name>[^\s→]+) #(?<id>\d+)",m=>
            uint.TryParse(m.Groups["id"].Value,out var id) && targetNames.TryGetValue(id,out var label) ? label : m.Groups["name"].Value+"（历史站位未记录）");
        var groups=board!.Enemies.GroupBy(e=>e.Name.ToString()).ToDictionary(g=>g.Key,g=>g.Count());
        string Shorten(string text)=>System.Text.RegularExpressions.Regex.Replace(text,
            @"([^\s（→]+)（(?:同列从上第 (\d+) 只 · )?同名左起第 (\d+) 只，起点 \d+ 血）",m=>{
            int n=int.Parse(m.Groups[3].Value);
            if(!groups.TryGetValue(m.Groups[1].Value,out int count) || n>count)return m.Groups[1].Value+$"（左{n}）";
            string side=n<=count-n+1?$"左{n}":$"右{count-n+1}";
            if(m.Groups[2].Success)side+="上"+m.Groups[2].Value;
            return groups.Count==1?side:m.Groups[1].Value+"（"+side+"）";
        });
        route=Shorten(route);
        if(saved.Hud is {} historical)saved=saved with {Hud=historical with {Steps=Shorten(historical.Steps),
            Kills=historical.Kills?.Select(k=>k with {Name=Shorten(k.Name)}).ToArray()}};
        if(saved.Hud==null)
        {
            var hp=saved.Ending?.PostCombatHp;
            var seats=participants.Select((p,i)=>new HudSeat(p.NetId,playerNames[i],p.Character.Title.GetFormattedText(),null,
                hp?.Count==participants.Length?startHp[i]-hp[i]:null,false,hp?.Count==participants.Length && hp[i]<=0,null)).ToArray();
            int turn=System.Text.RegularExpressions.Regex.Matches(route,@"(?m)^第 (\d+) 回合").Select(m=>int.Parse(m.Groups[1].Value)).DefaultIfEmpty(rootRound).Last();
            saved=saved with {Hud=new HudPlan(rootRound,turn,seats,route,saved.Summary,string.Join("\n",saved.Risks))};
        }
        if(saved.Hud is {} oldHud && oldHud.Party.Length==participants.Length && oldHud.Party.Where((s,i)=>s.Color!=CharacterInk(participants[i].Character)).Any())
        {
            var party=oldHud.Party.Select((s,i)=>s with {Color=CharacterInk(participants[i].Character),
                Hp=s.Hp??(s.ToEnd is int loss?startHp[i]-loss:null)}).ToArray();
            saved=saved with {Hud=oldHud with {Party=party}};
        }
        if(saved.Ending is not {} end || saved.Summary.Contains("发现时"))return saved with {Route=route};
        var health=participants.Select((p,i)=>$"{p.Character.Title.GetFormattedText()}  损失 {startHp[i]-end.PostCombatHp[i]} · 剩 {end.PostCombatHp[i]} 血");
        int last=System.Text.RegularExpressions.Regex.Matches(route,@"(?m)^第 (\d+) 回合").Select(m=>int.Parse(m.Groups[1].Value)).DefaultIfEmpty(rootRound).Last();
        string summary=(end.Victory?"模拟胜利":"完整路线 · 全队倒下")+$" · 第 {last} 回合（距当前 {last-rootRound} 回合）\n"+string.Join("\n",health)+"\n"+
            (saved.Summary.Split('\n').LastOrDefault(x=>x.Contains("用药"))??$"药水净消耗 {end.ResourcesConsumed}")+$" · {saved.Steps} 步 · 发现时搜索量未记录";
        if(!end.Victory)summary+=$"\n剩 {end.RemainingEnemies} 名敌人，共 {end.RemainingEnemyHp} 血";
        return saved with {Route=route,Summary=summary};
    }

    internal static string Compact(long value)=>value switch
    {
        >=1_000_000_000=>(value/1_000_000_000d).ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+"B",
        >=1_000_000=>(value/1_000_000d).ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+"M",
        >=1_000=>(value/1_000d).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"k",
        _=>value.ToString()
    };

    private void PaintInteractive(string title)
    {
        if(routeWindow==null)return;
        var s=engine?.Latest;
        var stats=s?.Generation==generation?s.Metrics:null;
        if(running && savedResult?.Ending?.Victory==true)title="正在优化胜线";
        if(Follow && liveMessage.StartsWith("实战偏离") && savedResult==null)title="正在重算 · 上一盘面路线";
        var time=activeTime.Elapsed;
        routeWindow.SetStatus(title,$"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}   ·   累计模拟 {Compact(meter.Actions)} 次\n"+
            (Follow?$"{liveMessage}"+(stats?.Recovering==true?" · 正在接续搜索":stats?.ReusedNodes>0 || stats?.ReusedJobs>0?" · 已续用缓存":""):"从战斗开局计算"));
        SyncWindowControl();
    }
}
