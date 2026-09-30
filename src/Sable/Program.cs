namespace Sable;

internal static class Program
{
    // STA for the WinForms open dialog.
    [STAThread]
    private static void Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        using var app = new App(options);
        app.Run();
    }
}

/// <summary>
/// Command line: <c>Sable [model] [--screenshot out.png] [--select name] [--selftest]</c>.
/// <c>--screenshot</c> loads the model, renders a few frames, saves the window and exits (for checking the app
/// without looking at it). <c>--select</c> makes the first object whose name contains it active.
/// <c>--selftest</c> also paints a few test strokes (in memory only, never saved) before the screenshot.
/// </summary>
internal sealed class AppOptions
{
    public string? ModelPath { get; private set; }
    public string? ScreenshotPath { get; private set; }
    public string? SelectPart { get; private set; }
    public bool SelfTest { get; private set; }
    /// <summary>Starting texture view for the 3D view: 0 pixel, 1 smooth, 2 smooth + mipmaps.</summary>
    public int TextureView { get; private set; }
    /// <summary>Quit after this many frames (0 = run until closed), saving settings as on a normal close.</summary>
    public int QuitAfterFrames { get; private set; }

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--screenshot" when i + 1 < args.Length: options.ScreenshotPath = Path.GetFullPath(args[++i]); break;
                case "--select" when i + 1 < args.Length: options.SelectPart = args[++i]; break;
                case "--selftest": options.SelfTest = true; break;
                case "--frames" when i + 1 < args.Length: options.QuitAfterFrames = int.TryParse(args[++i], out int f) ? f : 0; break;
                case "--texview" when i + 1 < args.Length: options.TextureView = int.TryParse(args[++i], out int v) ? Math.Clamp(v, 0, 2) : 0; break;
                default: if (!args[i].StartsWith("--")) options.ModelPath = args[i]; break;
            }
        }
        return options;
    }
}
