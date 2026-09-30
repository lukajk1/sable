using System.Diagnostics;
using System.Text;

namespace Sable.Diagnostics;

/// <summary>
/// Times each part of a frame and writes a line to %TEMP%\Sable\hitches.log whenever a frame runs long, with the
/// breakdown, how many dabs and ray casts it did, and whether the garbage collector ran. The last hitch is also
/// shown in the status bar for a few seconds.
/// </summary>
public sealed class FrameProfiler
{
    /// <summary>Frames longer than this are logged. A vsynced frame is ~16.7 ms at 60 Hz.</summary>
    public const double HitchMs = 40;

    /// <summary>Counted by the brush and the ray caster during the current frame.</summary>
    public static int Dabs, Raycasts;

    public string LogPath { get; } = Path.Combine(Path.GetTempPath(), "Sable", "hitches.log");
    public string? LastHitch { get; private set; }

    private readonly Stopwatch frame = Stopwatch.StartNew();
    private readonly Stopwatch section = Stopwatch.StartNew();
    private readonly List<(string Name, double Ms)> sections = new();
    private readonly Stopwatch sinceHitch = new();
    private int gen0, gen1, gen2;
    // The first frames pay for JIT and first uploads.
    private int warmup = 5;
    private bool skip;

    public FrameProfiler()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"--- Sable session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
        }
        catch (IOException) { }
        SnapshotGc();
    }

    /// <summary>Ends the named section (time since the previous mark).</summary>
    public void Mark(string name)
    {
        sections.Add((name, section.Elapsed.TotalMilliseconds));
        section.Restart();
    }

    /// <summary>Don't report this frame (loading, a modal dialog, anything expected to be slow).</summary>
    public void Skip() => skip = true;

    public void EndFrame(string context)
    {
        double total = frame.Elapsed.TotalMilliseconds;
        frame.Restart();
        section.Restart();

        int d0 = GC.CollectionCount(0) - gen0, d1 = GC.CollectionCount(1) - gen1, d2 = GC.CollectionCount(2) - gen2;
        if (warmup > 0) { warmup--; skip = true; }
        if (total > HitchMs && !skip)
        {
            var line = new StringBuilder();
            line.Append($"{DateTime.Now:HH:mm:ss.fff} {total:0} ms |");
            foreach (var (name, ms) in sections) line.Append($" {name} {ms:0.0}");
            line.Append($" | dabs {Dabs}, raycasts {Raycasts}, gc {d0}/{d1}/{d2} | {context}");
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch (IOException) { }

            var worst = sections.Count > 0 ? sections.MaxBy(s => s.Ms) : ("?", 0);
            LastHitch = $"hitch {total:0} ms ({worst.Name} {worst.Ms:0}{(d2 > 0 ? ", gc" : "")})";
            sinceHitch.Restart();
        }
        if (sinceHitch.IsRunning && sinceHitch.Elapsed.TotalSeconds > 4)
        {
            LastHitch = null;
            sinceHitch.Reset();
        }

        sections.Clear();
        Dabs = Raycasts = 0;
        skip = false;
        SnapshotGc();
    }

    private void SnapshotGc()
    {
        gen0 = GC.CollectionCount(0);
        gen1 = GC.CollectionCount(1);
        gen2 = GC.CollectionCount(2);
    }
}
