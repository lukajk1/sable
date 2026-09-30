using System.Numerics;
using PixelPainter.Rendering;
using Raylib_cs;

namespace PixelPainter.Views;

/// <summary>
/// The 3D view, rendered into its own texture. Blender controls while the mouse is over it:
/// MMB orbit, Shift+MMB pan, Ctrl+MMB or wheel zoom (Alt+LMB orbits too, for laptops); 1/3/7 front, right and top
/// (numpad or the number row; Ctrl for the opposite side), 2/4/6/8 step the orbit, 9 flips, 5 toggles ortho,
/// Home frames everything (the app handles framing the selection).
/// </summary>
public sealed class Viewport3D : IDisposable
{
    public OrbitCamera Camera { get; } = new();
    public float Shade = 1f;
    public bool Wireframe;
    public bool Grid = true;

    /// <summary>What orbiting swings around: the selection's centre, set by the app; null orbits the pivot.</summary>
    public Vector3? OrbitCenter;

    public bool Hovered { get; private set; }
    /// <summary>A navigation drag (orbit, pan, zoom) is in progress.</summary>
    public bool Navigating { get; private set; }
    public Vector2 LocalMouse { get; private set; }
    public int Width => width;
    public int Height => height;

    private RenderTexture2D target;
    private int width, height;

    public Texture2D Texture => target.Texture;

    public void Update(Rectangle rect, bool hovered, Vector3 frameMin, Vector3 frameMax)
    {
        Resize((int)rect.Width, (int)rect.Height);
        Hovered = hovered;
        LocalMouse = Raylib.GetMousePosition() - new Vector2(rect.X, rect.Y);

        bool alt = Raylib.IsKeyDown(KeyboardKey.LeftAlt) || Raylib.IsKeyDown(KeyboardKey.RightAlt);
        bool shift = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);
        bool ctrl = Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);

        bool navPressed = Raylib.IsMouseButtonPressed(MouseButton.Middle) || (alt && Raylib.IsMouseButtonPressed(MouseButton.Left));
        if (hovered && navPressed) Navigating = true;
        if (!Raylib.IsMouseButtonDown(MouseButton.Middle) && !(alt && Raylib.IsMouseButtonDown(MouseButton.Left))) Navigating = false;

        if (Navigating)
        {
            Vector2 delta = Raylib.GetMouseDelta();
            if (shift) Camera.Pan(delta, rect.Height);
            else if (ctrl) Camera.Zoom(MathF.Exp(delta.Y * 0.01f));
            else Camera.Orbit(delta, OrbitCenter);
        }

        if (!hovered) return;

        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0) Camera.Zoom(MathF.Pow(0.85f, wheel));

        const float step = 15f / 0.008f * MathF.PI / 180f; // 15 degrees, in Orbit's pixels
        if (ViewKey(KeyboardKey.Kp1, KeyboardKey.One)) Camera.SetView(ctrl ? 180 : 0, 0);
        if (ViewKey(KeyboardKey.Kp3, KeyboardKey.Three)) Camera.SetView(ctrl ? -90 : 90, 0);
        if (ViewKey(KeyboardKey.Kp7, KeyboardKey.Seven)) Camera.SetView(0, ctrl ? -90 : 90);
        if (ViewKey(KeyboardKey.Kp9, KeyboardKey.Nine)) Camera.Yaw += MathF.PI;
        if (ViewKey(KeyboardKey.Kp4, KeyboardKey.Four)) Camera.Orbit(new Vector2(-step, 0), OrbitCenter);
        if (ViewKey(KeyboardKey.Kp6, KeyboardKey.Six)) Camera.Orbit(new Vector2(step, 0), OrbitCenter);
        if (ViewKey(KeyboardKey.Kp8, KeyboardKey.Eight)) Camera.Orbit(new Vector2(0, -step), OrbitCenter);
        if (ViewKey(KeyboardKey.Kp2, KeyboardKey.Two)) Camera.Orbit(new Vector2(0, step), OrbitCenter);
        if (ViewKey(KeyboardKey.Kp5, KeyboardKey.Five)) Camera.Ortho = !Camera.Ortho;
        if (Raylib.IsKeyPressed(KeyboardKey.Home)) Camera.Frame(frameMin, frameMax);
    }

    private static bool ViewKey(KeyboardKey numpad, KeyboardKey row) => Raylib.IsKeyPressed(numpad) || Raylib.IsKeyPressed(row);

    /// <summary>The ray under the mouse, in world space.</summary>
    public Ray MouseRay() => Raylib.GetScreenToWorldRayEx(LocalMouse, Camera.ToRaylib(), width, height);

    public Vector2 WorldToScreen(Vector3 point) => Raylib.GetWorldToScreenEx(point, Camera.ToRaylib(), width, height);

    public void Render(EditorState state, LitShader shader)
    {
        if (width <= 0 || height <= 0) return;
        var model = state.Model;
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
                if (state.PartVisible(i)) model.DrawPart(i);

            DrawSelection(state, model);

            if (state.Cursor.Visible3D && state.Cursor.TexelOutline is { } outline)
            {
                Rlgl.DrawRenderBatchActive();
                Rlgl.DisableDepthTest();
                for (int i = 0; i < outline.Length; i++)
                    Raylib.DrawLine3D(outline[i], outline[(i + 1) % outline.Length], Color.White);
                Rlgl.DrawRenderBatchActive();
                Rlgl.EnableDepthTest();
            }
        }

        Raylib.EndMode3D();

        if (state.Cursor.Visible3D && state.Cursor.TexelOutline == null)
        {
            Raylib.DrawCircleLinesV(state.Cursor.Screen, state.Cursor.ScreenRadius, new Color(0, 0, 0, 160));
            Raylib.DrawCircleLinesV(state.Cursor.Screen, state.Cursor.ScreenRadius + 1, new Color(255, 255, 255, 220));
        }
        Raylib.EndTextureMode();
    }

    private void DrawSelection(EditorState state, GpuModel model)
    {
        var parts = model.Source.Parts;
        var orange = new Color(255, 160, 40, 255);
        for (int i = 0; i < parts.Count; i++)
        {
            if (!state.PartVisible(i)) continue;
            bool active = parts[i].ObjectIndex == state.ActiveObject;
            if (active && state.Mode == SelectMode.Object) DrawEdges(model, i, orange, -1);
            else if (active) DrawEdges(model, i, new Color(10, 10, 10, 150), -1);
            else if (Wireframe) DrawEdges(model, i, new Color(20, 20, 20, 140), -1);
        }
        if (state.Mode == SelectMode.Submesh && state.Submesh is { } sub && state.PartVisible(sub.Part))
            DrawEdges(model, sub.Part, orange, sub.Component);
    }

    /// <summary>Edges of visible submeshes, or of one submesh when <paramref name="onlyComponent"/> >= 0.</summary>
    private void DrawEdges(GpuModel model, int part, Color color, int onlyComponent)
    {
        var source = model.Source.Parts[part];
        var positions = source.Positions;
        var edges = model.Edges[part];
        Vector3 eye = Camera.Position;
        // Nudge lines toward the camera so they win the depth test against their own faces.
        float bias = Camera.Ortho ? 0f : 0.002f;
        Vector3 orthoNudge = Camera.Ortho ? Camera.Back * (Camera.ViewHeight * 0.002f) : Vector3.Zero;
        for (int e = 0; e < edges.A.Length; e++)
        {
            int component = edges.Component[e];
            if (source.ComponentHidden[component]) continue;
            if (onlyComponent >= 0 && component != onlyComponent) continue;
            Vector3 a = positions[edges.A[e]], b = positions[edges.B[e]];
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
