using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CoopSolver;

// The shield and menu panels are siblings of the layout, in the same viewport.
internal sealed class InlineMenus
{
    internal sealed record Value(string Text,string? Color=null,bool Local=false,Color? Ink=null,string? Prefix=null,int Padding=3);
    internal sealed record Item(int Id,string Text,string? Name=null,string? Key=null,Value[]? Left=null,Value[]? Right=null)
    {
        internal bool Same(Item other)=>Id==other.Id && Text==other.Text && Name==other.Name && Key==other.Key &&
            (Left??Array.Empty<Value>()).SequenceEqual(other.Left??Array.Empty<Value>()) &&
            (Right??Array.Empty<Value>()).SequenceEqual(other.Right??Array.Empty<Value>());
    }
    private sealed record Menu(Button Anchor,PanelContainer Panel,VBoxContainer Rows,ScrollContainer Scroll,
        Item[] Initial,Func<int> Current,Action<Item> Select,bool Compact)
    { internal Item[] Items=Initial;internal Control? First,Second,Band;internal Vector2 ColumnWidths;internal VBoxContainer? Fixed;internal Button? Auto;internal bool Dirty;internal Button[] Entries=Array.Empty<Button>();internal bool? Above; }
    private readonly Control shield=new(){Visible=false,MouseFilter=Control.MouseFilterEnum.Stop};
    private readonly List<Menu> menus=new();
    private Menu? open;
    internal bool Visible=>open!=null;
    internal Control InputShield=>shield;
    internal Control? VisiblePanel=>open?.Panel;

