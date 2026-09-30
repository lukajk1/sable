using System.IO.Compression;
using System.Text.Json;
using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// Keeps a texture's layers between sessions, beside the image it saves to: <c>.&lt;image&gt;.sable</c>, a zip of
/// <c>layers.json</c> and one PNG per layer. The image itself stays the flattened result, so Blender and Unity never
/// see the layers (Unity skips files starting with a dot). When the image was changed elsewhere after the layers
/// were saved, the layers no longer match it and are left alone.
/// </summary>
public static unsafe class LayerFile
{
    private sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Active { get; set; }
        public List<Entry> Layers { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Name { get; set; } = "";
        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1f;
        public string Blend { get; set; } = nameof(BlendMode.Normal);
        public string File { get; set; } = "";
    }

    public static string PathFor(string imagePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imagePath))!, $".{Path.GetFileName(imagePath)}.sable");

    /// <summary>
    /// Writes the layers beside <paramref name="imagePath"/>, or removes a stale layer file when the texture is a
    /// single plain layer again.
    /// </summary>
    public static void Write(PaintTexture texture, string imagePath)
    {
        string path = PathFor(imagePath);
        if (!texture.HasLayers)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var manifest = new Manifest { Width = texture.Width, Height = texture.Height, Active = texture.ActiveLayerIndex };
        string temp = path + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            for (int i = 0; i < texture.Layers.Count; i++)
            {
                var layer = texture.Layers[i];
                string file = $"layer{i}.png";
                manifest.Layers.Add(new Entry { Name = layer.Name, Visible = layer.Visible, Opacity = layer.Opacity, Blend = layer.Blend.ToString(), File = file });
                // PNG already compresses; don't compress twice.
                using var stream = zip.CreateEntry(file, CompressionLevel.NoCompression).Open();
                stream.Write(EncodePng(layer.Pixels, texture.Width, texture.Height));
            }
            using var json = zip.CreateEntry("layers.json").Open();
            JsonSerializer.Serialize(json, manifest, new JsonSerializerOptions { WriteIndented = true });
        }
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Replaces the texture's single layer with the saved layers, if there are any and they still add up to the
    /// image as loaded. <paramref name="note"/> says why saved layers weren't used.
    /// </summary>
    public static bool TryLoad(PaintTexture texture, string imagePath, out string? note)
    {
        note = null;
        string path = PathFor(imagePath);
        if (!File.Exists(path)) return false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            Manifest manifest;
            using (var json = zip.GetEntry("layers.json")!.Open())
                manifest = JsonSerializer.Deserialize<Manifest>(json) ?? throw new InvalidDataException("empty layers.json");
            if (manifest.Width != texture.Width || manifest.Height != texture.Height || manifest.Layers.Count == 0)
            {
                note = $"{texture.Name}: its saved layers are a different size from the image, so they weren't used.";
                return false;
            }

            var layers = new List<Layer>();
            foreach (var entry in manifest.Layers)
            {
                using var stream = zip.GetEntry(entry.File)!.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var pixels = PaintTexture.DecodeImage(".png", buffer.ToArray(), texture.Width, texture.Height, out _, out _);
                layers.Add(new Layer(entry.Name, pixels)
                {
                    Visible = entry.Visible,
                    Opacity = Math.Clamp(entry.Opacity, 0f, 1f),
                    Blend = Enum.TryParse<BlendMode>(entry.Blend, out var blend) ? blend : BlendMode.Normal,
                });
            }

            var flattened = new Color[texture.Width * texture.Height];
            Compositor.Composite(layers, flattened, texture.Width, 0, 0, texture.Width - 1, texture.Height - 1);
            if (!Matches(flattened, texture.Composite))
            {
                note = $"{texture.Name} was changed outside Sable after its layers were saved, so it opened as one layer "
                       + "(saving it with layers again replaces the old ones).";
                return false;
            }

            texture.Layers.Clear();
            texture.Layers.AddRange(layers);
            texture.ActiveLayerIndex = Math.Clamp(manifest.Active, 0, layers.Count - 1);
            texture.Touch();
            texture.Dirty = false;
            return true;
        }
        catch (Exception e)
        {
            note = $"{texture.Name}: couldn't read its saved layers ({e.Message}).";
            return false;
        }
    }

    /// <summary>Equal, counting any two fully transparent texels as the same.</summary>
    private static bool Matches(Color[] a, Color[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            Color p = a[i], q = b[i];
            if (p.A == 0 && q.A == 0) continue;
            if (p.R != q.R || p.G != q.G || p.B != q.B || p.A != q.A) return false;
        }
        return true;
    }

    private static byte[] EncodePng(Color[] pixels, int width, int height)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"sable-layer-{Guid.NewGuid():N}.png");
        try
        {
            fixed (Color* data = pixels)
            {
                var image = new Image { Data = data, Width = width, Height = height, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
                if (!Raylib.ExportImage(image, temp)) throw new IOException("couldn't encode a layer");
            }
            return File.ReadAllBytes(temp);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
