using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// A layer on its way to or from a PSD: full-canvas straight-alpha RGBA (row 0 at the top) and the settings Sable's
/// layers have. <see cref="Locked"/> is Photoshop's layer lock; writing sets "lock all".
/// </summary>
public sealed record PsdLayer(string Name, Color[] Pixels, bool Visible, float Opacity, BlendMode Blend, bool Locked = false);

/// <summary>
/// A PSD as read: its layers bottom first, each full-canvas RGBA; the flattened image saved with it (null when the
/// file has no usable one); and what couldn't be brought in exactly, in words for the user.
/// </summary>
public sealed record PsdDocument(int Width, int Height, IReadOnlyList<PsdLayer> Layers, Color[]? Composite, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads and writes layered Photoshop files: PSD version 1, 8 bits per channel, RGB with transparency. Writing gives
/// every layer full-canvas bounds and RLE (PackBits) channels, and stores the flattened image so programs that only
/// read that still show something sensible (laid over white, as Photoshop stores it). Reading takes what Photoshop saves: raw, RLE or ZIP channels, layers of
/// any bounds, layer masks (applied to the layer's alpha), groups (flattened away), clipping masks (baked in), and
/// text and smart object layers as the pixels Photoshop rendered for them. Adjustment layers, layer effects and
/// vector masks don't come in; <see cref="PsdDocument.Warnings"/> says so.
/// </summary>
public static class Psd
{
    /// <summary>The largest width or height a PSD (as opposed to a PSB) can have.</summary>
    public const int MaxSize = 30000;

    // Layers may reach past the canvas, but not absurdly far.
    private const int MaxLayerSide = 1 << 18;

    private static readonly short[] WrittenChannels = { -1, 0, 1, 2 };

    // ---- Writing

    /// <summary>
    /// Writes <paramref name="layers"/> (bottom first, each <paramref name="width"/> x <paramref name="height"/>) to
    /// <paramref name="path"/>, through a temporary file so a reader never sees half of it.
    /// <paramref name="composite"/> is the flattened image to store; null flattens the layers here.
    /// </summary>
    public static void Write(string path, int width, int height, IReadOnlyList<PsdLayer> layers, Color[]? composite = null)
    {
        Validate(width, height, layers, composite);
        string temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
                Write(stream, width, height, layers, composite);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Writes a PSD to a seekable stream, from its current position.</summary>
    public static void Write(Stream stream, int width, int height, IReadOnlyList<PsdLayer> layers, Color[]? composite = null)
    {
        Validate(width, height, layers, composite);
        if (!stream.CanSeek) throw new ArgumentException("Writing a PSD needs a seekable stream.", nameof(stream));
        composite ??= Flatten(width, height, layers);
        var s = stream;

        Ascii(s, "8BPS");
        U16(s, 1);
        s.Write(stackalloc byte[6]);
        U16(s, 4); // RGB, plus the flattened image's transparency
        U32(s, (uint)height);
        U32(s, (uint)width);
        U16(s, 8);
        U16(s, 3);
        U32(s, 0); // colour mode data
        U32(s, 0); // image resources

        long layerSection = Placeholder(s);
        long layerInfo = Placeholder(s);
        U16(s, -layers.Count); // negative: the flattened image's first extra channel is its transparency
        var lengthAt = new long[layers.Count * WrittenChannels.Length];
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            U32(s, 0);
            U32(s, 0);
            U32(s, (uint)height);
            U32(s, (uint)width);
            U16(s, WrittenChannels.Length);
            for (int c = 0; c < WrittenChannels.Length; c++)
            {
                U16(s, WrittenChannels[c]);
                lengthAt[i * WrittenChannels.Length + c] = Placeholder(s);
            }
            Ascii(s, "8BIM");
            Ascii(s, BlendKey(layer.Blend));
            s.WriteByte((byte)Math.Clamp((int)MathF.Round(layer.Opacity * 255f), 0, 255));
            s.WriteByte(0); // not clipped
            s.WriteByte((byte)(0x08 | (layer.Visible ? 0 : 0x02)));
            s.WriteByte(0);
            byte[] extra = ExtraData(layer);
            U32(s, (uint)extra.Length);
            s.Write(extra);
        }

        using (var rle = new RleEncoder(width, height))
        {
            for (int i = 0; i < layers.Count; i++)
                for (int c = 0; c < WrittenChannels.Length; c++)
                {
                    long start = s.Position;
                    U16(s, 1);
                    rle.Encode(layers[i].Pixels, WrittenChannels[c] < 0 ? 3 : WrittenChannels[c]);
                    rle.WriteCounts(s);
                    rle.WriteData(s);
                    Patch(s, lengthAt[i * WrittenChannels.Length + c], s.Position - start);
                }
            Pad(s, s.Position - layerInfo - 4, 4);
            Patch(s, layerInfo);
            U32(s, 0); // global layer mask info
            Patch(s, layerSection);

            U16(s, 1);
            long countsAt = s.Position;
            var counts = new byte[4 * height * 2];
            s.Write(counts);
            for (int c = 0; c < 4; c++)
            {
                rle.Encode(composite, c, matte: c < 3);
                rle.CopyCounts(counts.AsSpan(c * height * 2, height * 2));
                rle.WriteData(s);
            }
            long end = s.Position;
            s.Position = countsAt;
            s.Write(counts);
            s.Position = end;
        }
        s.Flush();
    }

    private static void Validate(int width, int height, IReadOnlyList<PsdLayer> layers, Color[]? composite)
    {
        if (width is < 1 or > MaxSize || height is < 1 or > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(width), $"A PSD is 1 to {MaxSize} pixels on a side, not {width}x{height}.");
        if (layers.Count is 0 or > short.MaxValue)
            throw new ArgumentException($"A PSD holds 1 to {short.MaxValue} layers, not {layers.Count}.", nameof(layers));
        int n = width * height;
        foreach (var layer in layers)
            if (layer.Pixels.Length != n)
                throw new ArgumentException($"Layer '{layer.Name}' has {layer.Pixels.Length} pixels; {width}x{height} needs {n}.", nameof(layers));
        if (composite != null && composite.Length != n)
            throw new ArgumentException($"The flattened image has {composite.Length} pixels; {width}x{height} needs {n}.", nameof(composite));
    }

    private static Color[] Flatten(int width, int height, IReadOnlyList<PsdLayer> layers)
    {
        var stack = layers.Select(l => new Layer(l.Name, l.Pixels) { Visible = l.Visible, Opacity = l.Opacity, Blend = l.Blend }).ToList();
        var output = new Color[width * height];
        Compositor.Composite(stack, output, width, 0, 0, width - 1, height - 1);
        return output;
    }

    /// <summary>A layer record's extra data: no mask, default blending ranges, the name twice, and the lock.</summary>
    private static byte[] ExtraData(PsdLayer layer)
    {
        var m = new MemoryStream();
        U32(m, 0);
        U32(m, 40); // blending ranges: grey and four channels, source and destination, all blending
        for (int i = 0; i < 10; i++) U32(m, 0x0000FFFF);

        byte[] pascal = Encoding.Latin1.GetBytes(layer.Name);
        if (pascal.Length > 255) pascal = pascal[..255];
        m.WriteByte((byte)pascal.Length);
        m.Write(pascal);
        Pad(m, 1 + pascal.Length, 4);

        Ascii(m, "8BIMluni");
        int length = 4 + 2 * layer.Name.Length;
        U32(m, (uint)((length + 3) & ~3));
        U32(m, (uint)layer.Name.Length);
        m.Write(Encoding.BigEndianUnicode.GetBytes(layer.Name));
        Pad(m, length, 4);

        Ascii(m, "8BIMlspf");
        U32(m, 4);
        U32(m, layer.Locked ? 0x80000000u : 0u);
        return m.ToArray();
    }

    private static string BlendKey(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => "mul ",
        BlendMode.Screen => "scrn",
        BlendMode.Overlay => "over",
        BlendMode.Add => "lddg",
        _ => "norm",
    };

    /// <summary>
    /// PackBits-compresses one channel of a whole image, rows in parallel, into a reused buffer, and writes the row
    /// byte counts and the data.
    /// </summary>
    private sealed class RleEncoder : IDisposable
    {
        private readonly int width, height, stride;
        private readonly byte[] rows;
        private readonly int[] lengths;

        public RleEncoder(int width, int height)
        {
            this.width = width;
            this.height = height;
            stride = width + (width + 127) / 128 + 1;
            rows = ArrayPool<byte>.Shared.Rent(stride * height);
            lengths = new int[height];
        }

        /// <summary>Compresses one channel of <paramref name="pixels"/>; <paramref name="matte"/> lays colour over white first.</summary>
        public void Encode(Color[] pixels, int channel, bool matte = false) =>
            Parallel.For(0, height, () => new byte[width], (y, _, row) =>
            {
                var bytes = MemoryMarshal.AsBytes(pixels.AsSpan(y * width, width));
                if (matte)
                    for (int x = 0, i = channel; x < width; x++, i += 4)
                    {
                        int a = bytes[i - channel + 3];
                        row[x] = (byte)((bytes[i] * a + 255 * (255 - a) + 127) / 255);
                    }
                else
                    for (int x = 0, i = channel; x < width; x++, i += 4) row[x] = bytes[i];
                lengths[y] = PackBits(row, rows.AsSpan(y * stride, stride));
                return row;
            }, _ => { });

        public void CopyCounts(Span<byte> into)
        {
            for (int y = 0; y < height; y++) BinaryPrimitives.WriteUInt16BigEndian(into[(y * 2)..], (ushort)lengths[y]);
        }

        public void WriteCounts(Stream s)
        {
            var counts = new byte[height * 2];
            CopyCounts(counts);
            s.Write(counts);
        }

        public void WriteData(Stream s)
        {
            for (int y = 0; y < height; y++) s.Write(rows, y * stride, lengths[y]);
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(rows);
    }

    /// <summary>PackBits: runs of three or more repeat, everything else goes in literal stretches of up to 128.</summary>
    private static int PackBits(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int n = src.Length, i = 0, o = 0;
        while (i < n)
        {
            byte v = src[i];
            int run = 1;
            while (run < 128 && i + run < n && src[i + run] == v) run++;
            if (run >= 3)
            {
                dst[o++] = (byte)(257 - run);
                dst[o++] = v;
                i += run;
                continue;
            }
            int start = i;
            i += run;
            while (i < n && i - start < 128 && !(i + 2 < n && src[i] == src[i + 1] && src[i] == src[i + 2])) i++;
            int count = i - start;
            dst[o++] = (byte)(count - 1);
            src.Slice(start, count).CopyTo(dst[o..]);
            o += count;
        }
        return o;
    }

    private static void U16(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        s.Write(b);
    }

    private static void U32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void Ascii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));

    private static void Pad(Stream s, long written, int multiple)
    {
        for (long i = written; i % multiple != 0; i++) s.WriteByte(0);
    }

    private static long Placeholder(Stream s)
    {
        long at = s.Position;
        U32(s, 0);
        return at;
    }

    /// <summary>Fills in a length written as a placeholder at <paramref name="at"/>: what follows it, by default.</summary>
    private static void Patch(Stream s, long at, long length = -1)
    {
        long end = s.Position;
        s.Position = at;
        U32(s, (uint)(length < 0 ? end - at - 4 : length));
        s.Position = end;
    }

    // ---- Reading

    /// <summary>
    /// Reads a PSD. Throws <see cref="InvalidDataException"/>, with a message for the user, for files Sable can't
    /// read (PSB, not RGB, not 8-bit, damaged, or cut short because it's still being written).
    /// </summary>
    public static PsdDocument Read(string path)
    {
        byte[] file;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > Array.MaxLength) throw new InvalidDataException("The file is over 2 GB, too big for a PSD.");
            file = new byte[stream.Length];
            stream.ReadExactly(file);
        }
        return Read(file);
    }

    /// <summary>Reads a PSD from its bytes; see <see cref="Read(string)"/>.</summary>
    public static PsdDocument Read(byte[] file)
    {
        var c = new Cursor(file, 0, file.Length);
        if (file.Length < 26 || c.Key() != "8BPS") throw new InvalidDataException("This isn't a Photoshop file.");
        int version = c.U16();
        if (version == 2)
            throw new InvalidDataException("This is a Large Document (PSB) file. Sable reads PSD: in Photoshop, use File > Save As and choose Photoshop (*.PSD).");
        if (version != 1) throw new InvalidDataException($"This is PSD version {version}, which Sable doesn't know.");
        c.Skip(6);
        int channels = c.U16();
        int height = c.I32(), width = c.I32();
        int depth = c.U16(), mode = c.U16();
        if (mode != 3)
            throw new InvalidDataException($"This PSD is in {ModeName(mode)} mode. Sable reads RGB: in Photoshop, use Image > Mode > RGB Color, then save.");
        if (depth != 8)
            throw new InvalidDataException($"This PSD has {depth} bits per channel. Sable reads 8: in Photoshop, use Image > Mode > 8 Bits/Channel, then save.");
        if (width is < 1 or > MaxSize || height is < 1 or > MaxSize)
            throw new InvalidDataException($"This PSD says it's {width}x{height} pixels, which can't be right; the file is damaged.");
        if (channels is < 3 or > 56) throw Damaged($"it says it has {channels} channels");

        c.Skip(c.Length32()); // colour mode data
        var resources = ReadResources(c.Take(c.Length32()));
        var section = c.Take(c.Length32());
        var merged = c;

        var warnings = new List<string>();
        var records = new List<Record>();
        int layerCount = 0;
        bool mergedTransparency = false;
        if (section.Left >= 4)
        {
            var info = section.Take(section.Length32());
            if (info.Left > 0) layerCount = ReadLayerInfo(info, records);
            if (section.Left >= 4) section.Skip(section.Length32()); // global layer mask info
            foreach (var (key, block) in TaggedBlocks(section))
            {
                if (key is "Mtrn") mergedTransparency = true;
                else if (key is "Layr" && records.Count == 0 && block.Left > 0) layerCount = ReadLayerInfo(block, records);
            }
        }

        bool compositeAlpha = channels >= 4 && (layerCount < 0 || mergedTransparency || (layerCount == 0 && !resources.AlphaNames));
        Color[]? composite = null;
        if (resources.RealMergedData)
            composite = ReadComposite(merged, width, height, channels, compositeAlpha, warnings);

        if (records.Count == 0)
        {
            if (composite == null) throw new InvalidDataException("This PSD has no layers and no image Sable can read.");
            return new PsdDocument(width, height, new[] { new PsdLayer("Background", (Color[])composite.Clone(), true, 1f, BlendMode.Normal) },
                composite, warnings);
        }
        var layers = BuildLayers(file, records, width, height, warnings);
        if (layers.Count == 0) warnings.Add("The PSD has no pixel layers Sable can use.");
        return new PsdDocument(width, height, layers, composite, warnings);
    }

    private static string ModeName(int mode) => mode switch
    {
        0 => "Bitmap",
        1 => "Grayscale",
        2 => "Indexed Color",
        4 => "CMYK",
        7 => "Multichannel",
        8 => "Duotone",
        9 => "Lab",
        _ => $"an unknown ({mode})",
    };

    private static InvalidDataException Damaged(string what) => new($"This PSD is damaged: {what}.");

    private static InvalidDataException Truncated() =>
        new("The PSD ends early: it may still be being saved, or it's damaged.");

    private enum Kind { Pixel, Text, SmartObject, Fill, Shape, Adjustment }

    private sealed class Channel
    {
        public short Id;
        public long Length;
        public int Offset;
    }

    private sealed record MaskInfo(int Top, int Left, int Bottom, int Right, byte Background, byte Flags)
    {
        public bool Disabled => (Flags & 2) != 0;
        public bool Sane => Bottom >= Top && Right >= Left && Bottom - Top <= MaxLayerSide && Right - Left <= MaxLayerSide;
    }

    private sealed class Record
    {
        public int Top, Left, Bottom, Right;
        public readonly List<Channel> Channels = new();
        public string BlendKey = "norm";
        public byte Opacity = 255, Clipping, Flags, Fill = 255;
        public MaskInfo? Mask, RealMask;
        public string Name = "";
        public int Section;
        public uint Lock;
        public Kind Kind;
        public bool Effects, VectorMask;

        public bool Hidden => (Flags & 2) != 0;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public Channel? Find(int id) => Channels.Find(ch => ch.Id == id);
        public bool HasPixels => Width > 0 && Height > 0 && Channels.Any(ch => ch.Id is >= 0 and <= 2 && ch.Length > 2);
    }

    private sealed record Resources(bool AlphaNames, bool RealMergedData);

    private static Resources ReadResources(Cursor c)
    {
        bool alphaNames = false, realMerged = true;
        try
        {
            while (c.Left >= 12)
            {
                if (c.Key() is not ("8BIM" or "MeSa" or "AgHg" or "PHUT" or "DCSR")) break;
                int id = c.U16();
                int nameLength = c.U8();
                c.Skip(((nameLength + 2) & ~1) - 1);
                long size = c.Length32();
                var data = c.Take(size);
                if (size % 2 == 1 && c.Left > 0) c.Skip(1);
                if (id is 1006 or 1045) alphaNames = true;
                else if (id == 1057 && data.Left >= 5)
                {
                    data.Skip(4);
                    realMerged = data.U8() != 0;
                }
            }
        }
        catch (InvalidDataException)
        {
            // Image resources only refine the flattened image; a damaged one leaves the defaults.
        }
        return new Resources(alphaNames, realMerged);
    }

    /// <summary>Reads the layer count, the layer records and where each channel's data is.</summary>
    private static int ReadLayerInfo(Cursor c, List<Record> records)
    {
        int count = c.I16();
        int n = Math.Abs(count);
        if ((long)n * 34 > c.Left) throw Truncated();
        for (int i = 0; i < n; i++) records.Add(ReadRecord(c));
        foreach (var record in records)
            foreach (var channel in record.Channels)
            {
                c.Need(channel.Length);
                channel.Offset = c.Pos;
                c.Skip(channel.Length);
            }
        return count;
    }

    private static Record ReadRecord(Cursor c)
    {
        var r = new Record { Top = c.I32(), Left = c.I32(), Bottom = c.I32(), Right = c.I32() };
        int channelCount = c.U16();
        if (channelCount > 56) throw Damaged($"a layer has {channelCount} channels");
        for (int i = 0; i < channelCount; i++) r.Channels.Add(new Channel { Id = c.I16(), Length = c.U32() });
        if (c.Key() is not ("8BIM" or "8B64")) throw Damaged("a layer record is out of place");
        r.BlendKey = c.Key();
        r.Opacity = c.U8();
        r.Clipping = c.U8();
        r.Flags = c.U8();
        c.Skip(1);
        var extra = c.Take(c.Length32());

        var mask = extra.Take(extra.Length32());
        if (mask.Left >= 18)
        {
            r.Mask = new MaskInfo(mask.I32(), mask.I32(), mask.I32(), mask.I32(), mask.U8(), mask.U8());
            if (r.Find(-3) != null && mask.Left >= 18)
            {
                byte flags = mask.U8(), background = mask.U8();
                r.RealMask = new MaskInfo(mask.I32(), mask.I32(), mask.I32(), mask.I32(), background, flags);
            }
        }
        extra.Skip(extra.Length32()); // blending ranges
        int nameLength = extra.U8();
        extra.Need(nameLength);
        r.Name = Encoding.Latin1.GetString(extra.Data, extra.Pos, nameLength);
        extra.Skip(nameLength);
        extra.Skip(Math.Min(extra.Left, ((nameLength + 4) & ~3) - 1 - nameLength));

        foreach (var (key, block) in TaggedBlocks(extra))
            switch (key)
            {
                case "luni" when block.Left >= 4:
                    long chars = block.U32();
                    if (chars * 2 <= block.Left) r.Name = Encoding.BigEndianUnicode.GetString(block.Data, block.Pos, (int)chars * 2).TrimEnd('\0');
                    break;
                case "lsct" or "lsdk" when block.Left >= 4:
                    r.Section = (int)block.U32();
                    break;
                case "lspf" when block.Left >= 4:
                    r.Lock = block.U32();
                    break;
                case "iOpa" when block.Left >= 1:
                    r.Fill = block.U8();
                    break;
                case "TySh" or "tySh":
                    r.Kind = Kind.Text;
                    break;
                case "SoLd" or "SoLE" or "PlLd" or "plLd":
                    if (r.Kind == Kind.Pixel) r.Kind = Kind.SmartObject;
                    break;
                case "SoCo" or "GdFl" or "PtFl":
                    if (r.Kind == Kind.Pixel) r.Kind = Kind.Fill;
                    break;
                case "vscg":
                    if (r.Kind is Kind.Pixel or Kind.Fill) r.Kind = Kind.Shape;
                    break;
                case "levl" or "curv" or "brit" or "blnc" or "blwh" or "expA" or "hue " or "hue2" or "mixr" or "nvrt"
                    or "phfl" or "post" or "selc" or "thrs" or "vibA" or "grdm" or "clrL":
                    r.Kind = Kind.Adjustment;
                    break;
                case "lfx2" or "lrFX" or "lmfx":
                    r.Effects = true;
                    break;
                case "vmsk" or "vsms":
                    r.VectorMask = true;
                    break;
            }

        if (r.Kind == Kind.Fill && r.VectorMask) r.Kind = Kind.Shape;
        if (r.Width < 0 || r.Height < 0 || r.Width > MaxLayerSide || r.Height > MaxLayerSide)
            throw Damaged($"layer '{r.Name}' has impossible bounds");
        return r;
    }

    /// <summary>
    /// The tagged blocks ("8BIM" key, length, data) to the end of <paramref name="c"/>, stepping over stray padding
    /// and stopping at anything that doesn't look like one.
    /// </summary>
    private static IEnumerable<(string Key, Cursor Block)> TaggedBlocks(Cursor c)
    {
        while (c.Left >= 12)
        {
            int skip = 0;
            while (skip <= 3 && c.Left - skip >= 12 && !IsSignature(c.Data, c.Pos + skip)) skip++;
            if (skip > 3 || c.Left - skip < 12) yield break;
            c.Skip(skip + 4);
            string key = c.Key();
            uint length = c.U32();
            if (length > c.Left) yield break;
            yield return (key, c.Take(length));
        }
    }

    private static bool IsSignature(byte[] d, int at) =>
        d[at] == '8' && d[at + 1] == 'B' && ((d[at + 2] == 'I' && d[at + 3] == 'M') || (d[at + 2] == '6' && d[at + 3] == '4'));

    /// <summary>
    /// Turns the layer records into full-canvas layers: groups flattened away (their visibility and opacity folded
    /// into their layers), masks and clipping applied to alpha, and layers without usable pixels left out.
    /// </summary>
    private static List<PsdLayer> BuildLayers(byte[] file, List<Record> records, int width, int height, List<string> warnings)
    {
        void Warn(string text)
        {
            if (!warnings.Contains(text)) warnings.Add(text);
        }

        int n = records.Count;
        var visible = new bool[n];
        var opacity = new float[n];
        var groups = new Stack<(bool Visible, float Opacity)>();
        for (int i = n - 1; i >= 0; i--)
        {
            var r = records[i];
            var parent = groups.Count > 0 ? groups.Peek() : (Visible: true, Opacity: 1f);
            switch (r.Section)
            {
                case 1 or 2:
                    Warn("Groups were flattened: their layers keep their order, and layers in a hidden group come in hidden.");
                    if (r.Opacity < 255) Warn($"Group '{r.Name}' has {Percent(r.Opacity)} opacity; Sable gave its layers that opacity instead.");
                    if (r.BlendKey is not ("pass" or "norm"))
                        Warn($"Group '{r.Name}' uses the {BlendName(r.BlendKey)} blend mode, which Sable can't apply to a group; its layers blend on their own.");
                    groups.Push((parent.Visible && !r.Hidden, parent.Opacity * r.Opacity / 255f));
                    continue;
                case 3:
                    if (groups.Count > 0) groups.Pop();
                    continue;
            }
            visible[i] = parent.Visible && !r.Hidden;
            opacity[i] = parent.Opacity * r.Opacity / 255f * r.Fill / 255f;
        }

        var keep = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var r = records[i];
            if (r.Section is 1 or 2 or 3) continue;
            if (r.Kind == Kind.Adjustment)
            {
                Warn($"'{r.Name}' is an adjustment layer; Sable left it out.");
                continue;
            }
            if (r.Kind != Kind.Pixel && !r.HasPixels)
            {
                Warn($"'{r.Name}' ({KindName(r.Kind)}) has no pixels saved with it; Sable left it out.");
                continue;
            }
            keep.Add(i);
        }

        long need = ((long)keep.Count * 4 + 1) * width * height + (long)width * height;
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available > 0 && need > available * 3 / 4)
            throw new InvalidDataException($"Its {keep.Count} layers at {width}x{height} need about {need >> 20} MB, more than this computer has free.");

        var layers = new List<PsdLayer>();
        PsdLayer? clipBase = null;
        int previous = -1;
        foreach (int i in keep)
        {
            // Clipped layers belong to the nearest unclipped layer under them, with nothing left out in between.
            if (i != previous + 1) clipBase = null;
            previous = i;
            var r = records[i];
            if (r.Kind is Kind.Text or Kind.SmartObject or Kind.Fill or Kind.Shape)
                Warn($"'{r.Name}' ({KindName(r.Kind)}) came in as the pixels Photoshop saved for it; once Sable saves, it's an ordinary pixel layer.");
            if (r.Effects) Warn($"'{r.Name}' has layer effects, which Sable doesn't show.");
            if (r.VectorMask && r.Kind == Kind.Pixel) Warn($"'{r.Name}' has a vector mask, which Sable ignores.");

            var pixels = DecodeLayer(file, r, width, height, Warn);
            var blend = r.BlendKey switch
            {
                "norm" => BlendMode.Normal,
                "mul " => BlendMode.Multiply,
                "scrn" => BlendMode.Screen,
                "over" => BlendMode.Overlay,
                "lddg" => BlendMode.Add,
                _ => BlendMode.Normal,
            };
            if (blend == BlendMode.Normal && r.BlendKey != "norm")
                Warn($"'{r.Name}' uses Photoshop's {BlendName(r.BlendKey)} blend mode, which Sable doesn't have; it's Normal in Sable.");
            bool locked = (r.Lock & 0x80000000u) != 0 || (r.Lock & 2) != 0;
            var layer = new PsdLayer(r.Name, pixels, visible[i], opacity[i], blend, locked);

            if (r.Clipping == 0)
                clipBase = layer;
            else if (clipBase != null)
            {
                Clip(pixels, clipBase.Pixels);
                layer = layer with { Visible = layer.Visible && clipBase.Visible, Opacity = layer.Opacity * clipBase.Opacity };
                Warn($"'{r.Name}' is clipped to the layer under it; Sable cut it to that layer's shape.");
            }
            else
                Warn($"'{r.Name}' is clipped to a layer Sable couldn't bring in, so it isn't clipped.");
            layers.Add(layer);
        }
        return layers;
    }

    private static string Percent(byte opacity) => $"{(int)MathF.Round(opacity / 2.55f)}%";

    private static string KindName(Kind kind) => kind switch
    {
        Kind.Text => "a text layer",
        Kind.SmartObject => "a smart object",
        Kind.Fill => "a fill layer",
        Kind.Shape => "a shape layer",
        _ => "a layer",
    };

    private static string BlendName(string key) => key switch
    {
        "pass" => "Pass Through",
        "diss" => "Dissolve",
        "dark" => "Darken",
        "idiv" => "Color Burn",
        "lbrn" => "Linear Burn",
        "dkCl" => "Darker Color",
        "lite" => "Lighten",
        "div " => "Color Dodge",
        "lgCl" => "Lighter Color",
        "sLit" => "Soft Light",
        "hLit" => "Hard Light",
        "vLit" => "Vivid Light",
        "lLit" => "Linear Light",
        "pLit" => "Pin Light",
        "hMix" => "Hard Mix",
        "diff" => "Difference",
        "smud" => "Exclusion",
        "fsub" => "Subtract",
        "fdiv" => "Divide",
        "hue " => "Hue",
        "sat " => "Saturation",
        "colr" => "Color",
        "lum " => "Luminosity",
        _ => $"'{key.Trim()}'",
    };

    private static Color[] DecodeLayer(byte[] file, Record r, int width, int height, Action<string> warn)
    {
        var pixels = new Color[width * height];
        var area = new Area(r.Top, r.Left, r.Width, r.Height, width, height);
        if (!area.Empty)
        {
            var sources = new Rows?[4];
            foreach (var channel in r.Channels)
                if (channel.Id is >= -1 and <= 2)
                    sources[channel.Id < 0 ? 3 : channel.Id] = ChannelRows(file, channel, area);
            Compose(pixels, area, sources, missingAlpha: r.Find(-1) == null ? (byte)255 : (byte)0, unmatte: false);
        }

        var (maskChannel, mask) = r.Find(-3) is { } real && r.RealMask != null ? (real, r.RealMask) : (r.Find(-2), r.Mask);
        if (maskChannel == null || mask == null) return pixels;
        if (mask.Disabled)
        {
            warn($"'{r.Name}' has a disabled layer mask; Sable ignored it.");
            return pixels;
        }
        if (!mask.Sane)
        {
            warn($"'{r.Name}' has a damaged layer mask; Sable ignored it.");
            return pixels;
        }
        var coverage = new byte[width * height];
        Array.Fill(coverage, mask.Background);
        var maskArea = new Area(mask.Top, mask.Left, mask.Right - mask.Left, mask.Bottom - mask.Top, width, height);
        if (!maskArea.Empty && ChannelRows(file, maskChannel, maskArea) is { } rows)
            Parallel.For(maskArea.FirstRow, maskArea.EndRow, () => new byte[maskArea.EndColumn], (y, _, scratch) =>
            {
                int x0 = maskArea.FirstColumn, count = maskArea.EndColumn - x0;
                rows.Row(y, scratch).Slice(x0, count).CopyTo(coverage.AsSpan((maskArea.Top + y) * width + maskArea.Left + x0, count));
                return scratch;
            }, _ => { });
        Parallel.For(0, height, y =>
        {
            var row = pixels.AsSpan(y * width, width);
            var m = coverage.AsSpan(y * width, width);
            for (int x = 0; x < width; x++) row[x].A = (byte)((row[x].A * m[x] + 127) / 255);
        });
        warn($"'{r.Name}' has a layer mask; Sable applied it to the layer's transparency.");
        return pixels;
    }

    private static void Clip(Color[] pixels, Color[] clipTo) =>
        Parallel.For(0, pixels.Length / 4096 + 1, chunk =>
        {
            int end = Math.Min(pixels.Length, (chunk + 1) * 4096);
            for (int i = chunk * 4096; i < end; i++) pixels[i].A = (byte)((pixels[i].A * clipTo[i].A + 127) / 255);
        });

    /// <summary>
    /// A channel's rectangle on the canvas. It may reach past the canvas: only rows <see cref="FirstRow"/> to
    /// <see cref="EndRow"/> and columns <see cref="FirstColumn"/> to <see cref="EndColumn"/> (counted within the
    /// rectangle) land on it.
    /// </summary>
    private readonly record struct Area(int Top, int Left, int Width, int Height, int CanvasWidth, int CanvasHeight)
    {
        public int FirstRow => Math.Clamp(-Top, 0, Height);
        public int EndRow => Math.Clamp(CanvasHeight - Top, 0, Height);
        public int FirstColumn => Math.Clamp(-Left, 0, Width);
        public int EndColumn => Math.Clamp(CanvasWidth - Left, 0, Width);
        public bool Empty => FirstRow >= EndRow || FirstColumn >= EndColumn;
    }

    /// <summary>The rows of one layer channel, or null when it holds no data.</summary>
    private static Rows? ChannelRows(byte[] file, Channel channel, Area area)
    {
        if (channel.Length < 2 || area.Width <= 0 || area.Height <= 0) return null;
        int at = channel.Offset + 2, end = channel.Offset + (int)channel.Length;
        int compression = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(channel.Offset));
        switch (compression)
        {
            case 0:
                return Rows.Raw(file, at, end, area.Width, area.Height);
            case 1:
                if ((long)area.Height * 2 > end - at) throw Damaged("a layer channel is too short");
                return Rows.Rle(file, at, at + area.Height * 2, end, area.Width, area.Height);
            case 2 or 3:
                return Rows.Unpacked(Inflate(file, at, end, (long)area.Width * area.EndRow, area.Width, compression == 3), 0, area.Width);
            default:
                throw Damaged("a layer channel uses an unknown compression");
        }
    }

    /// <summary>
    /// Writes the R, G, B and alpha rows of <paramref name="sources"/> (null for a channel that isn't there: colour 0,
    /// alpha <paramref name="missingAlpha"/>) into <paramref name="pixels"/> over <paramref name="area"/>, each pixel
    /// once, rows in parallel.
    /// </summary>
    private static void Compose(Color[] pixels, Area area, Rows?[] sources, byte missingAlpha, bool unmatte)
    {
        int x0 = area.FirstColumn, count = area.EndColumn - x0;
        Parallel.For(area.FirstRow, area.EndRow, () =>
        {
            var scratch = new byte[4][];
            for (int k = 0; k < 4; k++) scratch[k] = new byte[area.EndColumn];
            if (sources[3] == null) Array.Fill(scratch[3], missingAlpha);
            return scratch;
        }, (y, _, scratch) =>
        {
            var r = sources[0] is { } s0 ? s0.Row(y, scratch[0]) : scratch[0];
            var g = sources[1] is { } s1 ? s1.Row(y, scratch[1]) : scratch[1];
            var b = sources[2] is { } s2 ? s2.Row(y, scratch[2]) : scratch[2];
            var a = sources[3] is { } s3 ? s3.Row(y, scratch[3]) : scratch[3];
            var dst = pixels.AsSpan((area.Top + y) * area.CanvasWidth + area.Left + x0, count);
            for (int x = 0, i = x0; x < dst.Length; x++, i++)
            {
                byte alpha = a[i];
                dst[x] = !unmatte || alpha == 255 ? new Color(r[i], g[i], b[i], alpha)
                    : alpha == 0 ? default
                    : new Color(Unmatte(r[i], alpha), Unmatte(g[i], alpha), Unmatte(b[i], alpha), alpha);
            }
            return scratch;
        }, _ => { });
    }

    /// <summary>Takes the white back out of colour Photoshop stored laid over white.</summary>
    private static byte Unmatte(byte c, int a) => (byte)Math.Clamp(((c + a - 255) * 255 + a / 2) / a, 0, 255);

    /// <summary>One channel's rows, checked against the file up front and decoded one at a time from any thread.</summary>
    private sealed class Rows
    {
        private readonly byte[] data;
        private readonly int start, width;
        private readonly int[]? offsets;

        /// <summary>For RLE, the length of the compressed data, so the next channel's can be found.</summary>
        public int Length { get; }

        private Rows(byte[] data, int start, int width, int[]? offsets, int length)
        {
            this.data = data;
            this.start = start;
            this.width = width;
            this.offsets = offsets;
            Length = length;
        }

        public static Rows Raw(byte[] file, int at, int end, int width, int height)
        {
            if ((long)width * height > end - at) throw Truncated();
            return new Rows(file, at, width, null, width * height);
        }

        /// <summary>PackBits rows whose byte counts are at <paramref name="countsAt"/>, data from <paramref name="dataAt"/>.</summary>
        public static Rows Rle(byte[] file, int countsAt, int dataAt, int end, int width, int height)
        {
            var offsets = new int[height + 1];
            long pos = dataAt;
            for (int y = 0; y < height; y++)
            {
                offsets[y] = (int)pos;
                pos += BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(countsAt + y * 2));
                if (pos > end) throw Truncated();
            }
            offsets[height] = (int)pos;
            return new Rows(file, 0, width, offsets, (int)(pos - dataAt));
        }

        public static Rows Unpacked(byte[] data, int start, int width) => new(data, start, width, null, 0);

        /// <summary>Row <paramref name="y"/>, decoded into <paramref name="scratch"/> (as far as it reaches) if it has to be.</summary>
        public ReadOnlySpan<byte> Row(int y, byte[] scratch)
        {
            if (offsets == null) return data.AsSpan(start + y * width, width);
            UnpackBits(data.AsSpan(offsets[y], offsets[y + 1] - offsets[y]), scratch);
            return scratch;
        }
    }

    /// <summary>Fills <paramref name="dst"/> from PackBits data, zeros where the data runs out.</summary>
    private static void UnpackBits(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int i = 0, o = 0;
        while (o < dst.Length && i < src.Length)
        {
            int header = (sbyte)src[i++];
            if (header >= 0)
            {
                int count = Math.Min(header + 1, Math.Min(dst.Length - o, src.Length - i));
                if (count == 1) dst[o] = src[i];
                else src.Slice(i, count).CopyTo(dst[o..]);
                i += header + 1;
                o += count;
            }
            else if (header != -128 && i < src.Length)
            {
                int count = Math.Min(1 - header, dst.Length - o);
                dst.Slice(o, count).Fill(src[i++]);
                o += count;
            }
        }
        dst[o..].Clear();
    }

    /// <summary>Inflates ZIP channel data (zlib), undoing Photoshop's per-row prediction when it was used.</summary>
    private static byte[] Inflate(byte[] file, int at, int end, long size, int rowWidth, bool predicted)
    {
        if (size > Array.MaxLength) throw Damaged("a channel is too big");
        var data = new byte[size];
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(file, at, end - at, writable: false), CompressionMode.Decompress);
            zlib.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
        }
        catch (InvalidDataException)
        {
            throw Damaged("a ZIP-compressed channel doesn't decompress");
        }
        if (predicted)
            for (long row = 0; row < size; row += rowWidth)
                for (long i = row + 1; i < row + rowWidth; i++) data[i] += data[i - 1];
        return data;
    }

    /// <summary>
    /// The flattened image saved after the layers. With transparency, Photoshop stores its colour laid over white;
    /// this takes the white back out, so it comes back straight alpha (fully transparent pixels black).
    /// </summary>
    private static Color[]? ReadComposite(Cursor c, int width, int height, int channels, bool alpha, List<string> warnings)
    {
        if (c.Left < 2) throw Truncated();
        int compression = c.U16();
        int planes = alpha ? 4 : 3;
        long size = (long)width * height;
        // The least data an image this size can take, checked before allocating it: a damaged size can't cost much.
        c.Need(compression switch
        {
            0 => size * planes,
            1 => (long)channels * height * 2 + (long)planes * height * 2 * ((width + 127) / 128),
            2 or 3 => size * planes / 1100,
            _ => 0,
        });
        var sources = new Rows?[4];
        switch (compression)
        {
            case 0:
                for (int k = 0; k < planes; k++) sources[k] = Rows.Raw(c.Data, c.Pos + (int)(size * k), c.End, width, height);
                break;
            case 1:
                int at = c.Pos + channels * height * 2;
                for (int k = 0; k < planes; k++)
                {
                    var rows = Rows.Rle(c.Data, c.Pos + k * height * 2, at, c.End, width, height);
                    sources[k] = rows;
                    at += rows.Length;
                }
                break;
            case 2 or 3:
                var data = Inflate(c.Data, c.Pos, c.End, size * planes, width, compression == 3);
                for (int k = 0; k < planes; k++) sources[k] = Rows.Unpacked(data, (int)(size * k), width);
                break;
            default:
                warnings.Add("The PSD's flattened image uses an unknown compression; Sable left it out.");
                return null;
        }
        var pixels = new Color[size];
        Compose(pixels, new Area(0, 0, width, height, width, height), sources, missingAlpha: 255, unmatte: alpha);
        return pixels;
    }

    /// <summary>A bounds-checked big-endian reader over part of the file.</summary>
    private sealed class Cursor(byte[] data, int start, int end)
    {
        public readonly byte[] Data = data;
        public readonly int End = end;
        public int Pos = start;
        public int Left => End - Pos;

        public void Need(long n)
        {
            if (n < 0 || n > Left) throw Truncated();
        }

        public void Skip(long n)
        {
            Need(n);
            Pos += (int)n;
        }

        public Cursor Take(long n)
        {
            Need(n);
            var c = new Cursor(Data, Pos, Pos + (int)n);
            Pos += (int)n;
            return c;
        }

        public byte U8()
        {
            Need(1);
            return Data[Pos++];
        }

        public int U16()
        {
            Need(2);
            int v = BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(Pos));
            Pos += 2;
            return v;
        }

        public short I16() => (short)U16();

        public uint U32()
        {
            Need(4);
            uint v = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(Pos));
            Pos += 4;
            return v;
        }

        public int I32() => (int)U32();

        /// <summary>A 32-bit length, checked against what's left.</summary>
        public long Length32()
        {
            long n = U32();
            Need(n);
            return n;
        }

        public string Key()
        {
            Need(4);
            string key = Encoding.ASCII.GetString(Data, Pos, 4);
            Pos += 4;
            return key;
        }
    }
}
