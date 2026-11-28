using System;
using Godot;

namespace CoopSolver;

internal sealed class HudCanvas
{
    private const int Tile=32,MaxSupersampledPanelPixels=4_194_304;
    private static ShaderMaterial? composite;
    private readonly Node holder=new(){Name="CoopSolverHudCanvas"};
    private readonly Window window;
    private readonly Control bar,details;
    private readonly CanvasLayer layer=new(){Layer=92};
    private readonly SubViewport? surface;
    private readonly TextureRect? output;
    private Transform2D? lastDraw;

    internal HudCanvas(Window window,Control root,Control bar,Control details)
    {
        this.window=window;
        this.bar=bar;this.details=details;
        window.AddChild(holder);
        if(DisplayServer.GetName()!="headless")
        {
            surface=new SubViewport{
                Name="HudSamples",Size=new(2,2),World2D=new(),Disable3D=true,GuiDisableInput=true,
                TransparentBg=true,RenderTargetUpdateMode=SubViewport.UpdateMode.WhenVisible
            };
            holder.AddChild(surface);
            var screen=new CanvasLayer{Layer=92};
            holder.AddChild(screen);
            composite??=new ShaderMaterial{Shader=new Shader{Code="""
                shader_type canvas_item;
                render_mode blend_premul_alpha;
                void fragment() {
                    COLOR = texture(TEXTURE, UV);
                }
                """}};
            output=new TextureRect{
                Name="HudComposite",Texture=surface.GetTexture(),Material=composite,
                ExpandMode=TextureRect.ExpandModeEnum.IgnoreSize,TextureFilter=CanvasItem.TextureFilterEnum.Linear,
                MouseFilter=Control.MouseFilterEnum.Ignore,Visible=false
            };
            screen.AddChild(output);
            // Only the drawing moves to the smaller viewport. Godot still lays out the
            // controls and dispatches input in the actual window's coordinate space.
            layer.CustomViewport=surface;
        }
        holder.AddChild(layer);
        layer.AddChild(root);
    }

    internal static Transform2D DeviceTransform(Viewport view)
    {
        var transform=view.GetFinalTransform();
        // An embedded window's render target follows the screen scale of the
        // parent window even when its own logical viewport uses an identity scale.
        if(view is Window window && window.IsEmbedded())
            transform=transform.Scaled(window.GetScreenTransform().Scale/transform.Scale);
        return transform;
    }

    private static Rect2 Merge(Rect2 bounds,Control? control)
    {
        if(control is not {Visible:true})return bounds;
        var rect=control.GetGlobalRect();
        if(!rect.HasArea())return bounds;
        return bounds.HasArea()?bounds.Merge(rect):rect;
    }

    private static Vector2I TileExtent(Rect2 bounds)
    {
        if(!bounds.HasArea())return Vector2I.Zero;
        var extent=(Vector2I)(bounds.End.Ceil()-bounds.Position.Floor());
        return new(((extent.X+Tile-1)/Tile)*Tile,((extent.Y+Tile-1)/Tile)*Tile);
    }

    internal void Update(Control root,InlineMenus menus)
    {
        if(surface==null || output==null)return;
        if(!root.Visible){output.Hide();return;}
        Rect2 bounds=default;
        foreach(var child in root.GetChildren())
        {
            if(child is Control control && control!=menus.InputShield)
                bounds=Merge(bounds,control);
        }
        var transform=DeviceTransform(window);
        var scale=transform.Scale;
        if(!bounds.HasArea() || scale.X<=0 || scale.Y<=0){output.Hide();return;}

        // Snap the texture and its destination to the same device-pixel grid.
        var visible=transform*window.GetVisibleRect();
        Rect2 Measure(Rect2 rect,int padding)=>(transform*rect).Grow(padding).Intersection(visible);
        var measured=Measure(bounds,3);
        if(menus.VisiblePanel is { } popup)
        {
            var menuBounds=Measure(popup.GetGlobalRect(),8);
            if(menuBounds.HasArea())measured=measured.HasArea()?measured.Merge(menuBounds):menuBounds;
        }
        if(!measured.HasArea()){output.Hide();return;}
        var origin=measured.Position.Floor();
        var extent=TileExtent(measured);
        // Menus and hover panels extend the rendered area without changing how
        // the underlying HUD is sampled. The full-window shield only handles clicks.
        var panels=TileExtent(Measure(Merge(Merge(default,bar),details),3));
        int samples=4L*panels.X*panels.Y<=MaxSupersampledPanelPixels?2:1;
        var size=extent*samples;
        bool resized=surface.Size!=size;
        if(resized)surface.Size=size;
        var draw=new Transform2D(transform.X*samples,transform.Y*samples,(transform.Origin-origin)*samples);
        if(resized || lastDraw!=draw)
        {
            RenderingServer.ViewportSetCanvasTransform(surface.GetViewportRid(),layer.GetCanvas(),draw);
            lastDraw=draw;
        }
        output.Position=transform.AffineInverse()*origin;
        output.Size=(Vector2)extent/scale;
        output.Show();
    }

    internal void Hide()=>output?.Hide();
    internal void Remove(){Hide();holder.QueueFree();}
}
