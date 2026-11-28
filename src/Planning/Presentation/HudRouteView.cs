using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace CoopSolver;

internal sealed class HudRouteView
{
    private sealed record Line(int Step,int Seat=-1,bool End=false,string LegacyBody="",HudMove? Move=null,HudPick[]? Picks=null);
    private sealed record Page(int Number,List<Line> Lines)
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
    private HudPotionMenu? potionMenu;
    private HudRouteProjection.Prompt[]? graphPrompts;
    private ulong potionViewer;
    private readonly List<Page> pages=new();
    private readonly VBoxContainer rows=new(){Name="HudRows",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
    internal readonly ScrollContainer Scroll=new(){Name="HudSteps",HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,VerticalScrollMode=ScrollContainer.ScrollMode.Reserve,SizeFlagsVertical=Control.SizeFlags.ExpandFill};
    internal readonly Button Round=new(){Name="HudRound",Flat=true,ClipText=true,CustomMinimumSize=new(114,0),FocusMode=Control.FocusModeEnum.None};
    internal readonly RoundOverview Overview;
    internal int SelectedRound=>selected;
    internal int RoundIndex=>pages.FindIndex(p=>p.Number==selected);
    internal int RoundCount=>pages.Count;
    internal event Action<int>? Picked;
    private bool originalOrderOnly;
    private bool ListMode=>seats.Length<=1 || OwnOnly || originalOrderOnly;
    internal void ShowOriginalOrder(bool enabled)
    {
        if(originalOrderOnly==enabled)return;
        originalOrderOnly=enabled;
        if(RoundIndex>=0)Show(RoundIndex);
    }
    internal void CloseOverview(){Overview.Panel.Hide();Round.Icon=HudIcons.Down;Round.SetPressedNoSignal(false);Scroll.Visible=ListMode && pages.Count>0;}
    private const int ActionGap=HudTypography.FieldGap;
    private const int SeatBadgeInset=6;
    private sealed record Detail(Control Control,Label? Label=null,string Text="",float Outline=0);
    private sealed record ActionColumns(PanelContainer Badge,VBoxContainer Description,Label Card,string Name,
        HBoxContainer Target,Label? TargetLabel,HudTargetLabel? TargetModel,string TargetName,float TargetWidth,Label Damage,Label Energy,Control Divider)
    {
        internal readonly List<Detail> Details=new();
    }
    private readonly List<ActionColumns> visibleActions=new();
    private int selected=-1;
    private int localSeat;
    internal bool OwnOnly {get;private set;}
    private readonly Dictionary<int,string[]> waits=new();
    private Dictionary<int,string?> graphDamage=new();
    private readonly Dictionary<int,string?[]> roundDamage=new();
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
            if(Overview.Panel.Visible)RefreshOverview();
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
        if(ReferenceEquals(plan,lastPlan) && seat==localSeat)
        {
            // Playback can cross a round boundary before a new PV is published.
            if(!manual && current!=selected)
            {
                int follow=pages.FindIndex(page=>page.Number==current);
                if(follow>=0)Show(follow);
            }
            return;
        }
        if(lastPlan?.Steps!=plan?.Steps || !ReferenceEquals(lastPlan?.Moves,plan?.Moves))
        {waits.Clear();graphDamage.Clear();graphPrompts=null;waitRound=-1;}
        if(!ReferenceEquals(lastPlan,plan))roundDamage.Clear();
        lastPlan=plan;
        seats=plan?.Party??Array.Empty<HudSeat>();
        localSeat=seat;
        pages.Clear();
        if(plan?.Moves is {} recorded)
        {
            pages.Add(new(plan.Start,new()));
            for(int step=0;step<recorded.Length;step++)
            {
                var action=recorded[step];
                pages[^1].Lines.Add(new(step,action.Seat,action.EndTurn,Move:action));
                if(action.NextRound is not int round)continue;
                pages.Add(new(round,new()));
                if(action.NextPicks is {Length:>0} choices)pages[^1].Lines.Add(new(-1,Picks:choices));
            }
        }
        else ReadLegacyPages(plan?.Steps??"");
        foreach(var page in pages)
        {
            var report=plan?.Rounds?.FirstOrDefault(row=>row.Round==page.Number);
            var positions=page.Lines.Where(line=>line.Step>=0).Select(line=>(line.Step,line.Seat,line.End)).ToArray();
            page.DamageByStep=report?.DamageByAction(positions);
            if(plan?.Kills is {} kills && positions.Length>0)
                page.Kills=HudRoundHp.PlaceEndKills(kills.Where(k=>k.Step>=positions[0].Step && k.Step<=positions[^1].Step).ToArray(),seats,positions);
            page.EndLossByStep=report?.LossByAction(positions);
        }
        int index=pages.FindIndex(p=>p.Number==(manual?selected:current));
        if(index<0){manual=false;index=Math.Max(0,pages.FindIndex(p=>p.Number==current));}
        Show(index);
    }

