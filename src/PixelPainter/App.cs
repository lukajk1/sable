using System.Numerics;
using ImGuiNET;
using PixelPainter.Model;
using PixelPainter.Rendering;
using PixelPainter.Views;
using Raylib_cs;
using rlImGui_cs;

namespace PixelPainter;

/// <summary>
/// The window: a menu bar, an object panel on the left, the 3D view and the UV view side by side (drag the bar
/// between them), and a status bar. Models load on a background thread; drop one on the window or use File > Open.
/// </summary>
internal sealed class App : IDisposable
{
    private const float PanelWidth = 290f;
    private const float StatusHeight = 26f;
    private const float SplitterWidth = 6f;

    private readonly AppOptions options;
    private readonly Viewport3D view3d = new();
    private readonly UvView uvView = new();
    private LitShader? shader;
    private GpuModel? model;
    private int selected = -1;

    private Task<LoadedModel>? loading;
    private string? loadingPath;
    private volatile string progress = "";
    private string status = "Drop a model on the window, or File > Open (fbx, gltf, glb, obj, blend).";
    private bool statusIsError;

    private float split = 0.5f;
    private bool draggingSplit;
    private float menuHeight = 19f;
    private int screenshotFrames = -1;
    private bool quit;

    public App(AppOptions options) => this.options = options;

