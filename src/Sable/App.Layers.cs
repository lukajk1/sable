using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using Sable.Paint;
using BlendMode = Sable.Paint.BlendMode;

namespace Sable;

/// <summary>The layers window over the UV view, the New texture dialog, and layer shortcuts.</summary>
internal sealed partial class App
{
    private const float LayersWidth = 250f;
    /// <summary>Power-of-two sizes only: they mip down evenly and compress (BC/DXT, ASTC) without padding.</summary>
    private static readonly int[] TextureSizes = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };
    private static readonly string[] FillNames = { "Material colour", "White", "Paint colour", "Transparent" };

    private bool layersOpen = true;
    private LayerState? layerDragBefore;
    private int renamingLayer = -1;
    private string renameBuffer = "";
    private bool fillAllLayers;

    // New texture dialog.
    private bool openNewTexture;
    private int newTextureMaterial = -1;
    private int newTextureWidth = 256, newTextureHeight = 256;
    private int newTextureFill;
    private string newTextureName = "";

    private PaintTexture? ActiveTextureObject =>
        Model != null && state.ActiveTexture >= 0 && state.ActiveTexture < Model.Textures.Count ? Model.Textures[state.ActiveTexture] : null;

    /// <summary>Runs a change to a texture's layer stack as one undo step (nothing is recorded if nothing changed).</summary>
    private void LayerEdit(PaintTexture texture, Action change)
    {
        EndStroke();
        var before = texture.Snapshot();
        change();
        var after = texture.Snapshot();
        if (before.SameAs(after)) return;
        texture.Touch();
        undo.Push(new LayerStep(texture, before, after));
    }

    private void OnActiveLayers(Action<PaintTexture> change)
    {
        if (ActiveTextureObject is { } texture) LayerEdit(texture, () => change(texture));
    }

    private void NewLayer() => OnActiveLayers(t => t.InsertLayer(Layer.Transparent(t.NextLayerName(), t.Width, t.Height)));
    private void DuplicateLayer() => OnActiveLayers(t => t.InsertLayer(t.ActiveLayer.Clone($"{t.ActiveLayer.Name} copy")));
    private void MergeLayerDown() => OnActiveLayers(t => t.MergeDown());

    /// <summary>Ctrl+Shift+N new layer, Ctrl+J duplicate, Ctrl+E merge down (Photoshop's keys).</summary>
    private void HandleLayerShortcuts(bool shift)
    {
        if (shift && Raylib.IsKeyPressed(KeyboardKey.N)) NewLayer();
        if (Raylib.IsKeyPressed(KeyboardKey.J)) DuplicateLayer();
        if (Raylib.IsKeyPressed(KeyboardKey.E)) MergeLayerDown();
    }

    /// <summary>Adds an image file as a new layer of the active texture, stretched to its size if it differs.</summary>
    private void ImportImageAsLayer()
    {
        if (ActiveTextureObject is not { } texture) return;
        Pick(false, "Import image as layer", FileDialogs.ImageFilter, null, path =>
        {
            var pixels = PaintTexture.DecodeImage(Path.GetExtension(path).ToLowerInvariant(), File.ReadAllBytes(path),
                texture.Width, texture.Height, out _, out _);
            LayerEdit(texture, () => texture.InsertLayer(new Layer(Path.GetFileNameWithoutExtension(path), pixels)));
            SetStatus($"Added {Path.GetFileName(path)} as a layer of {texture.Name}.", error: false);
        });
    }

    /// <summary>Brings back each texture's saved layers (the hidden .sable file beside its image).</summary>
    private void LoadLayers()
    {
        if (Model == null) return;
        var notes = new List<string>();
        int restored = 0;
        foreach (var texture in Model.Textures)
        {
            if (texture.FilePath is not { } path) continue;
            if (LayerFile.TryLoad(texture, path, out string? note)) restored++;
            if (note != null) notes.Add(note);
        }
        foreach (string note in notes) Model.Source.Warnings.Add(note);
        if (notes.Count > 0) SetStatus(notes[0], error: true);
        else if (restored > 0) SetStatus($"{status}; layers restored on {restored} texture{(restored == 1 ? "" : "s")}", error: false);
    }

    // ---------- the layers window ----------

    private void DrawLayersWindow()
    {
        if (ActiveTextureObject is not { } texture) return;
        ImGui.SetNextWindowPos(new Vector2(uvRect.X + uvRect.Width - LayersWidth - 6, uvRect.Y + 56));
        ImGui.SetNextWindowSizeConstraints(new Vector2(LayersWidth, 0), new Vector2(LayersWidth, MathF.Max(120, uvRect.Height - 76)));
        ImGui.SetNextWindowCollapsed(!layersOpen, ImGuiCond.Appearing);
        ImGui.SetNextWindowBgAlpha(0.9f);
        bool open = ImGui.Begin("Layers###layers", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                                                   | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);
        layersOpen = open;
        if (!open)
        {
            ImGui.End();
            return;
        }

        var before = texture.Snapshot();
        var active = texture.ActiveLayer;

        ImGui.SetNextItemWidth(110);
        int blend = (int)active.Blend;
        if (ImGui.Combo("##blend", ref blend, Layer.BlendNames, Layer.BlendNames.Length))
            LayerEdit(texture, () => active.Blend = (BlendMode)blend);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("How this layer mixes with the ones below.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        float percent = active.Opacity * 100f;
        if (ImGui.SliderFloat("##opacity", ref percent, 0f, 100f, "%.0f%%"))
        {
            active.Opacity = percent / 100f;
            texture.Touch();
        }
        // A drag is one undo step, from where it started.
        if (ImGui.IsItemActivated()) layerDragBefore = before;
        if (ImGui.IsItemDeactivated() && layerDragBefore != null)
        {
            var after = texture.Snapshot();
            if (!layerDragBefore.SameAs(after)) undo.Push(new LayerStep(texture, layerDragBefore, after));
            layerDragBefore = null;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Layer opacity");

        float rows = MathF.Min(texture.Layers.Count, 8) * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.BeginChild("##layerlist", new Vector2(0, rows), ImGuiChildFlags.Borders);
        for (int i = texture.Layers.Count - 1; i >= 0; i--)
        {
            var layer = texture.Layers[i];
            ImGui.PushID(i);
            bool visible = layer.Visible;
            if (ImGui.Checkbox("##visible", ref visible)) LayerEdit(texture, () => layer.Visible = visible);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show / hide");
            ImGui.SameLine();
            if (renamingLayer == i)
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.IsWindowAppearing() || !ImGui.IsAnyItemActive()) ImGui.SetKeyboardFocusHere();
                bool done = ImGui.InputText("##name", ref renameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                if (done || ImGui.IsItemDeactivated())
                {
                    string name = renameBuffer.Trim();
                    if (name.Length > 0) LayerEdit(texture, () => layer.Name = name);
                    renamingLayer = -1;
                }
            }
            else
            {
                string label = layer.Name;
                if (layer.Blend != BlendMode.Normal) label += $"  {layer.Blend}";
                if (layer.Opacity < 1f) label += $"  {layer.Opacity * 100f:0}%";
                if (!layer.Visible) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                if (ImGui.Selectable($"{label}##layer", i == texture.ActiveLayerIndex, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    EndStroke();
                    texture.ActiveLayerIndex = i;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        renamingLayer = i;
                        renameBuffer = layer.Name;
                    }
                }
                if (!layer.Visible) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to paint on it, double-click to rename");
            }
            ImGui.PopID();
        }
        ImGui.EndChild();

        bool Button(string label, string tip, bool enabled = true)
        {
            ImGui.BeginDisabled(!enabled);
            bool clicked = ImGui.Button(label);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tip);
            ImGui.SameLine();
            return clicked;
        }
        int index = texture.ActiveLayerIndex, count = texture.Layers.Count;
        if (Button("New", "New transparent layer above this one (Ctrl+Shift+N)")) NewLayer();
        if (Button("Copy", "Duplicate this layer (Ctrl+J)")) DuplicateLayer();
        if (Button("Del", "Delete this layer", count > 1)) LayerEdit(texture, texture.DeleteActiveLayer);
        if (Button("Up", "Move up", index < count - 1)) LayerEdit(texture, () => texture.MoveActiveLayer(1));
        if (Button("Dn", "Move down", index > 0)) LayerEdit(texture, () => texture.MoveActiveLayer(-1));
        ImGui.NewLine();
        if (Button("Merge down", "Merge this layer into the one below (Ctrl+E)", index > 0)) MergeLayerDown();
        if (Button("Flatten", "Merge every layer into one", count > 1)) LayerEdit(texture, texture.Flatten);
        ImGui.NewLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(count > 1 || texture.HasLayers
            ? "Ctrl+S saves the flattened image, and the layers beside it in a hidden .sable file."
            : "Paint goes to the selected layer. Saving writes the flattened image.");
        ImGui.PopStyleColor();
        ImGui.End();
    }

    // ---------- New texture ----------

    /// <summary>Opens the New texture dialog for a material (-1: the active texture's material, or the first one).</summary>
    private void OpenNewTexture(int material = -1)
    {
        if (Model == null) return;
        var candidates = NewTextureMaterials();
        if (candidates.Count == 0)
        {
            SetStatus("Nothing to put a texture on: the object needs UVs (unwrap it in Blender).", error: true);
            return;
        }
        if (!candidates.Contains(material))
            material = candidates.FirstOrDefault(m => Model.Source.Materials[m].TextureIndex == state.ActiveTexture && state.ActiveTexture >= 0, candidates[0]);
        newTextureMaterial = material;
        newTextureName = Path.GetFileNameWithoutExtension(Model.UniqueTextureName(Model.Source.Materials[material].Name));
        if (ActiveTextureObject is { } current && TextureSizes.Contains(current.Width) && TextureSizes.Contains(current.Height))
            (newTextureWidth, newTextureHeight) = (current.Width, current.Height);
        openNewTexture = true;
    }

    /// <summary>The materials a new texture can go on: the active object's (or every object's) parts with UVs.</summary>
    private List<int> NewTextureMaterials()
    {
        var source = Model!.Source;
        return Enumerable.Range(0, source.Parts.Count)
            .Where(p => source.Parts[p].HasUvs && (state.ActiveObject < 0 || source.Parts[p].ObjectIndex == state.ActiveObject))
            .Select(p => source.Parts[p].MaterialIndex)
            .Distinct()
            .ToList();
    }

    private void DrawNewTexturePopup()
    {
        if (openNewTexture)
        {
            ImGui.OpenPopup("New texture");
            openNewTexture = false;
        }
        ImGui.SetNextWindowPos(new Vector2(uvRect.X + uvRect.Width * 0.5f, uvRect.Y + uvRect.Height * 0.4f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("New texture", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) return;
        if (Model == null || newTextureMaterial < 0 || newTextureMaterial >= Model.Source.Materials.Count)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var materials = Model.Source.Materials;
        var candidates = NewTextureMaterials();
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Material", materials[newTextureMaterial].Name))
        {
            foreach (int m in candidates)
                if (ImGui.Selectable($"{materials[m].Name}##m{m}", m == newTextureMaterial))
                {
                    newTextureMaterial = m;
                    newTextureName = Path.GetFileNameWithoutExtension(Model.UniqueTextureName(materials[m].Name));
                }
            ImGui.EndCombo();
        }

        void SizeCombo(string label, ref int value)
        {
            ImGui.SetNextItemWidth(90);
            if (!ImGui.BeginCombo(label, value.ToString())) return;
            foreach (int size in TextureSizes)
                if (ImGui.Selectable(size.ToString(), size == value)) value = size;
            ImGui.EndCombo();
        }
        SizeCombo("##width", ref newTextureWidth);
        ImGui.SameLine();
        ImGui.TextUnformatted("x");
        ImGui.SameLine();
        SizeCombo("Size", ref newTextureHeight);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Powers of two, so the texture mips down evenly and compresses cleanly in game engines.");

        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Fill", ref newTextureFill, FillNames, FillNames.Length);
        ImGui.SetNextItemWidth(220);
        ImGui.InputText("Name", ref newTextureName, 64);

        var info = materials[newTextureMaterial];
        string name = Model.UniqueTextureName(string.IsNullOrWhiteSpace(newTextureName) ? info.Name : newTextureName.Trim());
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 320);
        if (info.TextureIndex >= 0)
            ImGui.TextUnformatted($"{info.Name} uses {Model.Textures[info.TextureIndex].Name} now. The new texture takes its place; the old one stays open and can be switched back in the panel.");
        ImGui.TextUnformatted($"Ctrl+S saves it as {Path.GetFileName(DefaultSavePath(name))} next to the model. Hook it up to the material in Blender.");
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        if (ImGui.Button("Create", new Vector2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter))
        {
            Color? fill = newTextureFill switch
            {
                1 => Color.White,
                2 => PaintColor,
                3 => new Color(0, 0, 0, 0),
                _ => null,
            };
            EndStroke();
            state.ActiveTexture = Model.CreateTexture(newTextureMaterial, newTextureWidth, newTextureHeight, fill, name);
            state.Selection = null;
            uvView.RequestFit();
            SetStatus($"Created {name} ({newTextureWidth}x{newTextureHeight}) on {info.Name}; Ctrl+S saves it to {DefaultSavePath(name)}", error: false);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    // ---------- self-test ----------

    /// <summary>
    /// Layers on the active texture: paint on a new layer, blend modes and opacity, the eraser, soft paint on a
    /// transparent layer, undo and redo, the layer file round trip, and merge down. Leaves two layers for the screenshot.
    /// </summary>
    private void SelfTestLayers(PaintTexture tex)
    {
        string C(Color c) => $"({c.R},{c.G},{c.B},{c.A})";
        Color Shown(int x, int y) { tex.EnsureComposite(); return tex.Get(x, y); }
        var baseColor = Shown(1, 1);
        int startLayers = tex.Layers.Count;

        NewLayer();
        bool clear = tex.Pixels.All(c => c.A == 0);
        var red = new Color(255, 0, 0, 255);
        var paint = new Stroke(tex, red);
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) paint.Apply(x, y, 1f);
        undo.Push(paint.Finish()!);
        var normal = Shown(1, 1);
        var elsewhere = Shown(tex.Width / 2, tex.Height / 2);
        LayerEdit(tex, () => tex.ActiveLayer.Blend = BlendMode.Multiply);
        var multiply = Shown(1, 1);
        LayerEdit(tex, () => tex.ActiveLayer.Opacity = 0.5f);
        var half = Shown(1, 1);
        Console.WriteLine($"[selftest] layers: new layer transparent {clear}; base {C(baseColor)}; red normal {C(normal)} (expect red); "
                          + $"multiply {C(multiply)} (expect ({baseColor.R},0,0)); multiply 50% {C(half)}; untouched texel matches base {elsewhere.Equals(tex.Layers[0].Pixels[tex.Height / 2 * tex.Width + tex.Width / 2])}");

        var erase = new Stroke(tex, red, erase: true);
        erase.Apply(1, 1, 1f);
        undo.Push(erase.Finish()!);
        var erased = Shown(1, 1);
        var soft = new Stroke(tex, new Color(200, 100, 50, 255), 0.5f);
        soft.Apply(10, 10, 1f);
        undo.Push(soft.Finish()!);
        var softTexel = tex.Pixels[10 * tex.Width + 10];
        Console.WriteLine($"[selftest] layers: erased shows {C(erased)} (expect base {C(baseColor)}); soft paint on transparent {C(softTexel)} (expect (200,100,50,128))");

        undo.Undo();
        undo.Undo();
        var undone = Shown(1, 1);
        undo.Undo();
        undo.Undo();
        var undoneSettings = (tex.ActiveLayer.Blend, tex.ActiveLayer.Opacity, Shown(1, 1));
        undo.Redo();
        undo.Redo();
        Console.WriteLine($"[selftest] layers: undo eraser -> {C(undone)} (expect {C(half)}); undo blend+opacity -> {undoneSettings.Item1} {undoneSettings.Item2} {C(undoneSettings.Item3)}; "
                          + $"redo -> {tex.ActiveLayer.Blend} {tex.ActiveLayer.Opacity}");

        // Round trip through the image and its layer file, then again after the image changed elsewhere.
        string dir = Path.Combine(Path.GetTempPath(), "Sable", "selftest");
        Directory.CreateDirectory(dir);
        string png = Path.Combine(dir, "layers.png");
        tex.ExportTo(png);
        LayerFile.Write(tex, png);
        using (var reopened = PaintTexture.FromEncoded("layers.png", ".png", File.ReadAllBytes(png), png))
        {
            bool loaded = LayerFile.TryLoad(reopened, png, out string? note);
            reopened.EnsureComposite();
            bool same = reopened.Composite.SequenceEqual(tex.Composite);
            Console.WriteLine($"[selftest] layer file: restored {loaded}, {reopened.Layers.Count} layers (expect {tex.Layers.Count}), "
                              + $"top {reopened.Layers[^1].Blend} {reopened.Layers[^1].Opacity}, composite identical {same}{(note != null ? $", note: {note}" : "")}");
        }
        var changed = (Color[])tex.Composite.Clone();
        changed[0] = new Color(1, 2, 3, 255);
        using (var edited = PaintTexture.Create("edited", tex.Width, tex.Height, Color.Blank))
        {
            Array.Copy(changed, edited.Pixels, changed.Length);
            edited.Touch();
            edited.ExportTo(png);
        }
        using (var reopened = PaintTexture.FromEncoded("layers.png", ".png", File.ReadAllBytes(png), png))
        {
            bool loaded = LayerFile.TryLoad(reopened, png, out string? note);
            Console.WriteLine($"[selftest] layer file after an outside edit: restored {loaded} (expect False), note: {note}");
        }

        var beforeMerge = (Color[])tex.Composite.Clone();
        MergeLayerDown();
        tex.EnsureComposite();
        bool mergeSame = tex.Composite.SequenceEqual(beforeMerge);
        int merged = tex.Layers.Count;
        undo.Undo();
        Console.WriteLine($"[selftest] merge down: {startLayers + 1} -> {merged} layers, looks the same {mergeSame}; undo -> {tex.Layers.Count}");
    }
}
