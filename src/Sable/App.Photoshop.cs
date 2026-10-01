using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using Sable.Paint;
using Sable.Rendering;
using BlendMode = Sable.Paint.BlendMode;

namespace Sable;

/// <summary>
/// The Photoshop link (as in 3DCoat): the active texture's layers go to a PSD, with the UV layout on top as a locked
/// guide, which opens in Photoshop; every time Photoshop saves it, the layers come back into Sable as one undo step.
/// </summary>
internal sealed partial class App
{
    private const string GuideLayerName = "UV guide (Sable)";

    private PaintTexture? photoshopTexture;
    private string? photoshopPath;
    private (DateTime Time, long Length) photoshopStamp;
    private double photoshopChangedAt = -1, photoshopCheckedAt;
    private int photoshopSentVersion;

    private bool PhotoshopLinked => photoshopTexture != null && photoshopPath != null;

    /// <summary>Writes the active texture to a PSD and opens it with whatever opens PSDs (Photoshop).</summary>
    private void EditInPhotoshop()
    {
        if (WritePhotoshopFile() is not { } path) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetStatus($"Sent {photoshopTexture!.Name} to Photoshop ({Path.GetFileName(path)}). Each save there comes back here; "
                      + "the UV guide layer stays in Photoshop.", error: false);
        }
        catch (Exception e)
        {
            SetStatus($"Wrote {path}, but couldn't open it ({e.Message}). Open it in Photoshop yourself; saves still come back.", error: true);
        }
    }

    /// <summary>Writes the active texture's PSD and starts watching it. Returns its path, or null if it couldn't.</summary>
    private string? WritePhotoshopFile()
    {
        if (ActiveTextureObject is not { } texture || Model == null) return null;
        EndStroke();
        string folder = Path.Combine(Path.GetTempPath(), "Sable", "photoshop");
        Directory.CreateDirectory(folder);
        string stem = $"{Path.GetFileNameWithoutExtension(Model.Source.SourcePath)}_{Path.GetFileNameWithoutExtension(texture.Name)}";
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        string path = Path.Combine(folder, stem + ".psd");

        try
        {
            int index = Model.Textures.IndexOf(texture);
            var parts = Enumerable.Range(0, Model.Source.Parts.Count).Where(p => Model.TextureOf(p) == index);
            var guide = UvLayoutExport.Lines(Model, parts, texture.Width, texture.Height, Color.White);
            var layers = texture.Layers.Select(l => new PsdLayer(l.Name, l.Pixels, l.Visible, l.Opacity, l.Blend)).ToList();
            layers.Add(new PsdLayer(GuideLayerName, guide, true, 0.6f, BlendMode.Normal, Locked: true));
            texture.EnsureComposite();
            Psd.Write(path, texture.Width, texture.Height, layers, texture.Composite);
        }
        catch (Exception e)
        {
            SetStatus($"Couldn't write the PSD for Photoshop: {e.Message}", error: true);
            return null;
        }

        photoshopTexture = texture;
        photoshopPath = path;
        photoshopStamp = Stamp(path);
        photoshopChangedAt = -1;
        photoshopSentVersion = texture.Version;
        return path;
    }

    private void StopPhotoshopLink()
    {
        photoshopTexture = null;
        photoshopPath = null;
        SetStatus("Stopped the Photoshop link.", error: false);
    }

    private static (DateTime, long) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);
        }
        catch (IOException)
        {
            return (DateTime.MinValue, -1);
        }
    }

    /// <summary>
    /// Per frame: notice Photoshop saving the PSD (twice a second), wait until the file has been quiet for a moment
    /// (Photoshop writes it in several steps), then bring its layers in.
    /// </summary>
    private void UpdatePhotoshopLink()
    {
        if (!PhotoshopLinked || Model == null) return;
        if (!Model.Textures.Contains(photoshopTexture!))
        {
            StopPhotoshopLink();
            return;
        }
        double now = Raylib.GetTime();
        if (now - photoshopCheckedAt < 0.5) return;
        photoshopCheckedAt = now;

        var stamp = Stamp(photoshopPath!);
        if (stamp != photoshopStamp && stamp.Item2 > 0)
        {
            photoshopStamp = stamp;
            photoshopChangedAt = now;
            return;
        }
        if (photoshopChangedAt < 0 || now - photoshopChangedAt < 0.8 || stroke != null) return;

        PsdDocument document;
        try
        {
            document = Psd.Read(photoshopPath!);
        }
        catch (IOException)
        {
            return; // Still being written: try again next time.
        }
        catch (Exception e)
        {
            photoshopChangedAt = -1;
            SetStatus($"Couldn't read the PSD Photoshop saved: {e.Message}", error: true);
            return;
        }
        photoshopChangedAt = -1;
        ApplyPhotoshop(document);
    }

    /// <summary>
    /// Replaces the linked texture's layers with the PSD's (the UV guide left out): layers keep their place by name,
    /// layers added in Photoshop are added, layers deleted there go. One undo step.
    /// </summary>
    private void ApplyPhotoshop(PsdDocument document)
    {
        var texture = photoshopTexture!;
        if (document.Width != texture.Width || document.Height != texture.Height)
        {
            SetStatus($"Photoshop saved {document.Width}x{document.Height}, but {texture.Name} is {texture.Width}x{texture.Height}: "
                      + "resize in Sable instead (Resize... on the UV toolbar), then send it again.", error: true);
            return;
        }
        var incoming = document.Layers.Where(l => l.Name != GuideLayerName).ToList();
        if (incoming.Count == 0)
        {
            SetStatus("The PSD has no layers besides the UV guide; nothing came back.", error: true);
            return;
        }

        bool sableChanged = texture.Version != photoshopSentVersion;
        string activeName = texture.ActiveLayer.Name;
        var layers = incoming.Select(l => new Layer(l.Name, l.Pixels) { Visible = l.Visible, Opacity = l.Opacity, Blend = l.Blend }).ToList();
        int active = Math.Max(0, layers.FindLastIndex(l => l.Name == activeName));
        if (layers.All(l => l.Name != activeName)) active = layers.Count - 1;
        LayerEdit(texture, () =>
        {
            texture.Layers.Clear();
            texture.Layers.AddRange(layers);
            texture.ActiveLayerIndex = active;
        });
        photoshopSentVersion = texture.Version;
        string warnings = document.Warnings.Count > 0 ? $" ({string.Join("; ", document.Warnings)})" : "";
        SetStatus($"Updated {texture.Name} from Photoshop: {layers.Count} layer{(layers.Count == 1 ? "" : "s")}"
                  + (sableChanged ? ". It replaced changes made in Sable since sending (Ctrl+Z brings them back)" : "")
                  + warnings + ".", error: document.Warnings.Count > 0);
    }

    /// <summary>The link's line in the Layers window.</summary>
    private void DrawPhotoshopLinkStatus(PaintTexture texture)
    {
        if (photoshopTexture != texture) return;
        ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f), "Linked to Photoshop: saves there come back.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Stop")) StopPhotoshopLink();
    }

    /// <summary>
    /// The Photoshop link without Photoshop: write the PSD, change it as a Photoshop save would (paint a layer, add
    /// one), bring it back, and check the guide stays out and undo restores.
    /// </summary>
    private void SelfTestPhotoshop(PaintTexture tex)
    {
        state.ActiveTexture = Model!.Textures.IndexOf(tex);
        var beforeLayers = tex.Layers.ToList();
        tex.EnsureComposite();
        var beforeComposite = (Color[])tex.Composite.Clone();
        var path = WritePhotoshopFile();
        if (path == null) { Console.WriteLine("[selftest] photoshop: couldn't write the PSD"); return; }
        var written = Psd.Read(path);
        bool guideWritten = written.Layers.Any(l => l.Name == GuideLayerName && l.Locked);

        var edited = written.Layers.Select(l => l with { Pixels = (Color[])l.Pixels.Clone() }).ToList();
        int bottom = 0;
        edited[bottom].Pixels[0] = new Color(9, 8, 7, 255);
        edited.Insert(edited.Count - 1, new PsdLayer("Added in Photoshop", new Color[tex.Width * tex.Height], true, 1f, BlendMode.Multiply));
        Psd.Write(path, tex.Width, tex.Height, edited);
        ApplyPhotoshop(Psd.Read(path));

        bool painted = tex.Layers[0].Pixels[0].Equals(new Color(9, 8, 7, 255));
        bool added = tex.Layers.Any(l => l.Name == "Added in Photoshop" && l.Blend == BlendMode.Multiply);
        bool noGuide = tex.Layers.All(l => l.Name != GuideLayerName);
        int count = tex.Layers.Count;
        undo.Undo();
        tex.EnsureComposite();
        bool undone = tex.Layers.SequenceEqual(beforeLayers) && tex.Composite.SequenceEqual(beforeComposite);
        StopPhotoshopLink();
        Console.WriteLine($"[selftest] photoshop: PSD has {written.Layers.Count} layers (guide locked {guideWritten}); after a save there: "
                          + $"painted texel back {painted}, new layer added {added}, guide left out {noGuide} ({count} layers); undo restores {undone}");
    }
}
