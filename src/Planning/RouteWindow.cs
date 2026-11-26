using System;
using Godot;

namespace CoopSolver;

internal sealed class RouteWindow
{
    private const string LayoutFile="user://coop-solver-window.cfg";
    private readonly Window window=new() { Name="CoopSolverRouteWindow", Title=Ui.T("全队战斗路线","Party combat routes"), Visible=false, ForceNative=true,
        Transient=false, Exclusive=false, Borderless=false, Unresizable=false,
        InitialPosition=Window.WindowInitialPosition.Absolute, MinSize=new(500,390) };
    private Rect2I stored;
    private long geometryCheck;
    private bool shown;
    internal void Remove(){SaveGeometry();window.Hide();window.QueueFree();}
    internal bool IsShown=>window.Visible;
    internal readonly CombatHud Panel;

    internal RouteWindow(SceneTree tree,Func<Window,CombatHud> createHud)
    {
        var usable=DisplayServer.ScreenGetUsableRect(tree.Root.CurrentScreen);
        var defaultSize=new Vector2I(Math.Min(740,usable.Size.X),Math.Min(690,usable.Size.Y));
        var bounds=new Rect2I(usable.Position+(usable.Size-defaultSize)/2,defaultSize);
        var cfg=new ConfigFile();
        if(cfg.Load(LayoutFile)==Error.Ok)
        {
            var saved=cfg.GetValue("window","rect",bounds).AsRect2I();
            for(int i=0;i<DisplayServer.GetScreenCount();i++)
            {
                var screen=DisplayServer.ScreenGetUsableRect(i);
                // Restore only where the user can still reach the title bar.
                if(!screen.HasPoint(saved.Position+new Vector2I(50,1)))continue;
                var size=new Vector2I(Math.Clamp(saved.Size.X,500,Math.Max(500,screen.Size.X)),
                    Math.Clamp(saved.Size.Y,390,Math.Max(390,screen.Size.Y)));
                bounds=new Rect2I(saved.Position.Clamp(screen.Position, (screen.End-size).Max(screen.Position)),size);
                break;
            }
        }
        stored=bounds;window.Position=bounds.Position;window.Size=bounds.Size;window.Theme=HudTypography.Theme();
        tree.Root.AddChild(window);
        window.AlwaysOnTop=cfg.GetValue("window","always_on_top",false).AsBool();
        var backdrop=new ColorRect{Color=new Color("#111c23"),MouseFilter=Control.MouseFilterEnum.Ignore};
        window.AddChild(backdrop);backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        Panel=createHud(window);
        window.CloseRequested+=()=>{SaveGeometry();Conceal();};window.TreeExiting+=SaveGeometry;
    }
    internal void Conceal(){window.Hide();Panel.Hide();}
    internal void Open()
    {
        if(window.Visible){if(window.Mode==Window.ModeEnum.Minimized)window.Mode=Window.ModeEnum.Windowed;window.GrabFocus();return;}
        window.Show();
        window.Size=stored.Size;window.Position=stored.Position;shown=true;
    }
    internal void TickGeometry()
    {
        if(!window.Visible || System.Environment.TickCount64<geometryCheck)return;
        geometryCheck=System.Environment.TickCount64+1000;SaveGeometry();
    }
    private void SaveGeometry()
    {
        if(!shown || !window.Visible || window.Mode!=Window.ModeEnum.Windowed)return;
        var rect=new Rect2I(window.Position,window.Size);
        if(rect==stored)return;
        var cfg=new ConfigFile();cfg.Load(LayoutFile);cfg.SetValue("window","rect",rect);
        if(cfg.Save(LayoutFile)==Error.Ok)stored=rect;
    }
}
