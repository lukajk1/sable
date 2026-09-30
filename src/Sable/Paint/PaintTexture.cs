using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// A paintable texture: RGBA pixels on the CPU (row 0 at the top, like the UV view) mirrored to a point-filtered GPU
/// texture. Main thread only.
/// </summary>
public sealed unsafe class PaintTexture : IDisposable
{
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public Color[] Pixels { get; }
    public Texture2D Gpu => gpu;
    private Texture2D gpu;
    private TextureView view = TextureView.Pixel;
    private bool mipmapped;
    /// <summary>Where saving writes. Null until a path is chosen (embedded or newly created textures).</summary>
    public string? FilePath { get; set; }
    /// <summary>Changed since it was loaded or last saved.</summary>
    public bool Dirty { get; set; }

    // What changed since the last upload: a rectangle, or everything.
    private bool needsUpload, uploadAll;
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
        Pixels = pixels;
        FilePath = filePath;
        fixed (Color* data = pixels)
        {
            var image = new Image { Data = data, Width = width, Height = height, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
            gpu = Raylib.LoadTextureFromImage(image);
        }
        Raylib.SetTextureFilter(gpu, TextureFilter.Point);
        Raylib.SetTextureWrap(gpu, TextureWrap.Repeat);
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
        Image image = Raylib.LoadImageFromMemory(fileType, data);
        if (image.Width == 0) image = Raylib.GenImageColor(1, 1, Color.Magenta);
        Raylib.ImageFormat(ref image, PixelFormat.UncompressedR8G8B8A8);
        var pixels = new Span<Color>(image.Data, image.Width * image.Height).ToArray();
        var texture = new PaintTexture(name, image.Width, image.Height, pixels, filePath);
        Raylib.UnloadImage(image);
        return texture;
    }

    public static PaintTexture Create(string name, int width, int height, Color fill)
    {
        var pixels = new Color[width * height];
        Array.Fill(pixels, fill);
        return new PaintTexture(name, width, height, pixels, null) { Dirty = true };
    }

    public int Wrap(int x, int size) => ((x % size) + size) % size;

    public Color Get(int x, int y) => Pixels[Wrap(y, Height) * Width + Wrap(x, Width)];

    /// <summary>Marks all CPU pixels as changed; <see cref="Upload"/> sends them to the GPU once per frame.</summary>
    public void Touch()
    {
        needsUpload = uploadAll = true;
        Dirty = true;
    }

    /// <summary>Marks one texel as changed; only the changed rectangle is uploaded.</summary>
    public void Touch(int x, int y)
    {
        needsUpload = true;
        Dirty = true;
        dirtyX0 = Math.Min(dirtyX0, x);
        dirtyY0 = Math.Min(dirtyY0, y);
        dirtyX1 = Math.Max(dirtyX1, x);
        dirtyY1 = Math.Max(dirtyY1, y);
    }

    public void Upload()
    {
        if (!needsUpload) return;
        int w = dirtyX1 - dirtyX0 + 1, h = dirtyY1 - dirtyY0 + 1;
        if (uploadAll || dirtyX1 < 0 || w * h * 2 > Width * Height)
        {
            fixed (Color* data = Pixels) Raylib.UpdateTexture(Gpu, data);
        }
        else
        {
            if (uploadScratch == null || uploadScratch.Length < w * h) uploadScratch = new Color[Width * Height / 2 + 1];
            for (int row = 0; row < h; row++)
                Array.Copy(Pixels, (dirtyY0 + row) * Width + dirtyX0, uploadScratch, row * w, w);
            fixed (Color* data = uploadScratch) Raylib.UpdateTextureRec(Gpu, new Rectangle(dirtyX0, dirtyY0, w, h), data);
        }
        // Smaller mip levels are built from the top one, so they go stale as soon as it changes.
        if (mipmapped) Raylib.GenTextureMipmaps(ref gpu);
        needsUpload = uploadAll = false;
        dirtyX0 = dirtyY0 = int.MaxValue;
        dirtyX1 = dirtyY1 = -1;
    }

    /// <summary>
    /// The shared stroke buffers, reset for a new stroke: a copy of the pixels as they are now, and zeroed coverage.
    /// Only one stroke per texture is ever in progress.
    /// </summary>
    internal (Color[] Before, float[] Coverage) BeginStrokeBuffers()
    {
        strokeBefore ??= new Color[Pixels.Length];
        strokeCoverage ??= new float[Pixels.Length];
        Array.Copy(Pixels, strokeBefore, Pixels.Length);
        Array.Clear(strokeCoverage);
        return (strokeBefore, strokeCoverage);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        fixed (Color* data = Pixels)
        {
            var image = new Image { Data = data, Width = Width, Height = Height, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
            if (!Raylib.ExportImage(image, path)) throw new IOException($"Couldn't write {path}");
        }
        FilePath = path;
        Dirty = false;
    }

    public void Dispose() => Raylib.UnloadTexture(Gpu);
}

/// <summary>How textures are sampled in the 3D view.</summary>
public enum TextureView { Pixel, Smooth, Mipmapped }
