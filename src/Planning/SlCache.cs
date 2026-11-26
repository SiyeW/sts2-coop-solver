using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoopBots.Kernel;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace CoopSolver;

internal sealed record SavedStep(ulong Seat,string Id,int Slot,string Key,int Occurrence,uint? Target,
    KernelChoice[]? Choices,int[]? Ordinals,KernelTurnStartChoiceContract? Turn,TreeStep? Recipe)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Fee {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ChoiceLayout?[]? ChoiceLayouts {get;init;}
    internal IEnumerable<KernelChoice> Menus=>(Choices??Array.Empty<KernelChoice>()).Concat(Turn?.Choices??Array.Empty<KernelChoice>());
    internal static ChoiceLayout?[]? Layouts(IEnumerable<KernelChoice> choices)
    {
        var layouts=choices.Select(ChoiceLayout.For).ToArray();
        return layouts.Any(l=>l!=null)?layouts:null;
    }
    internal void RestoreLayouts()
    {
        if(ChoiceLayouts!=null)foreach(var pair in Menus.Zip(ChoiceLayouts))ChoiceLayout.Attach(pair.First,pair.Second);
    }
    internal static SavedStep From(KernelSession origin,KernelTeamSearch.Action a,TreeStep? recipe)=>new(a.Player.NetId,
        a.EndTurn?"end":a.Card?.Id.Entry??a.Potion!.Id.Entry,a.Potion==null?-1:CapturedBelt.Slot(origin,a.Potion),
        a.CardStateKey,a.CardStateOccurrence,a.Target?.CombatId,a.Choices?.ToArray(),a.ChoiceOrdinals?.ToArray(),a.TurnStartChoices,recipe)
        {Fee=a.Card==null?null:CardPresentation.FeeFor(a),ChoiceLayouts=Layouts((a.Choices??Array.Empty<KernelChoice>()).Concat(a.TurnStartChoices?.Choices??Array.Empty<KernelChoice>()))};
    internal KernelTeamSearch.Action? Bind(KernelSession state)
    {
        RestoreLayouts();
        var p=state.Party.FirstOrDefault(p=>p.NetId==Seat);
        if(p==null)return null;
        if(Id=="end")return new(p,null,null,EndTurn:true){TurnStartChoices=Turn};
        var card=Slot<0?state.Hand(p).Where(c=>c.Id.Entry==Id && state.CardStateKey(c)==Key).Skip(Occurrence).FirstOrDefault():null;
        var potion=Slot>=0?CapturedBelt.At(state,p,Slot):null;
        if(card==null && (potion==null || potion.Id.Entry!=Id))return null;
        var targets=card!=null?state.Targets(card):state.PotionTargets(potion!);
        var target=targets.FirstOrDefault(t=>t?.CombatId==Target);
        if(Target!=null && target==null)return null;
        return new(p,card,target,potion,Choices,false,Ordinals,Key,Occurrence){TurnStartChoices=Turn};
    }
}
internal sealed record SeedRoute(SavedStep[] Steps,int Turn,int[] Hp,int EnemyCount,int EnemyHp,int Bottles,bool Terminal,bool Won)
{
    internal static SeedRoute FromLine(KernelSession root,InteractiveSearch.Line line)=>new(
        line.Steps.Select((a,i)=>SavedStep.From(root,a,i<line.Recipes.Length?line.Recipes[i]:null)).ToArray(),
        line.EndTurn,line.Hp,line.EnemyCount,line.EnemyHp,line.Bottles,line.Ending!=null,line.Ending?.Victory==true);
}
internal sealed record CardCopy(string Key,int Occurrence);
internal sealed record TapeAction(ulong Seat,string Kind,string Id,int Slot,uint? Target,CardCopy? Copy=null,int ReadyRound=0);
internal sealed record TapeGroup(TapeAction[] Actions,string Witness)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? ObservedBoard {get;init;}
}
internal static class SlCache
{
    internal static string FileId(SeedRoute route)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(route)));
    private static string Digest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string BoardText(CombatState live)=>KernelSession.CaptureLivePartyText(live)+"|"+
        string.Join(",",live.Players.Select(CombatManager.Instance.IsPlayerReadyToEndTurn));
    internal static string Witness(CombatState live)=>Digest(BoardText(live));
    internal static TapeGroup CaptureGroup(CombatState live,TapeAction[] actions)
    {
        string text=BoardText(live);
        return new(actions,Digest(text)){ObservedBoard=text};
    }
    internal static string Difference(TapeGroup group,KernelSession state,string hash)
    {
        if(group.ObservedBoard is not {} expected)return "replayed board "+hash;
        string actual=state.PartyStateText()+"|"+string.Join(",",state.Party.Select(state.IsReady));
        int i=0;
        while(i<expected.Length && i<actual.Length && expected[i]==actual[i])i++;
        int from=Math.Max(0,i-42);
        string Near(string value)=>value.Substring(from,Math.Min(100,value.Length-from));
        return $"board differs at {i}: expected [{Near(expected)}], replay [{Near(actual)}]";
    }
    internal static string Witness(KernelSession state)=>Digest(state.PartyStateText()+"|"+string.Join(",",state.Party.Select(state.IsReady)));
    internal static void SaveSeed(string dir,SeedRoute seed)
    {
        Directory.CreateDirectory(dir);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(seed);
        var path=Path.Combine(dir,Convert.ToHexString(SHA256.HashData(bytes))+".route");
        if(File.Exists(path))return;
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal static void Append(string dir,string attempt,TapeGroup group)
    {
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir,attempt+".human"),JsonSerializer.Serialize(group)+"\n");
    }
}