    private void ReadLegacyPages(string steps)
    {
        int step=-1;
        foreach(string row in steps.Split('\n'))
        {
            if(TryRound(row,out int number,out var choices))
            {
                pages.Add(new(number,new()));
                if(choices.Trim() is {Length:>0} extra)pages[^1].Lines.Add(new(-1,LegacyBody:extra));
            }
            else if(pages.Count!=0 && !string.IsNullOrWhiteSpace(row))
            {
                var line=TryPlayerMove(row,out int seat,out string caption)?
                    new Line(++step,seat,caption.Split('〔')[0]=="结束回合",caption):
                    new Line(-1,LegacyBody:row);
                pages[^1].Lines.Add(line);
            }
        }
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
    internal void SetWaits(HudGraph? graph)
    {
        var updated=new Dictionary<int,string[]>();
        var damage=new Dictionary<int,string?>();
        HudRouteProjection.Prompt[]? prompts=null;
        bool totalsChanged=false;
        if(graph is {Conservative:false} && localSeat!=0)
        {
            var totals=graph.RoundDamageValues?.Select(values=>values.Text()).ToArray();
            if(totals==null)totalsChanged=roundDamage.Remove(graph.Round);
            else if(!roundDamage.TryGetValue(graph.Round,out var previous) || !previous.SequenceEqual(totals))
            {
                roundDamage[graph.Round]=totals;
                totalsChanged=true;
            }
            var route=HudRouteProjection.Create(graph,lastPlan,executed,readyRound,ready,potionMenu);
            prompts=route.Prompts;
            foreach(var action in route.Actions)
            {
                if(action.Seat+1==localSeat)damage.Add(action.Step,action.DamageText());
                if(action.Seat+1==localSeat && route.Waits.Contains(action.Step))
                    updated[action.Step]=HudRouteProjection.WaitFields(route.Waits[action.Step]);
            }
        }
        if(!totalsChanged && (graphPrompts??Array.Empty<HudRouteProjection.Prompt>()).SequenceEqual(prompts??Array.Empty<HudRouteProjection.Prompt>()) &&
            waitRound==(graph?.Round??-1) && waits.Count==updated.Count &&
            waits.All(x=>updated.TryGetValue(x.Key,out var value) && x.Value.SequenceEqual(value)) &&
            graphDamage.Count==damage.Count && graphDamage.All(x=>damage.TryGetValue(x.Key,out var value) && x.Value==value))return;
        waitRound=graph?.Round??-1;waits.Clear();
        graphDamage=damage;graphPrompts=prompts;
        foreach(var pair in updated)waits.Add(pair.Key,pair.Value);
        if(OwnOnly && RoundIndex>=0)Show(RoundIndex);
        else if(totalsChanged)
        {
            PlayerTotals();
            if(Overview.Panel.Visible)RefreshOverview();
        }
    }

    internal static string[] WaitFields(HudGraphAction[] waits,HudPlan? plan)
        =>HudRouteProjection.WaitFields(waits.Select(action=>new HudRouteProjection.Source(action.Step,action.Seat,action.Caption(plan))));

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

    internal void SetPotionMenu(HudPotionMenu? menu,ulong viewer)
    {
        if(potionViewer==viewer && HudPotionMenu.Same(potionMenu,menu))return;
        potionMenu=menu;potionViewer=viewer;
        Show(Math.Max(0,RoundIndex));
    }

    private void PlayerTotals()=>RoundOverview.RenderSummary(Roster,selected,lastPlan,localSeat,
        ListMode?HudTypography.RowInset+SeatBadgeInset:HudDependencyView.PlayerTitleInset,roundDamage.GetValueOrDefault(selected));
    private void RefreshOverview()=>Overview.Render(pages.Select(p=>p.Number).ToArray(),lastPlan,selected,localSeat,roundDamage);
    private void Show(int index)
    {
        visibleActions.Clear();
        int oldScroll=Scroll.ScrollVertical;
        foreach(var child in rows.GetChildren()){rows.RemoveChild(child);child.QueueFree();}
        Round.Visible=pages.Count>0;
        Scroll.Visible=ListMode && pages.Count>0 && !Overview.Panel.Visible;
        var menu=potionMenu;
        if(ListMode && menu!=null && (index>=pages.Count || pages[index].Number==menu.Round))
        {
            var hints=waitRound==menu.Round && graphPrompts!=null?graphPrompts:
                menu.Prompts.Select(prompt=>new HudRouteProjection.Prompt(null,
                    Array.FindIndex(menu.Party,player=>player.Id==prompt.Chooser),prompt.Text()??"")).ToArray();
            foreach(var hint in hints)
            {
                if(hint.Text.Length==0 || (uint)hint.Seat>=(uint)menu.Party.Length ||
                    OwnOnly && menu.Party[hint.Seat].Id!=potionViewer)continue;
                string text=hint.Text;
                int seat=hint.Seat+1;
                if(menu.Party.Length>1 && !OwnOnly)text=$"P{seat} "+text;
                var box=new PanelContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
                box.AddThemeStyleboxOverride("panel",HudTypography.Row(true));
                var label=CombatHud.Caption(text,HudTypography.Damage);
                label.Name="HudPotionChoice";label.AutowrapMode=TextServer.AutowrapMode.WordSmart;
                box.AddChild(label);rows.AddChild(box);
            }
        }
        if(index>=pages.Count){IdealWidth=0;PlayerTotals();return;}

        var page=pages[index];
        Round.Text=Ui.Turn(page.Number);
        bool same=selected==page.Number;
        selected=page.Number;
        PlayerTotals();
        if(Overview.Panel.Visible)RefreshOverview();
        if(!ListMode){IdealWidth=0;return;}
        int firstStep=page.Lines.FirstOrDefault(line=>line.Step>=0)?.Step??0;
        var started=menu?.Round==page.Number?menu.StartedSteps(page.Lines
            .Where(line=>line.Step>=0).Select(line=>(line.Step-firstStep,line.Move)),seats,executed):null;
        foreach(var line in page.Lines)
        {
            if(!originalOrderOnly && seats.Length>1 && line.Seat>=0 && line.Seat+1!=localSeat ||
                line.Step>=0 && (executed?.Includes(page.Number,line.Step-firstStep)==true ||
                    started?.ContainsKey(line.Step-firstStep)==true))continue;
            if(page.Number==readyRound && line.End && (uint)line.Seat<(uint)seats.Length && ready.Contains(seats[line.Seat].Id))
                continue;
            if(OwnOnly && !originalOrderOnly && waitRound==page.Number && line.Step>=0 && waits.TryGetValue(line.Step-firstStep,out var wait))
            {
                var row=TextFields(HudTypography.Damage,true,wait);
                row.Name="HudWait"+line.Step;row.CustomMinimumSize=new(0,HudTypography.RowHeight);
                rows.AddChild(row);
            }
            string? reading=OwnOnly && !originalOrderOnly && waitRound==page.Number &&
                graphDamage.TryGetValue(line.Step-firstStep,out var amount)?amount??"":null;
            AddRow(line,reading);
        }
        var report=lastPlan?.Rounds?.FirstOrDefault(r=>r.Round==selected);
        if(!OwnOnly && report is {IncludesBlockDamage:true,StepDamage:null,Damage:not null})
        {
            int unlisted=report.Damage.Sum()-(report.CardDamage?.Values.Sum()??0);
            if(unlisted>0)rows.AddChild(TextFields(HudTypography.Damage,false,Ui.Text("ui.076"),Ui.Text("ui.077",unlisted)));
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
            if(c.TargetLabel is {} targetLabel && c.TargetModel is {HasParenthesizedName:true} target && full>room)
            {
                float minimum=c.TargetWidth-TextWidth(targetLabel,c.TargetName)+
                    TextWidth(targetLabel,outlined?target.Minimum:"→ "+target.Minimum)+Padding(target.Minimum);
                cardWidth=Math.Min(cardWidth,Math.Max(1,room-ActionGap-minimum));
            }
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
                if(c.TargetModel is {HasParenthesizedName:true} model)
                {
                    string arrow=outlined?"":"→ ";
                    label.Text=arrow+model.Fit(value=>TextWidth(label,value),
                        Math.Max(1,fit-TextWidth(label,arrow)-Padding(model.Minimum)));
                }
                else label.Text=RouteTextWrap.Fit(c.TargetName,fit,TargetFit);
                var targetLines=label.Text.Split('\n');
                float text=targetLines.Max(t=>TextWidth(label,t));
                label.CustomMinimumSize=new(text,0);
                c.Target.CustomMinimumSize=new(text+trim,0);
                used+=ActionGap+targetLines.Max(TargetFit)+trim;
            }
            else used=titleLines.Max(CardFit);
            foreach(var detail in c.Details)
            {
                if(detail.Label is not {} detailLabel)
                {
                    float width=detail.Control.GetCombinedMinimumSize().X;
                    natural=Math.Max(natural,width+Gap(""));
                    used=Math.Max(used,width+Gap(""));
                    continue;
                }
                float DetailFit(string text)=>TextWidth(detailLabel,text)+detail.Outline+Gap(detail.Outline>0?"":text);
                natural=Math.Max(natural,DetailFit(detail.Text));
                detailLabel.Text=RouteTextWrap.Fit(detail.Text,room,DetailFit);
                used=Math.Max(used,detailLabel.Text.Split('\n').Max(DetailFit));
            }
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
    private static string LossText(int loss)=>loss<0?Ui.Text("ui.078",-loss):Ui.Text("ui.079",loss);
    private void AddRow(Line row,string? damageReading)
    {
        int seat=row.Seat+1,step=row.Step;
        var typed=row.Move;
        string detail="",name="";
        string? legacyTarget=null;
        bool potion=typed?.IsPotion??false;
        if(typed!=null)name=typed.Text();
        else
        {
            string body=row.LegacyBody;
            int at=body.IndexOf('〔');
            detail=at>=0?body[at..]:"";
            if(at>=0)body=body[..at];
            string[] move=body.Split(" → ",2);
            string original=move[0];
            potion=original.StartsWith("用药 ",StringComparison.Ordinal) ||
                lastPlan?.Terms?.Any(term=>term.Table=="potions" && (original==term.Source || original.StartsWith(term.Source+"（药水槽 ")))==true;
            name=Ui.Route(original.StartsWith("用药 ",StringComparison.Ordinal)?original[3..]:original,lastPlan);
            if(move.Length>1)legacyTarget=move[1];
        }
        string? liveTarget=lastPlan?.EnemyTargets?.TryGetValue(step,out uint id)==true && targetNames?.TryGetValue(id,out var label)==true?
            label:null;
        var targetModel=HudTargetLabel.For(typed?.Target,legacyTarget,lastPlan,liveTarget);
        if(potion && seats.Length==1 && targetModel?.Seat==seat)targetModel=null;
        string? targetText=targetModel?.Text();
        var bg=new PanelContainer{CustomMinimumSize=new(0,HudTypography.ActionRowHeight()),MouseFilter=Control.MouseFilterEnum.Pass};
        bg.AddThemeStyleboxOverride("panel",HudTypography.Row(rows.GetChildCount()%2==0));
        rows.AddChild(bg);
        var line=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
        line.AddThemeConstantOverride("separation",5);bg.AddChild(line);
        var who=CombatHud.Caption(seat>0?"P"+seat:"",HudTypography.Ink);
        var badge=new PanelContainer{SizeFlagsVertical=Control.SizeFlags.ShrinkCenter};
        badge.AddThemeStyleboxOverride("panel",seats.Length>1?SeatBox(seats.ElementAtOrDefault(seat-1),SeatBadgeInset,0):
            HudSurface.Box("#00000000","",SeatBadgeInset,0));
        badge.AddChild(who);line.AddChild(badge);
        var content=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,MouseFilter=Control.MouseFilterEnum.Pass};
        content.AddThemeConstantOverride("separation",1);line.AddChild(content);
        ActionColumns? columns=null;
        if(name.Length!=0)
        {
            var action=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
            action.AddThemeConstantOverride("separation",ActionGap);content.AddChild(action);
            var descriptionBlock=new VBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
            descriptionBlock.AddThemeConstantOverride("separation",1);action.AddChild(descriptionBlock);
            var description=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Pass};
            description.AddThemeConstantOverride("separation",ActionGap);descriptionBlock.AddChild(description);
            var snapshot=lastPlan?.Rounds?.FirstOrDefault(r=>r.Round==selected);
            var title=CombatHud.Caption(name,potion?HudTypography.Damage:row.End?new("#bdcbd1"):new("#fffcf4"));
            title.SizeFlagsHorizontal=Control.SizeFlags.Fill;
            description.AddChild(title);
            int dealt=pages[RoundIndex].DamageByStep?.GetValueOrDefault(step)??0;
            var damageText=damageReading??(dealt>0?dealt.ToString(Ui.Culture):"");
            var damage=CombatHud.Caption(damageText.Length>0?Ui.Text("ui.080",damageText):"",HudTypography.Damage,small:true);
            var energy=CombatHud.Caption(snapshot?.EnergyAfter?.TryGetValue(step,out int left)==true?Ui.Text("ui.081",left):"",new("#bacbd0"),small:true);
            if(pages[RoundIndex].EndLossByStep?.TryGetValue(step,out var loss)==true)
            {
                energy.Text=loss==0?"":LossText(loss);
                energy.AddThemeColorOverride("font_color",loss<0?HudTypography.Heal:HudTypography.Loss);
                energy.MouseFilter=Control.MouseFilterEnum.Pass;
            }
            damage.HorizontalAlignment=energy.HorizontalAlignment=HorizontalAlignment.Left;
            damage.VerticalAlignment=energy.VerticalAlignment=VerticalAlignment.Top;

            var divider=HudTypography.ActionMetricRule(continuous:true);
            divider.Name="HudListMetricRule";
            action.AddChild(divider);action.AddChild(damage);action.AddChild(energy);
            float targetWidth=0;Label? targetLabel=null;
            var destination=new HBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.Fill,Alignment=BoxContainer.AlignmentMode.Begin};
            description.AddChild(destination);
            if(targetText!=null)
            {
                var target=CombatHud.Caption("→ "+targetText,new("#c5d6dc"));
                target.Name="HudListTarget";
                targetLabel=target;target.HorizontalAlignment=HorizontalAlignment.Left;
                bool lethalTarget=pages[RoundIndex].Kills.Any(k=>k.Step==step && k.IsTarget);
                if(lethalTarget)
                {
                    destination.AddChild(CombatHud.Caption("→",new("#c5d6dc")));
                    target.Text=targetText;
                    target.AddThemeColorOverride("font_color",HudTypography.Kill);
                    destination.AddChild(CombatHud.Outline(target,Ui.Text("ui.082")));
                }
                else {target.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;destination.AddChild(target);}
                float nameWidth=TextWidth(target,target.Text)+2;
                targetWidth=nameWidth+(lethalTarget?destination.GetChild<Control>(0).GetCombinedMinimumSize().X+
                    destination.GetThemeConstant("separation")+((PanelContainer)target.GetParent()).GetThemeStylebox("panel").GetMinimumSize().X:0);
                if(lethalTarget)target.CustomMinimumSize=new(Math.Min(nameWidth,145),0);
            }
            columns=new(badge,descriptionBlock,title,title.Text,destination,targetLabel,targetModel,targetLabel?.Text??"",targetWidth,damage,energy,divider);
            visibleActions.Add(columns);
        }
        void AddDetail(Control control,Label? caption=null,float outline=0)
        {
            (columns?.Description??content).AddChild(control);
            columns?.Details.Add(new(control,caption,caption?.Text??"",outline));
        }
        var restores=lastPlan?.Rounds?.FirstOrDefault(r=>r.Round==selected)?.Revives?.GetValueOrDefault(step);
        if(restores!=null)foreach(var e in restores)
        {
            var revivedSeat=Array.FindIndex(lastPlan!.Party,s=>s.Id==e.Seat)+1;
            AddDetail(TextFields(new("#cfba90"),false,$"P{revivedSeat}",
                Ui.Text("ui.083",DeathSaves.Name(e)),Ui.Text("ui.084",e.Hp)));
        }
        foreach(var kill in pages[RoundIndex].Kills.Where(k=>k.Step==step && (!k.IsTarget || targetText==null)))
        {
            var caption=CombatHud.Caption(kill.Caption(lastPlan,targetNames),new("#ffedbf"));
            var victim=CombatHud.Outline(caption,Ui.Text("ui.085"));
            victim.SizeFlagsHorizontal=Control.SizeFlags.ShrinkBegin;
            AddDetail(victim,caption,victim.GetThemeStylebox("panel").GetMinimumSize().X);
        }
        if(detail.Length==0 && typed==null && row.Picks==null)return;
        var picks=new List<string>();
        foreach(Match m in Regex.Matches(detail,@"〔([^〕]+)〕"))
        {
            var pickText=m.Groups[1].Value;
            string prefix=$"P{seat} ";
            if(pickText.StartsWith(prefix,StringComparison.Ordinal))pickText=pickText[prefix.Length..];
            picks.Add(pickText);
        }
        var recordedPicks=typed?.InlinePicks()??row.Picks;
        string pickTextDisplay=recordedPicks!=null?string.Join(Ui.Text("ui.086"),recordedPicks.Select(pick=>pick.Text(seat))):
            Ui.Route(string.Join(Ui.Text("ui.086"),picks),lastPlan);
        if(pickTextDisplay.Length==0)return;
        var pick=CombatHud.Caption(pickTextDisplay,new("#f3d19a"));
        pick.Name="HudListChoice";
        if(columns==null)pick.AutowrapMode=TextServer.AutowrapMode.WordSmart;
        AddDetail(pick,pick);
    }
}
