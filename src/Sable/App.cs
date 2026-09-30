using System.Numerics;
using ImGuiNET;
using Sable.Input;
using Sable.Model;
using Sable.Paint;
using Sable.Rendering;
using Sable.UI;
using Sable.Views;
using Raylib_cs;
using rlImGui_cs;

namespace Sable;

/// <summary>
/// The window: a menu bar, a tool and object panel on the left, the 3D view and the UV view side by side (drag the
/// bar between them), and a status bar. Models load on a background thread; drop one on the window or use File > Open.
/// Painting always goes to the one active object, in either view.
/// </summary>
internal sealed class App : IDisposable
{
    private const float PanelWidth = 300f;
    private const float StatusHeight = 26f;
    private const float SplitterWidth = 6f;
    private static readonly int[] TextureSizes = { 16, 32, 64, 128, 256, 512, 1024, 2048 };

    private readonly AppOptions options;
    private readonly EditorState state = new();
    private readonly Viewport3D view3d = new();
    private readonly UvView uvView = new();
    private readonly UndoStack undo = new();
    private LitShader? shader;
    private GpuModel? Model => state.Model;

    // Tools and colour.
    private Tool tool = Tool.Select;
    private float brushSize = 8f;
    private float hardness = 0.5f;
    private float opacity = 1f;
    private float flow = 1f;
    // Pen pressure (Windows Ink): drives flow and optionally size, through a gamma curve.
    private bool pressureToFlow = true;
    private bool pressureToSize;
    private float pressureCurve = 1.6f;
    private float lastPressure = 1f;
    private float dabSize = 8f;
    private Vector3 hsv = new(0.07f, 0.75f, 0.9f);
    private Vector3 hsvAtPickerOpen;
    private bool openPicker, closePicker, pickerOpen;
    private Vector2 pickerPosition;
    private string hexInput = "";
    private int newTextureSize = 64;

    // The stroke in progress.
    private Stroke? stroke;
    private int strokeTexture = -1;
    private bool strokeIn3D;
    private Vector2 lastMouse;
    private (int X, int Y) lastTexel;
    private Vector2 lastDab;
    private float cursorScreenRadius = 4f;

    // Eyedropper (the I tool, or holding Alt).
    private Sample sample;
    private Sample altSample;
    private bool altWasDown;
    private bool eyedropperCursor;
    private Vector2 eyedropperTip;

    // Select-tool click detection in the 3D view.
    private bool selectPressed;
    private Vector2 selectPressPosition;

    private (Vector3, float, float, float, bool)? cameraBeforeLocalView;

    // Loading, status, layout.
    private Task<LoadedModel>? loading;
    private string? loadingPath;
    private volatile string progress = "";
    private string status = "Drop a model on the window, or File > Open (fbx, gltf, glb, obj, blend).";
    private bool statusIsError;
    private float split = 0.5f;
    private bool draggingSplit;
    private float menuHeight = 19f;
    private string title = "";
    private int screenshotFrames = -1;
    private int selfTestStep = -1;
    private bool quit;

    public App(AppOptions options) => this.options = options;

    private Color PaintColor
    {
        get
        {
            var rgb = ColorWheel.HsvToRgb(hsv);
            return new Color((byte)MathF.Round(rgb.X * 255), (byte)MathF.Round(rgb.Y * 255), (byte)MathF.Round(rgb.Z * 255), (byte)255);
        }
    }

    /// <summary>A stroke on a texture: the pencil always at full opacity, the brush at the opacity slider.</summary>
    private Stroke NewStroke(int texture) =>
        new(Model!.Textures[texture], PaintColor, tool == Tool.Brush ? opacity : 1f, MaskFor(texture));

    /// <summary>Pen pressure 0..1 while the pen is drawing; 1 with the mouse.</summary>
    private static float RawPressure =>
        PenInput.PenDetected && Environment.TickCount64 - PenInput.LastPenTime < 300
            ? (PenInput.InContact ? PenInput.Pressure : 0f)
            : 1f;

    /// <summary>
    /// Sets flow and size for the next dab from pressure through the curve (gamma above 1 spends more of the pen's
    /// range on light pressure). The pencil ignores pressure.
    /// </summary>
    private void SetDab(float pressure)
    {
        if (stroke == null) return;
        if (tool != Tool.Brush)
        {
            stroke.Flow = 1f;
            return;
        }
        float p = MathF.Pow(Math.Clamp(pressure, 0f, 1f), pressureCurve);
        stroke.Flow = flow * (pressureToFlow ? p : 1f);
        dabSize = pressureToSize ? MathF.Max(1f, brushSize * (0.15f + 0.85f * p)) : brushSize;
    }

