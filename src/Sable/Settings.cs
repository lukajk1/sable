using System.Text.Json;

namespace Sable;

/// <summary>
/// Editor settings kept between sessions in %APPDATA%\Sable\settings.json: paint settings, display toggles and
/// the window. Not the scene (camera, selection, hidden objects), and not the tool, which always starts as Select.
/// </summary>
internal sealed class Settings
{
    // Paint.
    public float[] Hsv { get; set; } = { 0.07f, 0.75f, 0.9f };
    public float BrushSize { get; set; } = 8f;
    public float Hardness { get; set; } = 0.5f;
    public float Opacity { get; set; } = 1f;
    public float Flow { get; set; } = 1f;
    public bool PressureToFlow { get; set; } = true;
    public bool PressureToSize { get; set; }
    public float PressureCurve { get; set; } = 1.6f;
    public float FillTolerance { get; set; }
    public bool FillContiguous { get; set; } = true;
    public bool FillAllLayers { get; set; }
    /// <summary>0 similar colour, 1 UV island, 2 face.</summary>
    public int FillMode { get; set; }
    /// <summary>Texels saved images are padded past their UV islands.</summary>
    public int EdgePadding { get; set; } = 4;
    public bool MirrorX { get; set; }
    /// <summary>The stroke stabilizer's string, 0..1 of its longest.</summary>
    public float Smoothing { get; set; }

    // Display.
    public int TextureView { get; set; }
    public float Lighting { get; set; } = 1f;
    public bool Grid { get; set; } = true;
    public bool Wireframe { get; set; }
    public bool UvTexelGrid { get; set; } = true;
    public bool UvShowSiblings { get; set; } = true;
    public float Split { get; set; } = 0.5f;
    public int NewTextureSize { get; set; } = 256;
    public bool LayersOpen { get; set; } = true;
    /// <summary>The file browser's recent folders, newest first.</summary>
    public List<string>? RecentFolders { get; set; }

    // Window (0 = leave to the default).
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // Palette (UI/Palette.cs). Colours are "#RRGGBB".
    /// <summary>The palette shown in the panel: a built-in's name or "Custom".</summary>
    public string? PaletteName { get; set; }
    /// <summary>The Custom palette's colours.</summary>
    public List<string>? PaletteCustom { get; set; }
    /// <summary>Recently painted colours, newest first.</summary>
    public List<string>? ColorHistory { get; set; }

    /// <summary>%APPDATA%\Sable\settings.json, or SABLE_SETTINGS when set (for tests).</summary>
    private static string FilePath => Environment.GetEnvironmentVariable("SABLE_SETTINGS") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sable", "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The saved settings, or defaults when there are none or the file can't be read.</summary>
    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[settings] couldn't read {FilePath}: {e.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[settings] couldn't write {FilePath}: {e.Message}");
        }
    }
}
