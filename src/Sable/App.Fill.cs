using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using Sable.Model;
using Sable.Paint;
using Sable.UI;

namespace Sable;

/// <summary>The fill tool's shape modes: a whole UV island, or a whole face, picked in either view.</summary>
internal sealed partial class App
{
    private enum FillMode { Similar, Island, Face }
    private static readonly string[] FillModeNames = { "Similar colour", "UV island", "Face" };
    private FillMode fillMode;

    /// <summary>
    /// Fills the UV island or the face (polygon) under <paramref name="hit"/> with the paint colour, and its mirror
    /// when mirroring. Texels the island only partly covers are included, so no gaps show along its edges. A
    /// texel selection still limits it. One undo step.
    /// </summary>
    private void FillShape(SurfaceHit hit)
    {
        int texture = Model!.TextureOf(hit.Part);
        var tex = Model.Textures[texture];
        state.ActiveTexture = texture;
        palette.Remember(ColorWheel.HsvToRgb(hsv));
        var fill = new Stroke(tex, PaintColor, 1f, MaskFor(texture));
        int triangles = FillTriangles(fill, hit);
        if (MirrorHit(hit, out var mirrored) && Model.TextureOf(mirrored.Part) == texture) triangles += FillTriangles(fill, mirrored);
        if (fill.Finish() is { } step) undo.Push(step);
        SetStatus($"Filled {(fillMode == FillMode.Island ? "the UV island" : "the face")} ({triangles} triangle{(triangles == 1 ? "" : "s")}).", error: false);
    }

    private int FillTriangles(Stroke fill, SurfaceHit hit)
    {
        var part = Model!.Source.Parts[hit.Part];
        List<int> triangles;
        if (fillMode == FillMode.Island)
        {
            var island = UvIslands.IslandOfTriangle(part, out _);
            int id = island[hit.Triangle];
            triangles = new List<int>();
            for (int t = 0; t < part.TriangleCount; t++)
                if (island[t] == id && part.TriangleVisible(t)) triangles.Add(t);
        }
        else
        {
            triangles = UvIslands.FaceTriangles(part, hit.Triangle).Where(part.TriangleVisible).ToList();
        }
        var tex = fill.Texture;
        UvRaster.Triangles(part, triangles, tex.Width, tex.Height, (x, y) => fill.Apply(x, y, 1f), dilate: 0.5f);
        return triangles.Count;
    }

    /// <summary>Texels of padding saved images get past the edge of their UV islands (0 = none).</summary>
    private int edgePadding = 4;

    /// <summary>
    /// What saving writes for a texture: the flattened layers with colour bled <see cref="edgePadding"/> texels past
    /// the UV islands of every part that uses it. Without padding, or UVs, it's the flattened layers as they are.
    /// </summary>
    private Color[] PaddedPixels(PaintTexture texture)
    {
        texture.EnsureComposite();
        return PadToIslands(texture, texture.Composite);
    }

    /// <summary>
    /// <paramref name="pixels"/> (the texture's size) bled <see cref="edgePadding"/> texels past the UV islands of
    /// every part that uses the texture, or as they are without padding or UVs.
    /// </summary>
    private Color[] PadToIslands(PaintTexture texture, Color[] pixels)
    {
        int index = Model!.Textures.IndexOf(texture);
        if (edgePadding <= 0 || index < 0) return pixels;
        var inside = UvRaster.Coverage(Model.Source, p => Model.TextureOf(p) == index, texture.Width, texture.Height);
        if (!inside.Contains(true)) return pixels;
        return EdgePadding.Pad(pixels, texture.Width, texture.Height, inside, edgePadding);
    }

    // ---------- bleed edges ----------

    private bool openBleed;
    private int bleedTexels = 4;

    /// <summary>
    /// Grows the active layer's colour <paramref name="texels"/> texels outward from every UV island of the
    /// texture (the same bleed as edge padding on save, but into the layer, where it can be seen and painted over).
    /// Texels inside the islands don't change. One undo step.
    /// </summary>
    private void BleedActiveLayer(int texels)
    {
        if (ActiveTextureObject is not { } texture) return;
        int index = Model!.Textures.IndexOf(texture);
        var inside = UvRaster.Coverage(Model.Source, p => Model.TextureOf(p) == index, texture.Width, texture.Height);
        if (!inside.Contains(true))
        {
            SetStatus($"No UVs use {texture.Name}, so there are no island edges to bleed from.", error: true);
            return;
        }
        var layer = texture.ActiveLayer;
        var bled = EdgePadding.Pad(layer.Pixels, texture.Width, texture.Height, inside, texels);
        int changed = 0;
        for (int i = 0; i < bled.Length; i++) if (!bled[i].Equals(layer.Pixels[i])) changed++;
        // A new layer object with the result, so the layer undo step can swap the old one back.
        LayerEdit(texture, () => texture.Layers[texture.ActiveLayerIndex] = layer.WithPixels(bled));
        SetStatus($"Bled {layer.Name} {texels} texel{(texels == 1 ? "" : "s")} past the UV islands ({changed} texels changed).", error: false);
    }