    public void Run()
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint);
        Raylib.InitWindow(1600, 900, "Sable");
        unsafe { PenInput.Attach((IntPtr)Raylib.GetWindowHandle()); }
        Raylib.SetWindowMinSize(900, 560);
        Raylib.SetExitKey(KeyboardKey.Null);
        rlImGui.Setup(true);
        shader = new LitShader();

        if (options.ModelPath != null) StartLoad(options.ModelPath);
        else if (options.ScreenshotPath != null) screenshotFrames = 3;

        while (!Raylib.WindowShouldClose() && !quit)
        {
            FinishLoad();
            HandleDroppedFiles();

            float w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
            var area = new Rectangle(PanelWidth, menuHeight, w - PanelWidth, h - menuHeight - StatusHeight);
            var (rect3d, rectUv, splitter) = SplitArea(area);

            var io = ImGui.GetIO();
            Vector2 mouse = Raylib.GetMousePosition();
            bool free = !io.WantCaptureMouse && !draggingSplit && !pickerOpen;
            UpdateSplitter(area, splitter, mouse, free);

            var (sMin, sMax) = VisibleBounds();
            view3d.OrbitCenter = SelectionCenter();
            view3d.Update(rect3d, free && Raylib.CheckCollisionPointRec(mouse, rect3d), sMin, sMax);
            uvView.Update(rectUv, free && Raylib.CheckCollisionPointRec(mouse, rectUv), state);
            HandleShortcuts();
            UpdateTools(free);
            RunSelfTest();
            Model?.UploadTextures();
            UpdateTitle();

            view3d.Render(state, shader);
            uvView.Render(state);

            Raylib.BeginDrawing();
            Raylib.ClearBackground(new Color(30, 30, 30, 255));
            DrawTarget(view3d.Texture, rect3d);
            DrawTarget(uvView.Texture, rectUv);
            Raylib.DrawRectangleRec(splitter, draggingSplit || Raylib.CheckCollisionPointRec(mouse, splitter)
                ? new Color(90, 90, 90, 255) : new Color(22, 22, 22, 255));
            DrawViewLabel(rect3d, $"3D  {(view3d.Camera.Ortho ? "ortho" : "persp")}  {state.Mode}{(state.Isolated != null ? "  local view" : "")}");
            DrawViewLabel(rectUv, state.ActiveTexture >= 0 && Model != null ? $"UV  {Model.Textures[state.ActiveTexture].Name}" : "UV");

            rlImGui.Begin();
            DrawMenu();
            DrawPanel(h);
            DrawStatusBar(w, h);
            DrawColorPicker();
            DrawEyedropper();
            rlImGui.End();
            UpdateSystemCursor();

            TakeScreenshotIfDue();
            Raylib.EndDrawing();
        }
    }

    // ---------- layout ----------

    private (Rectangle view3d, Rectangle uv, Rectangle splitter) SplitArea(Rectangle area)
    {
        float left = MathF.Round(area.Width * split - SplitterWidth * 0.5f);
        var r3d = new Rectangle(area.X, area.Y, left, area.Height);
        var bar = new Rectangle(area.X + left, area.Y, SplitterWidth, area.Height);
        var ruv = new Rectangle(area.X + left + SplitterWidth, area.Y, area.Width - left - SplitterWidth, area.Height);
        return (r3d, ruv, bar);
    }

    private void UpdateSplitter(Rectangle area, Rectangle bar, Vector2 mouse, bool free)
    {
        bool over = free && stroke == null && Raylib.CheckCollisionPointRec(mouse, bar);
        if (over && Raylib.IsMouseButtonPressed(MouseButton.Left)) draggingSplit = true;
        if (!Raylib.IsMouseButtonDown(MouseButton.Left)) draggingSplit = false;
        if (draggingSplit) split = Math.Clamp((mouse.X - area.X) / area.Width, 0.15f, 0.85f);
        Raylib.SetMouseCursor(over || draggingSplit ? MouseCursor.ResizeEw : MouseCursor.Default);
    }

    private static void DrawTarget(Texture2D texture, Rectangle rect) =>
        // Render textures are stored upside down.
        Raylib.DrawTextureRec(texture, new Rectangle(0, 0, rect.Width, -rect.Height), new Vector2(rect.X, rect.Y), Color.White);

    private static void DrawViewLabel(Rectangle rect, string text) =>
        Raylib.DrawText(text, (int)rect.X + 8, (int)rect.Y + 6, 10, new Color(160, 160, 160, 255));

    private (Vector3 Min, Vector3 Max) VisibleBounds()
    {
        if (Model == null) return (new Vector3(-5), new Vector3(5));
        var objects = Model.Source.Objects;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < objects.Count; i++)
        {
            if (!state.ObjectVisible(i)) continue;
            min = Vector3.Min(min, objects[i].Min);
            max = Vector3.Max(max, objects[i].Max);
        }
        return min.X > max.X ? (Model.Source.Min, Model.Source.Max) : (min, max);
    }

    /// <summary>What numpad . / F frames: the selected submesh, else the active object, else everything shown.</summary>
    private (Vector3 Min, Vector3 Max) SelectionBounds()
    {
        if (Model != null && state.Mode == SelectMode.Submesh && state.Submesh is { } sub)
        {
            var part = Model.Source.Parts[sub.Part];
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int t = 0; t < part.TriangleCount; t++)
            {
                if (part.TriangleComponent[t] != sub.Component) continue;
                for (int k = 0; k < 3; k++)
                {
                    var p = part.Positions[part.Indices[t * 3 + k]];
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            return (min, max);
        }
        if (Model != null && state.ActiveObject >= 0)
            return (Model.Source.Objects[state.ActiveObject].Min, Model.Source.Objects[state.ActiveObject].Max);
        return VisibleBounds();
    }

    /// <summary>Centre of the selected submesh or active object, for orbiting around; null with nothing selected.</summary>
    private Vector3? SelectionCenter()
    {
        if (Model == null || state.ActiveObject < 0 || !state.ObjectVisible(state.ActiveObject)) return null;
        var (min, max) = SelectionBounds();
        return (min + max) * 0.5f;
    }

    // ---------- loading and saving ----------

    private void StartLoad(string path)
    {
        if (loading != null) return;
        if (!ModelLoader.IsSupported(path))
        {
            SetStatus($"Not a supported model: {Path.GetFileName(path)}", error: true);
            return;
        }
        loadingPath = path;
        progress = "Loading...";
        loading = Task.Run(() => ModelLoader.Load(path, message => progress = message));
    }

    private void FinishLoad()
    {
        if (loading is not { IsCompleted: true }) return;
        var task = loading;
        loading = null;

        if (task.IsFaulted)
        {
            var error = task.Exception!.InnerException ?? task.Exception;
            SetStatus($"Couldn't open {Path.GetFileName(loadingPath)}: {error.Message}", error: true);
            if (options.ScreenshotPath != null) screenshotFrames = 3;
            return;
        }

        EndStroke();
        Model?.Dispose();
        undo.Clear();
        state.Model = new GpuModel(task.Result, shader!.Shader);
        state.ClearSelection();
        state.Isolated = null;
        state.Mode = SelectMode.Object;
        cameraBeforeLocalView = null;
        state.ActiveTexture = Model!.Textures.Count > 0 ? 0 : -1;

        if (options.SelectPart != null)
        {
            int found = Model.Source.Objects.FindIndex(o => o.Name.Contains(options.SelectPart, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) SelectObject(found);
        }
        view3d.Camera.Frame(Model.Source.Min, Model.Source.Max);
        uvView.RequestFit();

        var s = Model.Source;
        SetStatus($"Opened {s.Name}: {s.Objects.Count} objects, {s.Parts.Sum(p => p.TriangleCount)} tris, {s.Textures.Count} textures"
                  + (s.Warnings.Count > 0 ? $", {s.Warnings.Count} warnings" : ""), error: false);
        if (options.SelfTest) selfTestStep = 0;
        else if (options.ScreenshotPath != null) screenshotFrames = 4;
    }

    private void HandleDroppedFiles()
    {
        if (!Raylib.IsFileDropped()) return;
        string[] files = Raylib.GetDroppedFiles();
        string? first = files.FirstOrDefault(ModelLoader.IsSupported);
        if (first != null) StartLoad(first);
        else if (files.Length > 0) SetStatus($"Not a supported model: {Path.GetFileName(files[0])}", error: true);
    }

    private void OpenDialog()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Open model",
            Filter = "Models|*.fbx;*.gltf;*.glb;*.obj;*.blend;*.dae;*.3ds;*.ply|All files|*.*",
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) StartLoad(dialog.FileName);
    }

    private void Reload()
    {
        if (Model != null) StartLoad(Model.Source.SourcePath);
    }

    /// <summary>
    /// Writes every changed texture: back to its own file when it has one (for a .blend, the image file the .blend
    /// uses), otherwise to "&lt;model&gt;_&lt;texture&gt;.png" next to the model.
    /// </summary>
    private void SaveAll()
    {
        if (Model == null) return;
        EndStroke();
        var saved = new List<string>();
        foreach (var texture in Model.Textures.Where(t => t.Dirty))
        {
            string path = texture.FilePath ?? DefaultSavePath(texture);
            try
            {
                texture.Save(path);
                saved.Add(Path.GetFileName(path));
            }
            catch (Exception e)
            {
                SetStatus($"Couldn't save {texture.Name}: {e.Message}", error: true);
                return;
            }
        }
        SetStatus(saved.Count == 0 ? "Nothing to save." : $"Saved {string.Join(", ", saved)}", error: false);
    }

    private string DefaultSavePath(PaintTexture texture)
    {
        string source = Model!.Source.SourcePath;
        string name = Path.GetFileNameWithoutExtension(texture.Name);
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Replace(' ', '_').Replace("(", "").Replace(")", "");
        return Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileNameWithoutExtension(source)}_{name}.png");
    }

    private void SetStatus(string text, bool error)
    {
        status = text;
        statusIsError = error;
    }

    private void UpdateTitle()
    {
        string next = Model == null ? "Sable"
            : $"Sable - {Model.Source.Name}{(Model.Textures.Any(t => t.Dirty) ? " *" : "")}";
        if (next == title) return;
        title = next;
        Raylib.SetWindowTitle(title);
    }

    // ---------- keyboard ----------

    private void HandleShortcuts()
    {
        if (ImGui.GetIO().WantCaptureKeyboard) return;
        bool ctrl = Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);
        bool shift = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);
        bool alt = Raylib.IsKeyDown(KeyboardKey.LeftAlt) || Raylib.IsKeyDown(KeyboardKey.RightAlt);
        bool Pressed(KeyboardKey key) => Raylib.IsKeyPressed(key) || Raylib.IsKeyPressedRepeat(key);

        if (ctrl)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.O)) OpenDialog();
            if (Raylib.IsKeyPressed(KeyboardKey.R)) Reload();
            if (Raylib.IsKeyPressed(KeyboardKey.S)) SaveAll();
            if (Pressed(KeyboardKey.Z)) { EndStroke(); if (shift) undo.Redo(); else undo.Undo(); }
            if (Pressed(KeyboardKey.Y)) { EndStroke(); undo.Redo(); }
            if (Raylib.IsKeyPressed(KeyboardKey.D)) state.Selection = null;
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.D)) { if (pickerOpen) closePicker = true; else { openPicker = true; pickerPosition = Raylib.GetMousePosition(); } }
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            if (pickerOpen) closePicker = true;
            else if (state.Selection != null) state.Selection = null;
            else if (state.Mode == SelectMode.Submesh && state.Submesh != null) state.Submesh = null;
            else state.ClearSelection();
        }
        if (pickerOpen) return;

        if (Raylib.IsKeyPressed(KeyboardKey.Tab)) ToggleMode();
        if (Raylib.IsKeyPressed(KeyboardKey.V)) tool = Tool.Select;
        if (Raylib.IsKeyPressed(KeyboardKey.N)) tool = Tool.Pencil;
        if (Raylib.IsKeyPressed(KeyboardKey.B)) tool = Tool.Brush;
        if (Raylib.IsKeyPressed(KeyboardKey.I)) tool = Tool.Eyedropper;
        if (Raylib.IsKeyPressed(KeyboardKey.X)) tool = Tool.Lasso;
        if (Pressed(KeyboardKey.W)) brushSize = brushSize < 4 ? brushSize + 1 : MathF.Min(MathF.Round(brushSize * 1.25f), 256);
        if (Pressed(KeyboardKey.Q)) brushSize = brushSize <= 4 ? MathF.Max(brushSize - 1, 1) : MathF.Round(brushSize / 1.25f);
        if (Raylib.IsKeyPressed(KeyboardKey.Z)) view3d.Wireframe = !view3d.Wireframe;
        if (Raylib.IsKeyPressed(KeyboardKey.H)) { if (alt) Reveal(); else if (shift) HideUnselected(); else HideSelected(); }
        if (Raylib.IsKeyPressed(KeyboardKey.Slash) || Raylib.IsKeyPressed(KeyboardKey.KpDivide)) ToggleLocalView();
        if (view3d.Hovered && (Raylib.IsKeyPressed(KeyboardKey.F) || Raylib.IsKeyPressed(KeyboardKey.KpDecimal)))
        {
            var (min, max) = SelectionBounds();
            view3d.Camera.Frame(min, max);
        }
    }

    private void ToggleMode()
    {
        state.Mode = state.Mode == SelectMode.Object ? SelectMode.Submesh : SelectMode.Object;
        state.Submesh = null;
    }

    // ---------- selection, hiding, local view ----------

    private void SelectObject(int index)
    {
        state.ActiveObject = index;
        state.Submesh = null;
        if (index < 0 || Model == null) return;
        int texture = FirstTextureOf(index);
        if (texture >= 0) state.ActiveTexture = texture;
    }

    private int FirstTextureOf(int objectIndex)
    {
        foreach (int p in Model!.Source.Objects[objectIndex].Parts)
            if (Model.TextureOf(p) >= 0 && Model.Source.Parts[p].HasUvs) return Model.TextureOf(p);
        return -1;
    }

    private void SelectAt(SurfaceHit? hit)
    {
        if (Model == null) return;
        if (hit is not { } h)
        {
            if (state.Mode == SelectMode.Submesh) state.Submesh = null;
            else state.ClearSelection();
            return;
        }
        var part = Model.Source.Parts[h.Part];
        if (part.ObjectIndex != state.ActiveObject) SelectObject(part.ObjectIndex);
        if (state.Mode == SelectMode.Submesh) state.Submesh = (h.Part, part.TriangleComponent[h.Triangle]);
        if (Model.TextureOf(h.Part) >= 0) state.ActiveTexture = Model.TextureOf(h.Part);
    }

    private void HideSelected()
    {
        if (Model == null) return;
        if (state.Mode == SelectMode.Object)
        {
            if (state.ActiveObject < 0) return;
            Model.Source.Objects[state.ActiveObject].Hidden = true;
            if (state.Isolated == state.ActiveObject) ToggleLocalView();
            state.ClearSelection();
        }
        else if (state.Submesh is { } sub)
        {
            Model.Source.Parts[sub.Part].ComponentHidden[sub.Component] = true;
            Model.RebuildPart(sub.Part);
            state.Submesh = null;
        }
    }

    private void HideUnselected()
    {
        if (Model == null || state.ActiveObject < 0) return;
        if (state.Mode == SelectMode.Object)
        {
            for (int i = 0; i < Model.Source.Objects.Count; i++)
                if (i != state.ActiveObject) Model.Source.Objects[i].Hidden = true;
            return;
        }
        if (state.Submesh is not { } sub) return;
        foreach (int p in Model.Source.Objects[state.ActiveObject].Parts)
        {
            var part = Model.Source.Parts[p];
            for (int c = 0; c < part.ComponentCount; c++)
                part.ComponentHidden[c] = !(p == sub.Part && c == sub.Component);
            Model.RebuildPart(p);
        }
    }

    /// <summary>Alt+H: object mode reveals hidden objects; submesh mode, the active object's hidden submeshes.</summary>
    private void Reveal()
    {
        if (Model == null) return;
        if (state.Mode == SelectMode.Object)
        {
            foreach (var obj in Model.Source.Objects) obj.Hidden = false;
            return;
        }
        var objects = state.ActiveObject >= 0 ? new[] { state.ActiveObject } : Enumerable.Range(0, Model.Source.Objects.Count).ToArray();
        foreach (int o in objects)
        foreach (int p in Model.Source.Objects[o].Parts)
        {
            var hidden = Model.Source.Parts[p].ComponentHidden;
            if (!hidden.Any(x => x)) continue;
            Array.Fill(hidden, false);
            Model.RebuildPart(p);
        }
    }

    private void ToggleLocalView()
    {
        if (Model == null) return;
        if (state.Isolated != null)
        {
            state.Isolated = null;
            if (cameraBeforeLocalView is { } saved) view3d.Camera.Restore(saved);
            cameraBeforeLocalView = null;
            return;
        }
        if (state.ActiveObject < 0) return;
        state.Isolated = state.ActiveObject;
        cameraBeforeLocalView = view3d.Camera.Save();
        var obj = Model.Source.Objects[state.ActiveObject];
        view3d.Camera.Frame(obj.Min, obj.Max);
    }

    // ---------- tools ----------

    private void UpdateTools(bool free)
    {
        state.Cursor = default;
        sample = default;
        eyedropperCursor = false;
        eyedropperTip = Raylib.GetMousePosition();
        bool alt = Raylib.IsKeyDown(KeyboardKey.LeftAlt) || Raylib.IsKeyDown(KeyboardKey.RightAlt);
        try
        {
            UpdateTools(free, alt);
        }
        finally
        {
            // Holding Alt samples without clicking: letting go takes the last texel it was over.
            if (alt && sample.Valid) altSample = sample;
            if (!alt && altWasDown && altSample.Valid) SetColor(altSample.Color);
            if (!alt) altSample = default;
            altWasDown = alt;
        }
    }

    private void UpdateTools(bool free, bool alt)
    {
        if (Model == null) return;
        var source = Model.Source;
        bool painting = tool is Tool.Pencil or Tool.Brush;
        // Holding Alt turns any tool into the eyedropper until it's released.
        bool sampling = (tool == Tool.Eyedropper || alt) && stroke == null && lassoDrag == LassoDrag.None;
        bool pressed = Raylib.IsMouseButtonPressed(MouseButton.Left);

        UpdateLasso(free, pressed && !sampling);

        if (stroke != null)
        {
            if (!Raylib.IsMouseButtonDown(MouseButton.Left)) EndStroke();
            else if (strokeIn3D) Continue3D();
            else ContinueUv();
        }

        // 3D view.
        if (view3d.Hovered && !view3d.Navigating && free)
        {
            SurfaceHit hit = default;
            bool hasHit = false;
            if (painting || sampling)
            {
                var ray = view3d.MouseRay();
                Func<int, bool> filter = sampling ? state.ObjectVisible : i => i == state.ActiveObject && state.ObjectVisible(i);
                hasHit = Raycast.Cast(source, ray.Position, ray.Direction, filter, out hit);
            }

            if (sampling)
            {
                eyedropperCursor = true;
                if (hasHit) sample = SampleAt(hit);
            }
            if (painting && !sampling && hasHit) Show3DCursor(hit);
            if (pressed && stroke == null)
            {
                if (sampling) { if (sample.Valid) SetColor(sample.Color); }
                else if (painting)
                {
                    if (state.ActiveObject < 0) SetStatus("Select an object to paint (V, then click it).", error: true);
                    else if (hasHit) Begin3D(hit);
                }
                else if (tool == Tool.Select) { selectPressed = true; selectPressPosition = view3d.LocalMouse; }
                else if (tool == Tool.Lasso) SetStatus("The lasso works in the UV view.", error: false);
            }
        }

        if (selectPressed && Raylib.IsMouseButtonReleased(MouseButton.Left))
        {
            selectPressed = false;
            if (view3d.Hovered && Vector2.Distance(view3d.LocalMouse, selectPressPosition) < 4f)
            {
                var ray = view3d.MouseRay();
                SelectAt(Raycast.Cast(source, ray.Position, ray.Direction, state.ObjectVisible, out var hit) ? hit : null);
            }
        }

        // UV view.
        if (uvView.Hovered && free && state.ActiveTexture >= 0)
        {
            bool canPaint = state.ActiveObject >= 0;
            if (sampling)
            {
                eyedropperCursor = true;
                if (uvView.MouseOnTexture)
                {
                    var tex = Model.Textures[state.ActiveTexture];
                    int x = (int)uvView.MouseTexel.X, y = (int)uvView.MouseTexel.Y;
                    sample = new Sample { Valid = true, Texture = state.ActiveTexture, X = x, Y = y, Color = tex.Get(x, y) };
                }
            }
            if (painting && !sampling && canPaint && stroke == null) ShowUvCursor();
            if (pressed && stroke == null)
            {
                if (sampling) { if (sample.Valid) SetColor(sample.Color); }
                else if (painting && canPaint) BeginUv();
                else if (painting) SetStatus("Select an object to paint (V, then click it).", error: true);
                else if (tool == Tool.Select && state.Mode == SelectMode.Submesh) SelectInUv();
            }
        }
        if (stroke != null && !strokeIn3D && tool is Tool.Pencil or Tool.Brush) ShowUvCursor();
        if (sampling && uvView.Hovered && free && state.ActiveTexture < 0) eyedropperCursor = true;
    }

    // ---------- lasso ----------

    private enum LassoDrag { None, Drawing, Moving }
    private LassoDrag lassoDrag;
    private SelectionOp lassoOp;
    private SelectionMove? selectionMove;
    private Vector2 moveStart;

    /// <summary>
    /// The lasso, in the UV view: drag to draw a selection (Shift adds, Ctrl subtracts); drag inside it to move the
    /// selected texels (Ctrl+drag moves a copy); a click outside it deselects.
    /// </summary>
    private void UpdateLasso(bool free, bool pressed)
    {
        var mouse = uvView.MouseTexel;
        if (lassoDrag == LassoDrag.Drawing && state.Lasso != null)
        {
            if (Raylib.IsMouseButtonDown(MouseButton.Left))
            {
                if (Vector2.Distance(state.Lasso[^1], mouse) >= 0.35f) state.Lasso.Add(mouse);
            }
            else
            {
                FinishLasso();
            }
            return;
        }
        if (lassoDrag == LassoDrag.Moving && selectionMove != null && state.Selection != null)
        {
            if (Raylib.IsMouseButtonDown(MouseButton.Left))
            {
                selectionMove.MoveTo((int)MathF.Round(mouse.X - moveStart.X), (int)MathF.Round(mouse.Y - moveStart.Y), state.Selection);
            }
            else
            {
                if (selectionMove.Finish() is { } step) undo.Push(step);
                selectionMove = null;
                lassoDrag = LassoDrag.None;
            }
            return;
        }

        if (tool != Tool.Lasso || !pressed || !free || !uvView.Hovered || state.ActiveTexture < 0 || Model == null) return;
        bool shift = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);
        bool ctrl = Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);
        var selection = state.ActiveSelection;
        if (selection != null && !shift && selection.Contains((int)MathF.Floor(mouse.X), (int)MathF.Floor(mouse.Y)))
        {
            selectionMove = new SelectionMove(Model.Textures[state.ActiveTexture], selection, duplicate: ctrl);
            selectionMove.MoveTo(0, 0, selection);
            moveStart = mouse;
            lassoDrag = LassoDrag.Moving;
            return;
        }
        lassoOp = shift ? SelectionOp.Add : ctrl ? SelectionOp.Subtract : SelectionOp.Replace;
        state.Lasso = new List<Vector2> { mouse };
        lassoDrag = LassoDrag.Drawing;
    }

    private void FinishLasso()
    {
        var polygon = state.Lasso!;
        state.Lasso = null;
        lassoDrag = LassoDrag.None;

        float extent = 0;
        foreach (var p in polygon) extent = MathF.Max(extent, Vector2.Distance(p, polygon[0]));
        if (polygon.Count < 3 || extent < 0.75f)
        {
            // A click rather than a drag: deselect.
            if (lassoOp == SelectionOp.Replace) state.Selection = null;
            return;
        }

        var tex = Model!.Textures[state.ActiveTexture];
        if (lassoOp == SelectionOp.Replace || state.ActiveSelection == null)
        {
            if (lassoOp == SelectionOp.Subtract) return;
            state.Selection = new TexelSelection(state.ActiveTexture, tex.Width, tex.Height);
        }
        state.Selection!.Apply(polygon, lassoOp);
        if (!state.Selection.Any) state.Selection = null;
    }

    /// <summary>The selection mask for strokes on <paramref name="texture"/>: paint stays inside it.</summary>
    private bool[]? MaskFor(int texture) =>
        state.Selection is { Any: true } s && s.Texture == texture ? s.Mask : null;

    private void Show3DCursor(SurfaceHit hit)
    {
        var part = Model!.Source.Parts[hit.Part];
        int texture = Model.TextureOf(hit.Part);
        if (texture < 0 || part.Uvs == null) return;
        if (stroke == null) state.ActiveTexture = texture;
        var tex = Model.Textures[texture];
        var size = new Vector2(tex.Width, tex.Height);
        Vector2 uv = Raycast.UvAt(part, hit.Triangle, hit.Barycentric) * size;

        state.Cursor.Visible = texture == state.ActiveTexture;
        state.Cursor.Visible3D = true;
        if (tool == Tool.Pencil)
        {
            var texel = ((int)MathF.Floor(uv.X), (int)MathF.Floor(uv.Y));
            state.Cursor.Pencil = true;
            state.Cursor.Texel = (tex.Wrap(texel.Item1, tex.Width), tex.Wrap(texel.Item2, tex.Height));
            state.Cursor.TexelOutline = TexelOutline(part, hit, texel, size);
            cursorScreenRadius = 1f;
        }
        else
        {
            float radius = brushSize * 0.5f * Raycast.TexelWorldSize(part, hit.Triangle, size);
            Vector2 center = view3d.WorldToScreen(hit.Point);
            Vector2 edge = view3d.WorldToScreen(hit.Point + view3d.Camera.Right * radius);
            state.Cursor.Screen = center;
            state.Cursor.ScreenRadius = cursorScreenRadius = MathF.Max(Vector2.Distance(center, edge), 2f);
            state.Cursor.TexelCenter = new Vector2(tex.Wrap((int)MathF.Floor(uv.X), tex.Width) + (uv.X - MathF.Floor(uv.X)),
                tex.Wrap((int)MathF.Floor(uv.Y), tex.Height) + (uv.Y - MathF.Floor(uv.Y)));
            state.Cursor.TexelRadius = brushSize * 0.5f;
        }
    }

    /// <summary>The pencil texel's square mapped onto the hit triangle's plane, lifted off the surface a hair.</summary>
    private Vector3[] TexelOutline(MeshPart part, SurfaceHit hit, (int X, int Y) texel, Vector2 size)
    {
        var i = part.Indices;
        int t = hit.Triangle;
        Vector2 ua = part.Uvs![i[t * 3]] * size, ub = part.Uvs[i[t * 3 + 1]] * size, uc = part.Uvs[i[t * 3 + 2]] * size;
        float lift = Raycast.TexelWorldSize(part, t, size) * 0.05f;
        var corners = new[] { new Vector2(texel.X, texel.Y), new Vector2(texel.X + 1, texel.Y), new Vector2(texel.X + 1, texel.Y + 1), new Vector2(texel.X, texel.Y + 1) };
        return corners.Select(c => Raycast.PositionAt(part, t, Raycast.Barycentric2D(c, ua, ub, uc)) + hit.Normal * lift).ToArray();
    }

    private void ShowUvCursor()
    {
        var tex = Model!.Textures[state.ActiveTexture];
        state.Cursor.Visible = true;
        if (tool == Tool.Pencil)
        {
            state.Cursor.Pencil = true;
            state.Cursor.Texel = ((int)MathF.Floor(uvView.MouseTexel.X), (int)MathF.Floor(uvView.MouseTexel.Y));
            if (!uvView.MouseOnTexture) state.Cursor.Visible = false;
        }
        else
        {
            state.Cursor.TexelCenter = Brush.SnapCenter(uvView.MouseTexel, brushSize);
            state.Cursor.TexelRadius = brushSize * 0.5f;
        }
    }

    private void Begin3D(SurfaceHit hit)
    {
        int texture = Model!.TextureOf(hit.Part);
        if (texture < 0 || !Model.Source.Parts[hit.Part].HasUvs)
        {
            SetStatus(Model.Source.Parts[hit.Part].HasUvs
                ? "This part has no texture yet: use New texture in the panel."
                : "This part has no UVs: unwrap it in Blender first.", error: true);
            return;
        }
        state.ActiveTexture = texture;
        strokeTexture = texture;
        strokeIn3D = true;
        stroke = NewStroke(texture);
        lastPressure = RawPressure;
        SetDab(lastPressure);
        Dab3D(hit);
        lastMouse = view3d.LocalMouse;
    }

    private void Continue3D()
    {
        Vector2 to = view3d.LocalMouse;
        float spacing = tool == Tool.Pencil ? 1f : MathF.Max(1f, cursorScreenRadius * 0.3f);
        float distance = Vector2.Distance(lastMouse, to);
        if (distance < spacing) return;
        int steps = (int)(distance / spacing);
        var camera = view3d.Camera.ToRaylib();
        float pressure = RawPressure;
        for (int s = 1; s <= steps; s++)
        {
            SetDab(float.Lerp(lastPressure, pressure, s / (float)steps));
            Vector2 p = Vector2.Lerp(lastMouse, to, s / (float)steps);
            var ray = Raylib.GetScreenToWorldRayEx(p, camera, view3d.Width, view3d.Height);
            if (Raycast.Cast(Model!.Source, ray.Position, ray.Direction, i => i == state.ActiveObject && state.ObjectVisible(i), out var hit)
                && Model.TextureOf(hit.Part) == strokeTexture)
                Dab3D(hit);
        }
        lastMouse = to;
        lastPressure = pressure;
    }

    private void Dab3D(SurfaceHit hit)
    {
        if (tool == Tool.Pencil)
        {
            var tex = stroke!.Texture;
            var (x, y) = Brush.TexelAt(Model!.Source, hit, new Vector2(tex.Width, tex.Height));
            stroke.Apply(x, y, 1f);
        }
        else
        {
            Brush.DabSurface(stroke!, Model!.Source, state.ActiveObject, strokeTexture, hit, dabSize, hardness, Model.TextureOf);
        }
    }

    private void BeginUv()
    {
        strokeTexture = state.ActiveTexture;
        strokeIn3D = false;
        stroke = NewStroke(strokeTexture);
        lastPressure = RawPressure;
        SetDab(lastPressure);
        if (tool == Tool.Pencil)
        {
            lastTexel = ((int)MathF.Floor(uvView.MouseTexel.X), (int)MathF.Floor(uvView.MouseTexel.Y));
            Brush.Line(stroke, lastTexel.X, lastTexel.Y, lastTexel.X, lastTexel.Y, clip: true);
        }
        else
        {
            lastDab = Brush.SnapCenter(uvView.MouseTexel, dabSize);
            Brush.DabTexels(stroke, lastDab, dabSize, hardness);
        }
    }

    private void ContinueUv()
    {
        if (tool == Tool.Pencil)
        {
            var texel = ((int)MathF.Floor(uvView.MouseTexel.X), (int)MathF.Floor(uvView.MouseTexel.Y));
            if (texel == lastTexel) return;
            Brush.Line(stroke!, lastTexel.X, lastTexel.Y, texel.Item1, texel.Item2, clip: true);
            lastTexel = texel;
            return;
        }
        Vector2 center = Brush.SnapCenter(uvView.MouseTexel, brushSize);
        float spacing = MathF.Max(0.5f, brushSize * 0.25f);
        float distance = Vector2.Distance(lastDab, center);
        if (distance < spacing) return;
        int steps = (int)MathF.Ceiling(distance / spacing);
        float pressure = RawPressure;
        for (int s = 1; s <= steps; s++)
        {
            SetDab(float.Lerp(lastPressure, pressure, s / (float)steps));
            Brush.DabTexels(stroke!, Brush.SnapCenter(Vector2.Lerp(lastDab, center, s / (float)steps), dabSize), dabSize, hardness);
        }
        lastDab = center;
        lastPressure = pressure;
    }

    private void EndStroke()
    {
        if (stroke == null) return;
        if (stroke.Finish() is { } step) undo.Push(step);
        stroke = null;
    }

    private void SelectInUv()
    {
        if (state.ActiveObject < 0) return;
        var tex = Model!.Textures[state.ActiveTexture];
        Vector2 p = uvView.MouseTexel / new Vector2(tex.Width, tex.Height);
        foreach (int partIndex in Model.Source.Objects[state.ActiveObject].Parts)
        {
            var part = Model.Source.Parts[partIndex];
            if (part.Uvs == null || Model.TextureOf(partIndex) != state.ActiveTexture) continue;
            for (int t = 0; t < part.TriangleCount; t++)
            {
                if (!part.TriangleVisible(t)) continue;
                var b = Raycast.Barycentric2D(p, part.Uvs[part.Indices[t * 3]], part.Uvs[part.Indices[t * 3 + 1]], part.Uvs[part.Indices[t * 3 + 2]]);
                if (b.X < 0 || b.Y < 0 || b.Z < 0) continue;
                state.Submesh = (partIndex, part.TriangleComponent[t]);
                return;
            }
        }
        state.Submesh = null;
    }

    /// <summary>What the eyedropper is over: a texel of a texture, or (Texture = -1) a plain material colour.</summary>
    private struct Sample
    {
        public bool Valid;
        public int Texture;
        public int X, Y;
        public Color Color;
    }

    // Eyedropper: always the unlit colour, from the texture (or the material colour where there is none).
    private Sample SampleAt(SurfaceHit hit)
    {
        int texture = Model!.TextureOf(hit.Part);
        var part = Model.Source.Parts[hit.Part];
        if (texture >= 0 && part.HasUvs)
        {
            var tex = Model.Textures[texture];
            var (x, y) = Brush.TexelAt(Model.Source, hit, new Vector2(tex.Width, tex.Height));
            x = tex.Wrap(x, tex.Width);
            y = tex.Wrap(y, tex.Height);
            return new Sample { Valid = true, Texture = texture, X = x, Y = y, Color = tex.Get(x, y) };
        }
        return new Sample { Valid = true, Texture = -1, Color = GpuModel.ToColor(Model.Source.Materials[part.MaterialIndex].Color with { W = 1 }) };
    }

    private void SetColor(Color c) => hsv = ColorWheel.RgbToHsv(new Vector3(c.R, c.G, c.B) / 255f, hsv.X);

    /// <summary>Shows the system cursor, or hides it while the eyedropper draws its own.</summary>
    private void UpdateSystemCursor()
    {
        if (eyedropperCursor == Raylib.IsCursorHidden()) return;
        if (eyedropperCursor) Raylib.HideCursor();
        else Raylib.ShowCursor();
    }

    /// <summary>
    /// The eyedropper's pipette cursor (tip on the hot spot, bulb filled with the colour under it) and, beside it, a
    /// loupe: the texels around the sampled one, enlarged, with the sample and the current colour side by side.
    /// </summary>
    private void DrawEyedropper()
    {
        if (!eyedropperCursor) return;
        var draw = ImGui.GetForegroundDrawList();
        Vector2 tip = eyedropperTip;
        uint black = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 1));
        uint white = ImGui.ColorConvertFloat4ToU32(Vector4.One);
        uint U32(Color c) => ImGui.ColorConvertFloat4ToU32(new Vector4(c.R, c.G, c.B, 255f) / 255f);

        // Pipette, pointing down-left at the hot spot.
        Vector2 dir = Vector2.Normalize(new Vector2(1, -1));
        Vector2 neck = tip + dir * 7f, top = tip + dir * 24f, bulb = tip + dir * 30f;
        draw.AddLine(tip, neck, black, 5f);
        draw.AddLine(neck, top, black, 10f);
        draw.AddLine(tip + dir * 0.5f, neck, white, 2f);
        draw.AddLine(neck, top, white, 6f);
        draw.AddCircleFilled(bulb, 8f, black);
        draw.AddCircleFilled(bulb, 6f, sample.Valid ? U32(sample.Color) : white);

        if (!sample.Valid) return;

        const int span = 5;                // texels either side of the sampled one
        const float cell = 12f;
        const int count = span * 2 + 1;
        float gridSize = count * cell;
        float boxHeight = gridSize + 34f;
        Vector2 at = tip + new Vector2(20f, 16f);
        if (at.X + gridSize + 8 > Raylib.GetScreenWidth()) at.X = tip.X - 20f - gridSize;
        if (at.Y + boxHeight + 8 > Raylib.GetScreenHeight()) at.Y = tip.Y - 16f - boxHeight;

        draw.AddRectFilled(at - new Vector2(4), at + new Vector2(gridSize + 4, boxHeight), ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.1f, 0.1f, 0.95f)), 4f);
        if (sample.Texture >= 0)
        {
            var tex = Model!.Textures[sample.Texture];
            for (int dy = -span; dy <= span; dy++)
            for (int dx = -span; dx <= span; dx++)
            {
                Vector2 min = at + new Vector2((dx + span) * cell, (dy + span) * cell);
                draw.AddRectFilled(min, min + new Vector2(cell), U32(tex.Get(sample.X + dx, sample.Y + dy)));
            }
            Vector2 c0 = at + new Vector2(span * cell);
            draw.AddRect(c0 - new Vector2(1), c0 + new Vector2(cell + 1), black, 0, ImDrawFlags.None, 2f);
            draw.AddRect(c0, c0 + new Vector2(cell), white, 0, ImDrawFlags.None, 1f);
        }
        else
        {
            draw.AddRectFilled(at, at + new Vector2(gridSize), U32(sample.Color));
            draw.AddText(at + new Vector2(6, 6), white, "material");
        }

        // New (sample) | current.
        Vector2 swatch = at + new Vector2(0, gridSize + 5);
        float halfWidth = gridSize * 0.5f;
        draw.AddRectFilled(swatch, swatch + new Vector2(halfWidth, 12), U32(sample.Color));
        draw.AddRectFilled(swatch + new Vector2(halfWidth, 0), swatch + new Vector2(gridSize, 12), U32(PaintColor));
        var c = sample.Color;
        string label = sample.Texture >= 0 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}  {sample.X},{sample.Y}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        draw.AddText(swatch + new Vector2(0, 14), white, label);
    }

    // ---------- UI ----------

    private void DrawMenu()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        menuHeight = ImGui.GetWindowHeight();
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open...", "Ctrl+O")) OpenDialog();
            if (ImGui.MenuItem("Reload", "Ctrl+R", false, Model != null)) Reload();
            if (ImGui.MenuItem("Save textures", "Ctrl+S", false, Model != null)) SaveAll();
            ImGui.Separator();
            if (ImGui.MenuItem("Quit")) quit = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", "Ctrl+Z", false, undo.CanUndo)) undo.Undo();
            if (ImGui.MenuItem("Redo", "Ctrl+Shift+Z", false, undo.CanRedo)) undo.Redo();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            ImGui.MenuItem("Wireframe", "Z", ref view3d.Wireframe);
            ImGui.MenuItem("Grid", null, ref view3d.Grid);
            bool ortho = view3d.Camera.Ortho;
            if (ImGui.MenuItem("Orthographic", "5", ref ortho)) view3d.Camera.Ortho = ortho;
            if (ImGui.MenuItem("Local view", "/", state.Isolated != null, Model != null && (state.ActiveObject >= 0 || state.Isolated != null))) ToggleLocalView();
            ImGui.Separator();
            ImGui.MenuItem("UV texel grid", null, ref uvView.PixelGrid);
            ImGui.MenuItem("UV: show other objects on the texture", null, ref uvView.ShowSiblings);
            ImGui.Separator();
            if (ImGui.MenuItem("Frame all", "Home", false, Model != null)) { var (min, max) = VisibleBounds(); view3d.Camera.Frame(min, max); }
            ImGui.EndMenu();
        }
        ImGui.EndMainMenuBar();
    }

    private void DrawPanel(float screenHeight)
    {
        ImGui.SetNextWindowPos(new Vector2(0, menuHeight));
        ImGui.SetNextWindowSize(new Vector2(PanelWidth, screenHeight - menuHeight - StatusHeight));
        ImGui.Begin("##panel", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
                               | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBringToFrontOnFocus);

        DrawToolSection();
        ImGui.Separator();

        if (Model == null)
        {
            ImGui.TextWrapped(loading != null ? progress : "No model loaded.");
            ImGui.End();
            return;
        }

        var source = Model.Source;
        ImGui.TextUnformatted(source.Name);
        ImGui.TextDisabled(Path.GetDirectoryName(source.SourcePath) ?? "");
        if (source.ImportedPath != source.SourcePath) ImGui.TextDisabled("(converted with Blender)");

        if (ImGui.RadioButton("Object", state.Mode == SelectMode.Object) && state.Mode != SelectMode.Object) ToggleMode();
        ImGui.SameLine();
        if (ImGui.RadioButton("Submesh", state.Mode == SelectMode.Submesh) && state.Mode != SelectMode.Submesh) ToggleMode();
        ImGui.SameLine();
        ImGui.TextDisabled("(Tab)");

        if (ImGui.CollapsingHeader($"Objects ({source.Objects.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.BeginChild("##objects", new Vector2(0, 220), ImGuiChildFlags.Borders);
            for (int i = 0; i < source.Objects.Count; i++)
            {
                var obj = source.Objects[i];
                bool visible = !obj.Hidden;
                if (ImGui.Checkbox($"##vis{i}", ref visible)) obj.Hidden = !visible;
                ImGui.SameLine();
                bool hasUvs = obj.Parts.Any(p => source.Parts[p].HasUvs);
                if (!hasUvs) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.45f, 1f));
                int hiddenPieces = obj.Parts.Sum(p => source.Parts[p].ComponentHidden.Count(x => x));
                string label = hiddenPieces > 0 ? $"{obj.Name}  ({hiddenPieces} hidden)" : obj.Name;
                if (ImGui.Selectable($"{label}##obj{i}", state.ActiveObject == i)) SelectObject(state.ActiveObject == i ? -1 : i);
                if (!hasUvs) ImGui.PopStyleColor();
            }
            ImGui.EndChild();
        }

        if (state.ActiveObject >= 0 && ImGui.CollapsingHeader("Active object", ImGuiTreeNodeFlags.DefaultOpen))
            DrawActiveObject();

        if (Model.Textures.Count > 0 && ImGui.CollapsingHeader($"Textures ({Model.Textures.Count})"))
        {
            for (int i = 0; i < Model.Textures.Count; i++)
            {
                var t = Model.Textures[i];
                if (ImGui.Selectable($"{t.Name}{(t.Dirty ? " *" : "")}  {t.Width}x{t.Height}##tex{i}", state.ActiveTexture == i)) state.ActiveTexture = i;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(t.FilePath ?? $"not saved yet; would go to {DefaultSavePath(t)}");
            }
        }

        if (source.Warnings.Count > 0 && ImGui.CollapsingHeader($"Warnings ({source.Warnings.Count})"))
            foreach (string warning in source.Warnings) ImGui.TextWrapped(warning);

        ImGui.End();
    }

    private void DrawToolSection()
    {
        float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        void ToolButton(string label, Tool value, string key)
        {
            bool active = tool == value;
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            if (ImGui.Button($"{label}  ({key})", new Vector2(half, 0))) tool = value;
            if (active) ImGui.PopStyleColor();
        }
        ToolButton("Select", Tool.Select, "V");
        ImGui.SameLine();
        ToolButton("Pencil 1px", Tool.Pencil, "N");
        ToolButton("Brush", Tool.Brush, "B");
        ImGui.SameLine();
        ToolButton("Eyedropper", Tool.Eyedropper, "I");
        ToolButton("Lasso (UV)", Tool.Lasso, "X");
        if (state.ActiveSelection != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Deselect (Ctrl+D)", new Vector2(half, 0))) state.Selection = null;
        }

        var rgb = ColorWheel.HsvToRgb(hsv);
        if (ImGui.ColorButton("##color", new Vector4(rgb, 1f), ImGuiColorEditFlags.NoTooltip, new Vector2(40, 22)))
        {
            openPicker = true;
            pickerPosition = ImGui.GetMousePos();
        }
        ImGui.SameLine();
        var c = PaintColor;
        ImGui.TextUnformatted($"#{c.R:X2}{c.G:X2}{c.B:X2}   D: colour picker");

        ImGui.SetNextItemWidth(150);
        ImGui.SliderFloat("Size (Q/W)", ref brushSize, 1f, 128f, "%.0f", ImGuiSliderFlags.Logarithmic);
        brushSize = MathF.Max(1f, MathF.Round(brushSize));
        ImGui.SetNextItemWidth(150);
        ImGui.SliderFloat("Hardness", ref hardness, 0f, 1f, "%.2f");
        // Percent sliders on a log scale: most of their travel goes to the low values that washes need.
        float opacityPercent = opacity * 100f, flowPercent = flow * 100f;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderFloat("Opacity (brush)", ref opacityPercent, 1f, 100f, "%.0f%%", ImGuiSliderFlags.Logarithmic)) opacity = opacityPercent / 100f;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The most one stroke can reach.");
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderFloat("Flow", ref flowPercent, 1f, 100f, "%.0f%%", ImGuiSliderFlags.Logarithmic)) flow = flowPercent / 100f;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("How much each dab adds. Low flow builds up as you go over a spot, up to the opacity.");
        DrawPressureSection();
        ImGui.SetNextItemWidth(150);
        ImGui.SliderFloat("Lighting", ref view3d.Shade, 0f, 1f, view3d.Shade <= 0 ? "flat" : "%.2f");
    }

    private void DrawPressureSection()
    {
        ImGui.Checkbox("Pressure: flow", ref pressureToFlow);
        ImGui.SameLine();
        ImGui.Checkbox("size", ref pressureToSize);
        ImGui.SetNextItemWidth(150);
        ImGui.SliderFloat("Pressure curve", ref pressureCurve, 0.4f, 3f, pressureCurve >= 1f ? "%.2f (soft)" : "%.2f (firm)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Above 1: more of the pen's range goes to light pressure, for washes; full pressure still reaches full.\nBelow 1: reaches full strength sooner.");
        if (PenInput.PenDetected)
        {
            float shown = PenInput.InContact ? MathF.Pow(PenInput.Pressure, pressureCurve) : 0f;
            ImGui.ProgressBar(shown, new Vector2(150, 0), $"pen {PenInput.Pressure:0.00}");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped("No pen seen yet. Wacom: tick \"Use Windows Ink\" in Wacom Tablet Properties.");
            ImGui.PopStyleColor();
        }
    }

    private void DrawActiveObject()
    {
        var source = Model!.Source;
        var obj = source.Objects[state.ActiveObject];
        ImGui.TextUnformatted(obj.Name);
        int pieces = obj.Parts.Sum(p => source.Parts[p].ComponentCount);
        ImGui.TextDisabled($"{obj.Parts.Sum(p => source.Parts[p].TriangleCount)} tris, {pieces} submeshes");

        foreach (int p in obj.Parts)
        {
            var part = source.Parts[p];
            var material = source.Materials[part.MaterialIndex];
            ImGui.PushID(p);
            ImGui.ColorButton("##mat", material.Color, ImGuiColorEditFlags.NoTooltip, new Vector2(12, 12));
            ImGui.SameLine();
            ImGui.TextUnformatted(material.Name);
            if (!part.HasUvs)
            {
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.45f, 1f), "  No UVs: unwrap it in Blender to paint.");
            }
            else if (material.TextureIndex >= 0)
            {
                var t = Model.Textures[material.TextureIndex];
                ImGui.TextDisabled($"  {t.Name} {t.Width}x{t.Height}{(t.Dirty ? " (unsaved)" : "")}");
            }
            else
            {
                ImGui.SetNextItemWidth(70);
                if (ImGui.BeginCombo("##size", newTextureSize.ToString()))
                {
                    foreach (int size in TextureSizes)
                        if (ImGui.Selectable(size.ToString(), size == newTextureSize)) newTextureSize = size;
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                if (ImGui.Button("New texture"))
                {
                    state.ActiveTexture = Model.CreateTexture(part.MaterialIndex, newTextureSize);
                    SetStatus($"Created {Model.Textures[state.ActiveTexture].Name}; Ctrl+S saves it to {DefaultSavePath(Model.Textures[state.ActiveTexture])}", error: false);
                }
            }
            ImGui.PopID();
        }
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped("H hide, Shift+H hide others, Alt+H reveal, / local view. Hold Alt over a colour and let go to pick it.");
        ImGui.PopStyleColor();
    }

    private void DrawColorPicker()
    {
        if (openPicker)
        {
            openPicker = false;
            hsvAtPickerOpen = hsv;
            ImGui.SetNextWindowPos(pickerPosition, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
            ImGui.OpenPopup("##picker");
        }
        pickerOpen = ImGui.BeginPopup("##picker", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
        if (!pickerOpen) return;

        ColorWheel.Draw("##wheel", ref hsv, 230f);

        var now = ColorWheel.HsvToRgb(hsv);
        var before = ColorWheel.HsvToRgb(hsvAtPickerOpen);
        ImGui.ColorButton("##new", new Vector4(now, 1f), ImGuiColorEditFlags.NoTooltip, new Vector2(56, 24));
        ImGui.SameLine(0, 0);
        if (ImGui.ColorButton("##old", new Vector4(before, 1f), ImGuiColorEditFlags.NoTooltip, new Vector2(56, 24))) hsv = hsvAtPickerOpen;
        ImGui.SameLine();
        var c = PaintColor;
        if (!ImGui.IsAnyItemActive()) hexInput = $"{c.R:X2}{c.G:X2}{c.B:X2}";
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputText("##hex", ref hexInput, 7, ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.EnterReturnsTrue)
            && hexInput.Length == 6)
        {
            int v = Convert.ToInt32(hexInput, 16);
            SetColor(new Color((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)255));
        }

        if (closePicker) ImGui.CloseCurrentPopup();
        closePicker = false;
        ImGui.EndPopup();
    }

    private void DrawStatusBar(float screenWidth, float screenHeight)
    {
        ImGui.SetNextWindowPos(new Vector2(0, screenHeight - StatusHeight));
        ImGui.SetNextWindowSize(new Vector2(screenWidth, StatusHeight));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 4));
        ImGui.Begin("##status", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
                                | ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.PopStyleVar();

        string text = loading != null ? progress : status;
        if (statusIsError && loading == null) ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), text);
        else ImGui.TextUnformatted(text);

        string right = uvView.Hovered && uvView.MouseOnTexture
            ? $"texel {(int)uvView.MouseTexel.X}, {(int)uvView.MouseTexel.Y}   ({uvView.TextureSize.X:0}x{uvView.TextureSize.Y:0})"
            : "MMB orbit  Shift+MMB pan  Wheel zoom  1/3/7 views  5 ortho  / local  Tab submesh  H hide";
        float width = ImGui.CalcTextSize(right).X;
        ImGui.SameLine(MathF.Max(screenWidth - width - 12f, ImGui.GetCursorPosX() + 20f));
        ImGui.TextDisabled(right);
        ImGui.End();
    }

    // ---------- checks without a human ----------

    /// <summary>
    /// --selftest: selects the object, frames it, paints a brush dab and a pencil texel through the middle of the 3D
    /// view and a pencil line in the UV view, then takes the screenshot. Nothing is saved.
    /// </summary>
    private void RunSelfTest()
    {
        if (selfTestStep < 0 || Model == null) return;
        var source = Model.Source;
        switch (selfTestStep++)
        {
            case 0:
                if (state.ActiveObject < 0)
                    SelectObject(source.Objects.FindIndex(o => o.Parts.Any(p => Model.TextureOf(p) >= 0 && source.Parts[p].HasUvs)));
                if (state.ActiveObject >= 0)
                {
                    var obj = source.Objects[state.ActiveObject];
                    view3d.Camera.Frame(obj.Min, obj.Max);
                    foreach (int p in obj.Parts)
                        if (source.Parts[p].HasUvs && Model.TextureOf(p) < 0)
                            state.ActiveTexture = Model.CreateTexture(source.Parts[p].MaterialIndex, 64);
                }
                foreach (var obj in source.Objects)
                    Console.WriteLine($"[selftest] {obj.Name}: {obj.Parts.Sum(p => source.Parts[p].ComponentCount)} submeshes");
                break;
            case 1:
            {
                if (state.ActiveObject < 0) { Console.WriteLine("[selftest] no paintable object"); break; }
                var center = new Vector2(view3d.Width, view3d.Height) * 0.5f;
                var camera = view3d.Camera.ToRaylib();
                bool Hit(Vector2 p, out SurfaceHit h)
                {
                    var ray = Raylib.GetScreenToWorldRayEx(p, camera, view3d.Width, view3d.Height);
                    return Raycast.Cast(source, ray.Position, ray.Direction, i => i == state.ActiveObject, out h);
                }

                tool = Tool.Brush; brushSize = 9; hardness = 0.5f; hsv = new Vector3(0f, 1f, 1f);
                if (Hit(center, out var hit)) { Begin3D(hit); EndStroke(); Console.WriteLine($"[selftest] brush dab on part {hit.Part}"); }
                else Console.WriteLine("[selftest] brush ray missed");

                tool = Tool.Pencil; hsv = new Vector3(0.33f, 1f, 1f);
                if (Hit(center + new Vector2(view3d.Height * 0.08f, 0), out hit)) { Begin3D(hit); EndStroke(); Console.WriteLine("[selftest] pencil texel painted"); }

                if (state.ActiveTexture >= 0)
                {
                    tool = Tool.Pencil; hsv = new Vector3(0.6f, 1f, 1f);
                    stroke = new Stroke(Model.Textures[state.ActiveTexture], PaintColor);
                    Brush.Line(stroke, 2, 2, 30, 14, clip: true);
                    EndStroke();
                    Console.WriteLine("[selftest] UV line painted");

                    // Lasso a triangle, move it right (leaving a hole), then a masked dab that must stay inside it.
                    var tex = Model.Textures[state.ActiveTexture];
                    var selection = new TexelSelection(state.ActiveTexture, tex.Width, tex.Height);
                    selection.Apply(new[] { new Vector2(6, 30), new Vector2(26, 30), new Vector2(10, 50) }, SelectionOp.Replace);
                    state.Selection = selection;
                    int selected = selection.Mask.Count(m => m);
                    var move = new SelectionMove(tex, selection, duplicate: false);
                    move.MoveTo(0, 0, selection);
                    move.MoveTo(12, 0, selection);
                    if (move.Finish() is { } moveStep) undo.Push(moveStep);
                    stroke = new Stroke(tex, new Color(255, 255, 0, 255), mask: MaskFor(state.ActiveTexture));
                    Brush.DabTexels(stroke, new Vector2(24, 38), 30, 1f);
                    EndStroke();
                    int yellowOutside = 0;
                    for (int i = 0; i < tex.Pixels.Length; i++)
                        if (!selection.Mask[i] && tex.Pixels[i] is { R: 255, G: 255, B: 0 }) yellowOutside++;
                    Console.WriteLine($"[selftest] lasso selected {selected} texels, moved to {selection.Mask.Count(m => m)}; masked dab texels outside selection: {yellowOutside}");
                }

                // Small soft brushes should lay down partial colour, not solid texels.
                foreach (float testSize in new[] { 1f, 2f })
                {
                    using var probe = PaintTexture.Create("probe", 8, 8, new Color(0, 0, 0, 255));
                    var probeStroke = new Stroke(probe, new Color(255, 255, 255, 255));
                    Brush.DabTexels(probeStroke, Brush.SnapCenter(new Vector2(4.2f, 4.2f), testSize), testSize, 0.5f);
                    var rows = Enumerable.Range(3, 3).Select(y => string.Join(" ", Enumerable.Range(3, 3).Select(x => probe.Get(x, y).R.ToString().PadLeft(3))));
                    Console.WriteLine($"[selftest] size {testSize} soft dab, grey 0-255 around the centre: {string.Join(" | ", rows)}");
                }

                // Flow builds up over repeated dabs within a stroke, capped by the stroke's opacity.
                {
                    using var probe = PaintTexture.Create("probe", 8, 8, new Color(0, 0, 0, 255));
                    var probeStroke = new Stroke(probe, new Color(255, 255, 255, 255), opacity: 0.6f) { Flow = 0.25f };
                    var levels = new List<int>();
                    for (int dab = 0; dab < 8; dab++)
                    {
                        Brush.DabTexels(probeStroke, new Vector2(4.5f, 4.5f), 3, 1f);
                        levels.Add(probe.Get(4, 4).R);
                    }
                    Console.WriteLine($"[selftest] flow 25% opacity 60%, centre after each dab: {string.Join(", ", levels)} (cap {0.6f * 255:0})");
                }

                tool = Tool.Brush;
                Console.WriteLine($"[selftest] undo available: {undo.CanUndo}; dirty textures: {Model.Textures.Count(t => t.Dirty)}");
                break;
            }
            case 2:
                // Show the colour picker in the screenshot too.
                openPicker = true;
                pickerPosition = new Vector2(PanelWidth + 150, menuHeight + 170);
                screenshotFrames = options.ScreenshotPath != null ? 3 : -1;
                break;
            default:
            {
                // Until the screenshot: the eyedropper, as if Alt were held with the mouse mid-3D-view.
                selfTestStep = 3;
                var local = new Vector2(view3d.Width, view3d.Height) * 0.5f;
                var ray = Raylib.GetScreenToWorldRayEx(local, view3d.Camera.ToRaylib(), view3d.Width, view3d.Height);
                if (Raycast.Cast(source, ray.Position, ray.Direction, state.ObjectVisible, out var hit)) sample = SampleAt(hit);
                eyedropperCursor = true;
                eyedropperTip = new Vector2(PanelWidth, menuHeight) + local;
                break;
            }
        }
    }

    private void TakeScreenshotIfDue()
    {
        if (options.ScreenshotPath == null || screenshotFrames < 0) return;
        if (screenshotFrames-- > 0) return;
        Image image = Raylib.LoadImageFromScreen();
        Raylib.ExportImage(image, options.ScreenshotPath);
        Raylib.UnloadImage(image);
        quit = true;
    }

    public void Dispose()
    {
        Model?.Dispose();
        view3d.Dispose();
        uvView.Dispose();
        shader?.Dispose();
        if (Raylib.IsWindowReady())
        {
            PenInput.Detach();
            rlImGui.Shutdown();
            Raylib.CloseWindow();
        }
    }
}
