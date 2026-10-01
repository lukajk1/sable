using System.Numerics;
using ImGuiNET;
using Sable.Diagnostics;
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
internal sealed partial class App : IDisposable
{
    private const float PanelWidth = 300f;
    private const float StatusHeight = 26f;
    private const float SplitterWidth = 6f;

    private readonly AppOptions options;
    private readonly EditorState state = new();
    private readonly Viewport3D view3d = new();
    private readonly UvView uvView = new();
    private readonly UndoStack undo = new();
    private readonly FrameProfiler profiler = new();
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
    private float fillTolerance;
    private bool fillContiguous = true;
    // Scrubby zoom: drag right to zoom in, left to zoom out, about where the drag started.
    private enum ZoomDrag { None, View3D, Uv }
    private ZoomDrag zoomDrag;
    private Vector3 zoomFocus;
    private Vector2 zoomAnchor;
    private float lastZoomX, zoomTravel;
    private Rectangle uvRect;
    /// <summary>Texture sampling in the 3D view; the UV view always shows exact texels.</summary>
    private TextureView textureView = TextureView.Pixel;
    private float dabSize = 8f;
    private Vector3 hsv = new(0.07f, 0.75f, 0.9f);
    private Vector3 hsvAtPickerOpen;
    private bool openPicker, closePicker, pickerOpen;
    private Vector2 pickerPosition;
    private string hexInput = "";

    // The stroke in progress.
    private Stroke? stroke;
    private int strokeTexture = -1;
    private bool strokeIn3D;
    private Vector2 lastMouse;
    private (int X, int Y) lastTexel;
    private Vector2 lastDab;
    private float cursorScreenRadius = 4f;
    /// <summary>Shown beside the cursor while it's over a part that can't be painted (and why).</summary>
    private string? hoverNote;

    // Eyedropper (the I tool, or holding Alt).
    private Sample sample;
    private Sample altSample;
    private bool altWasDown;
    private CursorIcon cursorIcon;
    private bool selfTestIcons;
    private Vector2 cursorTip;

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
    private int framesRun;

    public App(AppOptions options)
    {
        this.options = options;
        textureView = (TextureView)options.TextureView;
    }

    // The pointer the tools follow this frame: the pen's own reports while the pen is in use (they arrive
    // without Windows' tap/drag threshold and between frames), otherwise the mouse.
    private Vector2 pointer;
    private bool usingPen, pointerDown, pointerPressed, pointerReleased;
    private readonly List<PenInput.Sample> penSamples = new();

    private void UpdatePointer()
    {
        penSamples.Clear();
        PenInput.Drain(penSamples);
        bool wasDown = pointerDown;
        usingPen = PenInput.PenDetected
                   && (PenInput.InContact || penSamples.Count > 0 || Environment.TickCount64 - PenInput.LastPenTime < 250);
        if (usingPen)
        {
            pointer = penSamples.Count > 0 ? penSamples[^1].Position : PenInput.Position;
            pointerDown = PenInput.InContact;
            // A tap can start and end between two frames; the samples still show it touched down.
            bool touched = pointerDown || penSamples.Exists(p => p.InContact);
            pointerPressed = !wasDown && touched;
            pointerReleased = (wasDown || pointerPressed) && !pointerDown;
        }
        else
        {
            pointer = Raylib.GetMousePosition();
            pointerDown = Raylib.IsMouseButtonDown(MouseButton.Left);
            pointerPressed = Raylib.IsMouseButtonPressed(MouseButton.Left);
            pointerReleased = Raylib.IsMouseButtonReleased(MouseButton.Left);
        }
    }

    /// <summary>The points a stroke passes through this frame, with pressure: every pen sample, or the mouse.</summary>
    private IEnumerable<(Vector2 Point, float Pressure)> StrokePoints()
    {
        if (!usingPen)
        {
            yield return (pointer, 1f);
            yield break;
        }
        foreach (var sample in penSamples)
            if (sample.InContact) yield return (sample.Position, sample.Pressure);
    }

    private Color PaintColor
    {
        get
        {
            var rgb = ColorWheel.HsvToRgb(hsv);
            return new Color((byte)MathF.Round(rgb.X * 255), (byte)MathF.Round(rgb.Y * 255), (byte)MathF.Round(rgb.Z * 255), (byte)255);
        }
    }

    /// <summary>A stroke on a texture's active layer: the pencil always at full opacity, the brush and eraser at the opacity slider.</summary>
    private Stroke NewStroke(int texture)
    {
        if (tool != Tool.Eraser) palette.Remember(ColorWheel.HsvToRgb(hsv));
        return new(Model!.Textures[texture], PaintColor, tool is Tool.Brush or Tool.Eraser ? opacity : 1f, MaskFor(texture), erase: tool == Tool.Eraser);
    }

    /// <summary>Pen pressure 0..1 while the pen is drawing; 1 with the mouse.</summary>
    private float RawPressure => usingPen ? (PenInput.InContact ? PenInput.Pressure : 0f) : 1f;

    /// <summary>
    /// Sets flow and size for the next dab from pressure through the curve (gamma above 1 spends more of the pen's
    /// range on light pressure). The pencil ignores pressure.
    /// </summary>
    private void SetDab(float pressure)
    {
        if (stroke == null) return;
        if (tool is not (Tool.Brush or Tool.Eraser))
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
        SetWindowIcon();
        Raylib.SetWindowMinSize(900, 560);
        ApplySettings();
        Raylib.SetExitKey(KeyboardKey.Null);
        rlImGui.Setup(true);
        // No imgui.ini: Sable keeps its own settings, and the install folder may not be writable.
        unsafe { ImGui.GetIO().NativePtr->IniFilename = null; }
        shader = new LitShader();
        // Keeps the garbage collector from stopping everything for a full collection mid-stroke.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        if (options.ModelPath != null) StartLoad(options.ModelPath);
        else if (options.ScreenshotPath != null) screenshotFrames = 3;

        while (!Raylib.WindowShouldClose() && !quit)
        {
            FinishLoad();
            UpdateLink();
            HandleDroppedFiles();
            profiler.Mark("load");

            float w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
            var area = new Rectangle(PanelWidth, menuHeight, w - PanelWidth, h - menuHeight - StatusHeight);
            var (rect3d, rectUv, splitter) = SplitArea(area);
            uvRect = rectUv;

            var io = ImGui.GetIO();
            UpdatePointer();
            Vector2 mouse = Raylib.GetMousePosition();
            bool free = !io.WantCaptureMouse && !draggingSplit && !pickerOpen;
            UpdateSplitter(area, splitter, mouse, free);

            var (sMin, sMax) = VisibleBounds();
            view3d.OrbitCenter = SelectionCenter();
            view3d.Update(rect3d, free && Raylib.CheckCollisionPointRec(pointer, rect3d), pointer, pointerDown, sMin, sMax);
            uvView.Update(rectUv, free && Raylib.CheckCollisionPointRec(pointer, rectUv), pointer, state);
            profiler.Mark("views");
            HandleShortcuts();
            UpdateTools(free);
            RunSelfTest();
            RunBench();
            profiler.Mark("tools");
            Model?.UploadTextures();
            UpdateTitle();
            profiler.Mark("upload");

            SetTextureView(textureView);
            view3d.Render(state, shader);
            profiler.Mark("3d");
            SetTextureView(TextureView.Pixel);
            uvView.Render(state);
            profiler.Mark("uv");

            Raylib.BeginDrawing();
            Raylib.ClearBackground(new Color(30, 30, 30, 255));
            DrawTarget(view3d.Texture, rect3d);
            DrawTarget(uvView.Texture, rectUv);
            Raylib.DrawRectangleRec(splitter, draggingSplit || Raylib.CheckCollisionPointRec(mouse, splitter)
                ? new Color(90, 90, 90, 255) : new Color(22, 22, 22, 255));
            DrawViewLabel(rect3d, $"3D  {(view3d.Camera.Ortho ? "ortho" : "persp")}  {state.Mode}{(state.Isolated != null ? "  local view" : "")}");
            DrawViewLabel(rectUv, state.ActiveTexture >= 0 && Model != null ? $"UV  {Model.Textures[state.ActiveTexture].Name}" : "UV");

            rlImGui.Begin();
            // Tab is Sable's Object/Submesh key, so ImGui must not use it to hop between widgets.
            ImGui.PushItemFlag(ImGuiItemFlags.NoTabStop, true);
            DrawMenu();
            DrawPanel(h);
            DrawStatusBar(w, h);
            DrawUvToolbar();
            DrawLayersWindow();
            PruneThumbnails();
            DrawOpenPathPopup();
            fileBrowser.Draw(new Vector2(w, h));
            DrawNewTexturePopup();
            DrawResizePopup();
            DrawColorPicker();
            DrawToolCursor();
            DrawSmoothingString();
            ImGui.PopItemFlag();
            rlImGui.End();
            UpdateSystemCursor();

            TakeScreenshotIfDue();
            profiler.Mark("ui");
            Raylib.EndDrawing();
            profiler.Mark("present");
            profiler.EndFrame(FrameContext());
            if (options.QuitAfterFrames > 0 && ++framesRun >= options.QuitAfterFrames) quit = true;
        }
    }

    /// <summary>What was going on, for the hitch log.</summary>
    private string FrameContext()
    {
        string where = stroke == null ? "no stroke" : strokeIn3D ? "stroke in 3D" : "stroke in UV";
        string texture = state.ActiveTexture >= 0 && Model != null
            ? $"{Model.Textures[state.ActiveTexture].Width}x{Model.Textures[state.ActiveTexture].Height}" : "none";
        return $"{tool}, {where}, size {brushSize:0}, texture {texture}, pen {(PenInput.PenDetected ? "yes" : "no")}";
    }

