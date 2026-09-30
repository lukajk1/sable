using System.Numerics;
using PixelPainter.Rendering;
using Raylib_cs;

namespace PixelPainter.Views;

/// <summary>
/// The UV view: the part's texture (point-filtered) with the UV layout drawn over it. MMB drag pans, the wheel
/// zooms around the cursor, Home fits the texture. A texel grid appears once texels are big enough to see.
/// Without a texture, the layout is drawn over an empty 0-1 square.
/// </summary>
public sealed class UvView : IDisposable
{
    public bool PixelGrid = true;
    /// <summary>Also draw, dimmed, the other parts that share the selected part's texture.</summary>
    public bool ShowSiblings = true;

    /// <summary>Texel under the mouse (x right, y down), when over the texture area.</summary>
    public (int X, int Y)? HoverTexel { get; private set; }
    public Vector2 TextureSize { get; private set; } = new(EmptySize);

    private const int EmptySize = 256;
    private RenderTexture2D target;
    private int width, height;
    private Vector2 offset;
    private float zoom = 1f;
    private bool fitPending = true;
    private bool dragging;
    private Vector2 lastSize;

    public Texture2D Texture => target.Texture;

    public void RequestFit() => fitPending = true;

    public void Update(Rectangle rect, bool hovered, GpuModel? model, int selected)
    {
        Resize((int)rect.Width, (int)rect.Height);
        TextureSize = ResolveTexture(model, selected, out _);
        if (TextureSize != lastSize) { fitPending = true; lastSize = TextureSize; }
        if (fitPending) Fit();

        Vector2 local = Raylib.GetMousePosition() - new Vector2(rect.X, rect.Y);

        if (hovered && Raylib.IsMouseButtonPressed(MouseButton.Middle)) dragging = true;
        if (!Raylib.IsMouseButtonDown(MouseButton.Middle)) dragging = false;
        if (dragging) offset += Raylib.GetMouseDelta();

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

        HoverTexel = null;
        if (hovered)
        {
            Vector2 texel = (local - offset) / zoom;
            if (texel.X >= 0 && texel.Y >= 0 && texel.X < TextureSize.X && texel.Y < TextureSize.Y)
                HoverTexel = ((int)texel.X, (int)texel.Y);
        }
    }

    private void Fit()
    {
        if (width <= 0 || height <= 0) return;
        zoom = MathF.Min(width / TextureSize.X, height / TextureSize.Y) * 0.9f;
        offset = new Vector2(width, height) * 0.5f - TextureSize * zoom * 0.5f;
        fitPending = false;
    }

    private static Vector2 ResolveTexture(GpuModel? model, int selected, out int textureIndex)
    {
        textureIndex = -1;
        if (model == null) return new Vector2(EmptySize);
        textureIndex = selected >= 0 ? model.TextureOf(selected) : (model.Textures.Count > 0 ? 0 : -1);
        if (textureIndex < 0) return new Vector2(EmptySize);
        var t = model.Textures[textureIndex];
        return new Vector2(t.Width, t.Height);
    }

    public void Render(GpuModel? model, int selected)
    {
        if (width <= 0 || height <= 0) return;
        Raylib.BeginTextureMode(target);
        Raylib.ClearBackground(new Color(40, 40, 40, 255));

        ResolveTexture(model, selected, out int textureIndex);
        var area = new Rectangle(offset.X, offset.Y, TextureSize.X * zoom, TextureSize.Y * zoom);

        if (textureIndex >= 0)
        {
            DrawChecker(area);
            var texture = model!.Textures[textureIndex];
            Raylib.DrawTexturePro(texture, new Rectangle(0, 0, texture.Width, texture.Height), area, Vector2.Zero, 0, Color.White);
            if (PixelGrid && zoom >= 6f) DrawTexelGrid(area);
        }
        else
        {
            Raylib.DrawRectangleRec(area, new Color(62, 62, 62, 255));
            Raylib.DrawText("no texture", (int)area.X, (int)(area.Y + area.Height) + 6, 10, new Color(120, 120, 120, 255));
        }
        Raylib.DrawRectangleLinesEx(area, 1, new Color(110, 110, 110, 255));

        if (model != null) DrawLayout(model, selected, textureIndex);

        Raylib.EndTextureMode();
    }

    private void DrawLayout(GpuModel model, int selected, int textureIndex)
    {
        var parts = model.Source.Parts;
        for (int i = 0; i < parts.Count; i++)
        {
            if (i == selected || !parts[i].Visible || parts[i].Uvs == null) continue;
            bool sibling = selected >= 0 && textureIndex >= 0 && model.TextureOf(i) == textureIndex;
            if (selected >= 0 && !(ShowSiblings && sibling)) continue;
            DrawPartUvs(model, i, selected >= 0 ? new Color(170, 170, 170, 90) : new Color(225, 225, 225, 190));
        }
        if (selected >= 0 && parts[selected].Uvs != null) DrawPartUvs(model, selected, new Color(255, 160, 40, 255));
    }

    private void DrawPartUvs(GpuModel model, int part, Color color)
    {
        var uvs = model.Source.Parts[part].Uvs!;
        var edges = model.Edges[part];
        Vector2 scale = TextureSize * zoom;
        for (int e = 0; e < edges.Length; e += 2)
        {
            Vector2 a = offset + uvs[edges[e]] * scale;
            Vector2 b = offset + uvs[edges[e + 1]] * scale;
            Raylib.DrawLineV(a, b, color);
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
        bool first = width == 0;
        if (!first)
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
