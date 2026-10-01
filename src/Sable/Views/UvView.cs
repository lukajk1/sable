using System.Numerics;
using Sable.Rendering;
using Raylib_cs;

namespace Sable.Views;

/// <summary>
/// The UV view: the active texture (point-filtered) with the active object's UV layout over it. MMB drag pans, the
/// wheel zooms around the cursor, Home fits the texture. A texel grid appears once texels are big enough to see.
/// Without a texture, the layout is drawn over an empty 0-1 square.
/// </summary>
public sealed class UvView : IDisposable
{
    public bool PixelGrid = true;
    /// <summary>Also draw, dimmed, other objects that use the same texture.</summary>
    public bool ShowSiblings = true;
    /// <summary>Draw every UV edge; off, only the islands' outlines (follows the 3D view's wireframe toggle).</summary>
    public bool InnerEdges = true;

    public bool Hovered { get; private set; }
    /// <summary>Mouse position in texels (x right, y down; may be outside the texture).</summary>
    public Vector2 MouseTexel { get; private set; }
    public bool MouseOnTexture => MouseTexel.X >= 0 && MouseTexel.Y >= 0 && MouseTexel.X < TextureSize.X && MouseTexel.Y < TextureSize.Y;
    public Vector2 TextureSize { get; private set; } = new(EmptySize);

    private const int EmptySize = 256;
    private RenderTexture2D target;
    private int width, height;
    private Vector2 offset;
    private float zoom = 1f;
    private bool leftDragging, lastLeftDown;
    private bool fitPending = true;
    private bool dragging;
    private Vector2 lastSize;
    private Vector2 origin, lastPointer;

    public Texture2D Texture => target.Texture;

    public void RequestFit() => fitPending = true;

