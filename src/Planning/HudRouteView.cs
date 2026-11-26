using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace CoopSolver;

// The persisted and shared route uses the same text as the second-screen view.
// Keep unknown lines verbatim, including menus attached to a round boundary.
internal sealed class HudRouteView
{
    private sealed record Page(int Number,List<(string Text,int Step)> Lines)
    {
        internal Dictionary<int,int>? EndLossByStep;
        internal Dictionary<int,int>? DamageByStep;
        internal HudKill[] Kills=Array.Empty<HudKill>();
    }
    private static readonly Regex RoundHeading=new(@"^第 (\d+) 回合(.*)$");
    private static readonly Regex PlayerMove=new(@"^P(\d+) [^：]+：(.*)$");
    internal static bool TryRound(string row,out int round,out string choices)
    {
        var match=RoundHeading.Match(row);
        choices=match.Groups[2].Value;
        return int.TryParse(match.Groups[1].Value,out round);
    }
    internal static bool TryPlayerMove(string row,out int seat,out string caption)
    {
        var match=PlayerMove.Match(row);
        caption=match.Groups[2].Value;
        if(!int.TryParse(match.Groups[1].Value,out seat))return false;
        seat--;return true;
    }
    private HudSeat[] seats=Array.Empty<HudSeat>();
    private HudPlan? lastPlan;
    private readonly List<Page> pages=new();
    private readonly VBoxContainer rows=new(){Name="HudRows",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
    internal readonly ScrollContainer Scroll=new(){Name="HudSteps",HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,VerticalScrollMode=ScrollContainer.ScrollMode.Reserve,SizeFlagsVertical=Control.SizeFlags.ExpandFill};
    internal readonly Button Round=new(){Name="HudRound",Flat=true,ClipText=true,CustomMinimumSize=new(114,0),FocusMode=Control.FocusModeEnum.None};
    internal readonly RoundOverview Overview;
    internal int SelectedRound=>selected;
    internal int RoundIndex=>pages.FindIndex(p=>p.Number==selected);
    internal int RoundCount=>pages.Count;
    internal event Action<int>? Picked;
    private bool ListMode=>seats.Length<=1 || OwnOnly;
    internal void CloseOverview(){Overview.Panel.Hide();Round.Icon=HudIcons.Down;Round.SetPressedNoSignal(false);Scroll.Visible=ListMode && pages.Count>0;}
    private const int ActionGap=HudTypography.FieldGap;
    private readonly List<(PanelContainer Badge,Control Description,Label Card,string Name,HBoxContainer Target,Label? TargetLabel,string TargetName,float TargetWidth,Label Damage,Label Energy,Control Divider)> visibleActions=new();
    private int selected=-1;
    private int localSeat;
    internal bool OwnOnly {get;private set;}
    private readonly Dictionary<int,string[]> waits=new();
    private int waitRound=-1;
    private ulong[] ready=Array.Empty<ulong>();
    private int readyRound=-1;
    internal readonly VBoxContainer Roster=new(){Name="HudRoster"};
    private bool manual;
    internal int RoundFor(HudPlan? plan,int current)=>plan==null?current:
        manual && selected>=plan.Start && selected<=plan.End?selected:
        Math.Clamp(current,plan.Start,Math.Max(plan.Start,plan.End));
    internal int Count=>rows.GetChildCount();
    internal float ContentHeight=>rows.GetCombinedMinimumSize().Y;

    internal HudRouteView()
    {
        Roster.AddThemeConstantOverride("separation",2);
        rows.AddThemeConstantOverride("separation",0);
        Scroll.AddChild(rows);Scroll.Visible=Round.Visible=false;
        CombatHud.StyleButton(Round);Round.ToggleMode=true;Round.Icon=HudIcons.Down;Round.IconAlignment=HorizontalAlignment.Right;
        Overview=new RoundOverview(number=>{CloseOverview();SelectNumber(number);Picked?.Invoke(number);});
        Round.Pressed+=()=>{
            Overview.Panel.Visible=!Overview.Panel.Visible;
            Round.Icon=Overview.Panel.Visible?HudIcons.Up:HudIcons.Down;Round.SetPressedNoSignal(Overview.Panel.Visible);Scroll.Visible=ListMode && !Overview.Panel.Visible && pages.Count>0;
            if(Overview.Panel.Visible)Overview.Render(pages.Select(p=>p.Number).ToArray(),lastPlan,selected,localSeat);
        };
    }
    internal static Color SeatColor(HudSeat? seat)
    {
        if(seat?.Color is not {} css || !Godot.Color.HtmlIsValid(css))return new("#cbd6df");
        var color=new Color(css);
        // Also normalize already shared and archived Necrobinder seat colors.
        bool necro=css.Equals("766b92",StringComparison.OrdinalIgnoreCase) ||
            css.Equals(MegaCrit.Sts2.Core.Helpers.StsColors.pink.ToHtml(false),StringComparison.OrdinalIgnoreCase);
        var baseColor=necro?new Color("#766b92"):color;
        return Color.FromHsv(baseColor.H,.35f,necro?baseColor.V:.80f);
    }
    internal static StyleBoxFlat SeatBox(HudSeat? seat,int x,int y,bool lethal=false)
    {
        return TintedBox(SeatColor(seat),x,y,lethal);
    }
    internal static StyleBoxFlat TintedBox(Color color,int x,int y,bool lethal=false)
    {
        return HudSurface.Box(new Color(color,.30f).ToHtml(),lethal?"#ffe0a5":"",x,y);
    }
    internal void Reset(){CloseOverview();manual=false;selected=-1;Set(null,0);}
    private IReadOnlyDictionary<uint,string>? targetNames;
    internal void SetTargetNames(IReadOnlyDictionary<uint,string>? names)
    {
        if(Ui.SameTargetNames(targetNames,names))return;
        targetNames=names;
        if(RoundIndex>=0)Show(RoundIndex);
    }

    internal void Set(HudPlan? plan,int current,ulong me=0)
    {
        int seat=Array.FindIndex(plan?.Party??Array.Empty<HudSeat>(),s=>s.Id==me)+1;
        if(ReferenceEquals(plan,lastPlan) && seat==localSeat)return;
        if(lastPlan?.Steps!=plan?.Steps){waits.Clear();waitRound=-1;}
        lastPlan=plan;
        seats=plan?.Party??Array.Empty<HudSeat>();
        localSeat=seat;
        string text=plan?.Steps??"";
        int step=-1;
        pages.Clear();
        foreach(var line in text.Split('\n'))
        {
            if(TryRound(line,out int n,out var choices))
            {
                pages.Add(new(n,new()));
                string extra=choices.Trim();
                if(extra.Length>0)pages[^1].Lines.Add((extra,-1));
            }
            else if(pages.Count!=0 && !string.IsNullOrWhiteSpace(line))pages[^1].Lines.Add((line,PlayerMove.IsMatch(line)?++step:-1));
        }
        foreach(var page in pages)
        {
            var report=plan?.Rounds?.FirstOrDefault(r=>r.Round==page.Number);
            var moves=new List<(int Step,int Seat,bool End)>();
            foreach(var (line,ordinal) in page.Lines)
                if(ordinal>=0 && TryPlayerMove(line,out int actor,out string caption))
                    moves.Add((ordinal,actor,caption.Split('〔')[0]=="结束回合"));
            var positions=moves.ToArray();
            page.DamageByStep=report?.DamageByAction(positions);
            if(plan?.Kills is {} kills && moves.Count>0)
                page.Kills=HudRoundHp.PlaceEndKills(kills.Where(k=>k.Step>=moves[0].Step && k.Step<=moves[^1].Step).ToArray(),seats,positions);
            page.EndLossByStep=report?.LossByAction(positions);
        }
        int index=pages.FindIndex(p=>p.Number==(manual?selected:current));
        if(index<0){manual=false;index=Math.Max(0,pages.FindIndex(p=>p.Number==current));}
        Show(index);
    }
    internal void MoveRound(int delta)
    {
        int index=RoundIndex+delta;
        if(index<0 || index>=pages.Count)return;
        manual=true;Show(index);Picked?.Invoke(selected);
    }
    internal void Filter(bool own)
    {
        OwnOnly=own;
        if(RoundIndex>=0)Show(RoundIndex);
    }
    internal void SelectNumber(int number)
    {
        int i=pages.FindIndex(p=>p.Number==number);
        if(i>=0 && number!=selected){manual=true;Show(i);}
    }
    private bool Other(string text)
    {
        var m=PlayerMove.Match(text);
        return seats.Length>1 && m.Success && int.Parse(m.Groups[1].Value)!=localSeat;
    }
    internal void SetWaits(HudGraph? graph)
    {
        var updated=new Dictionary<int,string[]>();
        if(graph is {Conservative:false} && localSeat!=0)
        {
            var steps=graph.Actions.ToDictionary(a=>a.Step);
            foreach(var group in graph.Dependencies.GroupBy(edge=>edge.After))
            {
                if(!steps.TryGetValue(group.Key,out var target) || target.Seat+1!=localSeat)continue;
                var sources=group.Select(e=>steps.GetValueOrDefault(e.Before)).OfType<HudGraphAction>()
                    .Where(a=>a.Seat!=target.Seat && executed?.Includes(graph.Round,a.Step)!=true && (graph.Round!=readyRound || a.Name!="结束回合" ||
                        (uint)a.Seat>=(uint)graph.Party.Length || !ready.Contains(graph.Party[a.Seat].Id)))
                    .ToArray();
                if(sources.Length>0)updated[group.Key]=WaitFields(sources,lastPlan);
            }
        }
        if(waitRound==(graph?.Round??-1) && waits.Count==updated.Count && waits.All(x=>updated.TryGetValue(x.Key,out var value) && x.Value.SequenceEqual(value)))return;
        waitRound=graph?.Round??-1;waits.Clear();
        foreach(var pair in updated)waits.Add(pair.Key,pair.Value);
        if(OwnOnly && RoundIndex>=0)Show(RoundIndex);
    }

    internal static string[] WaitFields(HudGraphAction[] waits,HudPlan? plan)
    {
        var fields=new List<string>{Ui.T("等","Wait")};
        for(int i=0;i<waits.Length;i++)
        {
            var action=waits[i];
            fields.Add($"P{action.Seat+1}");
            string name=Ui.Route(action.Name.StartsWith("用药 ",StringComparison.Ordinal)?action.Name[3..]:action.Name,plan);
            fields.Add(i+1<waits.Length?name+Ui.T("、",","):name);
        }
        return fields.ToArray();
    }

    private static HBoxContainer TextFields(Color ink,bool small,params string[] fields)
    {
        var row=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
        row.AddThemeConstantOverride("separation",HudTypography.FieldGap);
        foreach(var field in fields)row.AddChild(CombatHud.Caption(field,ink,small));
        return row;
    }

    private HudExecuted? executed;
    internal void SetReadyEnds(int round,ulong[] seats,HudExecuted? progress=null)
    {
        if(round==readyRound && ready.SequenceEqual(seats) && HudExecuted.Same(executed,progress))return;
        readyRound=round;ready=seats;executed=progress;
        if(RoundIndex>=0)Show(RoundIndex);
    }

    private void PlayerTotals()=>RoundOverview.RenderSummary(Roster,selected,lastPlan,localSeat);
    private void Show(int index)
    {
        visibleActions.Clear();
        int oldScroll=Scroll.ScrollVertical;
        foreach(var child in rows.GetChildren()){rows.RemoveChild(child);child.QueueFree();}
        Round.Visible=pages.Count>0;
        Scroll.Visible=ListMode && pages.Count>0 && !Overview.Panel.Visible;
        if(index>=pages.Count){IdealWidth=0;PlayerTotals();return;}

        var page=pages[index];
        Round.Text=Ui.Turn(page.Number);
        bool same=selected==page.Number;
        selected=page.Number;
        PlayerTotals();
        if(Overview.Panel.Visible)Overview.Render(pages.Select(p=>p.Number).ToArray(),lastPlan,selected,localSeat);
        if(!ListMode){IdealWidth=0;return;}
        int firstStep=page.Lines.FirstOrDefault(line=>line.Step>=0).Step;
        foreach(var line in page.Lines)
        {
            if(Other(line.Text) || line.Step>=0 && executed?.Includes(page.Number,line.Step-firstStep)==true)continue;
            if(page.Number==readyRound && TryPlayerMove(line.Text,out int seat,out var caption) &&
                caption.StartsWith("结束回合",StringComparison.Ordinal) && (uint)seat<(uint)seats.Length && ready.Contains(seats[seat].Id))
                continue;
            if(OwnOnly && waitRound==page.Number && line.Step>=0 && waits.TryGetValue(line.Step-firstStep,out var wait))
            {
                var row=TextFields(HudTypography.Damage,true,wait);
                row.Name="HudWait"+line.Step;row.CustomMinimumSize=new(0,HudTypography.RowHeight);
                rows.AddChild(row);
            }
            AddRow(line.Text,line.Step);
        }
        var report=lastPlan?.Rounds?.FirstOrDefault(r=>r.Round==selected);
        if(!OwnOnly && report is {IncludesBlockDamage:true,StepDamage:null,Damage:not null})
        {
            int unlisted=report.Damage.Sum()-(report.CardDamage?.Values.Sum()??0);
            if(unlisted>0)rows.AddChild(TextFields(HudTypography.Damage,false,Ui.T("其他结算","Other effects"),Ui.T($"打{unlisted}",$"{unlisted} dmg")));
        }
        FitActionColumns();
        Scroll.ScrollVertical=same?oldScroll:0;
    }
    private static float TextWidth(Label label,string text)=>text.Length==0?0:
        Mathf.Ceil(label.GetThemeFont("font").GetStringSize(text,fontSize:label.GetThemeFontSize("font_size")).X)+1;

    internal float IdealWidth {get;private set;}
    private float layoutWidth;
    internal void SizeForWidth(float width)
    {
        if(Mathf.IsEqualApprox(layoutWidth,width))return;
        layoutWidth=width;FitActionColumns();
    }
    private void FitActionColumns()
    {
        if(visibleActions.Count==0){IdealWidth=0;return;}
        float badge=visibleActions.Max(c=>c.Badge.GetChild<Label>(0).GetCombinedMinimumSize().X+c.Badge.GetThemeStylebox("panel").GetMinimumSize().X);
        float damage=visibleActions.Max(c=>TextWidth(c.Damage,c.Damage.Text));
        float energy=visibleActions.Max(c=>TextWidth(c.Energy,c.Energy.Text));
        float rightGap=visibleActions.Select(c=>c.Damage.Text.Length>0?c.Damage.Text:c.Energy.Text)
            .Where(text=>text.Length>0).Select(HudTypography.ExtraGapAfterMetricRule).DefaultIfEmpty().Max();
        float numbers=(damage>0?damage+ActionGap:0)+(energy>0?energy+ActionGap:0);
        if(numbers>0)numbers+=1+ActionGap+rightGap;
        var first=visibleActions[0];
        var seatRow=first.Badge.GetParent<HBoxContainer>();
        float margins=seatRow.GetParent<PanelContainer>().GetThemeStylebox("panel").GetMinimumSize().X+seatRow.GetThemeConstant("separation");
        float reserved=margins+badge+numbers+Scroll.GetVScrollBar().GetCombinedMinimumSize().X;
        float room=layoutWidth<=0?float.MaxValue:Math.Max(64,layoutWidth-reserved);
        float natural=0,rendered=0;
        float Gap(string text)=>numbers>0?HudTypography.ExtraGapBeforeMetrics(text):0;
        foreach(var c in visibleActions)
        {
            float card=TextWidth(c.Card,c.Name);
            bool outlined=c.TargetLabel?.GetParent() is PanelContainer;
            float Padding(string text)=>Gap(outlined?"":text);
            float full=card+(c.TargetLabel==null?Gap(c.Name):ActionGap+c.TargetWidth+Padding(c.TargetName));
            natural=Math.Max(natural,full);
            float cardWidth=Math.Min(card,room);
            if(c.TargetLabel!=null && full>room)
                cardWidth=Math.Min(card,Math.Max(room*.42f,room-ActionGap-c.TargetWidth));
            float CardFit(string text)=>TextWidth(c.Card,text)+(c.TargetLabel==null?Gap(text):0);
            c.Card.Text=RouteTextWrap.Fit(c.Name,c.TargetLabel==null?room:cardWidth,CardFit);
            var titleLines=c.Card.Text.Split('\n');
            float used=titleLines.Max(t=>TextWidth(c.Card,t));
            c.Card.CustomMinimumSize=new(used,0);
            if(c.TargetLabel is {} label)
            {
                float trim=c.TargetWidth-TextWidth(label,c.TargetName);
                float fit=Math.Max(1,room-used-ActionGap-trim);
                float TargetFit(string value)=>TextWidth(label,value)+Padding(value);
                label.Text=RouteTextWrap.Fit(c.TargetName,fit,TargetFit);
                var targetLines=label.Text.Split('\n');
                float text=targetLines.Max(t=>TextWidth(label,t));
                label.CustomMinimumSize=new(text,0);
                c.Target.CustomMinimumSize=new(text+trim,0);
                used+=ActionGap+targetLines.Max(TargetFit)+trim;
            }
            else used=titleLines.Max(CardFit);
            rendered=Math.Max(rendered,used);
        }
        IdealWidth=reserved+natural;
        foreach(var c in visibleActions)
        {
            c.Badge.CustomMinimumSize=new(badge,0);
            c.Description.CustomMinimumSize=new(rendered,0);
            c.Damage.CustomMinimumSize=new(damage,0);c.Energy.CustomMinimumSize=new(energy,0);
            c.Divider.CustomMinimumSize=new(1+rightGap,0);
            c.Divider.Visible=c.Damage.Text.Length!=0 || c.Energy.Text.Length!=0;
            c.Target.Visible=c.TargetLabel!=null;c.Damage.Visible=damage>0;c.Energy.Visible=energy>0;
        }
    }
    private static string LossText(int loss)=>loss<0?Ui.T($"回{-loss}血",$"+{-loss} HP"):Ui.T($"扣{loss}血",$"Loss {loss}");
    private void AddRow(string text,int step)
    {
        var match=PlayerMove.Match(text);
        int seat=match.Success?int.Parse(match.Groups[1].Value):0;
        string body=match.Success?match.Groups[2].Value:text;
        int at=body.IndexOf('〔');
        string detail=at>=0?body[at..]:"";
        if(at>=0)body=body[..at];
        string[] move=body.Split(" → ",2);
        if(move.Length==2 && lastPlan?.EnemyTargets?.TryGetValue(step,out uint id)==true)
            move[1]=Ui.EnemyTarget(move[1],id,targetNames);
        var bg=new PanelContainer{CustomMinimumSize=new(0,HudTypography.ActionRowHeight()),MouseFilter=Control.MouseFilterEnum.Pass};
        bg.AddThemeStyleboxOverride("panel",HudTypography.Row(rows.GetChildCount()%2==0));
        rows.AddChild(bg);
        var line=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
        line.AddThemeConstantOverride("separation",5);bg.AddChild(line);
        var who=CombatHud.Caption(seat>0?"P"+seat:"",HudTypography.Ink);
        var badge=new PanelContainer{SizeFlagsVertical=Control.SizeFlags.ShrinkCenter};
        badge.AddThemeStyleboxOverride("panel",seats.Length>1?SeatBox(seats.ElementAtOrDefault(seat-1),6,0):
            HudSurface.Box("#00000000","",6,0));
        badge.AddChild(who);line.AddChild(badge);
        var content=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,MouseFilter=Control.MouseFilterEnum.Pass};
        content.AddThemeConstantOverride("separation",1);line.AddChild(content);
        VBoxContainer? descriptionBlock=null;
        if(move[0].Length!=0)
        {
            var action=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
            action.AddThemeConstantOverride("separation",ActionGap);content.AddChild(action);
            descriptionBlock=new VBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
            descriptionBlock.AddThemeConstantOverride("separation",1);action.AddChild(descriptionBlock);
            var description=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
            description.AddThemeConstantOverride("separation",ActionGap);descriptionBlock.AddChild(description);
            var snapshot=lastPlan?.Rounds?.FirstOrDefault(r=>r.Round==selected);
            string name=move[0];
            if(!name.StartsWith("用药 ") && lastPlan?.Terms?.Any(t=>t.Table=="potions" && (name==t.Source || name.StartsWith(t.Source+"（药水槽 ")))==true)
                name="用药 "+name;
            bool potion=name.StartsWith("用药 ",StringComparison.Ordinal);
            string caption=Ui.Route(potion?name[3..]:name,lastPlan);
            var title=CombatHud.Caption(caption,potion?HudTypography.Damage:
                name=="结束回合"?new("#bdcbd1"):new("#fffcf4"));
            title.SizeFlagsHorizontal=Control.SizeFlags.Fill;
            description.AddChild(title);
            int dealt=pages[RoundIndex].DamageByStep?.GetValueOrDefault(step)??0;
            var damage=CombatHud.Caption(dealt>0?Ui.T($"打{dealt}",$"{dealt} dmg"):"",HudTypography.Damage,small:true);
            var energy=CombatHud.Caption(snapshot?.EnergyAfter?.TryGetValue(step,out int left)==true?Ui.T($"余{left}费",$"{left} EN"):"",new("#bacbd0"),small:true);
            if(pages[RoundIndex].EndLossByStep?.TryGetValue(step,out var loss)==true)
            {
                energy.Text=loss==0?"":LossText(loss);
                energy.AddThemeColorOverride("font_color",loss<0?HudTypography.Heal:HudTypography.Loss);
                energy.MouseFilter=Control.MouseFilterEnum.Pass;
            }
            damage.HorizontalAlignment=energy.HorizontalAlignment=HorizontalAlignment.Left;
            damage.VerticalAlignment=energy.VerticalAlignment=VerticalAlignment.Top;

            var divider=HudTypography.ActionMetricRule();
            divider.Name="HudListMetricRule";
            action.AddChild(divider);action.AddChild(damage);action.AddChild(energy);
            float targetWidth=0;Label? targetLabel=null;
            var destination=new HBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.Fill,Alignment=BoxContainer.AlignmentMode.Begin};
            description.AddChild(destination);
            if(move.Length==2)
            {
                var target=CombatHud.Caption("→ "+Ui.Route(move[1],lastPlan),new("#c5d6dc"));
                targetLabel=target;target.HorizontalAlignment=HorizontalAlignment.Left;
                bool lethalTarget=pages[RoundIndex].Kills.Any(k=>k.Step==step && k.IsTarget);
                if(lethalTarget)
                {
                    destination.AddChild(CombatHud.Caption("→",new("#c5d6dc")));
                    target.Text=Ui.Route(move[1],lastPlan);
                    target.AddThemeColorOverride("font_color",HudTypography.Kill);
                    destination.AddChild(CombatHud.Outline(target,Ui.T("击杀","Kill")));
                }
                else {target.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;destination.AddChild(target);}
                float nameWidth=TextWidth(target,target.Text)+2;
                targetWidth=nameWidth+(lethalTarget?destination.GetChild<Control>(0).GetCombinedMinimumSize().X+
                    destination.GetThemeConstant("separation")+((PanelContainer)target.GetParent()).GetThemeStylebox("panel").GetMinimumSize().X:0);
                if(lethalTarget)target.CustomMinimumSize=new(Math.Min(nameWidth,145),0);
            }
            visibleActions.Add((badge,descriptionBlock,title,title.Text,destination,targetLabel,targetLabel?.Text??"",targetWidth,damage,energy,divider));
        }
        var restores=lastPlan?.Rounds?.FirstOrDefault(r=>r.Round==selected)?.Revives?.GetValueOrDefault(step);
        if(restores!=null)foreach(var e in restores)
        {
            var revivedSeat=Array.FindIndex(lastPlan!.Party,s=>s.Id==e.Seat)+1;
            content.AddChild(TextFields(new("#cfba90"),false,$"P{revivedSeat}",
                Ui.T($"{DeathSaves.Name(e)}复活",$"{DeathSaves.Name(e)} revive"),Ui.T($"回{e.Hp}血",$"+{e.Hp} HP")));
        }
        foreach(var kill in pages[RoundIndex].Kills.Where(k=>k.Step==step && (!k.IsTarget || move.Length==1)))
        {
            var victim=CombatHud.Outline(CombatHud.Caption(Ui.Route(Ui.EnemyTarget(kill.Name,kill.EnemyId,targetNames),lastPlan),new("#ffedbf")),Ui.T("击杀","Kill"));
            victim.SizeFlagsHorizontal=Control.SizeFlags.ShrinkBegin;content.AddChild(victim);
        }
        if(detail.Length==0)return;
        var picks=new List<string>();
        foreach(Match m in Regex.Matches(detail,@"〔([^〕]+)〕"))
        {
            var pickText=m.Groups[1].Value;
            string prefix=$"P{seat} ";
            if(pickText.StartsWith(prefix,StringComparison.Ordinal))pickText=pickText[prefix.Length..];
            picks.Add(pickText);
        }
        var pick=CombatHud.Caption(Ui.Route(string.Join(Ui.T("、",", "),picks),lastPlan),new("#f3d19a"));
        pick.AutowrapMode=TextServer.AutowrapMode.WordSmart;
        if(descriptionBlock==null)content.AddChild(pick);
        else
        {
            var padding=new MarginContainer();
            padding.AddThemeConstantOverride("margin_right",(int)HudTypography.ExtraGapBeforeMetrics(""));
            padding.AddChild(pick);descriptionBlock.AddChild(padding);
        }
    }
}