    private void DrawBleedPopup()
    {
        if (openBleed)
        {
            ImGui.OpenPopup("Bleed edges");
            openBleed = false;
        }
        ImGui.SetNextWindowPos(new Vector2(uvRect.X + uvRect.Width * 0.5f, uvRect.Y + uvRect.Height * 0.4f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Bleed edges", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) return;
        if (ActiveTextureObject is not { } texture)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.TextUnformatted($"Grow {texture.ActiveLayer.Name}'s colour outward from every UV island.");
        ImGui.SetNextItemWidth(200);
        ImGui.SliderInt("Texels", ref bleedTexels, 1, 64);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted("Each ring takes the average of the colour next to it. Texels inside the islands\nstay as they are. Ctrl+Z undoes it.");
        ImGui.PopStyleColor();
        if (ImGui.Button("Bleed", new Vector2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter))
        {
            BleedActiveLayer(bleedTexels);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>The surface point of the active object whose UVs are at <paramref name="texelPoint"/> on the active texture.</summary>
    private bool HitAtTexel(Vector2 texelPoint, out SurfaceHit hit)
    {
        hit = default;
        if (state.ActiveObject < 0 || state.ActiveTexture < 0) return false;
        var tex = Model!.Textures[state.ActiveTexture];
        Vector2 uv = texelPoint / new Vector2(tex.Width, tex.Height);
        foreach (int partIndex in Model.Source.Objects[state.ActiveObject].Parts)
        {
            var part = Model.Source.Parts[partIndex];
            if (part.Uvs == null || Model.TextureOf(partIndex) != state.ActiveTexture) continue;
            for (int t = 0; t < part.TriangleCount; t++)
            {
                if (!part.TriangleVisible(t)) continue;
                int i0 = part.Indices[t * 3], i1 = part.Indices[t * 3 + 1], i2 = part.Indices[t * 3 + 2];
                var b = Raycast.Barycentric2D(uv, part.Uvs[i0], part.Uvs[i1], part.Uvs[i2]);
                if (b.X < -1e-4f || b.Y < -1e-4f || b.Z < -1e-4f) continue;
                Vector3 a = part.Positions[i0], bb = part.Positions[i1], c = part.Positions[i2];
                hit = new SurfaceHit
                {
                    Part = partIndex,
                    Triangle = t,
                    Point = a * b.X + bb * b.Y + c * b.Z,
                    Normal = Vector3.Normalize(Vector3.Cross(bb - a, c - a)),
                    Barycentric = b,
                };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Edge padding: texels inside the UV islands are saved exactly, some outside get bled colour, and layers
    /// saved beside a padded image still come back when it's reopened.
    /// </summary>
    private void SelfTestPadding(PaintTexture tex)
    {
        int index = Model!.Textures.IndexOf(tex);
        var inside = UvRaster.Coverage(Model.Source, p => Model.TextureOf(p) == index, tex.Width, tex.Height);
        int saved = edgePadding;
        edgePadding = 4;
        var padded = PaddedPixels(tex);
        edgePadding = saved;
        bool insideExact = true;
        int changedOutside = 0;
        for (int i = 0; i < padded.Length; i++)
        {
            if (inside[i]) insideExact &= padded[i].Equals(tex.Composite[i]);
            else if (!padded[i].Equals(tex.Composite[i])) changedOutside++;
        }

        string dir = Path.Combine(Path.GetTempPath(), "Sable", "selftest");
        Directory.CreateDirectory(dir);
        string png = Path.Combine(dir, "padded.png");
        tex.ExportTo(png, padded);
        LayerFile.Write(tex, png, padded);
        bool restored;
        using (var reopened = PaintTexture.FromEncoded("padded.png", ".png", File.ReadAllBytes(png), png))
            restored = LayerFile.TryLoad(reopened, png, out _) && reopened.Layers.Count == tex.Layers.Count;
        int activeBefore = tex.ActiveLayerIndex;
        tex.ActiveLayerIndex = 0;
        var layerBefore = (Color[])tex.ActiveLayer.Pixels.Clone();
        BleedActiveLayer(4);
        var bledPixels = tex.ActiveLayer.Pixels;
        int bledOutside = 0;
        bool bleedInsideKept = true;
        for (int i = 0; i < bledPixels.Length; i++)
        {
            if (inside[i]) bleedInsideKept &= bledPixels[i].Equals(layerBefore[i]);
            else if (!bledPixels[i].Equals(layerBefore[i])) bledOutside++;
        }
        undo.Undo();
        bool bleedUndone = tex.ActiveLayer.Pixels.SequenceEqual(layerBefore);
        Console.WriteLine($"[selftest] bleed edges (4) on {tex.ActiveLayer.Name}: {bledOutside} texels outside grew, inside kept {bleedInsideKept}, undone {bleedUndone}");
        tex.ActiveLayerIndex = activeBefore;
        Console.WriteLine($"[selftest] edge padding (4): {inside.Count(b => b)} texels inside islands saved exactly {insideExact}, "
                          + $"{changedOutside} outside bled; layers restored beside the padded image {restored} ({tex.Layers.Count} layers)");
    }
}