    // Join the facing edges, including diagonal paths between panels of different widths.
    internal static bool InGap(Vector2 point,Rect2 a,Rect2 b)
    {
        if(a.Position.Y>b.Position.Y)(a,b)=(b,a);
        float gap=b.Position.Y-a.End.Y;
        if(gap<=0 || point.Y<a.End.Y || point.Y>b.Position.Y)return false;
        float t=(point.Y-a.End.Y)/gap;
        return point.X>=Mathf.Lerp(a.Position.X,b.Position.X,t) && point.X<=Mathf.Lerp(a.End.X,b.End.X,t);
    }
    internal bool Contains(Vector2 point)
    {
        if(open==null)return false;
        var rect=shield.GetGlobalTransform().AffineInverse()*open.Anchor.GetGlobalRect();
        var panel=open.Panel.GetRect();
        return panel.HasPoint(point) || InGap(point,rect,panel);
    }
    internal InlineMenus(Control surface)
    {
        surface.AddChild(shield);
        shield.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        shield.GuiInput+=e=>{
            if(e is not InputEventMouseButton b)return;
            shield.AcceptEvent();
            if(b.Pressed || b.ButtonIndex!=MouseButton.Left)return;
            var other=menus.FirstOrDefault(m=>m!=open && m.Anchor.IsVisibleInTree() && !m.Anchor.Disabled &&
                new Rect2(Vector2.Zero,m.Anchor.Size).HasPoint(m.Anchor.GetLocalMousePosition()));
            Close();
            if(other!=null)Open(other);
        };
    }
    internal void Bind(Button anchor,string menuName,Item[] items,Func<int> current,Action<int> select)
    {
        BindCore(anchor,menuName,items,current,item=>select(item.Id),false);
    }
    internal void BindOptions(Button anchor,Control first,Control second,Control band,Func<int> current,Action<Item> select)
    {
        var menu=BindCore(anchor,anchor.Name+"Menu",Array.Empty<Item>(),current,select,true);
        menu.First=first;menu.Second=second;menu.Band=band;
    }
    internal void Items(Button anchor,Item[] items)
    {
        var menu=menus.First(m=>m.Anchor==anchor);
        if(menu.Items.Length==items.Length && items.Select((item,i)=>menu.Items[i].Same(item)).All(same=>same))return;
        menu.Items=items;menu.Dirty=true;
        if(open==menu)Open(menu,menu.Scroll.ScrollVertical);
    }
    private Menu BindCore(Button anchor,string menuName,Item[] items,Func<int> current,Action<Item> select,bool compact)
    {
        anchor.FocusMode=Control.FocusModeEnum.None;anchor.Icon=HudIcons.Down;anchor.IconAlignment=HorizontalAlignment.Right;
        if(!anchor.HasThemeFontSizeOverride("font_size"))anchor.AddThemeFontSizeOverride("font_size",HudTypography.Body);
        anchor.Alignment=HorizontalAlignment.Left;anchor.ToggleMode=true;CombatHud.StyleButton(anchor,compact);
        var panel=new PanelContainer{Name=menuName,Visible=false,MouseFilter=Control.MouseFilterEnum.Stop};
        var surface=HudSurface.Box("#15232cff","#9aafb8aa",compact?0:4,compact?1:4);
        surface.ShadowColor=new Color("#00000070");surface.ShadowSize=4;surface.ShadowOffset=new Vector2(0,2);
        panel.AddThemeStyleboxOverride("panel",surface);
        var scroll=new ScrollContainer{HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode=compact?ScrollContainer.ScrollMode.Disabled:ScrollContainer.ScrollMode.Auto};
        var rows=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
        rows.AddThemeConstantOverride("separation",1);
        shield.AddChild(panel);
        var menu=new Menu(anchor,panel,rows,scroll,items,current,select,compact);menus.Add(menu);
        if(compact)
        {
            var column=new VBoxContainer();column.AddThemeConstantOverride("separation",1);panel.AddChild(column);
            menu.Fixed=new VBoxContainer();column.AddChild(menu.Fixed);column.AddChild(scroll);
            scroll.SizeFlagsVertical=Control.SizeFlags.ExpandFill;
        }
        else panel.AddChild(scroll);
        scroll.AddChild(rows);
        Populate(menu);
        anchor.Pressed+=()=>{if(open==menu)Close();else Open(menu);};
        if(!compact)
        {
            float longest=items.Max(i=>anchor.GetThemeFont("font").GetStringSize(i.Text,fontSize:anchor.GetThemeFontSize("font_size")).X);
            anchor.CustomMinimumSize=new(Mathf.Ceil(longest)+anchor.Icon.GetWidth()+18,anchor.CustomMinimumSize.Y);
        }
        Refresh();return menu;
    }
    private void Populate(Menu menu)
    {
        foreach(var old in menu.Rows.GetChildren()){menu.Rows.RemoveChild(old);old.QueueFree();}
        if(menu.Auto!=null){menu.Fixed!.RemoveChild(menu.Auto);menu.Auto.QueueFree();menu.Auto=null;}
        menu.ColumnWidths=Vector2.Zero;menu.Dirty=false;menu.Above=null;
        menu.Entries=new Button[menu.Items.Length];
        for(int i=0;i<menu.Items.Length;i++)
        {
            var item=menu.Items[i];
            var entry=menu.Entries[i]=new Button{Name=item.Name??(menu.Anchor.Name.ToString()+"Item"+item.Id),Text=item.Text,
                Flat=false,ToggleMode=true,FocusMode=Control.FocusModeEnum.None,Alignment=HorizontalAlignment.Left};
            int inset=menu.Compact?1:3;
            entry.AddThemeStyleboxOverride("normal",CombatHud.Box("#00000000",8,inset));
            entry.AddThemeStyleboxOverride("hover",HudSurface.Box("#91b4bc48","",8,inset));
            var selected=HudSurface.Box("#72bba750","#a4d2c3a0",8,inset);
            entry.AddThemeStyleboxOverride("pressed",selected);entry.AddThemeStyleboxOverride("hover_pressed",selected);
            entry.AddThemeFontSizeOverride("font_size",menu.Anchor.GetThemeFontSize("font_size"));
            entry.CustomMinimumSize=new(0,HudTypography.ControlHeight);
            if(menu.Fixed!=null && item.Id<0){menu.Auto=entry;menu.Fixed.AddChild(entry);}
            else menu.Rows.AddChild(entry);
            entry.Pressed+=()=>{Close();menu.Select(item);Refresh();};
        }
    }
    private static Button Entry(Menu menu,int i)=>menu.Entries[i];
    private void Open(Menu menu,int? scrollOffset=null)
    {
        if(!menu.Anchor.IsVisibleInTree() || menu.Anchor.Disabled)return;
        Close();open=menu;menu.Anchor.Icon=HudIcons.Up;menu.Anchor.SetPressedNoSignal(true);shield.MoveToFront();shield.Show();menu.Panel.Show();
        if(menu.Dirty)Populate(menu);
        int selected=menu.Current();
        for(int i=0;i<menu.Items.Length;i++)
        {
            var entry=Entry(menu,i);
            bool picked=menu.Items[i].Id==selected;
            entry.SetPressedNoSignal(picked);
            entry.Icon=picked?HudIcons.Check:null;
            entry.IconAlignment=HorizontalAlignment.Right;
        }
        Position(menu);
        ScrollTo(menu,scrollOffset??(menu.Above==true?int.MaxValue:0));
    }
    internal void Close()
    {
        if(open!=null){open.Anchor.Icon=HudIcons.Down;open.Anchor.SetPressedNoSignal(false);open.Panel.Hide();}
        open=null;shield.Hide();
    }
    internal void Refresh()
    {
        foreach(var menu in menus)
        {
            int selected=menu.Current();
            var item=Array.Find(menu.Items,i=>i.Id==selected);
            if(!menu.Compact && item!=null && menu.Anchor.Text!=item.Text)menu.Anchor.Text=item.Text;
        }
        if(open==null)return;
        if(!open.Anchor.IsVisibleInTree() || open.Anchor.Disabled || Input.IsKeyPressed(Key.Escape)){Close();return;}
        Position(open);
    }
    private static void OptionColumns(Menu menu,float left,float right)
    {
        var widths=new Vector2(left,right);
        if(widths==menu.ColumnWidths)return;
        menu.ColumnWidths=widths;
        for(int i=0;i<menu.Items.Length;i++)
        {
            var entry=Entry(menu,i);var item=menu.Items[i];
            foreach(var old in entry.GetChildren()){entry.RemoveChild(old);old.QueueFree();}
            if(item.Id<0)
            {
                entry.Text=item.Text;
                entry.CustomMinimumSize=new(0,HudTypography.ControlHeight);
                continue;
            }
            float height=HudTypography.ControlHeight;
            void Column(Value[]? values,float x,float width)
            {
                float offset=0,y=1,lineHeight=0;
                var font=entry.GetThemeFont("font");
                float Measure(string t)=>Mathf.Ceil(font.GetStringSize(t,fontSize:HudTypography.Body).X);
                float available=Math.Max(1,width-8);
                foreach(var value in values??Array.Empty<Value>())
                {
                    var box=new PanelContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
                    var tint=value.Color is {} css && Color.HtmlIsValid(css)?new Color(css):Colors.Transparent;
                    box.AddThemeStyleboxOverride("panel",value.Color==null?HudSurface.Box("#00000000","",value.Padding,0):
                        HudRouteView.TintedBox(tint,value.Padding,0));
                    var ink=value.Ink??HudTypography.Ink;
                    var label=CombatHud.Caption(value.Text,ink);
                    float labelWidth=available-2*value.Padding;
                    if(value.Prefix is {} prefix)
                    {
                        var fields=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};
                        fields.AddThemeConstantOverride("separation",HudTypography.FieldGap);
                        fields.AddChild(CombatHud.Caption(prefix,ink));fields.AddChild(label);
                        box.AddChild(fields);labelWidth-=Measure(prefix)+HudTypography.FieldGap;
                    }
                    else box.AddChild(label);
                    label.Text=RouteTextWrap.Fit(label.Text,Math.Max(1,labelWidth),Measure);
                    CombatHud.SeatUnderline(box,value.Local,ink);
                    entry.AddChild(box);
                    var size=box.GetCombinedMinimumSize();
                    if(offset>0 && offset+size.X>available){offset=0;y+=lineHeight+1;lineHeight=0;}
                    box.Position=new(x+5+offset,y);offset+=size.X+4;lineHeight=Math.Max(lineHeight,size.Y);
                }
                height=Math.Max(height,y+lineHeight+1);
            }
            Column(item.Left,0,left);Column(item.Right,left,right);
            entry.CustomMinimumSize=new(0,height);
        }
    }

    private static void ScrollTo(Menu menu,int offset)=>
        menu.Scroll.SetDeferred("scroll_vertical",offset);

    private static void Order(Menu menu,bool above)
    {
        if(menu.Above==above)return;
        menu.Above=above;
        if(menu.Fixed!=null)menu.Fixed.GetParent().MoveChild(menu.Fixed,above?1:0);
        var rows=menu.Entries.Where(entry=>entry!=menu.Auto).ToArray();
        for(int i=0;i<rows.Length;i++)menu.Rows.MoveChild(rows[i],above?rows.Length-1-i:i);
        ScrollTo(menu,above?int.MaxValue:0);
    }

    private void Position(Menu menu)
    {
        var transform=shield.GetGlobalTransform().AffineInverse()*menu.Anchor.GetGlobalTransform();
        var rect=transform*new Rect2(Vector2.Zero,menu.Anchor.Size);
        float optionWidth=0,scrollWidth=0;
        if(menu.First is {} first && menu.Second is {} second && menu.Band is {} band)
        {
            Rect2 Local(Control c)=>shield.GetGlobalTransform().AffineInverse()*c.GetGlobalRect();
            var loss=Local(first);var pots=Local(second);var strip=Local(band);
            rect=new Rect2(loss.Position.X,strip.Position.Y,pots.End.X-loss.Position.X,strip.Size.Y);
            var font=menu.Anchor.GetThemeFont("font");
            float TextWidth(string text)=>Mathf.Ceil(font.GetStringSize(Ui.Punctuation(text),fontSize:HudTypography.Body).X);
            float ValueWidth(Value value)=>TextWidth(value.Text)+(value.Prefix is {} prefix?TextWidth(prefix)+HudTypography.FieldGap:0);
            float ColumnWidth(Value[]? values)=>(values??Array.Empty<Value>()).Sum(v=>ValueWidth(v)+2*v.Padding+4)+8;
            float left=Math.Max(pots.Position.X-loss.Position.X,menu.Items.Max(item=>ColumnWidth(item.Left)));
            float right=Math.Max(pots.Size.X,menu.Items.Max(item=>ColumnWidth(item.Right)));
            float checkWidth=HudIcons.Check.GetWidth()+8;
            float autoWidth=TextWidth(menu.Items[0].Text)+16;
            scrollWidth=menu.Scroll.GetVScrollBar().GetCombinedMinimumSize().X;
            optionWidth=Math.Min(shield.Size.X,Math.Max(autoWidth+checkWidth,left+right+checkWidth));
            left=Math.Min(left,optionWidth-1);
            rect=new Rect2(Math.Max(0,pots.End.X-optionWidth),strip.Position.Y,optionWidth,strip.Size.Y);
            OptionColumns(menu,left,Math.Max(1,optionWidth-left-checkWidth));
        }
        float above=rect.Position.Y-2,below=shield.Size.Y-rect.End.Y-2;
        float contentHeight=Mathf.Ceil(menu.Rows.GetCombinedMinimumSize().Y);
        float optionsHeight=menu.Compact?Math.Min(contentHeight,6*(HudTypography.ControlHeight+3)):contentHeight;
        float insets=menu.Scroll.GetThemeStylebox("panel").GetMinimumSize().Y+
            (menu.Auto!=null?Mathf.Ceil(menu.Auto.GetCombinedMinimumSize().Y)+1:0)+
            menu.Panel.GetThemeStylebox("panel").GetMinimumSize().Y;
        float wanted=optionsHeight+insets;
        bool down=!(wanted<=above || above>=below);
        Order(menu,!down);
        float available=Math.Max(0,down?below:above);
        float height=Math.Min(wanted,available);
        bool scrolls=contentHeight>optionsHeight || wanted>available+.001f;
        if(menu.Compact)menu.Scroll.VerticalScrollMode=scrolls?ScrollContainer.ScrollMode.Auto:ScrollContainer.ScrollMode.Disabled;
        float width=Math.Min(shield.Size.X,menu.Compact?optionWidth+(scrolls?scrollWidth:0):
            Math.Max(rect.Size.X,menu.Rows.GetCombinedMinimumSize().X+8+(height<wanted?16:0)));
        menu.Panel.Size=new(width,height);
        float x=menu.Compact?rect.End.X-width:rect.Position.X;
        menu.Panel.Position=new(Math.Clamp(x,0,Math.Max(0,shield.Size.X-width)),
            down?rect.End.Y+2:Math.Max(0,above-height));
    }
}
