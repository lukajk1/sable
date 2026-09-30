using Raylib_cs;

namespace PixelPainter.Paint;

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
    public Texture2D Gpu { get; }
    /// <summary>Where saving writes. Null until a path is chosen (embedded or newly created textures).</summary>
    public string? FilePath { get; set; }
    /// <summary>Changed since it was loaded or last saved.</summary>
    public bool Dirty { get; set; }

    private bool needsUpload;

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
            Gpu = Raylib.LoadTextureFromImage(image);
        }
        Raylib.SetTextureFilter(Gpu, TextureFilter.Point);
        Raylib.SetTextureWrap(Gpu, TextureWrap.Repeat);
    }

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

    /// <summary>Marks the CPU pixels as changed; <see cref="Upload"/> sends them to the GPU once per frame.</summary>
    public void Touch()
    {
        needsUpload = true;
        Dirty = true;
    }

    public void Upload()
    {
        if (!needsUpload) return;
        fixed (Color* data = Pixels) Raylib.UpdateTexture(Gpu, data);
        needsUpload = false;
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