    private void SetTextureView(TextureView mode)
    {
        if (Model == null) return;
        foreach (var texture in Model.Textures) texture.SetView(mode);
    }

    private static readonly string[] TextureViewNames = { "Pixel (nearest)", "Smooth (bilinear)", "Smooth + mipmaps" };

    /// <summary>The title bar and taskbar icon, from the PNG embedded in the exe.</summary>
    private static void SetWindowIcon()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("Sable.icon.png");
        if (stream == null) return;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Image icon = Raylib.LoadImageFromMemory(".png", memory.ToArray());
        Raylib.ImageFormat(ref icon, PixelFormat.UncompressedR8G8B8A8);
        Raylib.SetWindowIcon(icon);
        Raylib.UnloadImage(icon);
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

        profiler.Skip();
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
        if (state.ActiveObject >= 0)
            view3d.Camera.Frame(Model.Source.Objects[state.ActiveObject].Min, Model.Source.Objects[state.ActiveObject].Max);
        else
            view3d.Camera.Frame(Model.Source.Min, Model.Source.Max);
        uvView.RequestFit();

        var s = Model.Source;
        SetStatus($"Opened {s.Name}: {s.Objects.Count} objects, {s.Parts.Sum(p => p.TriangleCount)} tris, {s.Textures.Count} textures"
                  + (s.Warnings.Count > 0 ? $", {s.Warnings.Count} warnings" : ""), error: false);
        LoadLayers(Model!.Textures);
        if (options.Bench) benchStep = 0;
        else if (options.SelfTest) selfTestStep = 0;
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

    // A file dialog open in the helper process, and what to do with its answer.
    private readonly FileBrowser fileBrowser = new();
    private readonly Palette palette = new();
    private bool openPathPopup;
    private string pathInput = "";

    private string? ModelDirectory => Model != null ? Path.GetDirectoryName(Model.Source.SourcePath) : null;

    /// <summary>Asks for a file with Sable's own browser, then runs <paramref name="then"/> on it (errors go to the status bar).</summary>
    private void Pick(bool save, string title, string filter, string? fileName, Action<string> then)
    {
        if (fileBrowser.IsOpen) return;
        fileBrowser.Open(save, title, filter, ModelDirectory, fileName, path =>
        {
            try { then(path); }
            catch (Exception e) { SetStatus(e.Message, error: true); }
        });
    }

    private void OpenDialog() => Pick(false, "Open model", FileBrowser.ModelFilter, null, StartLoad);

    /// <summary>Replaces the active texture's pixels with an image file; it still saves to its own file.</summary>
    private void ImportImage()
    {
        if (Model == null || state.ActiveTexture < 0) return;
        int index = state.ActiveTexture;
        Pick(false, "Import image into texture", FileBrowser.ImageFilter, null, path =>
        {
            var old = Model.Textures[index];
            var image = PaintTexture.FromEncoded(old.Name, Path.GetExtension(path).ToLowerInvariant(), File.ReadAllBytes(path), old.FilePath);
            if (image.Width == 1 && image.Height == 1 && new FileInfo(path).Length > 200)
            {
                image.Dispose();
                throw new InvalidOperationException($"Couldn't read {Path.GetFileName(path)} as an image.");
            }
            EndStroke();
            image.Dirty = true;
            Model.ReplaceTexture(index, image);
            // Undo steps point at the old pixels, and a selection at the old size.
            undo.Clear();
            if (state.Selection?.Texture == index) state.Selection = null;
            uvView.RequestFit();
            SetStatus($"Imported {Path.GetFileName(path)} ({image.Width}x{image.Height}) into {old.Name}; Ctrl+S saves it.", error: false);
        });
    }

    private void ExportTexture()
    {
        if (Model == null || state.ActiveTexture < 0) return;
        var texture = Model.Textures[state.ActiveTexture];
        Pick(true, "Export texture", FileBrowser.PngFilter, Path.ChangeExtension(texture.Name, ".png"), path =>
        {
            texture.ExportTo(path);
            SetStatus($"Exported {texture.Name} to {path}", error: false);
        });
    }

    /// <summary>Writes the UV layout the UV view shows (the active object's, or everything on the texture).</summary>
    private void ExportUvLayout(int scale, bool overTexture)
    {
        if (Model == null) return;
        int texture = state.ActiveTexture;
        var parts = Enumerable.Range(0, Model.Source.Parts.Count)
            .Where(p => state.PartVisible(p) && Model.Source.Parts[p].HasUvs)
            .Where(p => state.ActiveObject < 0 || Model.Source.Parts[p].ObjectIndex == state.ActiveObject)
            .Where(p => texture < 0 || Model.TextureOf(p) == texture)
            .ToList();
        if (parts.Count == 0)
        {
            SetStatus("Nothing with UVs to export: select an object with UVs.", error: true);
            return;
        }
        string stem = texture >= 0 ? Path.GetFileNameWithoutExtension(Model.Textures[texture].Name)
            : state.ActiveObject >= 0 ? Model.Source.Objects[state.ActiveObject].Name : Path.GetFileNameWithoutExtension(Model.Source.Name);
        var size = uvView.TextureSize;
        Pick(true, "Export UV layout", FileBrowser.PngFilter, $"{stem}_uv.png", path =>
        {
            UvLayoutExport.Export(Model, parts, size, texture >= 0 ? Model.Textures[texture] : null, scale, overTexture, path);
            SetStatus($"Exported the UV layout ({size.X * scale:0}x{size.Y * scale:0}) to {path}", error: false);
        });
    }

    private void DrawOpenPathPopup()
    {
        if (openPathPopup)
        {
            ImGui.OpenPopup("Open path");
            openPathPopup = false;
        }
        ImGui.SetNextWindowSize(new Vector2(560, 0));
        bool open = true;
        if (!ImGui.BeginPopupModal("Open path", ref open, ImGuiWindowFlags.NoResize)) return;
        ImGui.TextWrapped("Paste the path of a model (fbx, gltf, glb, obj, blend...):");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        bool go = ImGui.InputText("##path", ref pathInput, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        go |= ImGui.Button("Open");
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        if (go)
        {
            string path = pathInput.Trim().Trim('"');
            if (File.Exists(path)) { StartLoad(path); ImGui.CloseCurrentPopup(); }
            else SetStatus($"No file at {path}", error: true);
        }
        ImGui.EndPopup();
    }

    private void Reload()
    {
        if (Model == null) return;
        if (Model.Source.LinkPath != null) StartLinkRefresh(force: true);
        else StartLoad(Model.Source.SourcePath);
    }

    /// <summary>
    /// Writes every changed texture: back to its own file when it has one (for a .blend, the image file the .blend
    /// uses), otherwise to "&lt;model&gt;_&lt;texture&gt;.png" next to the model.
    /// </summary>
    private void SaveAll()
    {
        if (Model == null) return;
        profiler.Skip();
        EndStroke();
        var saved = new List<string>();
        foreach (var texture in Model.Textures.Where(t => t.Dirty))
        {
            string path = texture.FilePath ?? DefaultSavePath(texture);
            try
            {
                texture.Save(path);
                LayerFile.Write(texture, path);
                saved.Add(Path.GetFileName(path) + (texture.HasLayers ? $" ({texture.Layers.Count} layers)" : ""));
            }
            catch (Exception e)
            {
                SetStatus($"Couldn't save {texture.Name}: {e.Message}", error: true);
                return;
            }
        }
        SetStatus(saved.Count == 0 ? "Nothing to save." : $"Saved {string.Join(", ", saved)}", error: false);
    }

    private string DefaultSavePath(PaintTexture texture) => DefaultSavePath(texture.Name);

    private string DefaultSavePath(string textureName)
    {
        string source = Model!.Source.SourcePath;
        string name = Path.GetFileNameWithoutExtension(textureName);
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
            HandleLayerShortcuts(shift);
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
        if (Raylib.IsKeyPressed(KeyboardKey.E)) tool = Tool.Eraser;
        if (Raylib.IsKeyPressed(KeyboardKey.I)) tool = Tool.Eyedropper;
        if (Raylib.IsKeyPressed(KeyboardKey.X)) tool = Tool.Lasso;
        if (Raylib.IsKeyPressed(KeyboardKey.M)) tool = Tool.BoxSelect;
        if (Raylib.IsKeyPressed(KeyboardKey.G)) tool = Tool.Fill;
        if (Pressed(KeyboardKey.W)) brushSize = brushSize < 4 ? brushSize + 1 : MathF.Min(MathF.Round(brushSize * 1.25f), 256);
        if (Pressed(KeyboardKey.Q)) brushSize = brushSize <= 4 ? MathF.Max(brushSize - 1, 1) : MathF.Round(brushSize / 1.25f);
        if (Raylib.IsKeyPressed(KeyboardKey.Z))
        {
            if (shift) view3d.Wireframe = !view3d.Wireframe;
            else tool = Tool.Zoom;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.H)) RecordVisibility(alt ? Reveal : shift ? HideUnselected : HideSelected);
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

    /// <summary>Runs a hide or reveal as one undoable step (only if it changed anything).</summary>
    private void RecordVisibility(Action change)
    {
        if (Model == null) return;
        var before = VisibilityStep.Snapshot(Model);
        change();
        if (VisibilityStep.IfChanged(Model, before) is { } step) undo.Push(step);
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
        hoverNote = null;
        sample = default;
        cursorIcon = CursorIcon.System;
        cursorTip = pointer;
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
        if (cursorIcon == CursorIcon.System) cursorIcon = ToolIcon(free);
    }

    /// <summary>The cursor for the current tool while it is over a view (or dragging); the system arrow elsewhere.</summary>
    private CursorIcon ToolIcon(bool free)
    {
        bool overView = free && ((view3d.Hovered && !view3d.Navigating) || uvView.Hovered);
        if (!overView && stroke == null && lassoDrag == LassoDrag.None) return CursorIcon.System;
        switch (tool)
        {
            case Tool.Pencil: return CursorIcon.Pencil;
            case Tool.Brush: return CursorIcon.Brush;
            case Tool.Eraser: return CursorIcon.Eraser;
            case Tool.Fill: return CursorIcon.Fill;
            case Tool.Zoom: return CursorIcon.Zoom;
            case Tool.Lasso:
            case Tool.BoxSelect:
                bool shift = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);
                bool overSelection = uvView.Hovered && !shift && state.ActiveSelection is { } selection
                    && selection.Contains((int)MathF.Floor(uvView.MouseTexel.X), (int)MathF.Floor(uvView.MouseTexel.Y));
                if (lassoDrag == LassoDrag.Moving || (lassoDrag == LassoDrag.None && overSelection)) return CursorIcon.Move;
                return tool == Tool.Lasso ? CursorIcon.Lasso : CursorIcon.Box;
            default: return CursorIcon.System;
        }
    }

