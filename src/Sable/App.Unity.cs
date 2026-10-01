using System.IO.Compression;
using System.Text.RegularExpressions;
using ImGuiNET;
using Raylib_cs;
using Sable.Paint;

namespace Sable;

/// <summary>
/// Export as a Unity material: the texture's colour, its smoothness mask and a URP Lit .mat using both (see
/// <see cref="UnityMaterial"/>). The last place each texture went is remembered, so exporting again is one click.
/// </summary>
internal sealed partial class App
{
    /// <summary>The .mat each texture was last exported to, by <see cref="UnityKey"/>.</summary>
    private readonly Dictionary<string, string> unityExports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A texture's identity between sessions: the image it saves to.</summary>
    private string UnityKey(PaintTexture texture) => Path.GetFullPath(texture.FilePath ?? DefaultSavePath(texture));

    private string? LastUnityExport(PaintTexture texture) => unityExports.GetValueOrDefault(UnityKey(texture));

    /// <summary>The File menu's two items: export (asking where), and export again to the last place.</summary>
    private void DrawUnityExportMenuItems(bool hasTexture)
    {
        if (ImGui.MenuItem("Export Unity material...", null, false, hasTexture)) ExportUnityMaterial();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Writes <name>.png (the colour), <name>_mask.png (the smoothness mask, if there is one)\n"
                             + "and <name>.mat (URP Lit) with their .meta files, set up for pixel art.");
        if (ActiveTextureObject is not { } texture || LastUnityExport(texture) is not { } last) return;
        string shown = $"{Path.GetFileName(Path.GetDirectoryName(last))}\\{Path.GetFileName(last)}";
        if (ImGui.MenuItem($"Re-export Unity material to {shown}")) ReexportUnityMaterial();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(last);
    }

    /// <summary>Asks where the .mat goes (its images go beside it), then exports the active texture there.</summary>
    private void ExportUnityMaterial()
    {
        if (ActiveTextureObject is not { } texture) return;
        string? last = LastUnityExport(texture);
        string fileName = last != null ? Path.GetFileName(last) : Path.GetFileNameWithoutExtension(texture.Name) + ".mat";
        Pick(true, "Export Unity material", "Unity material|*.mat", fileName, path => ReportUnityExport(WriteUnityMaterial(texture, path)),
            openIn: last != null ? Path.GetDirectoryName(last) : null);
    }

    private void ReexportUnityMaterial()
    {
        if (ActiveTextureObject is not { } texture || LastUnityExport(texture) is not { } last) return;
        if (!Directory.Exists(Path.GetDirectoryName(last)))
        {
            SetStatus($"{Path.GetDirectoryName(last)} isn't there any more: use Export Unity material... to pick a folder.", error: true);
            return;
        }
        try
        {
            ReportUnityExport(WriteUnityMaterial(texture, last));
        }
        catch (Exception e)
        {
            SetStatus($"Couldn't export {texture.Name} to Unity: {e.Message}", error: true);
        }
    }

    private void ReportUnityExport(UnityMaterial.Result result)
    {
        var files = new List<string> { Path.GetFileName(result.MaterialPath), Path.GetFileName(result.AlbedoPath) };
        if (result.MaskPath != null) files.Add(Path.GetFileName(result.MaskPath));
        SetStatus($"Exported {string.Join(", ", files)} to {Path.GetDirectoryName(result.MaterialPath)}"
                  + (result.MaskPath != null ? " (smoothness from the mask)." : " (no smoothness mask, so it's matte)."), error: false);
    }

    /// <summary>
    /// Writes the texture as a Unity material at <paramref name="materialPath"/> and remembers the place. The colour
    /// is what Ctrl+S writes (edge padding included). When that image is the texture's own file, it's saved as
    /// Ctrl+S would, layers and all, so the layers still match it.
    /// </summary>
    private UnityMaterial.Result WriteUnityMaterial(PaintTexture texture, string materialPath)
    {
        EndStroke();
        var albedo = PaddedPixels(texture);
        var smoothness = texture.Mask is { } mask ? SmoothnessPixels(texture, mask) : null;
        var result = UnityMaterial.Export(materialPath, texture.Width, texture.Height, albedo, smoothness);
        if (string.Equals(Path.GetFullPath(result.AlbedoPath), UnityKey(texture), StringComparison.OrdinalIgnoreCase))
        {
            texture.Save(result.AlbedoPath, albedo);
            LayerFile.Write(texture, result.AlbedoPath, albedo);
        }
        unityExports[UnityKey(texture)] = result.MaterialPath;
        return result;
    }

    /// <summary>
    /// The smoothness map for URP Lit's Metallic Alpha layout: metallic 0 in red, and in alpha the smoothness, which is
    /// the mask's alpha times its smoothness. Padded past the UV islands like the colour.
    /// </summary>
    private Color[] SmoothnessPixels(PaintTexture texture, Layer mask)
    {
        // Padded as opaque grey, so the bleed carries the smoothness itself rather than alpha-weighted colour.
        var grey = new Color[mask.Pixels.Length];
        for (int i = 0; i < grey.Length; i++)
        {
            byte s = (byte)Math.Clamp((int)MathF.Round(mask.Pixels[i].A * mask.Opacity), 0, 255);
            grey[i] = new Color(s, s, s, (byte)255);
        }
        var padded = PadToIslands(texture, grey);
        var result = new Color[padded.Length];
        for (int i = 0; i < result.Length; i++) result[i] = new Color((byte)0, (byte)0, (byte)0, padded[i].R);
        return result;
    }

    // ---------- self-test ----------

    /// <summary>
    /// The smoothness mask: it leaves the colour alone; new layers go under it and flatten keeps it; the layer file
    /// keeps it (and older files read as normal layers); it survives the Photoshop link; undo and redo; then the
    /// Unity export (mask alpha, GUIDs in the .mat, a re-export keeping GUIDs and Unity's own settings, and a texture
    /// without a mask). Leaves the mask on for the screenshot.
    /// </summary>
    private void SelfTestSmoothness(PaintTexture tex)
    {
        int index = Model!.Textures.IndexOf(tex);
        state.ActiveTexture = index;
        tex.EnsureComposite();
        var colour = (Color[])tex.Composite.Clone();
        int layers = tex.Layers.Count;
        var inside = UvRaster.Coverage(Model.Source, p => Model.TextureOf(p) == index, tex.Width, tex.Height);
        var islandTexels = Enumerable.Range(0, inside.Length).Where(i => inside[i]).Take(40).ToList();
        bool Same() { tex.EnsureComposite(); return tex.Composite.SequenceEqual(colour); }
        // A PSD keeps opacity in 1/255 steps, so a faded layer can come back a step off.
        bool Close()
        {
            tex.EnsureComposite();
            return tex.Composite.Zip(colour).All(p => Math.Abs(p.First.R - p.Second.R) <= 1 && Math.Abs(p.First.G - p.Second.G) <= 1
                                                      && Math.Abs(p.First.B - p.Second.B) <= 1 && Math.Abs(p.First.A - p.Second.A) <= 1);
        }

        NewSmoothnessMask();
        bool made = tex.Mask != null && tex.ActiveLayer.IsMask && tex.Mask.Opacity == Layer.DefaultSmoothness;
        var solid = new Stroke(tex, new Color(0, 0, 255, 255));
        foreach (int i in islandTexels.Take(20)) solid.Apply(i % tex.Width, i / tex.Width, 1f);
        undo.Push(solid.Finish()!);
        var soft = new Stroke(tex, new Color(0, 255, 0, 255), 0.5f);
        foreach (int i in islandTexels.Skip(20)) soft.Apply(i % tex.Width, i / tex.Width, 1f);
        undo.Push(soft.Finish()!);
        LayerEdit(tex, () => tex.Mask!.Opacity = 0.65f);
        bool colourKept = Same();
        Model.UploadTextures();
        bool onGpu = tex.MaskGpu.Id != 0;
        var maskAlpha = tex.Mask!.Pixels.Select(c => c.A).ToArray();

        NewLayer();
        bool underMask = tex.Mask != null && tex.ActiveLayerIndex == tex.Layers.Count - 2;
        undo.Undo();
        LayerEdit(tex, tex.Flatten);
        bool flattenKeeps = tex.Mask != null && Same();
        undo.Undo();
        Console.WriteLine($"[selftest] smoothness: mask made {made}, colour unchanged by it {colourKept}, on the GPU {onGpu}; "
                          + $"new layer goes under it {underMask}; flatten keeps it {flattenKeeps}");

        // The layer file keeps the mask; a layers.json without kinds (older files) reads as normal layers.
        string dir = Path.Combine(Path.GetTempPath(), "Sable", "selftest");
        Directory.CreateDirectory(dir);
        string png = Path.Combine(dir, "smoothness.png");
        tex.ExportTo(png);
        LayerFile.Write(tex, png, tex.Composite);
        string fileNote;
        using (var reopened = PaintTexture.FromEncoded("smoothness.png", ".png", File.ReadAllBytes(png), png))
        {
            bool loaded = LayerFile.TryLoad(reopened, png, out string? note);
            reopened.EnsureComposite();
            fileNote = $"restored {loaded}, mask {reopened.Mask != null} at {reopened.Mask?.Opacity:0.00}, "
                       + $"mask alpha same {reopened.Mask?.Pixels.Select(c => c.A).SequenceEqual(maskAlpha)}, colour same {reopened.Composite.SequenceEqual(colour)}"
                       + (note != null ? $", note: {note}" : "");
        }
        string old = Path.Combine(dir, "smoothness-old.sable");
        File.Copy(LayerFile.PathFor(png), old, overwrite: true);
        using (var zip = ZipFile.Open(old, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("layers.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("layers.json").Open());
            writer.Write(Regex.Replace(json, @"\s*""Kind"":\s*""\w+"",", ""));
        }
        var older = LayerFile.ReadZip(old);
        Console.WriteLine($"[selftest] smoothness layer file: {fileNote}; without kinds (older files) all normal {older?.State.Layers.All(l => !l.IsMask)}");

        // Photoshop: the mask goes as a named layer and comes back as the mask, even moved to the bottom there.
        var psd = WritePhotoshopFile();
        if (psd != null)
        {
            var written = Psd.Read(psd);
            bool inPsd = written.Layers.Any(l => l.Name == MaskLayerName);
            var moved = written.Layers.OrderBy(l => l.Name == MaskLayerName ? 0 : 1).ToList();
            Psd.Write(psd, tex.Width, tex.Height, moved);
            ApplyPhotoshop(Psd.Read(psd));
            bool back = tex.Mask is { } m && m.Pixels.Select(c => c.A).SequenceEqual(maskAlpha) && MathF.Abs(m.Opacity - 0.65f) < 0.01f
                        && tex.Layers.Count(l => l.IsMask) == 1 && Close();
            undo.Undo();
            StopPhotoshopLink();
            Console.WriteLine($"[selftest] smoothness photoshop: in the PSD as '{MaskLayerName}' {inPsd}; back as the mask on top, colour unchanged (within a step) {back}");
        }

        // Undo back to before the mask, and redo to it.
        undo.Undo();
        undo.Undo();
        undo.Undo();
        bool cleared = tex.Mask != null && tex.Mask.Pixels.All(c => c.A == 0);
        undo.Undo();
        bool gone = tex.Mask == null && tex.Layers.Count == layers && Same();
        for (int i = 0; i < 4; i++) undo.Redo();
        bool redone = tex.Mask is { } again && again.Pixels.Select(c => c.A).SequenceEqual(maskAlpha) && again.Opacity == 0.65f;
        Console.WriteLine($"[selftest] smoothness undo: paint undone {cleared}, mask gone {gone}; redo brings it back {redone}");

        // Export as a Unity material.
        string unity = Path.Combine(dir, "unity");
        if (Directory.Exists(unity)) Directory.Delete(unity, recursive: true);
        var result = WriteUnityMaterial(tex, Path.Combine(unity, "SelfTest.mat"));
        Color[] Read(string path) => PaintTexture.DecodeImage(".png", File.ReadAllBytes(path), 0, 0, out _, out _);
        bool albedoSame = Read(result.AlbedoPath).SequenceEqual(PaddedPixels(tex));
        var map = Read(result.MaskPath!);
        int wrong = Enumerable.Range(0, map.Length).Count(i => inside[i]
            && (map[i].R != 0 || map[i].A != (byte)Math.Clamp((int)MathF.Round(tex.Mask!.Pixels[i].A * 0.65f), 0, 255)));
        var painted = map[islandTexels[0]].A;
        var half = map[islandTexels[^1]].A;
        string mat = File.ReadAllText(result.MaterialPath);
        bool references = Regex.Matches(mat, $"guid: {result.AlbedoGuid},").Count == 2 && mat.Contains($"guid: {result.MaskGuid},")
                          && UnityMaterial.GuidIn(result.AlbedoPath + ".meta") == result.AlbedoGuid
                          && UnityMaterial.GuidIn(result.MaskPath + ".meta") == result.MaskGuid
                          && UnityMaterial.GuidIn(result.MaterialPath + ".meta") == result.MaterialGuid
                          && mat.Contains("- _METALLICSPECGLOSSMAP") && mat.Contains("_Smoothness: 1") && mat.Contains(UnityMaterial.LitShaderGuid);
        bool linear = File.ReadAllText(result.MaskPath + ".meta").Contains("sRGBTexture: 0");
        Console.WriteLine($"[selftest] unity export: albedo is the saved image {albedoSame}; mask alpha painted {painted} (expect 166), soft {half} (expect 83), "
                          + $"island texels wrong {wrong}; .mat references the metas' GUIDs {references}; mask linear {linear}");

        // Unity changes an import setting Sable owns and one it doesn't; exporting again keeps the GUIDs and the latter.
        string meta = result.AlbedoPath + ".meta";
        File.WriteAllText(meta, File.ReadAllText(meta).Replace("filterMode: 0", "filterMode: 1").Replace("aniso: 1", "aniso: 4"));
        ReexportUnityMaterial();
        string metaText = File.ReadAllText(meta);
        bool guidsKept = UnityMaterial.GuidIn(meta) == result.AlbedoGuid && UnityMaterial.GuidIn(result.MaskPath + ".meta") == result.MaskGuid
                         && UnityMaterial.GuidIn(result.MaterialPath + ".meta") == result.MaterialGuid
                         && File.ReadAllText(result.MaterialPath).Contains($"guid: {result.AlbedoGuid},");
        bool settings = metaText.Contains("filterMode: 0") && metaText.Contains("aniso: 4");
        using var plain = PaintTexture.Create("plain.png", 8, 8, Color.White);
        var noMask = WriteUnityMaterial(plain, Path.Combine(unity, "NoMask.mat"));
        string plainMat = File.ReadAllText(noMask.MaterialPath);
        bool matte = noMask.MaskPath == null && !File.Exists(UnityMaterial.MaskPathFor(noMask.MaterialPath))
                     && plainMat.Contains("_Smoothness: 0") && plainMat.Contains("m_ValidKeywords: []");
        Console.WriteLine($"[selftest] unity re-export: GUIDs kept {guidsKept}; filter set back, Unity's other settings kept {settings}; "
                          + $"without a mask: no mask image, Smoothness 0 {matte}; {unity}");
    }
}