internal sealed partial class AnytimeSearch
{
    internal int LoadedSeeds {get;private set;}
    internal int JoinedContinuations {get;private set;}
    internal int LoadedHumanSteps {get;private set;}
    internal int LoadedSeedSteps {get;private set;}
    internal int RestoreBoundaries {get;private set;}
    private IEnumerator<bool>? seedReader;
    internal void LoadSeeds(string? directory,string activeAttempt,SeedRoute? incumbent)
        =>seedReader=ReadSeeds(directory,activeAttempt,incumbent).GetEnumerator();

    private IEnumerable<bool> ReadSeeds(string? dir,string activeAttempt,SeedRoute? incumbent)
    {
        var routes=Directory.Exists(dir)?Directory.GetFiles(dir,"*.route").OrderByDescending(File.GetLastWriteTimeUtc).ToArray():Array.Empty<string>();
        var oldId=incumbent==null?null:SlCache.FileId(incumbent);
        if(incumbent!=null)foreach(var step in ReplaySeed(incumbent,root,top))yield return step;
        if(!Directory.Exists(dir))yield break;
        foreach(var step in ReplayContinuation(dir,SlCache.Witness(root),root,top))yield return step;
        var tapes=Directory.GetFiles(dir,"*.human").Where(p=>System.IO.Path.GetFileNameWithoutExtension(p)!=activeAttempt)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        foreach(var tape in tapes)
            foreach(var step in ReplayHuman(tape))yield return step;
        foreach(var path in routes)
        {
            if(System.IO.Path.GetFileNameWithoutExtension(path)==oldId)continue;
            SeedRoute? seed=null;
            try{seed=JsonSerializer.Deserialize<SeedRoute>(File.ReadAllBytes(path));}
            catch(Exception e) when(e is JsonException or IOException){RestoreBoundaries++;}
            if(seed==null)continue;
            foreach(var step in ReplaySeed(seed,root,top))yield return step;
        }
    }
    internal IEnumerable<bool> RestoreChoice(string directory,TreeStep input,KernelSession after)
    {
        var node=Descend(top,new[]{new Edge(input,null)});
        return ReplayContinuation(directory,SlCache.Witness(after),after,node);
    }
    private IEnumerable<bool> ReplayContinuation(string directory,string witness,KernelSession start,Node parent)
    {
        var file=System.IO.Path.Combine(directory,witness+".continuation");
        if(!File.Exists(file))yield break;
        OpeningResult? saved;
        try{saved=JsonSerializer.Deserialize<OpeningResult>(File.ReadAllBytes(file));}
        catch(Exception e) when(e is JsonException or IOException){RestoreBoundaries++;yield break;}
        if(saved?.Seed==null)yield break;
        int before=LoadedSeeds;
        foreach(var tick in ReplaySeed(saved.Seed,start,parent))yield return tick;
        if(parent!=top && before!=LoadedSeeds)JoinedContinuations++;
    }
    internal IEnumerable<bool> ReplaySeed(SeedRoute seed,KernelSession start,Node parent,Action? accepted=null,bool fromOpening=false)
    {
        if(seed.Steps==null || seed.Hp==null){RestoreBoundaries++;yield break;}
        var state=start.ForkForReplay();var edges=new List<Edge>();
        foreach(var step in seed.Steps)
        {
            if(step==null){RestoreBoundaries++;yield break;}
            if(state.HasTerminal){if(fromOpening)break;RestoreBoundaries++;yield break;}
            var recipe=step.Recipe;
            if(recipe!=null && fromOpening)recipe=ActionFrontier.RebindEquivalent(state,recipe,step.Occurrence);
            var portable=step;
            var action=recipe==null?portable.Bind(state):null;
            if(recipe==null && action==null && fromOpening && step.Occurrence>0 && step.Slot<0)
            {
                portable=step with {Occurrence=0};
                action=portable.Bind(state);
            }
            if(recipe==null && action==null){RestoreBoundaries++;yield break;}
            var child=recipe==null?state.ForkForReplay():state;
            if(!ActionFrontier.ReplayRecorded(state,ref child,action,ref recipe,out _,out _,recoverKey:true))
            {RestoreBoundaries++;yield break;}
            state=child;
            edges.Add(new Edge(recipe,recipe==null?action:null,recipe==null?portable:null));yield return false;
        }
        var enemy=state.RemainingOpposition();
        if(!fromOpening && (state.HasTerminal!=seed.Terminal || state.HasWon!=seed.Won || state.TurnNumber!=seed.Turn ||
            enemy!=(seed.EnemyCount,seed.EnemyHp) || !seed.Hp.SequenceEqual(party.Select(p=>state.Hp(p.Creature))) ||
            party.Sum(state.HeldPotionCount)!=seed.Bottles)){RestoreBoundaries++;yield break;}
        if(accepted==null){LoadedSeeds++;LoadedSeedSteps+=edges.Count;}
        else accepted();
        InstallSeed(parent,edges,state,fromOpening?"开局续线":accepted==null?"SL-复算路线":"本次实战续线");
    }