    /// <param name="pointer">Where the pen or mouse is, in window pixels (the app picks the source).</param>
    /// <param name="space">Space is held: a left drag pans, as the middle button does.</param>
    public void Update(Rectangle rect, bool hovered, Vector2 pointer, EditorState state, bool leftDown = false, bool space = false)
    {
        Resize((int)rect.Width, (int)rect.Height);
        Hovered = hovered;
        TextureSize = SizeOf(state);
        if (TextureSize != lastSize) { fitPending = true; lastSize = TextureSize; }
        if (fitPending) Fit();

        origin = new Vector2(rect.X, rect.Y);
        Vector2 local = pointer - origin;
        Vector2 delta = pointer - lastPointer;
        lastPointer = pointer;

        if (hovered && Raylib.IsMouseButtonPressed(MouseButton.Middle)) (dragging, leftDragging) = (true, false);
        if (hovered && space && leftDown && !lastLeftDown && !dragging) (dragging, leftDragging) = (true, true);
        lastLeftDown = leftDown;
        if (leftDragging ? !leftDown : !Raylib.IsMouseButtonDown(MouseButton.Middle)) dragging = false;
        if (dragging) offset += delta;

        if (hovered)
        {
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0)
            {
                Vector2 texelUnderMouse = (local - offset) / zoom;
                zoom = Math.Clamp(zoom * MathF.Pow(1.2f, wheel), 0.05f, 256f);
                offset = local - texelUnderMouse * zoom;
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Home)) Fit();
        }

        MouseTexel = (local - offset) / zoom;
    }

    /// <summary>Zooms by <paramref name="factor"/> (above 1 zooms in) keeping the texel under a window position still.</summary>
    public void ZoomAt(Vector2 screen, float factor)
    {
        Vector2 local = screen - origin;
        Vector2 texel = (local - offset) / zoom;
        zoom = Math.Clamp(zoom * factor, 0.05f, 256f);
        offset = local - texel * zoom;
    }

    /// <summary>A window position in texels of the shown texture.</summary>
    public Vector2 ScreenToTexel(Vector2 screen) => (screen - origin - offset) / zoom;

    private void Fit()
    {
        if (width <= 0 || height <= 0) return;
        zoom = MathF.Min(width / TextureSize.X, height / TextureSize.Y) * 0.9f;
        offset = new Vector2(width, height) * 0.5f - TextureSize * zoom * 0.5f;
        fitPending = false;
    }

    private static Vector2 SizeOf(EditorState state)
    {
        if (state.Model == null || state.ActiveTexture < 0) return new Vector2(EmptySize);
        var t = state.Model.Textures[state.ActiveTexture];
        return new Vector2(t.Width, t.Height);
    }

    public void Render(EditorState state)
    {
        if (width <= 0 || height <= 0) return;
        Raylib.BeginTextureMode(target);
        Raylib.ClearBackground(new Color(40, 40, 40, 255));

        var model = state.Model;
        var area = new Rectangle(offset.X, offset.Y, TextureSize.X * zoom, TextureSize.Y * zoom);

        if (model != null && state.ActiveTexture >= 0)
        {
            DrawChecker(area);
            var paint = model.Textures[state.ActiveTexture];
            var texture = paint.Gpu;
            Raylib.DrawTexturePro(texture, new Rectangle(0, 0, texture.Width, texture.Height), area, Vector2.Zero, 0, Color.White);
            // The smoothness mask, while shown: red at half strength where painted, as in the 3D view.
            if (paint.Mask is { Visible: true } && paint.MaskGpu.Id != 0)
                Raylib.DrawTexturePro(paint.MaskGpu, new Rectangle(0, 0, texture.Width, texture.Height), area, Vector2.Zero, 0, new Color(255, 31, 31, 128));
            if (PixelGrid && zoom >= 6f) DrawTexelGrid(area);
        }
        else
        {
            Raylib.DrawRectangleRec(area, new Color(62, 62, 62, 255));
            Raylib.DrawText("no texture", (int)area.X, (int)(area.Y + area.Height) + 6, 10, new Color(120, 120, 120, 255));
        }
        Raylib.DrawRectangleLinesEx(area, 1, new Color(110, 110, 110, 255));

        if (model != null) DrawLayout(state, model);
        DrawSelection(state);
        DrawCursor(state);

        Raylib.EndTextureMode();
    }

    private void DrawLayout(EditorState state, GpuModel model)
    {
        var parts = model.Source.Parts;
        int texture = state.ActiveTexture;
        bool UsesTexture(int p) => texture < 0 || model.TextureOf(p) == texture;

        var dim = new Color(170, 170, 170, 80);
        var normal = new Color(225, 225, 225, 190);
        var orange = new Color(255, 160, 40, 255);

        // Other objects first, dimmed (or, with nothing selected, every object on this texture).
        for (int i = 0; i < parts.Count; i++)
        {
            if (!state.PartVisible(i) || parts[i].Uvs == null || !UsesTexture(i)) continue;
            if (parts[i].ObjectIndex == state.ActiveObject) continue;
            if (state.ActiveObject >= 0 && !(ShowSiblings && texture >= 0)) continue;
            DrawPartUvs(model, i, state.ActiveObject >= 0 ? dim : normal, -1);
        }
        if (state.ActiveObject < 0) return;

        foreach (int p in model.Source.Objects[state.ActiveObject].Parts)
        {
            if (!state.PartVisible(p) || parts[p].Uvs == null || !UsesTexture(p)) continue;
            DrawPartUvs(model, p, state.Mode == SelectMode.Object ? orange : normal, -1);
        }
        if (state.Mode == SelectMode.Submesh && state.Submesh is { } sub && parts[sub.Part].Uvs != null && UsesTexture(sub.Part))
            DrawPartUvs(model, sub.Part, orange, sub.Component);
    }

    private void DrawPartUvs(GpuModel model, int part, Color color, int onlyComponent)
    {
        var source = model.Source.Parts[part];
        var uvs = source.Uvs!;
        var edges = model.Edges[part];
        Vector2 scale = TextureSize * zoom;
        for (int e = 0; e < edges.A.Length; e++)
        {
            int component = edges.Component[e];
            if (source.ComponentHidden[component]) continue;
            if (onlyComponent >= 0 && component != onlyComponent) continue;
            if (!InnerEdges && !edges.Boundary[e]) continue;
            Raylib.DrawLineV(offset + uvs[edges.A[e]] * scale, offset + uvs[edges.B[e]] * scale, color);
        }
    }

    /// <summary>Marching ants around the lasso selection, and the lasso while it's being drawn.</summary>
    private void DrawSelection(EditorState state)
    {
        if (state.ActiveSelection is { } selection)
        {
            int phase = (int)(Raylib.GetTime() * 6);
            foreach (var (a, b) in selection.Outline)
            {
                // Each outline segment is one texel long; alternate them and shift the pattern over time.
                bool dark = (((int)(a.X + a.Y) + phase) & 1) == 0;
                Raylib.DrawLineEx(offset + a * zoom, offset + b * zoom, 1.5f, dark ? Color.Black : Color.White);
            }
        }
        if (state.TransformCorners is { Length: 4 } corners)
        {
            var screen = corners.Select(c => offset + c * zoom).ToArray();
            for (int i = 0; i < 4; i++)
            {
                Raylib.DrawLineEx(screen[i], screen[(i + 1) % 4], 3f, new Color(0, 0, 0, 200));
                Raylib.DrawLineEx(screen[i], screen[(i + 1) % 4], 1f, new Color(120, 200, 255, 255));
            }
            for (int i = 0; i < 4; i++)
            {
                foreach (var c in new[] { screen[i], (screen[i] + screen[(i + 1) % 4]) * 0.5f })
                {
                    Raylib.DrawRectangleRec(new Rectangle(c.X - 4, c.Y - 4, 8, 8), Color.Black);
                    Raylib.DrawRectangleRec(new Rectangle(c.X - 3, c.Y - 3, 6, 6), Color.White);
                }
            }
        }
        if (state.Lasso is { Count: > 1 } lasso)
        {
            for (int i = 1; i < lasso.Count; i++)
                Raylib.DrawLineEx(offset + lasso[i - 1] * zoom, offset + lasso[i] * zoom, 1.5f, Color.White);
            Raylib.DrawLineV(offset + lasso[^1] * zoom, offset + lasso[0] * zoom, new Color(255, 255, 255, 110));
        }
    }

    private void DrawCursor(EditorState state)
    {
        var cursor = state.Cursor;
        if (!cursor.Visible) return;
        if (cursor.Pencil)
        {
            var rect = new Rectangle(offset.X + cursor.Texel.X * zoom, offset.Y + cursor.Texel.Y * zoom, zoom, zoom);
            Raylib.DrawRectangleLinesEx(new Rectangle(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2), 1, new Color(0, 0, 0, 180));
            Raylib.DrawRectangleLinesEx(rect, 1, Color.White);
        }
        else
        {
            Vector2 center = offset + cursor.TexelCenter * zoom;
            float radius = MathF.Max(cursor.TexelRadius * zoom, 2f);
            Raylib.DrawCircleLinesV(center, radius + 1, new Color(0, 0, 0, 160));
            Raylib.DrawCircleLinesV(center, radius, Color.White);
        }
        if (cursor.MirrorUv)
        {
            Vector2 mirror = offset + cursor.MirrorTexelCenter * zoom;
            float radius = cursor.Pencil ? MathF.Max(zoom * 0.5f, 2f) : MathF.Max(cursor.TexelRadius * zoom, 2f);
            Raylib.DrawCircleLinesV(mirror, radius + 1, new Color(0, 0, 0, 110));
            Raylib.DrawCircleLinesV(mirror, radius, new Color(120, 200, 255, 170));
        }
    }

    private void DrawTexelGrid(Rectangle area)
    {
        byte alpha = (byte)Math.Clamp((zoom - 6f) * 6f, 0f, 60f);
        var color = new Color((byte)0, (byte)0, (byte)0, alpha);
        int x0 = Math.Max(0, (int)(-offset.X / zoom)), x1 = Math.Min((int)TextureSize.X, (int)((width - offset.X) / zoom) + 1);
        int y0 = Math.Max(0, (int)(-offset.Y / zoom)), y1 = Math.Min((int)TextureSize.Y, (int)((height - offset.Y) / zoom) + 1);
        for (int x = x0; x <= x1; x++)
        {
            float sx = offset.X + x * zoom;
            Raylib.DrawLineV(new Vector2(sx, MathF.Max(area.Y, 0)), new Vector2(sx, MathF.Min(area.Y + area.Height, height)), color);
        }
        for (int y = y0; y <= y1; y++)
        {
            float sy = offset.Y + y * zoom;
            Raylib.DrawLineV(new Vector2(MathF.Max(area.X, 0), sy), new Vector2(MathF.Min(area.X + area.Width, width), sy), color);
        }
    }

    private void DrawChecker(Rectangle area)
    {
        // Behind the texture so transparent texels read as transparent.
        Raylib.DrawRectangleRec(area, new Color(90, 90, 90, 255));
        const float cell = 12f;
        var dark = new Color(70, 70, 70, 255);
        float xStart = MathF.Max(area.X, 0), yStart = MathF.Max(area.Y, 0);
        float xEnd = MathF.Min(area.X + area.Width, width), yEnd = MathF.Min(area.Y + area.Height, height);
        for (float y = yStart - ((yStart - area.Y) % cell); y < yEnd; y += cell)
        for (float x = xStart - ((xStart - area.X) % cell); x < xEnd; x += cell)
        {
            if ((((int)((x - area.X) / cell) + (int)((y - area.Y) / cell)) & 1) == 0) continue;
            float cx = MathF.Max(x, area.X), cy = MathF.Max(y, area.Y);
            float cw = MathF.Min(x + cell, xEnd) - cx, ch = MathF.Min(y + cell, yEnd) - cy;
            if (cw > 0 && ch > 0) Raylib.DrawRectangleRec(new Rectangle(cx, cy, cw, ch), dark);
        }
    }

    private void Resize(int w, int h)
    {
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);
        if (w == width && h == height) return;
        if (width > 0)
        {
            // Keep the texture centred where it was when the view changes size.
            offset += new Vector2(w - width, h - height) * 0.5f;
            Raylib.UnloadRenderTexture(target);
        }
        target = Raylib.LoadRenderTexture(w, h);
        width = w;
        height = h;
    }

    public void Dispose()
    {
        if (width > 0) Raylib.UnloadRenderTexture(target);
        width = height = 0;
    }
}
