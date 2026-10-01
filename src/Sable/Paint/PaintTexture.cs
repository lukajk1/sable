using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// A paintable texture: a stack of <see cref="Layer"/>s on the CPU (row 0 at the top, like the UV view), flattened
/// into <see cref="Composite"/> and mirrored to a GPU texture. Painting goes to the active layer; the views, the
/// eyedropper and saving see the flattened result. Main thread only.
/// </summary>
public sealed unsafe class PaintTexture : IDisposable
{
    public string Name { get; set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Bottom first. Never empty.</summary>
    public List<Layer> Layers { get; } = new();
    public int ActiveLayerIndex { get; set; }
    public Layer ActiveLayer => Layers[Math.Clamp(ActiveLayerIndex, 0, Layers.Count - 1)];
    /// <summary>The active layer's pixels: what the paint tools change.</summary>
    public Color[] Pixels => ActiveLayer.Pixels;
    /// <summary>
    /// The layers flattened: what the views show and what saving writes. Brought up to date by
    /// <see cref="EnsureComposite"/> (and every <see cref="Upload"/>).
    /// </summary>
    public Color[] Composite { get; private set; }
    public Texture2D Gpu => gpu;
    private Texture2D gpu;
    private TextureView view = TextureView.Pixel;
    private bool mipmapped;
    /// <summary>Where saving writes. Null until a path is chosen (embedded or newly created textures).</summary>
    public string? FilePath { get; set; }
    /// <summary>Changed since it was loaded or last saved.</summary>
    public bool Dirty { get; set; }
    /// <summary>Goes up with every change to any layer, for caches such as the layer thumbnails.</summary>
    public int Version { get; private set; }

    // What changed since the last upload: a rectangle, or everything. The composite may already be current for it.
    private bool needsUpload, uploadAll, compositeCurrent;
    private int dirtyX0 = int.MaxValue, dirtyY0 = int.MaxValue, dirtyX1 = -1, dirtyY1 = -1;
    private Color[]? uploadScratch;
    // Reused by every stroke, so painting doesn't allocate texture-sized arrays (and set off GC pauses) per stroke.
    private Color[]? strokeBefore;
    private float[]? strokeCoverage;

    private PaintTexture(string name, int width, int height, Color[] pixels, string? filePath)
    {
        Name = name;
        Width = width;
        Height = height;
        Layers.Add(new Layer("Layer 1", pixels));
        Composite = (Color[])pixels.Clone();
        FilePath = filePath;
        fixed (Color* data = Composite)
        {
            var image = new Image { Data = data, Width = width, Height = height, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
            gpu = Raylib.LoadTextureFromImage(image);
        }
        Raylib.SetTextureFilter(gpu, TextureFilter.Point);
        Raylib.SetTextureWrap(gpu, TextureWrap.Repeat);
    }

    /// <summary>
    /// Changes the size and replaces the layers (all of that size) in one go: the resize itself and its undo. Makes a
    /// new GPU texture; <see cref="Rendering.GpuModel"/> notices the new id and points its materials at it.
    /// </summary>
    public void SetContents(int width, int height, LayerState state)
    {
        if (width != Width || height != Height)
        {
            Width = width;
            Height = height;
            Composite = new Color[width * height];
            strokeBefore = null;
            strokeCoverage = null;
            uploadScratch = null;
            dirtyX0 = dirtyY0 = int.MaxValue;
            dirtyX1 = dirtyY1 = -1;
            Raylib.UnloadTexture(gpu);
            fixed (Color* data = Composite)
            {
                var image = new Image { Data = data, Width = width, Height = height, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
                gpu = Raylib.LoadTextureFromImage(image);
            }
            Raylib.SetTextureFilter(gpu, TextureFilter.Point);
            Raylib.SetTextureWrap(gpu, TextureWrap.Repeat);
            // The new texture starts as Pixel with no mipmaps; reapply the view the old one had.
            var wanted = view;
            view = TextureView.Pixel;
            mipmapped = false;
            SetView(wanted);
        }
        Restore(state);
    }

    /// <summary>Resamples every layer to a new size (nearest, or smooth), as one undo step.</summary>
    public ResizeStep Resize(int width, int height, bool smooth)
    {
        var before = Snapshot();
        int oldWidth = Width, oldHeight = Height;
        var layers = Layers.Select(l => new Layer(l.Name, Resampler.Resample(l.Pixels, Width, Height, width, height, smooth))
            { Visible = l.Visible, Opacity = l.Opacity, Blend = l.Blend }).ToArray();
        var after = new LayerState(layers, before.Settings, before.Active);
        SetContents(width, height, after);
        return new ResizeStep(this, oldWidth, oldHeight, before, width, height, after);
    }

    /// <summary>
    /// How the texture is sampled: <see cref="TextureView.Pixel"/> (nearest), <see cref="TextureView.Smooth"/>
    /// (bilinear), or <see cref="TextureView.Mipmapped"/> (trilinear with 16x anisotropic filtering, mipmaps made on
    /// first use and kept current as it is painted). Cheap to call every frame.
    /// </summary>
    public void SetView(TextureView mode)
    {
        if (mode == view) return;
        view = mode;
        if (mode == TextureView.Mipmapped && !mipmapped)
        {
            Raylib.GenTextureMipmaps(ref gpu);
            mipmapped = true;
        }
        switch (mode)
        {
            case TextureView.Pixel:
                Raylib.SetTextureFilter(gpu, TextureFilter.Point);
                Rlgl.TextureParameters(gpu.Id, AnisotropyParameter, 1);
                break;
            case TextureView.Smooth:
                Raylib.SetTextureFilter(gpu, TextureFilter.Bilinear);
                Rlgl.TextureParameters(gpu.Id, AnisotropyParameter, 1);
                break;
            case TextureView.Mipmapped:
                Raylib.SetTextureFilter(gpu, TextureFilter.Trilinear);
                Raylib.SetTextureFilter(gpu, TextureFilter.Anisotropic16X);
                break;
        }
    }

    // rlgl's RL_TEXTURE_FILTER_ANISOTROPIC: raylib has no way to turn anisotropy back down, so set it directly.
    private const int AnisotropyParameter = 0x3000;

    public static PaintTexture FromEncoded(string name, string fileType, byte[] data, string? filePath)
    {
        var pixels = DecodeImage(fileType, data, 0, 0, out int width, out int height);
        return new PaintTexture(name, width, height, pixels, filePath);
    }

    /// <summary>
    /// Decodes an image to RGBA pixels (magenta 1x1 if it can't be read), resized with nearest-neighbour to
    /// <paramref name="toWidth"/> x <paramref name="toHeight"/> when those are given and differ.
    /// </summary>
    public static Color[] DecodeImage(string fileType, byte[] data, int toWidth, int toHeight, out int width, out int height)
    {
        Image image = Raylib.LoadImageFromMemory(fileType, data);
        if (image.Width == 0) image = Raylib.GenImageColor(1, 1, Color.Magenta);
        Raylib.ImageFormat(ref image, PixelFormat.UncompressedR8G8B8A8);
        if (toWidth > 0 && toHeight > 0 && (image.Width != toWidth || image.Height != toHeight))
            Raylib.ImageResizeNN(ref image, toWidth, toHeight);
        width = image.Width;
        height = image.Height;
        var pixels = new Span<Color>(image.Data, width * height).ToArray();
        Raylib.UnloadImage(image);
        return pixels;
    }

    public static PaintTexture Create(string name, int width, int height, Color fill)
    {
        var pixels = new Color[width * height];
        Array.Fill(pixels, fill);
        return new PaintTexture(name, width, height, pixels, null) { Dirty = true };
    }

    public int Wrap(int x, int size) => ((x % size) + size) % size;

    /// <summary>The flattened colour of a texel.</summary>
    public Color Get(int x, int y)
    {
        EnsureComposite();
        return Composite[Wrap(y, Height) * Width + Wrap(x, Width)];
    }

    /// <summary>Marks all CPU pixels as changed; <see cref="Upload"/> sends them to the GPU once per frame.</summary>
    public void Touch()
    {
        needsUpload = uploadAll = true;
        compositeCurrent = false;
        Dirty = true;
        Version++;
    }

    /// <summary>Marks one texel as changed; only the changed rectangle is uploaded.</summary>
    public void Touch(int x, int y) => Touch(x, y, x, y);

    /// <summary>Marks a rectangle of texels (inclusive corners) as changed.</summary>
    public void Touch(int x0, int y0, int x1, int y1)
    {
        needsUpload = true;
        compositeCurrent = false;
        Dirty = true;
        Version++;
        dirtyX0 = Math.Min(dirtyX0, x0);
        dirtyY0 = Math.Min(dirtyY0, y0);
        dirtyX1 = Math.Max(dirtyX1, x1);
        dirtyY1 = Math.Max(dirtyY1, y1);
    }

    /// <summary>Flattens what changed since the last composite into <see cref="Composite"/>.</summary>
    public void EnsureComposite()
    {
        if (!needsUpload || compositeCurrent) return;
        if (uploadAll || dirtyX1 < 0) Compositor.Composite(Layers, Composite, Width, 0, 0, Width - 1, Height - 1);
        else Compositor.Composite(Layers, Composite, Width, dirtyX0, dirtyY0, dirtyX1, dirtyY1);
        compositeCurrent = true;
    }

    public void Upload()
    {
        if (!needsUpload) return;
        EnsureComposite();
        int w = dirtyX1 - dirtyX0 + 1, h = dirtyY1 - dirtyY0 + 1;
        if (uploadAll || dirtyX1 < 0 || w * h * 2 > Width * Height)
        {
            fixed (Color* data = Composite) Raylib.UpdateTexture(Gpu, data);
        }
        else
        {
            if (uploadScratch == null || uploadScratch.Length < w * h) uploadScratch = new Color[Width * Height / 2 + 1];
            for (int row = 0; row < h; row++)
                Array.Copy(Composite, (dirtyY0 + row) * Width + dirtyX0, uploadScratch, row * w, w);
            fixed (Color* data = uploadScratch) Raylib.UpdateTextureRec(Gpu, new Rectangle(dirtyX0, dirtyY0, w, h), data);
        }
        // Smaller mip levels are built from the top one, so they go stale as soon as it changes.
        if (mipmapped) Raylib.GenTextureMipmaps(ref gpu);
        needsUpload = uploadAll = compositeCurrent = false;
        dirtyX0 = dirtyY0 = int.MaxValue;
        dirtyX1 = dirtyY1 = -1;
    }

    /// <summary>
    /// The shared stroke buffers, reset for a new stroke: a copy of the active layer as it is now, and zeroed
    /// coverage. Only one stroke per texture is ever in progress.
    /// </summary>
    internal (Color[] Before, float[] Coverage) BeginStrokeBuffers()
    {
        strokeBefore ??= new Color[Pixels.Length];
        strokeCoverage ??= new float[Pixels.Length];
        Array.Copy(Pixels, strokeBefore, Pixels.Length);
        Array.Clear(strokeCoverage);
        return (strokeBefore, strokeCoverage);
    }

    /// <summary>Writes the texture and makes <paramref name="path"/> where it saves from now on.</summary>
    /// <param name="pixels">What to write instead of the plain flattened layers (the edge-padded version).</param>
    public void Save(string path, Color[]? pixels = null)
    {
        ExportTo(path, pixels);
        FilePath = path;
        Dirty = false;
    }

    /// <summary>Writes a flattened copy, leaving where the texture saves (and its unsaved state) alone.</summary>
    public void ExportTo(string path, Color[]? pixels = null)
    {
        EnsureComposite();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        fixed (Color* data = pixels ?? Composite)
        {
            var image = new Image { Data = data, Width = Width, Height = Height, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
            if (!Raylib.ExportImage(image, path)) throw new IOException($"Couldn't write {path}");
        }
    }

    // ---------- layers ----------

    /// <summary>More than one layer, or one that doesn't show its pixels as they are: worth keeping beside the image.</summary>
    public bool HasLayers => Layers.Count > 1 || !Layers[0].IsPlain;

    public LayerState Snapshot() => new(Layers.ToArray(),
        Layers.Select(l => (l.Name, l.Visible, l.Opacity, l.Blend)).ToArray(), ActiveLayerIndex);

    public void Restore(LayerState state)
    {
        Layers.Clear();
        Layers.AddRange(state.Layers);
        for (int i = 0; i < Layers.Count; i++)
        {
            var (name, visible, opacity, blend) = state.Settings[i];
            Layers[i].Name = name;
            Layers[i].Visible = visible;
            Layers[i].Opacity = opacity;
            Layers[i].Blend = blend;
        }
        ActiveLayerIndex = Math.Clamp(state.Active, 0, Layers.Count - 1);
        Touch();
    }

    /// <summary>A name no layer has yet: "Layer N".</summary>
    public string NextLayerName()
    {
        for (int n = Layers.Count + 1; ; n++)
            if (Layers.All(l => l.Name != $"Layer {n}")) return $"Layer {n}";
    }

    /// <summary>Puts a layer above the active one and makes it active.</summary>
    public void InsertLayer(Layer layer)
    {
        ActiveLayerIndex = Math.Clamp(ActiveLayerIndex, 0, Layers.Count - 1) + 1;
        Layers.Insert(ActiveLayerIndex, layer);
        Touch();
    }

    public void DeleteActiveLayer()
    {
        if (Layers.Count <= 1) return;
        Layers.RemoveAt(ActiveLayerIndex);
        ActiveLayerIndex = Math.Clamp(ActiveLayerIndex - 1, 0, Layers.Count - 1);
        Touch();
    }

    /// <summary>Moves the active layer up (+1) or down (-1) the stack.</summary>
    public void MoveActiveLayer(int delta)
    {
        int to = ActiveLayerIndex + delta;
        if (to < 0 || to >= Layers.Count) return;
        (Layers[ActiveLayerIndex], Layers[to]) = (Layers[to], Layers[ActiveLayerIndex]);
        ActiveLayerIndex = to;
        Touch();
    }

    /// <summary>
    /// Merges the active layer into the one below: a new layer with the lower one's name and settings, holding the
    /// upper one laid over it with its opacity and blend mode (a hidden upper layer is dropped).
    /// </summary>
    public void MergeDown()
    {
        int upperIndex = ActiveLayerIndex;
        if (upperIndex <= 0) return;
        Layer upper = Layers[upperIndex], lower = Layers[upperIndex - 1];
        var pixels = (Color[])lower.Pixels.Clone();
        if (upper.Visible)
        {
            Parallel.For(0, Height, y =>
            {
                for (int i = y * Width; i < (y + 1) * Width; i++)
                {
                    var c = pixels[i];
                    float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f;
                    Compositor.Over(upper.Pixels[i], upper.Opacity, upper.Blend, ref r, ref g, ref b, ref a);
                    pixels[i] = Compositor.ToColor(r, g, b, a);
                }
            });
        }
        var merged = new Layer(lower.Name, pixels) { Visible = lower.Visible, Opacity = lower.Opacity, Blend = lower.Blend };
        Layers.RemoveAt(upperIndex);
        Layers[upperIndex - 1] = merged;
        ActiveLayerIndex = upperIndex - 1;
        Touch();
    }

    /// <summary>All layers into one plain layer holding what's shown.</summary>
    public void Flatten()
    {
        EnsureComposite();
        var flat = new Layer(Layers[0].Name, (Color[])Composite.Clone());
        Layers.Clear();
        Layers.Add(flat);
        ActiveLayerIndex = 0;
        Touch();
    }

    public void Dispose() => Raylib.UnloadTexture(Gpu);
}

/// <summary>How textures are sampled in the 3D view.</summary>
public enum TextureView { Pixel, Smooth, Mipmapped }