    private Node InstallSeed(Node parent,IEnumerable<Edge> edges,KernelSession state,string source)
    {
        double value=Estimate(state);
        foreach(var edge in edges)
        {
            var child=FindChild(parent,edge);
            if(child==null){child=new Node(parent,edge){Visits=1,Sum=value};parent.Children.Add(child);Nodes++;}
            parent=child;
        }
        MaxDepth=Math.Max(MaxDepth,parent.Depth);
        if(state.HasTerminal)Submit(new(Path(parent),Array.Empty<KernelTeamSearch.Action>(),WithInventory(TerminalRecord.Capture(state,party,true,""),state),source),parent);
        else if(parent!=top)
        {
            // A guaranteed turn at the replayed frontier, independent of its
            // first-round heuristic score. The root search continues alongside.
            queuedSeedNodes.Enqueue(parent);
        }
        return parent;
    }
    internal void ReuseVerifiedLine(IReadOnlyList<KernelTeamSearch.Action> steps,IReadOnlyList<TreeStep?> recipes,KernelSession state)
    {
        InstallSeed(top,steps.Select((a,i)=>new Edge(recipes[i],recipes[i]==null?a:null)),state,"实战续线");
    }
    private readonly Queue<Node> queuedSeedNodes=new();
    private Node? humanReplayPosition;
    private IEnumerable<bool> ReplayHuman(string file)
    {
        var state=root.ForkForReplay();var node=top;
        humanReplayPosition=node;
        try
        {
            foreach(string row in File.ReadLines(file))
            {
                TapeGroup? group;
                try{group=JsonSerializer.Deserialize<TapeGroup>(row);}
                catch(JsonException){RestoreBoundaries++;yield break;}
                if(group?.Actions is not {Length:>0})yield break;
                KernelSession? recovered=null;
                foreach(var tick in MatchGroup(state,group,(s,edges)=>{
                    recovered=s;humanReplayPosition=node=InstallSeed(node,edges,s,"SL-真人路径");LoadedHumanSteps+=edges.Count;}))yield return tick;
                if(recovered==null){RestoreBoundaries++;yield break;}
                state=recovered;
                foreach(var tick in ReplayContinuation(System.IO.Path.GetDirectoryName(file)!,group.Witness,state,node))yield return tick;
            }
        }
        finally{humanReplayPosition=null;}
    }
    private IEnumerable<bool> MatchGroup(KernelSession state,TapeGroup group,Action<KernelSession,List<Edge>> accept,Action<string>? failure=null)
    {
        KernelSession? matched=null;var chosen=new List<Edge>();
        int farthest=-1;string reason="no matching legal action";
        IEnumerable<bool> Find(KernelSession start,int index)
        {
            if(index==group.Actions.Length)
            {
                var witness=SlCache.Witness(start);
                if(witness==group.Witness)matched=start;
                else if(failure!=null && farthest<index){farthest=index;reason=SlCache.Difference(group,start,witness);}
                yield return false;yield break;
            }
            var want=group.Actions[index];
            if(failure!=null && index>farthest){farthest=index;reason=$"no legal match at action {index+1} ({want.Kind}/{want.Id}, target={want.Target}, slot={want.Slot})";}
            foreach(var family in new ActionFrontier(start,Boundary).Families)
            {
                var move=family.Move;var p=party[move.Seat];
                if(p.NetId!=want.Seat || move.Target!=want.Target)continue;
                bool ok=move.Kind switch {
                    MoveKind.Card=>want.Kind=="card" && start.Hand(p)[move.Slot].Id.Entry==want.Id,
                    MoveKind.Potion=>want.Kind=="potion" && move.Slot==want.Slot,
                    _=>want.Kind=="end"};
                if(!ok)continue;
                if(move.Kind==MoveKind.Card && want.Copy is {} copy && move.Key!=copy.Key)continue;
                while(!family.Finished)
                {
                    var next=start.ForkForReplay();
                    if(family.Expand(next,out var step,out _,out _))
                    {
                        chosen.Add(new(step,null));
                        foreach(var tick in Find(next,index+1))yield return tick;
                        if(matched!=null)yield break;
                        chosen.RemoveAt(chosen.Count-1);
                    }
                    yield return false;
                }
            }
        }
        foreach(var tick in Find(state,0))yield return tick;
        if(matched!=null)accept(matched,chosen);else failure?.Invoke(reason);
    }
}
