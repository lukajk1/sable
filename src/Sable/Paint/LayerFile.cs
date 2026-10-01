using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// Keeps a texture's layers between sessions, beside the image it saves to: <c>.&lt;image&gt;.sable</c>, a zip of
/// <c>layers.json</c> and one PNG per layer. The image itself stays the flattened result, so Blender and Unity never
/// see the layers (Unity skips files starting with a dot). When the image was changed elsewhere after the layers
/// were saved, the layers no longer match it and are left alone. Autosave (<see cref="Recovery"/>) keeps layers in
/// the same zip format, through <see cref="WriteZip"/> and <see cref="ReadZip"/>.
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
        /// <summary>
        /// <see cref="ImageHash"/> of the image saved beside the layers (as written, edge padding included), so
        /// reopening can tell whether the image is still the one these layers made.
        /// </summary>
        public string? ImageHash { get; set; }
    }

    private sealed class Entry
    {
        public string Name { get; set; } = "";
        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1f;
        public string Blend { get; set; } = nameof(BlendMode.Normal);
        /// <summary>A <see cref="LayerKind"/>; files from before the smoothness mask have none (Normal).</summary>
        public string Kind { get; set; } = nameof(LayerKind.Normal);
        public string File { get; set; } = "";
    }

    public static string PathFor(string imagePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imagePath))!, $".{Path.GetFileName(imagePath)}.sable");

    /// <summary>
    /// Writes the layers beside <paramref name="imagePath"/>, or removes a stale layer file when the texture is a
    /// single plain layer again.
    /// </summary>
    /// <param name="written">The pixels written to the image (flattened, after edge padding).</param>
    public static void Write(PaintTexture texture, string imagePath, Color[] written)
    {
        string path = PathFor(imagePath);
        if (!texture.HasLayers)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        string temp = path + ".tmp";
        // A temp file left by an earlier save that couldn't finish would block this one.
        if (File.Exists(temp)) File.Delete(temp);
        try
        {
            WriteZip(temp, texture.Width, texture.Height, texture.ActiveLayerIndex,
                texture.Layers.Select(l => new LayerData(l.Name, l.Visible, l.Opacity, l.Blend, l.Pixels, l.Kind)).ToArray(),
                imageHash: ImageHash(written));
            ReplaceWithRetry(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Moves <paramref name="temp"/> over <paramref name="path"/>, retrying for a moment while something else (a
    /// virus scanner, a sync client, Explorer's preview) briefly has the old file open.
    /// </summary>
    private static void ReplaceWithRetry(string temp, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(100);
            }
        }
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
            if (ReadZip(path, texture.Width, texture.Height) is not { } saved)
            {
                note = $"{texture.Name}: its saved layers are a different size from the image, so they weren't used.";
                return false;
            }
            var layers = saved.State.Layers;

            // The image must still be what Sable wrote: by its hash (edge padding means it isn't simply the layers
            // flattened), or, for layer files from before the hash, by flattening the layers.
            bool unchanged;
            if (ReadImageHash(path) is { } hash) unchanged = hash == ImageHash(texture.Composite);
            else
            {
                var flattened = new Color[texture.Width * texture.Height];
                Compositor.Composite(layers, flattened, texture.Width, 0, 0, texture.Width - 1, texture.Height - 1);
                unchanged = Matches(flattened, texture.Composite);
            }
            if (!unchanged)
            {
                note = $"{texture.Name} was changed outside Sable after its layers were saved, so it opened as one layer "
                       + "(saving it with layers again replaces the old ones).";
                return false;
            }

            texture.Layers.Clear();
            texture.Layers.AddRange(layers);
            texture.ActiveLayerIndex = Math.Clamp(saved.State.Active, 0, layers.Length - 1);
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

    /// <summary>
    /// Writes a zip of <c>layers.json</c> and one PNG per layer (bottom first) to <paramref name="path"/>. It touches
    /// no GPU state, so it can run off the main thread on pixels nothing else is changing. <paramref name="fast"/>
    /// encodes the PNGs many times quicker, into somewhat larger files (for autosave).
    /// </summary>
    public static void WriteZip(string path, int width, int height, int active, IReadOnlyList<LayerData> layers, bool fast = false,
        string? imageHash = null)
    {
        var manifest = new Manifest { Width = width, Height = height, Active = active, ImageHash = imageHash };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            string file = $"layer{i}.png";
            manifest.Layers.Add(new Entry
            {
                Name = layer.Name, Visible = layer.Visible, Opacity = layer.Opacity, Blend = layer.Blend.ToString(), Kind = layer.Kind.ToString(), File = file,
            });
            // PNG already compresses; don't compress twice.
            using var stream = zip.CreateEntry(file, CompressionLevel.NoCompression).Open();
            if (fast) WritePngFast(stream, layer.Pixels, width, height);
            else stream.Write(EncodePng(layer.Pixels, width, height));
        }
        using var json = zip.CreateEntry("layers.json").Open();
        JsonSerializer.Serialize(json, manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Reads a zip <see cref="WriteZip"/> wrote: its size, and its layers with their settings and the active one.
    /// Null when it holds no layers, or when <paramref name="width"/> and <paramref name="height"/> are given and it
    /// is a different size. Throws when it can't be read.
    /// </summary>
    public static (int Width, int Height, LayerState State)? ReadZip(string path, int width = 0, int height = 0)
    {
        using var zip = ZipFile.OpenRead(path);
        Manifest manifest;
        using (var json = zip.GetEntry("layers.json")!.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(json) ?? throw new InvalidDataException("empty layers.json");
        if (manifest.Layers.Count == 0 || manifest.Width <= 0 || manifest.Height <= 0) return null;
        if (width > 0 && height > 0 && (manifest.Width != width || manifest.Height != height)) return null;

        var layers = new List<Layer>();
        foreach (var entry in manifest.Layers)
        {
            using var stream = zip.GetEntry(entry.File)!.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var pixels = PaintTexture.DecodeImage(".png", buffer.ToArray(), manifest.Width, manifest.Height, out _, out _);
            layers.Add(new Layer(entry.Name, pixels)
            {
                Visible = entry.Visible,
                Opacity = Math.Clamp(entry.Opacity, 0f, 1f),
                Blend = Enum.TryParse<BlendMode>(entry.Blend, out var blend) ? blend : BlendMode.Normal,
                Kind = Enum.TryParse<LayerKind>(entry.Kind, out var kind) ? kind : LayerKind.Normal,
            });
        }
        var active = layers[Math.Clamp(manifest.Active, 0, layers.Count - 1)];
        layers = PaintTexture.ArrangeMask(layers);
        var state = new LayerState(layers.ToArray(), layers.Select(l => (l.Name, l.Visible, l.Opacity, l.Blend)).ToArray(),
            Math.Max(0, layers.IndexOf(active)));
        return (manifest.Width, manifest.Height, state);
    }

    /// <summary>The image hash a layer file records, or null (older layer files have none).</summary>
    public static string? ReadImageHash(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var json = zip.GetEntry("layers.json")!.Open();
        return JsonSerializer.Deserialize<Manifest>(json)?.ImageHash;
    }

    /// <summary>A hash of an image's pixels, counting every fully transparent texel as the same.</summary>
    public static string ImageHash(Color[] pixels)
    {
        var normalized = new Color[pixels.Length];
        for (int i = 0; i < pixels.Length; i++) normalized[i] = pixels[i].A == 0 ? default : pixels[i];
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(MemoryMarshal.AsBytes(normalized.AsSpan())));
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

    /// <summary>PNG bytes from raylib's encoder (stb_image_write), in memory: no GPU and no temporary file.</summary>
    internal static byte[] EncodePng(Color[] pixels, int width, int height)
    {
        int size = 0;
        byte* data;
        fixed (Color* source = pixels)
        fixed (byte* type = ".png\0"u8)
        {
            var image = new Image { Data = source, Width = width, Height = height, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
            data = (byte*)Raylib.ExportImageToMemory(image, (sbyte*)type, &size);
        }
        if (data == null || size <= 0) throw new IOException("couldn't encode a layer");
        try
        {
            return new ReadOnlySpan<byte>(data, size).ToArray();
        }
        finally
        {
            Raylib.MemFree(data);
        }
    }

    /// <summary>
    /// A PNG from .NET's zlib at its fastest level, every row stored as its difference from the row above (PNG's
    /// "Up" filter). stb_image_write tries five filters per row with a slow deflate; this is many times quicker.
    /// </summary>
    private static void WritePngFast(Stream output, Color[] pixels, int width, int height)
    {
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR"u8, header);

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = MemoryMarshal.AsBytes(pixels.AsSpan());
            int stride = width * 4;
            var row = new byte[stride + 1];
            for (int y = 0; y < height; y++)
            {
                var current = bytes.Slice(y * stride, stride);
                if (y == 0)
                {
                    row[0] = 0;
                    current.CopyTo(row.AsSpan(1));
                }
                else
                {
                    row[0] = 2;
                    var above = bytes.Slice((y - 1) * stride, stride);
                    var to = row.AsSpan(1);
                    int i = 0;
                    for (; i <= stride - Vector<byte>.Count; i += Vector<byte>.Count)
                        (new Vector<byte>(current[i..]) - new Vector<byte>(above[i..])).CopyTo(to[i..]);
                    for (; i < stride; i++) to[i] = (byte)(current[i] - above[i]);
                }
                zlib.Write(row);
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~Crc(Crc(0xFFFFFFFFu, type), data));
        output.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        var table = CrcTable.Value;
        foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static readonly Lazy<uint[]> CrcTable = new(() =>
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    });
}

/// <summary>One layer as a layer zip keeps it: its settings, and its pixels (row 0 at the top).</summary>
public readonly record struct LayerData(string Name, bool Visible, float Opacity, BlendMode Blend, Color[] Pixels,
    LayerKind Kind = LayerKind.Normal);
