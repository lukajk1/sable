using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using Sable.Paint;
using Sable.UI;
using BlendMode = Sable.Paint.BlendMode;

namespace Sable;

/// <summary>The layers window over the UV view, the New texture dialog, and layer shortcuts.</summary>
internal sealed partial class App
{
    private const float LayersWidth = 250f;
    /// <summary>Quick picks for the size; any multiple of 4 up to 4096 can be typed.</summary>
    private static readonly int[] TextureSizes = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };
    private const int MaxTextureSize = 4096;

    /// <summary>
    /// The nearest allowed size: a multiple of 4 (block compression works in 4x4 blocks) from 4 to
    /// <see cref="MaxTextureSize"/>.
    /// </summary>
    private static int ValidTextureSize(int size) => Math.Clamp((int)MathF.Round(size / 4f) * 4, 4, MaxTextureSize);
    private static readonly string[] FillNames = { "Material colour", "White", "Paint colour", "Transparent" };

    private const int ThumbSize = 34;
    /// <summary>Most rows the layer list shows before it scrolls.</summary>
    private const int LayerRows = 6;

    private sealed class Thumbnail
    {
        public Texture2D Gpu;
        public int Version = -1;
        public double Made;
        public int Seen;
    }
    private readonly Dictionary<Layer, Thumbnail> thumbnails = new();
    private readonly Color[] thumbnailPixels = new Color[ThumbSize * ThumbSize];
    private int thumbnailFrame;

    private bool layersOpen = true;
    private LayerState? layerDragBefore;
    private int renamingLayer = -1;
    private string renameBuffer = "";
    private bool fillAllLayers;

    // New texture dialog.
    private bool openNewTexture;
    private int newTextureMaterial = -1;
    /// <summary>Width and height of new textures: always square.</summary>
    private int newTextureSize = 256;
    private int newTextureFill;
    private string newTextureName = "";
    /// <summary>Other materials the new texture also goes on (they share the UV layout without overlapping).</summary>
    private readonly HashSet<int> newTextureAlso = new();
    private List<(int A, int B, int Cells)> uvOverlaps = new();

    /// <summary>Two materials' UVs cover the same part of the texture (more than a few stray cells of 256x256).</summary>
    private bool UvsOverlap(int a, int b) =>
        uvOverlaps.Any(o => ((o.A == a && o.B == b) || (o.A == b && o.B == a)) && o.Cells > 16);

    /// <summary>
    /// Ticks the other materials a texture for <paramref name="material"/> can cover: those without a texture of
    /// their own whose UVs overlap neither it nor each other.
    /// </summary>
    private void SuggestSharedMaterials(int material, List<int> candidates)
    {
        newTextureAlso.Clear();
        foreach (int m in candidates)
        {
            if (m == material || Model!.Source.Materials[m].TextureIndex >= 0 || UvsOverlap(m, material)) continue;
            if (newTextureAlso.Any(other => UvsOverlap(m, other))) continue;
            newTextureAlso.Add(m);
        }
    }

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
    private void DuplicateLayer() => OnActiveLayers(t =>
    {
        if (!t.ActiveLayer.IsMask) t.InsertLayer(t.ActiveLayer.Clone($"{t.ActiveLayer.Name} copy"));
    });

    /// <summary>Adds the texture's smoothness mask (one per texture) on top, ready to paint.</summary>
    private void NewSmoothnessMask()
    {
        if (ActiveTextureObject is not { Mask: null } texture) return;
        LayerEdit(texture, texture.AddSmoothnessMask);
        SetStatus($"Added a smoothness mask to {texture.Name}: paint where it's smooth (any colour; the alpha counts). "
                  + "File > Export Unity material writes it as the smoothness map.", error: false);
    }
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
        Pick(false, "Import image as layer", FileBrowser.ImageFilter, null, path =>
        {
            var pixels = PaintTexture.DecodeImage(Path.GetExtension(path).ToLowerInvariant(), File.ReadAllBytes(path),
                texture.Width, texture.Height, out _, out _);
            LayerEdit(texture, () => texture.InsertLayer(new Layer(Path.GetFileNameWithoutExtension(path), pixels)));
            SetStatus($"Added {Path.GetFileName(path)} as a layer of {texture.Name}.", error: false);
        });
    }

    /// <summary>Brings back each texture's saved layers (the hidden .sable file beside its image).</summary>
    private void LoadLayers(IEnumerable<PaintTexture> textures)
    {
        if (Model == null) return;
        var notes = new List<string>();
        int restored = 0;
        foreach (var texture in textures)
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

        // The mask has no blend mode, and its opacity is the smoothness where it's fully painted.
        bool changed;
        if (active.IsMask)
        {
            ImGui.SetNextItemWidth(-1);
            float smoothness = active.Opacity;
            changed = ImGui.SliderFloat("##smoothness", ref smoothness, 0f, 1f, "Smoothness %.2f");
            if (changed) active.Opacity = smoothness;
        }
        else
        {
            ImGui.SetNextItemWidth(110);
            int blend = (int)active.Blend;
            if (ImGui.Combo("##blend", ref blend, Layer.BlendNames, Layer.BlendNames.Length))
                LayerEdit(texture, () => active.Blend = (BlendMode)blend);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("How this layer mixes with the ones below.");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            float percent = active.Opacity * 100f;
            changed = ImGui.SliderFloat("##opacity", ref percent, 0f, 100f, "%.0f%%");
            if (changed) active.Opacity = percent / 100f;
        }
        if (changed) texture.Touch();
        // A drag is one undo step, from where it started.
        if (ImGui.IsItemActivated()) layerDragBefore = before;
        if (ImGui.IsItemDeactivated() && layerDragBefore != null)
        {
            var after = texture.Snapshot();
            if (!layerDragBefore.SameAs(after)) undo.Push(new LayerStep(texture, layerDragBefore, after));
            layerDragBefore = null;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(active.IsMask ? "Smoothness where the mask is fully painted (Unity's Smoothness); unpainted is 0" : "Layer opacity");

        float rows = MathF.Min(texture.Layers.Count, LayerRows) * (ThumbSize + ImGui.GetStyle().ItemSpacing.Y) + ImGui.GetStyle().WindowPadding.Y * 2 + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("##layerlist", new Vector2(0, rows), ImGuiChildFlags.Borders,
            texture.Layers.Count <= LayerRows ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None);
        for (int i = texture.Layers.Count - 1; i >= 0; i--)
        {
            var layer = texture.Layers[i];
            ImGui.PushID(i);
            float rowY = ImGui.GetCursorPosY();
            float centred = rowY + (ThumbSize - ImGui.GetFrameHeight()) * 0.5f;
            ImGui.SetCursorPosY(centred);
            bool visible = layer.Visible;
            if (ImGui.Checkbox("##visible", ref visible)) LayerEdit(texture, () => layer.Visible = visible);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show / hide");
            ImGui.SameLine();
            ImGui.SetCursorPosY(rowY);
            if (renamingLayer == i)
            {
                ImGui.Image(new IntPtr(ThumbnailOf(texture, layer).Gpu.Id), new Vector2(ThumbSize));
                ImGui.SameLine();
                ImGui.SetCursorPosY(centred);
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
                if (layer.IsMask) label += $"  mask {layer.Opacity:0.00}";
                else
                {
                    if (layer.Blend != BlendMode.Normal) label += $"  {layer.Blend}";
                    if (layer.Opacity < 1f) label += $"  {layer.Opacity * 100f:0}%";
                }
                // The whole row (thumbnail and name) is one selectable; the thumbnail and name are drawn over it.
                var at = ImGui.GetCursorScreenPos();
                bool clicked = ImGui.Selectable("##layer", i == texture.ActiveLayerIndex, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(0, ThumbSize));
                var draw = ImGui.GetWindowDrawList();
                draw.AddImage(new IntPtr(ThumbnailOf(texture, layer).Gpu.Id), at, at + new Vector2(ThumbSize));
                draw.AddRect(at - Vector2.One, at + new Vector2(ThumbSize + 1), ImGui.GetColorU32(ImGuiCol.Border));
                uint text = ImGui.GetColorU32(layer.Visible ? ImGuiCol.Text : ImGuiCol.TextDisabled);
                draw.AddText(at + new Vector2(ThumbSize + 8, (ThumbSize - ImGui.GetTextLineHeight()) * 0.5f), text, label);
                if (clicked)
                {
                    EndStroke();
                    texture.ActiveLayerIndex = i;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !layer.IsMask)
                    {
                        renamingLayer = i;
                        renameBuffer = layer.Name;
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(layer.IsMask ? "The smoothness mask: click to paint on it. Not part of the colour" : "Click to paint on it, double-click to rename");
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
        int count = texture.Layers.Count;
        if (Button("New", "New transparent layer above this one (Ctrl+Shift+N)")) NewLayer();
        if (Button("Copy", "Duplicate this layer (Ctrl+J)", !active.IsMask)) DuplicateLayer();
        if (Button("Del", "Delete this layer", texture.CanDeleteActiveLayer)) LayerEdit(texture, texture.DeleteActiveLayer);
        if (Button("Up", "Move up", texture.CanMoveActiveLayer(1))) LayerEdit(texture, () => texture.MoveActiveLayer(1));
        if (Button("Dn", "Move down", texture.CanMoveActiveLayer(-1))) LayerEdit(texture, () => texture.MoveActiveLayer(-1));
        ImGui.NewLine();
        if (Button("Merge down", "Merge this layer into the one below (Ctrl+E)", texture.CanMergeDown)) MergeLayerDown();
        if (Button("Flatten", "Merge every colour layer into one (the smoothness mask stays)", texture.ColorLayerCount > 1)) LayerEdit(texture, texture.Flatten);
        if (Button("Bleed...", "Grow this layer's colour outward from the UV islands")) openBleed = true;
        ImGui.NewLine();
        if (Button("Smoothness mask", "Add a smoothness mask on top (one per texture): paint where the surface is smooth.\n"
                                      + "It shows red and stays out of the colour; File > Export Unity material writes it\n"
                                      + "as the material's smoothness map", texture.Mask == null)) NewSmoothnessMask();
        ImGui.NewLine();
        if (Button("Edit in Photoshop", "Open these layers in Photoshop as a PSD (with the UV layout as a guide layer);\nevery save there comes back here as one undo step")) EditInPhotoshop();
        ImGui.NewLine();
        DrawPhotoshopLinkStatus(texture);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(active.IsMask
            ? "Paint where it's smooth: the alpha counts, not the colour. Hiding it only hides the red."
            : count > 1 || texture.HasLayers
                ? "Ctrl+S saves the flattened image, and the layers beside it in a hidden .sable file."
                : "Paint goes to the selected layer. Saving writes the flattened image.");
        ImGui.PopStyleColor();
        ImGui.End();
    }

    /// <summary>
    /// A layer's thumbnail: its pixels scaled to fit <see cref="ThumbSize"/> (box-filtered, aspect kept) over a
    /// checkerboard. Remade when the texture changes, at most four times a second while painting.
    /// </summary>
    private Thumbnail ThumbnailOf(PaintTexture texture, Layer layer)
    {
        if (!thumbnails.TryGetValue(layer, out var thumb))
        {
            Image blank = Raylib.GenImageColor(ThumbSize, ThumbSize, new Color(0, 0, 0, 0));
            thumb = new Thumbnail { Gpu = Raylib.LoadTextureFromImage(blank) };
            Raylib.UnloadImage(blank);
            thumbnails[layer] = thumb;
        }
        thumb.Seen = thumbnailFrame;
        double now = Raylib.GetTime();
        if (thumb.Version == texture.Version || (thumb.Version >= 0 && now - thumb.Made < 0.25)) return thumb;
        thumb.Version = texture.Version;
        thumb.Made = now;

        float scale = MathF.Max(texture.Width, texture.Height) / (float)ThumbSize;
        int contentW = Math.Max(1, (int)MathF.Round(texture.Width / scale)), contentH = Math.Max(1, (int)MathF.Round(texture.Height / scale));
        int offsetX = (ThumbSize - contentW) / 2, offsetY = (ThumbSize - contentH) / 2;
        int grid = Math.Clamp((int)MathF.Ceiling(scale), 1, 4);
        var pixels = layer.Pixels;
        bool mask = layer.IsMask;
        Array.Clear(thumbnailPixels);
        for (int ty = 0; ty < contentH; ty++)
        for (int tx = 0; tx < contentW; tx++)
        {
            // Average a few samples across the texels this thumbnail pixel covers, weighting colour by alpha.
            float r = 0, g = 0, b = 0, a = 0;
            for (int sy = 0; sy < grid; sy++)
            for (int sx = 0; sx < grid; sx++)
            {
                int x = Math.Min(texture.Width - 1, (int)((tx + (sx + 0.5f) / grid) * scale));
                int y = Math.Min(texture.Height - 1, (int)((ty + (sy + 0.5f) / grid) * scale));
                var c = pixels[y * texture.Width + x];
                // The mask shows red, as in the views: only its alpha means anything.
                if (mask) c = new Color((byte)255, (byte)31, (byte)31, c.A);
                float ca = c.A / 255f;
                r += c.R * ca; g += c.G * ca; b += c.B * ca; a += ca;
            }
            float n = grid * grid;
            float alpha = a / n;
            float check = ((tx / 4 + ty / 4) & 1) == 0 ? 200f : 150f;
            float Mix(float sum) => a > 0 ? (sum / a) * alpha + check * (1f - alpha) : check;
            thumbnailPixels[(ty + offsetY) * ThumbSize + tx + offsetX] =
                new Color((byte)Mix(r), (byte)Mix(g), (byte)Mix(b), (byte)255);
        }
        unsafe
        {
            fixed (Color* data = thumbnailPixels) Raylib.UpdateTexture(thumb.Gpu, data);
        }
        return thumb;
    }

    /// <summary>Frees thumbnails of layers not shown for a couple of seconds (deleted, or another texture's).</summary>
    private void PruneThumbnails()
    {
        thumbnailFrame++;
        foreach (var (layer, thumb) in thumbnails.Where(kv => thumbnailFrame - kv.Value.Seen > 120).ToList())
        {
            Raylib.UnloadTexture(thumb.Gpu);
            thumbnails.Remove(layer);
        }
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
        if (ActiveTextureObject is { } current && current.Width == current.Height && current.Width == ValidTextureSize(current.Width))
            newTextureSize = current.Width;
        uvOverlaps = Sable.Model.MeshCheck.UvOverlaps(Model.Source, 256);
        SuggestSharedMaterials(material, candidates);
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
                    SuggestSharedMaterials(m, candidates);
                    newTextureName = Path.GetFileNameWithoutExtension(Model.UniqueTextureName(materials[m].Name));
                }
            ImGui.EndCombo();
        }

        // Square, any multiple of 4: typed, stepped by 4 (Ctrl: 64), or picked from the common sizes.
        ImGui.SetNextItemWidth(140);
        int typed = newTextureSize;
        if (ImGui.InputInt("##size", ref typed, 4, 64)) newTextureSize = typed;
        if (ImGui.IsItemDeactivatedAfterEdit() || !ImGui.IsItemActive()) newTextureSize = ValidTextureSize(newTextureSize);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Square, any multiple of 4 (block compression works in 4x4 blocks).\nPowers of two (the list) also mip down evenly; that matters for textures seen at a distance with mipmaps on.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(72);
        if (ImGui.BeginCombo("Size", TextureSizes.Contains(newTextureSize) ? newTextureSize.ToString() : "...", ImGuiComboFlags.HeightLarge))
        {
            foreach (int size in TextureSizes)
                if (ImGui.Selectable(size.ToString(), size == newTextureSize)) newTextureSize = size;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"= {newTextureSize} x {newTextureSize}");

        if (candidates.Count > 1)
        {
            ImGui.TextUnformatted("Also use it for:");
            foreach (int m in candidates)
            {
                if (m == newTextureMaterial) continue;
                bool overlaps = UvsOverlap(m, newTextureMaterial) || newTextureAlso.Any(o => o != m && UvsOverlap(m, o));
                bool on = newTextureAlso.Contains(m);
                if (ImGui.Checkbox($"{materials[m].Name}##also{m}", ref on))
                {
                    if (on) newTextureAlso.Add(m);
                    else newTextureAlso.Remove(m);
                }
                string note = (overlaps ? "UVs overlap: painting one would paint the other" : "")
                              + (materials[m].TextureIndex >= 0 ? $"{(overlaps ? "; " : "")}has {Model.Textures[materials[m].TextureIndex].Name}" : "");
                if (note.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(overlaps ? new Vector4(1f, 0.55f, 0.45f, 1f) : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], $"({note})");
                }
            }
        }

        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Fill", ref newTextureFill, FillNames, FillNames.Length);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Material colour fills each material's UV islands with that material's own colour.");
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
            newTextureSize = ValidTextureSize(newTextureSize);
            CreateNewTexture(name);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>Makes the texture the New texture dialog describes and puts it on the ticked materials.</summary>
    private void CreateNewTexture(string name)
    {
        var materials = Model!.Source.Materials;
        Color? fill = newTextureFill switch
        {
            1 => Color.White,
            2 => PaintColor,
            3 => new Color(0, 0, 0, 0),
            _ => null,
        };
        EndStroke();
        int created = Model.CreateTexture(newTextureMaterial, newTextureSize, newTextureSize, fill, name);
        // With the material colour fill, each other material's UV islands get its own colour, so the model looks
        // as it did before the texture.
        foreach (int m in newTextureAlso)
        {
            Model.AssignTexture(m, created);
            if (fill == null) Model.FillUvIslands(Model.Textures[created], m, Sable.Rendering.GpuModel.ToColor(materials[m].BaseColor with { W = 1 }));
        }
        state.ActiveTexture = created;
        state.Selection = null;
        uvView.RequestFit();
        string on = string.Join(", ", new[] { materials[newTextureMaterial].Name }.Concat(newTextureAlso.Select(m => materials[m].Name)));
        SetStatus($"Created {name} ({newTextureSize}x{newTextureSize}) on {on}; Ctrl+S saves it to {DefaultSavePath(name)}", error: false);
    }

    // ---------- Resize texture ----------

    private bool openResize;
    private int resizeWidth, resizeHeight;
    private bool resizeKeepRatio = true, resizeSmooth;

    private void OpenResize()
    {
        if (ActiveTextureObject is not { } texture) return;
        (resizeWidth, resizeHeight) = (texture.Width, texture.Height);
        openResize = true;
    }

    private void DrawResizePopup()
    {
        if (openResize)
        {
            ImGui.OpenPopup("Resize texture");
            openResize = false;
        }
        ImGui.SetNextWindowPos(new Vector2(uvRect.X + uvRect.Width * 0.5f, uvRect.Y + uvRect.Height * 0.4f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Resize texture", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) return;
        if (ActiveTextureObject is not { } texture)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"{texture.Name}: {texture.Width} x {texture.Height}, {texture.Layers.Count} layer{(texture.Layers.Count == 1 ? "" : "s")}");
        float ratio = texture.Height / (float)texture.Width;
        int width = resizeWidth, height = resizeHeight;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("Width", ref width, 4, 64))
        {
            resizeWidth = width;
            if (resizeKeepRatio) resizeHeight = ValidTextureSize((int)MathF.Round(width * ratio));
        }
        if (!ImGui.IsItemActive()) resizeWidth = ValidTextureSize(resizeWidth);
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("Height", ref height, 4, 64))
        {
            resizeHeight = height;
            if (resizeKeepRatio) resizeWidth = ValidTextureSize((int)MathF.Round(height / ratio));
        }
        if (!ImGui.IsItemActive()) resizeHeight = ValidTextureSize(resizeHeight);
        ImGui.Checkbox("Keep proportions", ref resizeKeepRatio);

        void Scale(string label, float factor)
        {
            if (ImGui.Button(label))
                (resizeWidth, resizeHeight) = (ValidTextureSize((int)MathF.Round(texture.Width * factor)), ValidTextureSize((int)MathF.Round(texture.Height * factor)));
            ImGui.SameLine();
        }
        Scale("1/4", 0.25f);
        Scale("1/2", 0.5f);
        Scale("2x", 2f);
        Scale("4x", 4f);
        ImGui.NewLine();

        int mode = resizeSmooth ? 1 : 0;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Resampling", ref mode, new[] { "Nearest (hard pixels)", "Smooth" }, 2)) resizeSmooth = mode == 1;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Nearest keeps texels hard-edged: best for pixel art, and exact for 2x / 1/2.\nSmooth blends: better for painted textures, especially when shrinking.");
        ImGui.TextDisabled("All layers are resized. Ctrl+Z undoes it.");

        bool same = resizeWidth == texture.Width && resizeHeight == texture.Height;
        ImGui.BeginDisabled(same);
        if (ImGui.Button("Resize", new Vector2(100, 0)) || (!same && ImGui.IsKeyPressed(ImGuiKey.Enter)))
        {
            ResizeActiveTexture(ValidTextureSize(resizeWidth), ValidTextureSize(resizeHeight), resizeSmooth);
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void ResizeActiveTexture(int width, int height, bool smooth)
    {
        if (ActiveTextureObject is not { } texture) return;
        EndStroke();
        int oldWidth = texture.Width, oldHeight = texture.Height;
        undo.Push(texture.Resize(width, height, smooth));
        uvView.RequestFit();
        SetStatus($"Resized {texture.Name} from {oldWidth}x{oldHeight} to {width}x{height} ({(smooth ? "smooth" : "nearest")}); Ctrl+Z undoes it.", error: false);
    }

    // ---------- self-test ----------

    /// <summary>Resize: nearest 2x keeps texels exact, a stroke after it and the resize both undo, redo comes back.</summary>
    private void SelfTestResize(PaintTexture tex)
    {
        tex.EnsureComposite();
        var original = (Color[])tex.Composite.Clone();
        int w = tex.Width, h = tex.Height, layers = tex.Layers.Count;
        ResizeActiveTexture(w * 2, h * 2, smooth: false);
        tex.EnsureComposite();
        bool exact = true;
        for (int y = 0; y < h && exact; y++)
        for (int x = 0; x < w && exact; x++)
            exact = tex.Composite[(y * 2 + 1) * tex.Width + x * 2 + 1].Equals(original[y * w + x]) || original[y * w + x].A == 0;
        string size2x = $"{tex.Width}x{tex.Height}, {tex.Layers.Count} layers";

        var paint = new Stroke(tex, new Color(0, 255, 255, 255));
        paint.Apply(tex.Width - 1, tex.Height - 1, 1f);
        undo.Push(paint.Finish()!);
        undo.Undo();
        undo.Undo();
        tex.EnsureComposite();
        bool restored = tex.Width == w && tex.Height == h && tex.Composite.SequenceEqual(original) && tex.Layers.Count == layers;
        undo.Redo();
        string redone = $"{tex.Width}x{tex.Height}";
        undo.Undo();

        ResizeActiveTexture(w / 2, h / 2, smooth: true);
        string half = $"{tex.Width}x{tex.Height}";
        undo.Undo();
        Model!.UploadTextures();
        Console.WriteLine($"[selftest] resize: 2x nearest -> {size2x}, texels exact {exact}; stroke + resize undone -> {tex.Width}x{tex.Height} identical {restored}; "
                          + $"redo -> {redone}; smooth 1/2 -> {half}; material follows the GPU texture {Model.Source.Materials.Any(m => m.TextureIndex == state.ActiveTexture)}");
    }

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
        LayerFile.Write(tex, png, tex.Composite);
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
