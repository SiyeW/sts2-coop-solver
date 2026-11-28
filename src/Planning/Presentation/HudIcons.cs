using Godot;

namespace CoopSolver;

internal static class HudIcons
{
    // Use one coordinate system for every control so the icons do not depend on font fallback.
    private static ImageTexture Icon(string shape,int size)
    {
        using var image=new Image();
        image.LoadSvgFromString($"<svg xmlns='http://www.w3.org/2000/svg' width='{size}' height='{size}' viewBox='0 0 24 24'>{shape}</svg>",3);
        image.GenerateMipmaps();
        var texture=ImageTexture.CreateFromImage(image);
        texture.SetSizeOverride(new(size,size));return texture;
    }
    private const string Ring="<circle cx='12' cy='12' r='10' fill='none' stroke='white' stroke-width='1.7'/>";
    internal static readonly Texture2D Thumb=Icon("<circle cx='12' cy='12' r='9.5' fill='#d8e7e2' stroke='#708c87' stroke-width='1.4'/>",15);
    internal static readonly Texture2D Start=Icon(Ring+"<path d='M10 7l7 5-7 5z' fill='white'/>",24);
    internal static readonly Texture2D Pause=Icon(Ring+"<path d='M9 7v10m6-10v10' stroke='white' stroke-width='2.6'/>",24);
    internal static readonly Texture2D Previous=Icon("<path d='M3 12l9-7v5.5h9v3h-9V19z' fill='white'/>",17);
    internal static readonly Texture2D Next=Icon("<path d='M21 12l-9-7v5.5H3v3h9V19z' fill='white'/>",17);
    internal static readonly Texture2D Up=Icon("<path d='m5 15 7-7 7 7' fill='none' stroke='white' stroke-width='2.3' stroke-linecap='round' stroke-linejoin='round'/>",14);
    internal static readonly Texture2D Down=Icon("<path d='m5 9 7 7 7-7' fill='none' stroke='white' stroke-width='2.3' stroke-linecap='round' stroke-linejoin='round'/>",14);
    internal static readonly Texture2D External=Icon("<path d='M12 4h8v8m0-8-9 9M9 6H4v14h14v-5' fill='none' stroke='white' stroke-width='1.9' stroke-linecap='round' stroke-linejoin='round'/>",15);
    private static Texture2D LineIcon(string path)=>Icon(
        $"<path d='{path}' fill='none' stroke='white' stroke-width='2.1' stroke-linecap='round' stroke-linejoin='round'/>",15);
    internal static readonly Texture2D Play=LineIcon("M7 4v16l13-8z");
    internal static readonly Texture2D Stop=LineIcon("M5 5h14v14H5z");
    internal static readonly Texture2D Restart=LineIcon("M21 5v6h-6M3 19v-6h6M21 11A9 9 0 0 0 5.7 5.7M3 13a9 9 0 0 0 15.3 5.3");
    internal static readonly Texture2D Settings=Icon("<path d='M10 3h4l.8 3 2 .9 2.8-.9 2 3.5-2.3 2v2.8l2.3 2-2 3.5-2.8-.9-2 .9-.8 3h-4l-.8-3-2-.9-2.8.9-2-3.5 2.3-2v-2.8l-2.3-2 2-3.5 2.8.9 2-.9z' transform='translate(0 -1)' fill='none' stroke='white' stroke-width='1.7' stroke-linejoin='round'/><circle cx='12' cy='12' r='3' fill='none' stroke='white' stroke-width='1.8'/>",15);
    internal static readonly Texture2D Filter=LineIcon("M3 5h18l-7 8v5l-4 2v-7z");
    internal static readonly Texture2D EyeOpen=Icon("<path d='M2 12s3.5-6.5 10-6.5S22 12 22 12s-3.5 6.5-10 6.5S2 12 2 12z' fill='none' stroke='white' stroke-width='2'/><circle cx='12' cy='12' r='3' fill='white'/>",15);
    internal static readonly Texture2D EyeClosed=LineIcon("M3 9c2.5 4 5.5 6 9 6s6.5-2 9-6M6.8 13.4l-2.4 3M12 15v3.8m5.2-5.4 2.4 3");
    internal static readonly Texture2D Check=Icon("<path d='m4 12 5 5L20 6' fill='none' stroke='white' stroke-width='2.5' stroke-linecap='round' stroke-linejoin='round'/>",14);
    private const string Horizontal="<path d='M5 12h14' fill='none' stroke='white' stroke-width='2.6' stroke-linecap='round'/>";
    internal static readonly Texture2D Minus=Icon(Horizontal,16);
    internal static readonly Texture2D Plus=Icon(Horizontal+"<path d='M12 5v14' fill='none' stroke='white' stroke-width='2.6' stroke-linecap='round'/>",16);
    internal static readonly Texture2D Warning=Icon("<path d='M12 2.5 22.1 21H1.9z' fill='none' stroke='white' stroke-width='2.2' stroke-linejoin='round'/><path d='M12 9v4.5' stroke='white' stroke-width='2.6' stroke-linecap='round'/><circle cx='12' cy='17' r='1.4' fill='white'/>",19);
    internal static readonly Texture2D Pin=Icon("<g transform='rotate(32 12 12)' fill='white'><path d='M7 3h10v2h-1v6l3 3v1H5v-1l3-3V5H7zM11 16h2v5l-1 2-1-2z'/></g>",22);
}