    public void Run()
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint);
        Raylib.InitWindow(1600, 900, "Pixel Painter");
        Raylib.SetWindowMinSize(800, 500);
        Raylib.SetExitKey(KeyboardKey.Null);
        rlImGui.Setup(true);
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        shader = new LitShader();

        if (options.ModelPath != null) StartLoad(options.ModelPath);
        else if (options.ScreenshotPath != null) screenshotFrames = 3;

        while (!Raylib.WindowShouldClose() && !quit)
        {
            FinishLoad();
            HandleDroppedFiles();
            HandleShortcuts();

            float w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
            var area = new Rectangle(PanelWidth, menuHeight, w - PanelWidth, h - menuHeight - StatusHeight);
            var (rect3d, rectUv, splitter) = SplitArea(area);

            var io = ImGui.GetIO();
            Vector2 mouse = Raylib.GetMousePosition();
            bool free = !io.WantCaptureMouse && !draggingSplit;
            UpdateSplitter(area, splitter, mouse, free);

            var (sMin, sMax) = model == null ? (new Vector3(-5), new Vector3(5)) : (model.Source.Min, model.Source.Max);
            Vector3? selMin = selected >= 0 ? model!.Source.Parts[selected].Min : null;
            Vector3? selMax = selected >= 0 ? model!.Source.Parts[selected].Max : null;
            view3d.Update(rect3d, free && Raylib.CheckCollisionPointRec(mouse, rect3d), sMin, sMax, selMin, selMax);
            uvView.Update(rectUv, free && Raylib.CheckCollisionPointRec(mouse, rectUv), model, selected);
            if (view3d.ClickRay is { } ray && model != null) selected = model.Pick(ray);

            view3d.Render(model, selected, shader);
            uvView.Render(model, selected);

            Raylib.BeginDrawing();
            Raylib.ClearBackground(new Color(30, 30, 30, 255));
            DrawTarget(view3d.Texture, rect3d);
            DrawTarget(uvView.Texture, rectUv);
            Raylib.DrawRectangleRec(splitter, draggingSplit || Raylib.CheckCollisionPointRec(mouse, splitter)
                ? new Color(90, 90, 90, 255) : new Color(22, 22, 22, 255));
            DrawViewLabel(rect3d, view3d.Camera.Ortho ? "3D  ortho" : "3D  persp");
            DrawViewLabel(rectUv, "UV");

            rlImGui.Begin();
            DrawMenu();
            DrawPanel(h);
            DrawStatusBar(w, h);
            rlImGui.End();

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
        bool over = free && Raylib.CheckCollisionPointRec(mouse, bar);
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

    // ---------- loading ----------

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

        model?.Dispose();
        model = new GpuModel(task.Result, shader!.Shader);
        selected = -1;
        if (options.SelectPart != null)
            selected = model.Source.Parts.FindIndex(p => p.Name.Contains(options.SelectPart, StringComparison.OrdinalIgnoreCase));
        view3d.Camera.Frame(model.Source.Min, model.Source.Max);
        uvView.RequestFit();
        Raylib.SetWindowTitle($"Pixel Painter - {model.Source.Name}");

        var s = model.Source;
        SetStatus($"Opened {s.Name}: {s.Parts.Count} parts, {s.Parts.Sum(p => p.TriangleCount)} tris, {s.Textures.Count} textures"
                  + (s.Warnings.Count > 0 ? $", {s.Warnings.Count} warnings" : ""), error: false);
        if (options.ScreenshotPath != null) screenshotFrames = 4;
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
        if (model != null) StartLoad(model.Source.SourcePath);
    }

    private void SetStatus(string text, bool error)
    {
        status = text;
        statusIsError = error;
    }

    // ---------- input ----------

    private void HandleShortcuts()
    {
        if (ImGui.GetIO().WantCaptureKeyboard) return;
        bool ctrl = Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);
        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.O)) OpenDialog();
        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.R)) Reload();
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) selected = -1;
        if (Raylib.IsKeyPressed(KeyboardKey.Z) && !ctrl) view3d.Wireframe = !view3d.Wireframe;
    }

    // ---------- UI ----------

    private void DrawMenu()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        menuHeight = ImGui.GetWindowHeight();
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open...", "Ctrl+O")) OpenDialog();
            if (ImGui.MenuItem("Reload", "Ctrl+R", false, model != null)) Reload();
            ImGui.Separator();
            if (ImGui.MenuItem("Quit")) quit = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            ImGui.MenuItem("Wireframe", "Z", ref view3d.Wireframe);
            ImGui.MenuItem("Grid", null, ref view3d.Grid);
            bool ortho = view3d.Camera.Ortho;
            if (ImGui.MenuItem("Orthographic", "Numpad 5", ref ortho)) view3d.Camera.Ortho = ortho;
            ImGui.Separator();
            ImGui.MenuItem("UV texel grid", null, ref uvView.PixelGrid);
            ImGui.MenuItem("UV: show parts sharing the texture", null, ref uvView.ShowSiblings);
            ImGui.Separator();
            if (ImGui.MenuItem("Frame all", "Home", false, model != null)) view3d.Camera.Frame(model!.Source.Min, model.Source.Max);
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

        if (model == null)
        {
            ImGui.TextWrapped(loading != null ? progress : "No model loaded.");
            ImGui.End();
            return;
        }

        var source = model.Source;
        ImGui.TextUnformatted(source.Name);
        ImGui.TextDisabled(Path.GetDirectoryName(source.SourcePath) ?? "");
        if (source.ImportedPath != source.SourcePath) ImGui.TextDisabled("(converted with Blender)");

        ImGui.SliderFloat("Lighting", ref view3d.Shade, 0f, 1f, view3d.Shade <= 0 ? "flat" : "%.2f");

        if (ImGui.CollapsingHeader($"Objects ({source.Parts.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.BeginChild("##parts", new Vector2(0, 260), ImGuiChildFlags.Borders);
            for (int i = 0; i < source.Parts.Count; i++)
            {
                var part = source.Parts[i];
                bool visible = part.Visible;
                if (ImGui.Checkbox($"##vis{i}", ref visible)) part.Visible = visible;
                ImGui.SameLine();
                if (!part.HasUvs) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.45f, 1f));
                if (ImGui.Selectable($"{part.Name}##part{i}", selected == i)) selected = selected == i ? -1 : i;
                if (!part.HasUvs) ImGui.PopStyleColor();
            }
            ImGui.EndChild();
        }

        if (selected >= 0 && ImGui.CollapsingHeader("Selected", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var part = source.Parts[selected];
            var material = source.Materials[part.MaterialIndex];
            ImGui.TextUnformatted(part.Name);
            ImGui.ColorButton("##color", material.Color, ImGuiColorEditFlags.NoTooltip, new Vector2(14, 14));
            ImGui.SameLine();
            ImGui.TextUnformatted(material.Name);
            ImGui.TextUnformatted(material.TextureIndex >= 0
                ? $"Texture: {source.Textures[material.TextureIndex].Name} ({model.Textures[material.TextureIndex].Width}x{model.Textures[material.TextureIndex].Height})"
                : "Texture: none");
            ImGui.TextUnformatted($"{part.Positions.Length} verts, {part.TriangleCount} tris");
            ImGui.TextUnformatted(part.HasUvs ? "UVs: yes" : "UVs: none");
        }

        if (source.Textures.Count > 0 && ImGui.CollapsingHeader($"Textures ({source.Textures.Count})"))
        {
            for (int i = 0; i < source.Textures.Count; i++)
                ImGui.TextUnformatted($"{source.Textures[i].Name}  {model.Textures[i].Width}x{model.Textures[i].Height}");
        }

        if (source.Warnings.Count > 0 && ImGui.CollapsingHeader($"Warnings ({source.Warnings.Count})"))
        {
            foreach (string warning in source.Warnings) ImGui.TextWrapped(warning);
        }

        ImGui.End();
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

        string right = uvView.HoverTexel is { } t
            ? $"texel {t.X}, {t.Y}   ({uvView.TextureSize.X:0}x{uvView.TextureSize.Y:0})"
            : "MMB orbit  Shift+MMB pan  Wheel zoom  Numpad 1/3/7 views  5 ortho  . frame  Z wire";
        float width = ImGui.CalcTextSize(right).X;
        ImGui.SameLine(MathF.Max(screenWidth - width - 12f, ImGui.GetCursorPosX() + 20f));
        ImGui.TextDisabled(right);
        ImGui.End();
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
        model?.Dispose();
        view3d.Dispose();
        uvView.Dispose();
        shader?.Dispose();
        if (Raylib.IsWindowReady())
        {
            rlImGui.Shutdown();
            Raylib.CloseWindow();
        }
    }
}
