using System.Numerics;
using PixelPainter.Rendering;
using Raylib_cs;

namespace PixelPainter.Views;

/// <summary>
/// The 3D view, rendered into its own texture. Blender controls while the mouse is over it:
/// MMB orbit, Shift+MMB pan, Ctrl+MMB or wheel zoom (Alt+LMB orbits too, for laptops); numpad 1/3/7 front,
/// right and top (Ctrl for the opposite side), 2/4/6/8 step the orbit, 9 flips, 5 toggles ortho,
/// numpad . or F frames the selection, Home frames everything. A left click selects a part.
/// </summary>
public sealed class Viewport3D : IDisposable
{
    public OrbitCamera Camera { get; } = new();
    public float Shade = 1f;
    public bool Wireframe;
    public bool Grid = true;

    /// <summary>Set when a left click (not a drag) landed in the view; the app resolves it to a part.</summary>
    public Ray? ClickRay { get; private set; }

    private RenderTexture2D target;
    private int width, height;
    private bool dragging;
    private Vector2 pressPosition;
    private bool leftPressedHere;

    public Texture2D Texture => target.Texture;

    public void Update(Rectangle rect, bool hovered, Vector3 sceneMin, Vector3 sceneMax, Vector3? selectionMin, Vector3? selectionMax)
    {
        Resize((int)rect.Width, (int)rect.Height);
        ClickRay = null;
        Vector2 mouse = Raylib.GetMousePosition();

        bool alt = Raylib.IsKeyDown(KeyboardKey.LeftAlt) || Raylib.IsKeyDown(KeyboardKey.RightAlt);
        bool shift = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);
        bool ctrl = Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);

        bool navPressed = Raylib.IsMouseButtonPressed(MouseButton.Middle) || (alt && Raylib.IsMouseButtonPressed(MouseButton.Left));
        if (hovered && navPressed) dragging = true;
        if (!Raylib.IsMouseButtonDown(MouseButton.Middle) && !(alt && Raylib.IsMouseButtonDown(MouseButton.Left))) dragging = false;

        if (dragging)
        {
            Vector2 delta = Raylib.GetMouseDelta();
            if (shift) Camera.Pan(delta, rect.Height);
            else if (ctrl) Camera.Zoom(MathF.Exp(delta.Y * 0.01f));
            else Camera.Orbit(delta);
        }

        // Left click to select: only a click, not a drag, and not the Alt+LMB orbit.
        if (hovered && !alt && Raylib.IsMouseButtonPressed(MouseButton.Left)) { leftPressedHere = true; pressPosition = mouse; }
        if (leftPressedHere && Raylib.IsMouseButtonReleased(MouseButton.Left))
        {
            leftPressedHere = false;
            if (hovered && Vector2.Distance(mouse, pressPosition) < 4f)
            {
                var local = mouse - new Vector2(rect.X, rect.Y);
                ClickRay = Raylib.GetScreenToWorldRayEx(local, Camera.ToRaylib(), width, height);
            }
        }

        if (!hovered) return;

        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0) Camera.Zoom(MathF.Pow(0.85f, wheel));

        if (Raylib.IsKeyPressed(KeyboardKey.Kp1)) Camera.SetView(ctrl ? 180 : 0, 0);
        if (Raylib.IsKeyPressed(KeyboardKey.Kp3)) Camera.SetView(ctrl ? -90 : 90, 0);
        if (Raylib.IsKeyPressed(KeyboardKey.Kp7)) Camera.SetView(0, ctrl ? -90 : 90);
        if (Raylib.IsKeyPressed(KeyboardKey.Kp9)) Camera.Yaw += MathF.PI;
        if (Raylib.IsKeyPressed(KeyboardKey.Kp4)) Camera.Orbit(new Vector2(-15f / 0.008f * MathF.PI / 180f, 0));
        if (Raylib.IsKeyPressed(KeyboardKey.Kp6)) Camera.Orbit(new Vector2(15f / 0.008f * MathF.PI / 180f, 0));
        if (Raylib.IsKeyPressed(KeyboardKey.Kp8)) Camera.Orbit(new Vector2(0, -15f / 0.008f * MathF.PI / 180f));
        if (Raylib.IsKeyPressed(KeyboardKey.Kp2)) Camera.Orbit(new Vector2(0, 15f / 0.008f * MathF.PI / 180f));
        if (Raylib.IsKeyPressed(KeyboardKey.Kp5)) Camera.Ortho = !Camera.Ortho;
        if (Raylib.IsKeyPressed(KeyboardKey.Home)) Camera.Frame(sceneMin, sceneMax);
        if (Raylib.IsKeyPressed(KeyboardKey.KpDecimal) || Raylib.IsKeyPressed(KeyboardKey.F))
        {
            if (selectionMin.HasValue && selectionMax.HasValue) Camera.Frame(selectionMin.Value, selectionMax.Value);
            else Camera.Frame(sceneMin, sceneMax);
        }
    }

    public void Render(GpuModel? model, int selected, LitShader shader)
    {
        if (width <= 0 || height <= 0) return;
        Raylib.BeginTextureMode(target);
        Raylib.ClearBackground(new Color(57, 57, 57, 255));

        float sceneRadius = model == null ? 10f : (model.Source.Max - model.Source.Min).Length() * 0.5f;
        Raylib.BeginMode3D(Camera.ToRaylib());
        Rlgl.SetMatrixProjection(Camera.Projection(width / (float)height, sceneRadius));

        if (Grid) DrawGrid(sceneRadius);

        if (model != null)
        {
            // Light from over the viewer's left shoulder, like Blender's solid view.
            shader.Set(Camera.Forward * 0.7f - Camera.Up * 0.6f + Camera.Right * 0.4f, Shade);
            for (int i = 0; i < model.Source.Parts.Count; i++)
                if (model.Source.Parts[i].Visible) model.DrawPart(i);

            if (Wireframe)
                for (int i = 0; i < model.Source.Parts.Count; i++)
                    if (model.Source.Parts[i].Visible && i != selected) DrawEdges(model, i, new Color(20, 20, 20, 160));
            if (selected >= 0 && model.Source.Parts[selected].Visible) DrawEdges(model, selected, new Color(255, 160, 40, 255));
        }

        Raylib.EndMode3D();
        Raylib.EndTextureMode();
    }

    private void DrawEdges(GpuModel model, int part, Color color)
    {
        var positions = model.Source.Parts[part].Positions;
        var edges = model.Edges[part];
        Vector3 eye = Camera.Position;
        // Nudge lines toward the camera so they win the depth test against their own faces.
        float bias = Camera.Ortho ? 0f : 0.002f;
        Vector3 orthoNudge = Camera.Ortho ? Camera.Back * (Camera.ViewHeight * 0.002f) : Vector3.Zero;
        for (int e = 0; e < edges.Length; e += 2)
        {
            Vector3 a = positions[edges[e]], b = positions[edges[e + 1]];
            a += (eye - a) * bias + orthoNudge;
            b += (eye - b) * bias + orthoNudge;
            Raylib.DrawLine3D(a, b, color);
        }
    }

    private static void DrawGrid(float sceneRadius)
    {
        float spacing = MathF.Pow(10f, MathF.Floor(MathF.Log10(MathF.Max(sceneRadius, 0.01f) / 2f)));
        int half = 20;
        float extent = half * spacing;
        var line = new Color(75, 75, 75, 255);
        var major = new Color(90, 90, 90, 255);
        for (int i = -half; i <= half; i++)
        {
            if (i == 0) continue;
            var c = i % 10 == 0 ? major : line;
            Raylib.DrawLine3D(new Vector3(i * spacing, 0, -extent), new Vector3(i * spacing, 0, extent), c);
            Raylib.DrawLine3D(new Vector3(-extent, 0, i * spacing), new Vector3(extent, 0, i * spacing), c);
        }
        // Blender's axis colours: X red, Y (our -Z) green.
        Raylib.DrawLine3D(new Vector3(-extent, 0, 0), new Vector3(extent, 0, 0), new Color(160, 60, 70, 255));
        Raylib.DrawLine3D(new Vector3(0, 0, -extent), new Vector3(0, 0, extent), new Color(110, 150, 50, 255));
    }

    private void Resize(int w, int h)
    {
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);
        if (w == width && h == height) return;
        if (width > 0) Raylib.UnloadRenderTexture(target);
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
