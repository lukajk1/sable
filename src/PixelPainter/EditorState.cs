using System.Numerics;
using PixelPainter.Rendering;

namespace PixelPainter;

public enum SelectMode { Object, Submesh }

/// <summary>What is selected and shown, shared by the app and both views.</summary>
public sealed class EditorState
{
    public GpuModel? Model;
    public SelectMode Mode = SelectMode.Object;
    /// <summary>The one object that can be painted, or -1.</summary>
    public int ActiveObject = -1;
    /// <summary>The selected submesh (part, component) in submesh mode.</summary>
    public (int Part, int Component)? Submesh;
    /// <summary>Local view: the only object shown, or null.</summary>
    public int? Isolated;
    /// <summary>The texture the UV view shows and paints, or -1.</summary>
    public int ActiveTexture = -1;
    public BrushCursor Cursor;

    public bool ObjectVisible(int index) =>
        Model != null && !Model.Source.Objects[index].Hidden && (Isolated == null || Isolated == index);

    public bool PartVisible(int part) => Model != null && ObjectVisible(Model.Source.Parts[part].ObjectIndex);

    public void ClearSelection()
    {
        ActiveObject = -1;
        Submesh = null;
    }
}

/// <summary>Where the paint cursor is, drawn by both views so each shows what the other is about to touch.</summary>
public struct BrushCursor
{
    public bool Visible;
    public bool Pencil;
    /// <summary>Centre in texels (brush) and the texel (pencil), in the active texture.</summary>
    public Vector2 TexelCenter;
    public float TexelRadius;
    public (int X, int Y) Texel;

    /// <summary>The 3D view's version: a screen circle, or the pencil texel's outline on the surface.</summary>
    public bool Visible3D;
    public Vector2 Screen;
    public float ScreenRadius;
    public Vector3[]? TexelOutline;
}
