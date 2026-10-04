using System.Runtime.InteropServices;
using Raylib_cs;

namespace Sable.Input;

/// <summary>
/// Reads an image off the Windows clipboard, best source first: PNG data (browsers, Photoshop and most editors put
/// it there, alpha included), then a bitmap (screenshots: CF_DIBV5, then CF_DIB), then an image file copied in
/// Explorer.
/// </summary>
public static class ClipboardImage
{
    public sealed record Result(Color[] Pixels, int Width, int Height, string Source);

    private const uint CF_DIB = 8, CF_HDROP = 15, CF_DIBV5 = 17;
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif", ".psd", ".hdr", ".qoi" };

    /// <summary>The clipboard's image, or null with <paramref name="why"/> saying what was there instead.</summary>
    public static Result? Read(out string why)
    {
        why = "";
        if (!Open())
        {
            why = "another program is holding the clipboard; try again";
            return null;
        }
        try
        {
            uint png = RegisterClipboardFormatW("PNG");
            if (png != 0 && Bytes(png) is { } pngData && Decode(".png", pngData, "clipboard PNG") is { } fromPng) return fromPng;
            foreach (uint format in new[] { CF_DIBV5, CF_DIB })
                if (Bytes(format) is { } dib && BitmapFile(dib) is { } bmp && Decode(".bmp", bmp, "clipboard bitmap") is { } fromDib)
                    return fromDib;
            if (CopiedImageFile() is { } file)
                return Decode(Path.GetExtension(file).ToLowerInvariant(), File.ReadAllBytes(file), Path.GetFileName(file));
            why = "there's no image on the clipboard";
            return null;
        }
        catch (Exception e)
        {
            why = e.Message;
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    // Another program may have the clipboard open for a moment.
    private static bool Open()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private static byte[]? Bytes(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return null;
        IntPtr handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        IntPtr data = GlobalLock(handle);
        if (data == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[(int)GlobalSize(handle)];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>A clipboard DIB as a .bmp file: the 14-byte file header in front, pointing at where the pixels start.</summary>
    private static byte[]? BitmapFile(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToUInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int colorsUsed = BitConverter.ToInt32(dib, 32);
        // A plain 40-byte header with BI_BITFIELDS (3) or BI_ALPHABITFIELDS (6) has its masks after it.
        int masks = headerSize == 40 && compression == 3 ? 12 : headerSize == 40 && compression == 6 ? 16 : 0;
        int palette = (colorsUsed > 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0) * 4;
        int offset = 14 + headerSize + masks + palette;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(offset).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        return file;
    }

    /// <summary>The first image among files copied in Explorer.</summary>
    private static string? CopiedImageFile()
    {
        if (!IsClipboardFormatAvailable(CF_HDROP)) return null;
        IntPtr drop = GetClipboardData(CF_HDROP);
        if (drop == IntPtr.Zero) return null;
        uint count = DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var path = new char[1024];
            uint length = DragQueryFileW(drop, i, path, (uint)path.Length);
            string file = new(path, 0, (int)length);
            if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && File.Exists(file)) return file;
        }
        return null;
    }

    private static unsafe Result? Decode(string fileType, byte[] data, string source)
    {
        Image image = Raylib.LoadImageFromMemory(fileType, data);
        if (image.Width == 0 || image.Height == 0) return null;
        Raylib.ImageFormat(ref image, PixelFormat.UncompressedR8G8B8A8);
        var pixels = new Span<Color>(image.Data, image.Width * image.Height).ToArray();
        var result = new Result(pixels, image.Width, image.Height, source);
        Raylib.UnloadImage(image);
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr drop, uint index, char[]? file, uint length);
}