    private void UpdateTools(bool free, bool alt)
    {
        if (Model == null) return;
        var source = Model.Source;
        bool painting = tool is Tool.Pencil or Tool.Brush or Tool.Eraser;
        // Holding Alt turns any tool into the eyedropper until it's released.
        bool sampling = (tool == Tool.Eyedropper || alt) && stroke == null && lassoDrag == LassoDrag.None;
        bool pressed = pointerPressed;

        UpdateZoom(free, pressed && !sampling);
        UpdateLasso(free, pressed && !sampling);

        if (stroke != null)
        {
            // Every point the pen passed through, even on the frame it lifts.
            foreach (var (raw, pressure) in StrokePoints())
            {
                if (!Stabilize(raw, out var point)) continue;
                if (strokeIn3D) Continue3D(point, pressure);
                else ContinueUv(point, pressure);
            }
            if (!pointerDown)
            {
                // The stabilized line catches up with the pen where it lifted.
                if (SmoothingRadius >= 0.5f && Vector2.DistanceSquared(smoothPoint, smoothRaw) > 0.25f)
                {
                    if (strokeIn3D) Continue3D(smoothRaw, lastPressure);
                    else ContinueUv(smoothRaw, lastPressure);
                }
                EndStroke();
            }
        }

        // 3D view.
        if (view3d.Hovered && !view3d.Navigating && free)
        {
            SurfaceHit hit = default;
            bool hasHit = false;
            if (painting || sampling || tool == Tool.Fill)
            {
                var ray = view3d.MouseRay();
                Func<int, bool> filter = sampling ? state.ObjectVisible : i => i == state.ActiveObject && state.ObjectVisible(i);
                hasHit = Raycast.Cast(source, ray.Position, ray.Direction, filter, out hit);
            }

            if (sampling)
            {
                cursorIcon = CursorIcon.Eyedropper;
                if (hasHit) sample = SampleAt(hit);
            }
            if (painting && !sampling && hasHit) Show3DCursor(hit);
            if (pressed && stroke == null)
            {
                // A left drag that starts over nothing navigates, exactly like the middle button.
                if (sampling)
                {
                    if (sample.Valid) SetColor(sample.Color);
                    else view3d.BeginLeftDragNavigation();
                }
                else if (painting)
                {
                    if (hasHit) Begin3D(hit);
                    else if (NearActiveObject(out var near)) Begin3D(near, dab: false);
                    else if (!ObjectUnderMouse()) view3d.BeginLeftDragNavigation();
                    else if (state.ActiveObject < 0) SetStatus("Select an object to paint (V, then click it).", error: true);
                    else SetStatus($"Only the active object ({source.Objects[state.ActiveObject].Name}) is painted; select another with V.", error: false);
                }
                else if (tool == Tool.Select)
                {
                    selectPressed = true;
                    selectPressPosition = view3d.LocalMouse;
                    if (!ObjectUnderMouse()) view3d.BeginLeftDragNavigation();
                }
                else if (tool == Tool.Fill)
                {
                    if (hasHit) FillFrom(hit);
                    else if (NearActiveObject(out _)) { }
                    else if (!ObjectUnderMouse()) view3d.BeginLeftDragNavigation();
                    else if (state.ActiveObject < 0) SetStatus("Select an object to fill (V, then click it).", error: true);
                }
                else if (tool is Tool.Lasso or Tool.BoxSelect)
                {
                    if (ObjectUnderMouse()) SetStatus("The lasso works in the UV view.", error: false);
                    else view3d.BeginLeftDragNavigation();
                }
            }
        }

        if (selectPressed && pointerReleased)
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
                cursorIcon = CursorIcon.Eyedropper;
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
                else if (tool == Tool.Fill && canPaint && uvView.MouseOnTexture)
                    FillFrom(state.ActiveTexture, (int)MathF.Floor(uvView.MouseTexel.X), (int)MathF.Floor(uvView.MouseTexel.Y));
                else if (tool == Tool.Fill) SetStatus("Select an object to fill (V, then click it).", error: true);
                else if (tool == Tool.Select && state.Mode == SelectMode.Submesh) SelectInUv();
            }
        }
        if (stroke != null && !strokeIn3D && tool is Tool.Pencil or Tool.Brush or Tool.Eraser) ShowUvCursor();
        if (sampling && uvView.Hovered && free && state.ActiveTexture < 0) cursorIcon = CursorIcon.Eyedropper;
    }

    // ---------- lasso ----------

    private enum LassoDrag { None, Drawing, Moving }
    private LassoDrag lassoDrag;
    private SelectionOp lassoOp;
    private SelectionMove? selectionMove;
    private Vector2 moveStart;

    /// <summary>
    /// The lasso and box select, in the UV view: drag to draw a selection (Shift adds, Ctrl subtracts); drag inside
    /// it to move the selected texels (Ctrl+drag moves a copy); a click outside it deselects. The box snaps to whole
    /// texels.
    /// </summary>
    private void UpdateLasso(bool free, bool pressed)
    {
        var mouse = uvView.MouseTexel;
        if (lassoDrag == LassoDrag.Drawing && state.Lasso != null)
        {
            if (pointerDown)
            {
                if (drawingBox)
                {
                    boxMoved |= Vector2.Distance(boxStart, mouse) >= 0.5f;
                    state.Lasso = BoxPolygon(boxStart, mouse);
                }
                else if (Vector2.Distance(state.Lasso[^1], mouse) >= 0.35f) state.Lasso.Add(mouse);
            }
            else
            {
                FinishLasso();
            }
            return;
        }
        if (lassoDrag == LassoDrag.Moving && selectionMove != null && state.Selection != null)
        {
            if (pointerDown)
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

        if (tool is not (Tool.Lasso or Tool.BoxSelect) || !pressed || !free || !uvView.Hovered || state.ActiveTexture < 0 || Model == null) return;
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
        drawingBox = tool == Tool.BoxSelect;
        boxStart = mouse;
        boxMoved = false;
        state.Lasso = drawingBox ? BoxPolygon(mouse, mouse) : new List<Vector2> { mouse };
        lassoDrag = LassoDrag.Drawing;
    }

    private bool drawingBox, boxMoved;
    private Vector2 boxStart;

    /// <summary>The box from one texel to another, inclusive, on texel edges.</summary>
    private static List<Vector2> BoxPolygon(Vector2 a, Vector2 b)
    {
        float x0 = MathF.Floor(MathF.Min(a.X, b.X)), x1 = MathF.Floor(MathF.Max(a.X, b.X)) + 1;
        float y0 = MathF.Floor(MathF.Min(a.Y, b.Y)), y1 = MathF.Floor(MathF.Max(a.Y, b.Y)) + 1;
        return new List<Vector2> { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };
    }

    private void FinishLasso()
    {
        var polygon = state.Lasso!;
        state.Lasso = null;
        lassoDrag = LassoDrag.None;

        float extent = 0;
        foreach (var p in polygon) extent = MathF.Max(extent, Vector2.Distance(p, polygon[0]));
        bool click = drawingBox ? !boxMoved : polygon.Count < 3 || extent < 0.75f;
        if (click)
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

    // ---------- fill ----------

    private void FillFrom(SurfaceHit hit)
    {
        int texture = Model!.TextureOf(hit.Part);
        if (texture < 0 || !Model.Source.Parts[hit.Part].HasUvs)
        {
            SetStatus(Model.Source.Parts[hit.Part].HasUvs
                ? "This part has no texture yet: use New texture in the panel."
                : "This part has no UVs: unwrap it in Blender first.", error: true);
            return;
        }
        var tex = Model.Textures[texture];
        var (x, y) = Brush.TexelAt(Model.Source, hit, new Vector2(tex.Width, tex.Height));
        FillFrom(texture, tex.Wrap(x, tex.Width), tex.Wrap(y, tex.Height));
    }

    /// <summary>Bucket fill from a texel; with Shift, fills the whole selection instead. One undo step.</summary>
    private void FillFrom(int texture, int x, int y)
    {
        bool shift = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);
        var tex = Model!.Textures[texture];
        var mask = MaskFor(texture);
        if (shift && mask == null)
        {
            SetStatus("Shift+click fills the selection: make one first (X or M in the UV view).", error: true);
            return;
        }
        state.ActiveTexture = texture;
        palette.Remember(ColorWheel.HsvToRgb(hsv));
        var fill = new Stroke(tex, PaintColor, 1f, mask);
        if (shift)
        {
            for (int i = 0; i < mask!.Length; i++)
                if (mask[i]) fill.Apply(i % tex.Width, i / tex.Width, 1f);
        }
        else
        {
            if (fillAllLayers) tex.EnsureComposite();
            Brush.Flood(fill, x, y, fillTolerance, fillContiguous, mask, fillAllLayers ? tex.Composite : null);
        }
        if (fill.Finish() is { } step) undo.Push(step);
    }

    // ---------- scrubby zoom ----------

    /// <summary>
    /// The Zoom tool (Z): drag right to zoom in and left to zoom out, about the point where the drag started (in 3D,
    /// the surface under it); a click without dragging zooms in a step.
    /// </summary>
    private void UpdateZoom(bool free, bool pressed)
    {
        if (zoomDrag != ZoomDrag.None)
        {
            float dx = pointer.X - lastZoomX;
            lastZoomX = pointer.X;
            zoomTravel += MathF.Abs(dx);
            if (dx != 0) ApplyZoom(MathF.Exp(dx * 0.01f));
            if (!pointerDown)
            {
                if (zoomTravel < 3f) ApplyZoom(1.5f);
                zoomDrag = ZoomDrag.None;
            }
            return;
        }
        if (tool != Tool.Zoom || !pressed || !free) return;
        if (view3d.Hovered)
        {
            var ray = view3d.MouseRay();
            zoomFocus = Model != null && Raycast.Cast(Model.Source, ray.Position, ray.Direction, state.ObjectVisible, out var hit)
                ? hit.Point
                : ray.Position + ray.Direction * view3d.Camera.Distance;
            zoomDrag = ZoomDrag.View3D;
        }
        else if (uvView.Hovered)
        {
            zoomAnchor = pointer;
            zoomDrag = ZoomDrag.Uv;
        }
        lastZoomX = pointer.X;
        zoomTravel = 0;
    }

    /// <summary>Zooms the view being scrubbed; <paramref name="amount"/> above 1 zooms in.</summary>
    private void ApplyZoom(float amount)
    {
        if (zoomDrag == ZoomDrag.View3D) view3d.Camera.ZoomAbout(zoomFocus, 1f / amount);
        else if (zoomDrag == ZoomDrag.Uv) uvView.ZoomAt(zoomAnchor, amount);
    }

    /// <summary>The selection mask for strokes on <paramref name="texture"/>: paint stays inside it.</summary>
    private bool[]? MaskFor(int texture) =>
        state.Selection is { Any: true } s && s.Texture == texture
        && s.Width == Model!.Textures[texture].Width && s.Height == Model.Textures[texture].Height ? s.Mask : null;

    /// <summary>Whether any visible object is under the mouse in the 3D view.</summary>
    private bool ObjectUnderMouse()
    {
        var ray = view3d.MouseRay();
        return Raycast.Cast(Model!.Source, ray.Position, ray.Direction, state.ObjectVisible, out _);
    }

    private void Show3DCursor(SurfaceHit hit)
    {
        var part = Model!.Source.Parts[hit.Part];
        int texture = Model.TextureOf(hit.Part);
        if (texture < 0 || part.Uvs == null)
        {
            hoverNote = part.Uvs == null
                ? $"{part.Name}: no UVs"
                : $"{Model.Source.Materials[part.MaterialIndex].Name}: no texture (New texture..., or pick one under Active object)";
            return;
        }
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
        if (MirrorHit(hit, out var mirrored) && Model.TextureOf(mirrored.Part) == texture)
        {
            state.Cursor.Mirror3D = tool != Tool.Pencil;
            state.Cursor.MirrorScreen = view3d.WorldToScreen(mirrored.Point);
            state.Cursor.MirrorUv = true;
            state.Cursor.MirrorTexelCenter = Raycast.UvAt(Model.Source.Parts[mirrored.Part], mirrored.Triangle, mirrored.Barycentric) * size;
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
        Vector2 at;
        if (tool == Tool.Pencil)
        {
            state.Cursor.Pencil = true;
            state.Cursor.Texel = ((int)MathF.Floor(uvView.MouseTexel.X), (int)MathF.Floor(uvView.MouseTexel.Y));
            if (!uvView.MouseOnTexture) state.Cursor.Visible = false;
            at = new Vector2(state.Cursor.Texel.X + 0.5f, state.Cursor.Texel.Y + 0.5f);
        }
        else
        {
            state.Cursor.TexelCenter = at = Brush.SnapCenter(uvView.MouseTexel, brushSize);
            state.Cursor.TexelRadius = brushSize * 0.5f;
        }
        if (state.Cursor.Visible && MirrorTexel(at) is { } mirrored)
        {
            state.Cursor.MirrorUv = true;
            state.Cursor.MirrorTexelCenter = mirrored;
        }
    }

    /// <summary>
    /// The halo around the active object: whether a press that just missed it is within the brush's screen radius
    /// (at least 16 px) of its surface, and where. Such a press starts a stroke that paints once the brush reaches
    /// the surface, instead of turning into an accidental orbit.
    /// </summary>
    private bool NearActiveObject(out SurfaceHit near)
    {
        near = default;
        if (Model == null || state.ActiveObject < 0 || !state.ObjectVisible(state.ActiveObject)) return false;
        float halo = MathF.Max(11f, cursorScreenRadius * 0.7f);
        var camera = view3d.Camera.ToRaylib();
        bool OnActive(int i) => i == state.ActiveObject;
        foreach (float reach in new[] { 0.35f, 0.7f, 1f })
        for (int k = 0; k < 12; k++)
        {
            float angle = k / 12f * MathF.PI * 2f;
            var point = view3d.LocalMouse + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * halo * reach;
            var ray = Raylib.GetScreenToWorldRayEx(point, camera, view3d.Width, view3d.Height);
            if (Raycast.Cast(Model.Source, ray.Position, ray.Direction, OnActive, out near)) return true;
        }
        return false;
    }

    /// <summary>Starts a 3D stroke on the texture under <paramref name="hit"/>, with a first dab unless it came from the halo.</summary>
    private void Begin3D(SurfaceHit hit, bool dab = true)
    {
        int texture = Model!.TextureOf(hit.Part);
        if (texture < 0 || !Model.Source.Parts[hit.Part].HasUvs)
        {
            SetStatus(Model.Source.Parts[hit.Part].HasUvs
                ? $"{Model.Source.Materials[Model.Source.Parts[hit.Part].MaterialIndex].Name} has no texture: make one with New texture... on the UV toolbar, or give it an existing one under Active object."
                : "This part has no UVs: unwrap it in Blender first.", error: true);
            return;
        }
        state.ActiveTexture = texture;
        strokeTexture = texture;
        strokeIn3D = true;
        stroke = NewStroke(texture);
        lastPressure = FirstPressure();
        SetDab(lastPressure);
        StartSmoothing();
        if (ShiftDown && lastStrokeEnd3D is { } from && lastStrokeEndTexture3D == texture)
        {
            // Shift+click: a straight line (on screen) from where the last stroke ended.
            lastMouse = view3d.WorldToScreen(from);
            ContinueLocal3D(view3d.LocalMouse, lastPressure);
            return;
        }
        if (dab) Dab3D(hit);
        lastMouse = view3d.LocalMouse;
    }

    /// <summary>Pressure where a stroke starts: the pen's first touching sample this frame, else the latest.</summary>
    private float FirstPressure()
    {
        foreach (var (_, pressure) in StrokePoints()) return pressure;
        return RawPressure;
    }

    /// <summary>
    /// Carries a 3D stroke to <paramref name="screenPoint"/>: dabs every pixel for the pencil, and at a small fraction
    /// of the brush's screen radius for the brush, so small movements still paint.
    /// </summary>
    private void Continue3D(Vector2 screenPoint, float pressure) => ContinueLocal3D(view3d.ScreenToLocal(screenPoint), pressure);

    private void ContinueLocal3D(Vector2 to, float pressure)
    {
        float spacing = tool == Tool.Pencil ? 1f : MathF.Max(1f, cursorScreenRadius * 0.15f);
        float distance = Vector2.Distance(lastMouse, to);
        if (distance < spacing) return;
        int steps = (int)(distance / spacing);
        var camera = view3d.Camera.ToRaylib();
        for (int s = 1; s <= steps; s++)
        {
            SetDab(float.Lerp(lastPressure, pressure, s / (float)steps));
            Vector2 p = Vector2.Lerp(lastMouse, to, s / (float)steps);
            var ray = Raylib.GetScreenToWorldRayEx(p, camera, view3d.Width, view3d.Height);
            if (Raycast.Cast(Model!.Source, ray.Position, ray.Direction, i => i == state.ActiveObject && state.ObjectVisible(i), out var hit)
                && Model.TextureOf(hit.Part) == strokeTexture)
                Dab3D(hit);
        }
        lastMouse = Vector2.Lerp(lastMouse, to, steps * spacing / distance);
        lastPressure = pressure;
    }

    private void Dab3D(SurfaceHit hit)
    {
        lastDabPoint = hit.Point;
        DabAt(hit);
        if (MirrorHit(hit, out var mirrored) && Model!.TextureOf(mirrored.Part) == strokeTexture) DabAt(mirrored);
    }

    private void DabAt(SurfaceHit hit)
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

    /// <summary>A UV-view dab (brush) at a texel point, and its mirror.</summary>
    private void DabUv(Vector2 center)
    {
        Brush.DabTexels(stroke!, center, dabSize, hardness);
        if (MirrorTexel(center) is { } mirrored) Brush.DabTexels(stroke!, Brush.SnapCenter(mirrored, dabSize), dabSize, hardness);
    }

    /// <summary>A UV-view pencil line, and its mirror (joined between the mirrored ends).</summary>
    private void LineUv((int X, int Y) from, (int X, int Y) to)
    {
        Brush.Line(stroke!, from.X, from.Y, to.X, to.Y, clip: true);
        if (MirrorTexel(new Vector2(from.X + 0.5f, from.Y + 0.5f)) is { } a && MirrorTexel(new Vector2(to.X + 0.5f, to.Y + 0.5f)) is { } b)
            Brush.Line(stroke!, (int)MathF.Floor(a.X), (int)MathF.Floor(a.Y), (int)MathF.Floor(b.X), (int)MathF.Floor(b.Y), clip: true);
    }

    private void BeginUv()
    {
        strokeTexture = state.ActiveTexture;
        strokeIn3D = false;
        stroke = NewStroke(strokeTexture);
        lastPressure = FirstPressure();
        SetDab(lastPressure);
        StartSmoothing();
        bool line = ShiftDown && lastStrokeEndUv is { } && lastStrokeEndTextureUv == strokeTexture;
        if (tool == Tool.Pencil)
        {
            var texel = ((int)MathF.Floor(uvView.MouseTexel.X), (int)MathF.Floor(uvView.MouseTexel.Y));
            // Shift+click: a straight line from where the last stroke ended.
            var from = line ? ((int)MathF.Floor(lastStrokeEndUv!.Value.X), (int)MathF.Floor(lastStrokeEndUv.Value.Y)) : texel;
            LineUv(from, texel);
            lastTexel = texel;
        }
        else if (line)
        {
            lastDab = lastStrokeEndUv!.Value;
            ContinueUv(pointer, lastPressure);
        }
        else
        {
            lastDab = Brush.SnapCenter(uvView.MouseTexel, dabSize);
            DabUv(lastDab);
        }
    }

    /// <summary>Carries a UV-view stroke to <paramref name="screenPoint"/>.</summary>
    private void ContinueUv(Vector2 screenPoint, float pressure)
    {
        Vector2 texelPoint = uvView.ScreenToTexel(screenPoint);
        if (tool == Tool.Pencil)
        {
            var texel = ((int)MathF.Floor(texelPoint.X), (int)MathF.Floor(texelPoint.Y));
            if (texel == lastTexel) return;
            LineUv(lastTexel, texel);
            lastTexel = texel;
            return;
        }
        Vector2 center = Brush.SnapCenter(texelPoint, brushSize);
        float spacing = MathF.Max(0.35f, brushSize * 0.12f);
        float distance = Vector2.Distance(lastDab, center);
        if (distance < spacing) return;
        int steps = (int)MathF.Ceiling(distance / spacing);
        for (int s = 1; s <= steps; s++)
        {
            SetDab(float.Lerp(lastPressure, pressure, s / (float)steps));
            DabUv(Brush.SnapCenter(Vector2.Lerp(lastDab, center, s / (float)steps), dabSize));
        }
        lastDab = center;
        lastPressure = pressure;
    }

    private void EndStroke()
    {
        if (stroke == null) return;
        RememberStrokeEnd();
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

    private enum CursorIcon { System, Eyedropper, Pencil, Brush, Eraser, Lasso, Move, Fill, Zoom, Box }

    /// <summary>Shows the system cursor, or hides it while a tool draws its own.</summary>
    private void UpdateSystemCursor()
    {
        bool custom = cursorIcon != CursorIcon.System;
        if (custom == Raylib.IsCursorHidden()) return;
        if (custom) Raylib.HideCursor();
        else Raylib.ShowCursor();
    }

    private static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);
    private static uint U32(Color c) => ImGui.ColorConvertFloat4ToU32(new Vector4(c.R, c.G, c.B, 255f) / 255f);
    private static readonly uint Black = U32(new Vector4(0, 0, 0, 1));
    private static readonly uint White = U32(Vector4.One);

    /// <summary>Draws the current tool's cursor with its hot spot at <see cref="cursorTip"/>.</summary>
    private void DrawToolCursor()
    {
        var draw = ImGui.GetForegroundDrawList();
        if (hoverNote != null && cursorIcon != CursorIcon.System)
        {
            var at = cursorTip + new Vector2(18, 14);
            var size = ImGui.CalcTextSize(hoverNote);
            draw.AddRectFilled(at - new Vector2(4, 2), at + size + new Vector2(4, 2), U32(new Vector4(0.1f, 0.1f, 0.1f, 0.85f)), 3f);
            draw.AddText(at, U32(new Vector4(1f, 0.6f, 0.45f, 1f)), hoverNote);
        }
        switch (cursorIcon)
        {
            case CursorIcon.Eyedropper: DrawEyedropper(); break;
            case CursorIcon.Pencil: DrawPencilIcon(draw, cursorTip); break;
            case CursorIcon.Brush: DrawBrushIcon(draw, cursorTip); break;
            case CursorIcon.Eraser: DrawEraserIcon(draw, cursorTip); break;
            case CursorIcon.Lasso: DrawLassoIcon(draw, cursorTip); break;
            case CursorIcon.Move: DrawMoveIcon(draw, cursorTip); break;
            case CursorIcon.Fill: DrawBucketIcon(draw, cursorTip); break;
            case CursorIcon.Zoom: DrawZoomIcon(draw, cursorTip); break;
            case CursorIcon.Box: DrawBoxIcon(draw, cursorTip); break;
        }
        if (selfTestIcons)
        {
            var at = new Vector2(Raylib.GetScreenWidth() - 330, Raylib.GetScreenHeight() - 170);
            DrawPencilIcon(draw, at);
            DrawBrushIcon(draw, at + new Vector2(70, 0));
            DrawLassoIcon(draw, at + new Vector2(140, 0));
            DrawMoveIcon(draw, at + new Vector2(230, 0));
            DrawBucketIcon(draw, at + new Vector2(0, 60));
            DrawZoomIcon(draw, at + new Vector2(90, 60));
            DrawBoxIcon(draw, at + new Vector2(160, 60));
            DrawEraserIcon(draw, at + new Vector2(230, 60));
        }
    }

    // Icon shapes lean up and to the right of the hot spot, like a hand holding the tool.
    private static readonly Vector2 Along = Vector2.Normalize(new Vector2(1, -1));
    private static readonly Vector2 Across = new(-Along.Y, Along.X);

    private static void Outlined(ImDrawListPtr draw, Vector2[] points, uint fill)
    {
        draw.AddConvexPolyFilled(ref points[0], points.Length, fill);
        draw.AddPolyline(ref points[0], points.Length, Black, ImDrawFlags.Closed, 1.5f);
    }

    /// <summary>A pencil with the graphite tip on the hot spot and its body in the paint colour.</summary>
    private void DrawPencilIcon(ImDrawListPtr draw, Vector2 tip)
    {
        const float w = 4f;
        Vector2 cone = tip + Along * 9f, body = tip + Along * 27f, end = tip + Along * 32f;
        Outlined(draw, new[] { tip, cone + Across * w, cone - Across * w }, U32(new Vector4(0.93f, 0.85f, 0.7f, 1)));
        Outlined(draw, new[] { tip, tip + Along * 3.5f + Across * 1.6f, tip + Along * 3.5f - Across * 1.6f }, Black);
        Outlined(draw, new[] { cone + Across * w, body + Across * w, body - Across * w, cone - Across * w }, U32(PaintColor));
        Outlined(draw, new[] { body + Across * w, end + Across * w, end - Across * w, body - Across * w }, U32(new Vector4(0.95f, 0.6f, 0.65f, 1)));
    }

    /// <summary>The brush's crosshair and a small eraser beside it.</summary>
    private static void DrawEraserIcon(ImDrawListPtr draw, Vector2 center)
    {
        DrawCrosshair(draw, center);
        const float w = 4.5f;
        Vector2 tip = center + new Vector2(10, -10);
        Vector2 rubberEnd = tip + Along * 8f, sleeveEnd = rubberEnd + Along * 12f;
        Outlined(draw, new[] { tip + Across * w, rubberEnd + Across * w, rubberEnd - Across * w, tip - Across * w }, U32(new Vector4(0.95f, 0.6f, 0.65f, 1)));
        Outlined(draw, new[] { rubberEnd + Across * w, sleeveEnd + Across * w, sleeveEnd - Across * w, rubberEnd - Across * w }, U32(new Vector4(0.3f, 0.45f, 0.8f, 1)));
    }

    private static void DrawCrosshair(ImDrawListPtr draw, Vector2 center)
    {
        foreach (var (a, b) in new[] { (new Vector2(-7, 0), new Vector2(-2, 0)), (new Vector2(2, 0), new Vector2(7, 0)),
                                       (new Vector2(0, -7), new Vector2(0, -2)), (new Vector2(0, 2), new Vector2(0, 7)) })
        {
            draw.AddLine(center + a, center + b, Black, 3f);
            draw.AddLine(center + a, center + b, White, 1f);
        }
    }

    /// <summary>A crosshair on the hot spot (the view draws the brush's size around it) and a small brush beside it.</summary>
    private void DrawBrushIcon(ImDrawListPtr draw, Vector2 center)
    {
        DrawCrosshair(draw, center);
        const float w = 3.5f;
        Vector2 tip = center + new Vector2(11, -11);
        Vector2 bristleEnd = tip + Along * 9f, ferruleEnd = bristleEnd + Along * 4f, handleEnd = ferruleEnd + Along * 13f;
        Outlined(draw, new[] { tip, bristleEnd + Across * w, bristleEnd - Across * w }, U32(PaintColor));
        Outlined(draw, new[] { bristleEnd + Across * w, ferruleEnd + Across * w, ferruleEnd - Across * w, bristleEnd - Across * w }, U32(new Vector4(0.75f, 0.75f, 0.78f, 1)));
        Outlined(draw, new[] { ferruleEnd + Across * 2.2f, handleEnd + Across * 1.6f, handleEnd - Across * 1.6f, ferruleEnd - Across * 2.2f }, U32(new Vector4(0.55f, 0.35f, 0.2f, 1)));
    }

    /// <summary>A lasso: a loop up and to the right, its rope ending on the hot spot.</summary>
    private static void DrawLassoIcon(ImDrawListPtr draw, Vector2 tip)
    {
        Vector2 center = tip + new Vector2(16, -18);
        var loop = new Vector2[28];
        for (int i = 0; i < loop.Length; i++)
        {
            float a = i / (float)loop.Length * MathF.PI * 2f;
            loop[i] = center + new Vector2(MathF.Cos(a) * 11f, MathF.Sin(a) * 7f);
        }
        Vector2 knot = center + new Vector2(-7.5f, 5f);
        foreach (var (color, width) in new[] { (Black, 4f), (White, 2f) })
        {
            draw.AddPolyline(ref loop[0], loop.Length, color, ImDrawFlags.Closed, width);
            draw.AddBezierCubic(knot, knot + new Vector2(-2, 6), tip + new Vector2(6, -4), tip, color, width);
        }
        draw.AddCircleFilled(knot, 2.5f, Black);
    }

    /// <summary>A tipped bucket pouring a drop of the paint colour onto the hot spot.</summary>
    private void DrawBucketIcon(ImDrawListPtr draw, Vector2 tip)
    {
        Vector2 c = tip + new Vector2(15, -15);
        Vector2 u = Vector2.Normalize(new Vector2(1, -0.35f)), v = new(-u.Y, u.X);
        var body = new[] { c - u * 7 - v * 8, c + u * 7 - v * 8, c + u * 5 + v * 6, c - u * 5 + v * 6 };
        Outlined(draw, body, U32(new Vector4(0.85f, 0.85f, 0.88f, 1)));
        draw.AddBezierCubic(c - u * 7 - v * 8, c - u * 9 - v * 16, c + u * 9 - v * 16, c + u * 7 - v * 8, Black, 2.5f);
        // Paint spilling from the rim down to the hot spot.
        draw.AddLine(c - u * 7 - v * 8, tip + new Vector2(0, -3), Black, 4f);
        draw.AddLine(c - u * 7 - v * 8, tip + new Vector2(0, -3), U32(PaintColor), 2f);
        draw.AddCircleFilled(tip, 3.5f, Black);
        draw.AddCircleFilled(tip, 2.3f, U32(PaintColor));
    }

    /// <summary>A magnifier with its lens centred on the hot spot.</summary>
    private static void DrawZoomIcon(ImDrawListPtr draw, Vector2 center)
    {
        const float r = 8f;
        Vector2 handleStart = center + Vector2.Normalize(new Vector2(1, 1)) * r, handleEnd = handleStart + Vector2.Normalize(new Vector2(1, 1)) * 9f;
        draw.AddLine(handleStart, handleEnd, Black, 6f);
        draw.AddLine(handleStart, handleEnd, White, 3f);
        draw.AddCircle(center, r, Black, 0, 4f);
        draw.AddCircle(center, r, White, 0, 2f);
        draw.AddLine(center - new Vector2(4, 0), center + new Vector2(4, 0), White, 1.5f);
        draw.AddLine(center - new Vector2(0, 4), center + new Vector2(0, 4), White, 1.5f);
    }

    /// <summary>A crosshair with a dashed box beside it.</summary>
    private static void DrawBoxIcon(ImDrawListPtr draw, Vector2 center)
    {
        foreach (var (a, b) in new[] { (new Vector2(-6, 0), new Vector2(6, 0)), (new Vector2(0, -6), new Vector2(0, 6)) })
        {
            draw.AddLine(center + a, center + b, Black, 3f);
            draw.AddLine(center + a, center + b, White, 1f);
        }
        Vector2 min = center + new Vector2(8, -22), max = center + new Vector2(22, -8);
        draw.AddRect(min, max, Black, 0, ImDrawFlags.None, 3f);
        for (int i = 0; i < 4; i++)
        {
            // Dashes along each side.
            Vector2 from = i switch { 0 => min, 1 => new Vector2(max.X, min.Y), 2 => max, _ => new Vector2(min.X, max.Y) };
            Vector2 to = i switch { 0 => new Vector2(max.X, min.Y), 1 => max, 2 => new Vector2(min.X, max.Y), _ => min };
            for (float t = 0; t < 1f; t += 0.5f)
                draw.AddLine(Vector2.Lerp(from, to, t), Vector2.Lerp(from, to, t + 0.25f), White, 1.2f);
        }
    }

    /// <summary>Four arrows: dragging here moves the selected texels.</summary>
    private static void DrawMoveIcon(ImDrawListPtr draw, Vector2 center)
    {
        const float reach = 12f, head = 4f;
        foreach (var (color, width) in new[] { (Black, 4f), (White, 1.6f) })
        {
            draw.AddLine(center - new Vector2(reach, 0), center + new Vector2(reach, 0), color, width);
            draw.AddLine(center - new Vector2(0, reach), center + new Vector2(0, reach), color, width);
            foreach (var d in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
            {
                var side = new Vector2(-d.Y, d.X);
                Vector2 point = center + d * reach, back = center + d * (reach - head);
                var chevron = new[] { back + side * head, point, back - side * head };
                draw.AddPolyline(ref chevron[0], chevron.Length, color, ImDrawFlags.None, width);
            }
        }
    }

    /// <summary>
    /// The eyedropper's pipette cursor (tip on the hot spot, bulb filled with the colour under it) and, beside it, a
    /// loupe: the texels around the sampled one, enlarged, with the sample and the current colour side by side.
    /// </summary>
    private void DrawEyedropper()
    {
        var draw = ImGui.GetForegroundDrawList();
        Vector2 tip = cursorTip;
        uint black = Black, white = White;

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
            if (ImGui.MenuItem("Open path...")) openPathPopup = true;
            if (ImGui.MenuItem("Reload", "Ctrl+R", false, Model != null)) Reload();
            if (ImGui.MenuItem("Save textures", "Ctrl+S", false, Model != null)) SaveAll();
            ImGui.Separator();
            bool hasTexture = Model != null && state.ActiveTexture >= 0;
            if (ImGui.MenuItem("New texture...", null, false, Model != null)) OpenNewTexture();
            if (ImGui.MenuItem("Resize texture...", null, false, hasTexture)) OpenResize();
            if (ImGui.MenuItem("Import image as layer...", null, false, hasTexture)) ImportImageAsLayer();
            if (ImGui.MenuItem("Import image into texture...", null, false, hasTexture)) ImportImage();
            if (ImGui.MenuItem("Export texture as...", null, false, hasTexture)) ExportTexture();
            if (ImGui.BeginMenu("Export UV layout", Model != null))
            {
                foreach (bool over in new[] { false, true })
                {
                    ImGui.TextDisabled(over ? "Over the texture" : "Lines only (transparent)");
                    foreach (int scale in new[] { 1, 2, 4, 8 })
                        if (ImGui.MenuItem($"  {scale}x  ({uvView.TextureSize.X * scale:0}x{uvView.TextureSize.Y * scale:0})##{over}{scale}", null, false, !over || hasTexture))
                            ExportUvLayout(scale, over);
                }
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Quit")) quit = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", "Ctrl+Z", false, undo.CanUndo)) undo.Undo();
            if (ImGui.MenuItem("Redo", "Ctrl+Shift+Z", false, undo.CanRedo)) undo.Redo();
            ImGui.Separator();
            bool layers = ActiveTextureObject != null;
            if (ImGui.MenuItem("New layer", "Ctrl+Shift+N", false, layers)) NewLayer();
            if (ImGui.MenuItem("Duplicate layer", "Ctrl+J", false, layers)) DuplicateLayer();
            if (ImGui.MenuItem("Merge layer down", "Ctrl+E", false, layers && ActiveTextureObject!.ActiveLayerIndex > 0)) MergeLayerDown();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            ImGui.MenuItem("Wireframe", "Shift+Z", ref view3d.Wireframe);
            ImGui.MenuItem("Grid", null, ref view3d.Grid);
            if (ImGui.BeginMenu("Texture view (3D)"))
            {
                for (int i = 0; i < TextureViewNames.Length; i++)
                    if (ImGui.MenuItem(TextureViewNames[i], null, (int)textureView == i)) textureView = (TextureView)i;
                ImGui.EndMenu();
            }
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
        palette.Draw(ColorWheel.HsvToRgb(hsv),
            rgb => hsv = ColorWheel.RgbToHsv(rgb, hsv.X),
            then => fileBrowser.Open(false, "Load palette", Palette.FileFilter, null, null, then),
            then => fileBrowser.Open(true, "Save palette", Palette.SaveFilter, null, palette.Name, then));
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
                if (ImGui.Checkbox($"##vis{i}", ref visible)) RecordVisibility(() => obj.Hidden = !visible);
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
        ToolButton("Eraser", Tool.Eraser, "E");
        ToolButton("Eyedropper", Tool.Eyedropper, "I");
        ImGui.SameLine();
        ToolButton("Fill", Tool.Fill, "G");
        ToolButton("Zoom", Tool.Zoom, "Z");
        ImGui.TextDisabled("Lasso and box select: on the UV view.");

        if (tool == Tool.Fill)
        {
            float tolerancePercent = fillTolerance * 100f;
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderFloat("Tolerance", ref tolerancePercent, 0f, 100f, "%.0f%%")) fillTolerance = tolerancePercent / 100f;
            ImGui.Checkbox("Contiguous", ref fillContiguous);
            ImGui.SameLine();
            ImGui.Checkbox("All layers", ref fillAllLayers);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Find the area to fill by what all the layers show together, not just the selected layer.");
            ImGui.TextDisabled("Shift+click: fill selection");
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
        float smoothingPercent = smoothing * 100f;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderFloat("Smoothing", ref smoothingPercent, 0f, 100f, smoothingPercent <= 0 ? "off" : "%.0f%%")) smoothing = smoothingPercent / 100f;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A stabilizer: the stroke trails the pen on a string (up to 60 px), so small wobbles never reach the canvas.\nThe line catches up where the pen lifts. Shift+click draws a straight line from the last stroke.");
        ImGui.Checkbox("Mirror X", ref mirrorX);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Paint mirrored left/right across the middle of the active object, in both views.\nThe mirrored brush shows in blue; parts with no mirror image get nothing.");
        DrawPressureSection();
        ImGui.SetNextItemWidth(150);
        ImGui.SliderFloat("Lighting", ref view3d.Shade, 0f, 1f, view3d.Shade <= 0 ? "flat" : "%.2f");
        ImGui.Checkbox("Wireframe (Shift+Z)", ref view3d.Wireframe);
        ImGui.SetNextItemWidth(150);
        int viewIndex = (int)textureView;
        if (ImGui.Combo("Texture view", ref viewIndex, TextureViewNames, TextureViewNames.Length)) textureView = (TextureView)viewIndex;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How textures are sampled in the 3D view (the UV view always shows exact texels).\nPixel: hard texels, for pixel art.\nSmooth: bilinear, blends neighbouring texels; shimmers at a distance.\nSmooth + mipmaps: trilinear with 16x anisotropic, the usual game setting for high-res textures.");
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
            else
            {
                var assigned = material.TextureIndex >= 0 ? Model.Textures[material.TextureIndex] : null;
                string current = assigned != null ? $"{assigned.Name} {assigned.Width}x{assigned.Height}{(assigned.Dirty ? " *" : "")}" : "(no texture)";
                ImGui.SetNextItemWidth(-1);
                if (ImGui.BeginCombo("##texture", current))
                {
                    for (int i = 0; i < Model.Textures.Count; i++)
                    {
                        var t = Model.Textures[i];
                        if (ImGui.Selectable($"{t.Name}  {t.Width}x{t.Height}##t{i}", i == material.TextureIndex))
                        {
                            EndStroke();
                            Model.AssignTexture(part.MaterialIndex, i);
                            state.ActiveTexture = i;
                            uvView.RequestFit();
                        }
                    }
                    if (ImGui.Selectable("New texture...")) OpenNewTexture(part.MaterialIndex);
                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("The texture this material shows and paints (in Sable only; the model file isn't changed).");
            }
            ImGui.PopID();
        }
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped("H hide, Shift+H hide others, Alt+H reveal, / local view. Hold Alt over a colour and let go to pick it.");
        ImGui.PopStyleColor();
    }

    /// <summary>The UV view's own toolbar: the tools that only work there.</summary>
    private void DrawUvToolbar()
    {
        ImGui.SetNextWindowPos(new Vector2(uvRect.X + 6, uvRect.Y + 20));
        ImGui.SetNextWindowBgAlpha(0.85f);
        ImGui.Begin("##uvtools", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                                 | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove);
        void Tool(string label, Paint.Tool value)
        {
            bool active = tool == value;
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            if (ImGui.Button(label)) tool = value;
            if (active) ImGui.PopStyleColor();
            ImGui.SameLine();
        }
        Tool("Lasso (X)", Paint.Tool.Lasso);
        Tool("Box (M)", Paint.Tool.BoxSelect);
        if (state.ActiveSelection != null)
        {
            if (ImGui.Button("Deselect (Ctrl+D)")) state.Selection = null;
        }
        else
        {
            ImGui.TextDisabled("no selection");
        }
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        if (ImGui.Button("New texture...")) OpenNewTexture();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A new image for one of the active object's materials");
        ImGui.SameLine();
        ImGui.BeginDisabled(ActiveTextureObject == null);
        if (ImGui.Button("Resize...")) OpenResize();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Change the size of the texture shown here (all its layers); Ctrl+Z undoes it");
        ImGui.End();
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

        string right = profiler.LastHitch is { } hitch ? $"{hitch} - details in {profiler.LogPath}"
            : uvView.Hovered && uvView.MouseOnTexture
            ? $"texel {(int)uvView.MouseTexel.X}, {(int)uvView.MouseTexel.Y}   ({uvView.TextureSize.X:0}x{uvView.TextureSize.Y:0})"
            : "MMB orbit  Shift+MMB pan  Wheel zoom  1/3/7 views  5 ortho  / local  Tab submesh  H hide";
        float width = ImGui.CalcTextSize(right).X;
        ImGui.SameLine(MathF.Max(screenWidth - width - 12f, ImGui.GetCursorPosX() + 20f));
        ImGui.TextDisabled(right);
        ImGui.End();
    }

    // ---------- checks without a human ----------

    private int benchStep = -1;

    /// <summary>
    /// --bench: on the textured object with the most triangles, times hover ray casts, 3D strokes (pencil and
    /// brushes of 8, 32 and 96 texels along the same circle), a UV-view stroke, stroke setup and the texture upload,
    /// prints them and quits. Nothing is saved.
    /// </summary>
    private void RunBench()
    {
        if (benchStep < 0 || Model == null) return;
        var source = Model.Source;
        int TrianglesOf(int o) => source.Objects[o].Parts
            .Where(p => source.Parts[p].HasUvs && Model.TextureOf(p) >= 0).Sum(p => source.Parts[p].TriangleCount);
        if (benchStep == 0)
        {
            int best = Enumerable.Range(0, source.Objects.Count).OrderByDescending(TrianglesOf).FirstOrDefault(-1);
            if (best < 0 || TrianglesOf(best) == 0)
            {
                Console.WriteLine("[bench] no textured object with UVs");
                quit = true;
                return;
            }
            SelectObject(best);
            var obj = source.Objects[best];
            view3d.Camera.Frame(obj.Min, obj.Max);
            benchStep = 1;
            return;
        }
        benchStep = -1;
        quit = true;

        var bench = new Benchmark();
        int texture = state.ActiveTexture;
        var tex = Model.Textures[texture];
        var viewOrigin = new Vector2(PanelWidth, menuHeight);
        var center = new Vector2(view3d.Width, view3d.Height) * 0.5f;
        float radius = view3d.Height * 0.12f;
        Vector2 Local(float t) => center + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius;
        var camera = view3d.Camera.ToRaylib();
        bool OnActive(int i) => i == state.ActiveObject;

        int hits = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 500; i++)
        {
            var ray = Raylib.GetScreenToWorldRayEx(Local(i * 0.05f), camera, view3d.Width, view3d.Height);
            if (Raycast.Cast(source, ray.Position, ray.Direction, OnActive, out _)) hits++;
        }
        bench.Add("hover ray casts x500", watch.Elapsed.TotalMilliseconds, $"{hits} hit");

        foreach (var (label, strokeTool, size) in new[] { ("pencil", Tool.Pencil, 1f), ("brush 8", Tool.Brush, 8f), ("brush 32", Tool.Brush, 32f), ("brush 96", Tool.Brush, 96f) })
        {
            tool = strokeTool;
            brushSize = dabSize = size;
            hardness = 0.5f;
            var startRay = Raylib.GetScreenToWorldRayEx(Local(0), camera, view3d.Width, view3d.Height);
            if (Raycast.Cast(source, startRay.Position, startRay.Direction, OnActive, out var startHit)) Show3DCursor(startHit);
            FrameProfiler.Dabs = FrameProfiler.Raycasts = 0;
            watch.Restart();
            strokeTexture = texture;
            strokeIn3D = true;
            stroke = NewStroke(texture);
            lastPressure = 1f;
            SetDab(1f);
            lastMouse = Local(0);
            for (int i = 1; i <= 120; i++) Continue3D(viewOrigin + Local(i * 0.05f), 1f);
            EndStroke();
            bench.Add($"3D stroke, {label} (arc of 120 samples)", watch.Elapsed.TotalMilliseconds,
                $"{FrameProfiler.Dabs} dabs, {FrameProfiler.Raycasts} ray casts, {cursorScreenRadius:0} px radius");
        }

        bench.Time("texture upload after the strokes", () => tex.Upload());

        tool = Tool.Brush;
        brushSize = dabSize = 64f;
        bench.Time("UV stroke, brush 64 (100 dabs)", () =>
        {
            var uvStroke = NewStroke(texture);
            for (int i = 0; i < 100; i++)
                Brush.DabTexels(uvStroke, new Vector2(tex.Width * (0.2f + 0.006f * i), tex.Height * 0.5f), 64f, 0.5f);
            if (uvStroke.Finish() is { } step) undo.Push(step);
        });
        bench.Time("stroke setup x10 (copy + clear buffers)", () =>
        {
            for (int i = 0; i < 10; i++) NewStroke(texture);
        });

        var active = source.Objects[state.ActiveObject];
        bench.Print($"{source.Name} / {active.Name}: {TrianglesOf(state.ActiveObject)} tris, texture {tex.Width}x{tex.Height}");
    }

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
                            state.ActiveTexture = Model.CreateTexture(source.Parts[p].MaterialIndex, 64, 64);
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

                // Fill: a 3x3 white block in black; contiguous fills just the block, global fills both blocks.
                {
                    using var probe = PaintTexture.Create("probe", 8, 8, new Color(0, 0, 0, 255));
                    foreach (var (bx, by) in new[] { (1, 1), (5, 5) })
                        for (int y = by; y < by + 2; y++)
                        for (int x = bx; x < bx + 2; x++) probe.Pixels[y * 8 + x] = new Color(255, 255, 255, 255);
                    int Red() => probe.Pixels.Count(c => c is { R: 255, G: 0 });
                    var contiguousFill = new Stroke(probe, new Color(255, 0, 0, 255));
                    Brush.Flood(contiguousFill, 1, 1, 0f, contiguous: true, mask: null);
                    int contiguousCount = Red();
                    var globalFill = new Stroke(probe, new Color(255, 0, 0, 255));
                    Brush.Flood(globalFill, 0, 0, 0f, contiguous: false, mask: null);
                    Console.WriteLine($"[selftest] fill: contiguous {contiguousCount} texels (expect 4), then global fill of black makes {Red()} red (expect 60)");
                }

                // Box select snaps to whole texels: dragging from (1.2, 1.7) to (3.9, 2.1) covers 3 x 2 texels.
                {
                    var box = new TexelSelection(0, 8, 8);
                    box.Apply(BoxPolygon(new Vector2(1.2f, 1.7f), new Vector2(3.9f, 2.1f)), SelectionOp.Replace);
                    Console.WriteLine($"[selftest] box select covers {box.Mask.Count(m => m)} texels (expect 6)");
                }

                // UV layout export at 4x, lines only and over the texture, then import one back as an image.
                if (state.ActiveTexture >= 0)
                {
                    string dir = Path.Combine(Path.GetTempPath(), "Sable", "selftest");
                    var tex = Model.Textures[state.ActiveTexture];
                    var parts = source.Objects[state.ActiveObject].Parts.Where(p => source.Parts[p].HasUvs).ToList();
                    var size = new Vector2(tex.Width, tex.Height);
                    UvLayoutExport.Export(Model, parts, size, tex, 4, false, Path.Combine(dir, "uv_lines.png"));
                    UvLayoutExport.Export(Model, parts, size, tex, 4, true, Path.Combine(dir, "uv_over.png"));
                    using var imported = PaintTexture.FromEncoded("imported", ".png", File.ReadAllBytes(Path.Combine(dir, "uv_over.png")), null);
                    int lineTexels = imported.Pixels.Count(c => c is { R: 255, G: 255, B: 255 });
                    Console.WriteLine($"[selftest] UV export: {dir}; re-imported {imported.Width}x{imported.Height} (expect {tex.Width * 4}x{tex.Height * 4}), {lineTexels} white line pixels");
                }

                // Hiding is undoable: hide an object and a submesh of another, undo, redo, undo.
                {
                    int other = source.Objects.FindIndex(o => o.Parts.Any(p => source.Parts[p].ComponentCount > 1));
                    int part = other >= 0 ? source.Objects[other].Parts.First(p => source.Parts[p].ComponentCount > 1) : -1;
                    if (other >= 0)
                    {
                        RecordVisibility(() =>
                        {
                            source.Objects[0].Hidden = true;
                            source.Parts[part].ComponentHidden[0] = true;
                            Model.RebuildPart(part);
                        });
                        string Seen() => $"object hidden {source.Objects[0].Hidden}, submesh hidden {source.Parts[part].ComponentHidden[0]}";
                        string hidden = Seen();
                        undo.Undo();
                        string undone = Seen();
                        undo.Redo();
                        string redone = Seen();
                        undo.Undo();
                        Console.WriteLine($"[selftest] hide: [{hidden}] undo: [{undone}] redo: [{redone}]");
                    }
                }

                if (state.ActiveTexture >= 0) SelfTestLayers(Model.Textures[state.ActiveTexture]);
                if (state.ActiveTexture >= 0) SelfTestResize(Model.Textures[state.ActiveTexture]);
                SelfTestStrokes();

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
                cursorIcon = CursorIcon.Eyedropper;
                cursorTip = new Vector2(PanelWidth, menuHeight) + local;
                selfTestIcons = true;
                break;
            }
        }
    }

    private void TakeScreenshotIfDue()
    {
        if (options.ScreenshotPath == null || screenshotFrames < 0) return;
        if (screenshotFrames-- > 0) return;
        profiler.Skip();
        Image image = Raylib.LoadImageFromScreen();
        Raylib.ExportImage(image, options.ScreenshotPath);
        Raylib.UnloadImage(image);
        quit = true;
    }

    /// <summary>
    /// Settings are read at start and written on close. Screenshot and self-test runs leave them alone, so they
    /// start from the defaults and don't overwrite what the user set.
    /// </summary>
    private bool KeepsSettings => options.ScreenshotPath == null && !options.SelfTest && !options.Bench && !options.LinkTest;

    private void ApplySettings()
    {
        if (!KeepsSettings) return;
        var saved = Settings.Load();
        if (saved.Hsv is { Length: 3 }) hsv = new Vector3(saved.Hsv[0], saved.Hsv[1], saved.Hsv[2]);
        brushSize = Math.Clamp(saved.BrushSize, 1f, 256f);
        dabSize = brushSize;
        hardness = Math.Clamp(saved.Hardness, 0f, 1f);
        opacity = Math.Clamp(saved.Opacity, 0.01f, 1f);
        flow = Math.Clamp(saved.Flow, 0.01f, 1f);
        pressureToFlow = saved.PressureToFlow;
        pressureToSize = saved.PressureToSize;
        pressureCurve = Math.Clamp(saved.PressureCurve, 0.4f, 3f);
        fillTolerance = Math.Clamp(saved.FillTolerance, 0f, 1f);
        fillContiguous = saved.FillContiguous;
        if (options.TextureView == 0) textureView = (TextureView)Math.Clamp(saved.TextureView, 0, 2);
        view3d.Shade = Math.Clamp(saved.Lighting, 0f, 1f);
        view3d.Grid = saved.Grid;
        view3d.Wireframe = saved.Wireframe;
        uvView.PixelGrid = saved.UvTexelGrid;
        uvView.ShowSiblings = saved.UvShowSiblings;
        split = Math.Clamp(saved.Split, 0.15f, 0.85f);
        newTextureSize = ValidTextureSize(saved.NewTextureSize);
        fillAllLayers = saved.FillAllLayers;
        palette.LoadFrom(saved);
        mirrorX = saved.MirrorX;
        smoothing = Math.Clamp(saved.Smoothing, 0f, 1f);
        if (saved.RecentFolders != null) fileBrowser.RecentFolders.AddRange(saved.RecentFolders.Where(Directory.Exists).Take(8));
        layersOpen = saved.LayersOpen;

        // Only restore a window placement that is still on a monitor.
        if (saved.WindowWidth >= 900 && saved.WindowHeight >= 560)
        {
            Raylib.SetWindowSize(saved.WindowWidth, saved.WindowHeight);
            bool onScreen = false;
            for (int m = 0; m < Raylib.GetMonitorCount(); m++)
            {
                var at = Raylib.GetMonitorPosition(m);
                int mw = Raylib.GetMonitorWidth(m), mh = Raylib.GetMonitorHeight(m);
                if (saved.WindowX + 50 >= at.X && saved.WindowY + 20 >= at.Y && saved.WindowX + 50 < at.X + mw && saved.WindowY + 20 < at.Y + mh)
                    onScreen = true;
            }
            if (onScreen) Raylib.SetWindowPosition(saved.WindowX, saved.WindowY);
        }
        if (saved.WindowMaximized) Raylib.MaximizeWindow();
    }

    private void SaveSettings()
    {
        if (!KeepsSettings || !Raylib.IsWindowReady()) return;
        var saved = new Settings
        {
            Hsv = new[] { hsv.X, hsv.Y, hsv.Z },
            BrushSize = brushSize,
            Hardness = hardness,
            Opacity = opacity,
            Flow = flow,
            PressureToFlow = pressureToFlow,
            PressureToSize = pressureToSize,
            PressureCurve = pressureCurve,
            FillTolerance = fillTolerance,
            FillContiguous = fillContiguous,
            TextureView = (int)textureView,
            Lighting = view3d.Shade,
            Grid = view3d.Grid,
            Wireframe = view3d.Wireframe,
            UvTexelGrid = uvView.PixelGrid,
            UvShowSiblings = uvView.ShowSiblings,
            Split = split,
            NewTextureSize = newTextureSize,
            FillAllLayers = fillAllLayers,
            MirrorX = mirrorX,
            Smoothing = smoothing,
            RecentFolders = fileBrowser.RecentFolders.ToList(),
            LayersOpen = layersOpen,
            WindowMaximized = Raylib.IsWindowMaximized(),
        };
        // Keep the un-maximized placement, so restoring from maximized goes back to it.
        var previous = Settings.Load();
        if (saved.WindowMaximized)
        {
            (saved.WindowX, saved.WindowY, saved.WindowWidth, saved.WindowHeight) =
                (previous.WindowX, previous.WindowY, previous.WindowWidth, previous.WindowHeight);
        }
        else
        {
            var position = Raylib.GetWindowPosition();
            (saved.WindowX, saved.WindowY) = ((int)position.X, (int)position.Y);
            (saved.WindowWidth, saved.WindowHeight) = (Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
        }
        palette.SaveTo(saved);
        saved.Save();
    }

    public void Dispose()
    {
        SaveSettings();
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
