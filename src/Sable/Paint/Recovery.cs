using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Raylib_cs;
using Sable.Model;
using Sable.Rendering;

namespace Sable.Paint;

/// <summary>
/// Crash recovery: autosaved copies of a model's unsaved textures. Each set is a folder,
/// <c>%APPDATA%\Sable\recovery\&lt;hash of the model's path&gt;\&lt;time&gt;-&lt;process&gt;\</c>, holding one layer zip
/// per texture (the <see cref="LayerFile"/> format) and <c>session.json</c>. The session file is written last and
/// renamed into place, so a set only counts once everything in it is on disk: a crash mid-write leaves a folder that
/// <see cref="Find"/> ignores and <see cref="CleanUp"/> removes. <see cref="Autosaver"/> writes the sets; this class
/// finds, applies and discards them. Sets from a session that crashed are only ever removed by a discard, or after
/// two weeks.
/// </summary>
public static class Recovery
{
    /// <summary>How long an unclaimed set is kept.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>%APPDATA%\Sable\recovery, or SABLE_RECOVERY when set (for tests).</summary>
    public static string Root => Environment.GetEnvironmentVariable("SABLE_RECOVERY") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sable", "recovery");

    /// <summary>
    /// What recovery knows a model by: the file it came from. For a Blender link that's the .blend (the link folder
    /// changes with every Blender session), so opening the .blend directly finds the same sets.
    /// </summary>
    public static string KeyOf(LoadedModel model) => Path.GetFullPath(model.SourcePath);

    /// <summary>The folder holding a model's sets: 8 hex digits of a hash of its key, ignoring case.</summary>
    public static string FolderFor(string modelKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(modelKey).ToUpperInvariant()));
        return Path.Combine(Root, Convert.ToHexString(hash, 0, 4).ToLowerInvariant());
    }

    /// <summary>
    /// The newest set left behind for a model: by a session that crashed or closed with unsaved work, or by this
    /// one before the model was reloaded. Sets another running Sable is still writing are skipped. Null if none.
    /// </summary>
    public static RecoverySet? Find(string modelKey) =>
        SetsIn(FolderFor(modelKey))
            .Where(s => SameKey(s.ModelKey, modelKey) && !s.InUse)
            .OrderByDescending(s => s.SavedAt)
            .FirstOrDefault();

    /// <summary>Every complete set, for any model, newest first (with <see cref="RecoverySet.InUse"/> marked).</summary>
    public static IEnumerable<RecoverySet> All()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root).SelectMany(SetsIn).OrderByDescending(s => s.SavedAt).ToList();
    }

    /// <summary>
    /// Restores a set onto a model's textures, matched by file path, else by name. Each gets the saved size, layers
    /// and active layer through <see cref="PaintTexture.SetContents"/> and is left unsaved. A texture with no match
    /// (one made in Sable) is recreated and added to the model, and given back to the materials that showed it.
    /// Materials that showed a recovered texture show it again. Returns how many textures were restored; the
    /// <paramref name="notes"/> say what else happened. The set stays on disk until the autosaver has saved this
    /// session's state (or the model is saved), so a second crash before then loses nothing. Undo history from
    /// before no longer fits the textures: clear it. Main thread only.
    /// </summary>
    public static int Apply(RecoverySet set, GpuModel model, out List<string> notes)
    {
        notes = new List<string>();
        var claimed = new HashSet<PaintTexture>();
        int restored = 0;
        foreach (var entry in set.Textures)
        {
            var target = entry.FilePath != null
                ? model.Textures.FirstOrDefault(t => !claimed.Contains(t) && t.FilePath != null && SameKey(t.FilePath, entry.FilePath))
                : null;
            bool byName = false;
            if (target == null)
            {
                target = model.Textures.FirstOrDefault(t => !claimed.Contains(t) && string.Equals(t.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                byName = target != null && entry.FilePath != null;
            }
            try
            {
                var read = LayerFile.ReadZip(entry.ZipPath) ?? throw new InvalidDataException("it holds no layers");
                var state = read.State with { Active = Math.Clamp(entry.ActiveLayer, 0, read.State.Layers.Length - 1) };
                bool created = target == null;
                if (target == null)
                {
                    target = PaintTexture.Create(entry.Name, read.Width, read.Height, Color.Blank);
                    target.FilePath = entry.FilePath;
                    model.Textures.Add(target);
                }
                target.SetContents(read.Width, read.Height, state);
                target.Dirty = true;
                claimed.Add(target);
                restored++;

                int index = model.Textures.IndexOf(target);
                var given = new List<string>();
                foreach (string material in entry.Materials)
                {
                    int m = model.Source.Materials.FindIndex(x => x.Name == material);
                    if (m < 0 || model.Source.Materials[m].TextureIndex == index) continue;
                    model.AssignTexture(m, index);
                    given.Add(material);
                }
                if (created)
                    notes.Add(given.Count > 0
                        ? $"{entry.Name} (made in Sable) was recreated for {string.Join(", ", given)}."
                        : $"{entry.Name} (made in Sable) was recreated; pick it for a material under Active object.");
                else if (byName)
                    notes.Add($"{entry.Name} was matched by name: it was at {entry.FilePath}, and saves to {target.FilePath ?? "beside the model"} now.");
            }
            catch (Exception e)
            {
                notes.Add($"{entry.Name}: couldn't read its autosave ({e.Message}).");
            }
        }
        lock (DiskLock) adopted.Add(set.Folder);
        return restored;
    }

    /// <summary>Removes every set for a model, except ones another running Sable is still writing.</summary>
    public static void Discard(string modelKey)
    {
        List<string> folders;
        lock (DiskLock)
        {
            Bump(modelKey);
            folders = SetFolders(FolderFor(modelKey))
                .Where(f => ReadSession(f) is not { } s || (SameKey(s.ModelKey, modelKey) && !IsOtherLive(s)))
                .ToList();
            adopted.RemoveWhere(folders.Contains);
        }
        foreach (string folder in folders) DeleteSet(folder);
    }

    /// <summary>Removes one set.</summary>
    public static void Discard(RecoverySet set)
    {
        lock (DiskLock)
        {
            Bump(set.ModelKey);
            adopted.Remove(set.Folder);
        }
        DeleteSet(set.Folder);
    }

    /// <summary>
    /// Removes sets older than <see cref="MaxAge"/> and folders a crash left half written. Never throws; fine to
    /// run off the main thread at startup.
    /// </summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var now = DateTime.UtcNow;
            foreach (string modelFolder in Directory.EnumerateDirectories(Root))
            {
                foreach (string folder in SetFolders(modelFolder))
                {
                    var session = ReadSession(folder);
                    bool stale = session != null
                        ? now - session.SavedAt > MaxAge && !IsOtherLive(session)
                        : now - Directory.GetLastWriteTimeUtc(folder) > TimeSpan.FromHours(1);
                    if (stale) DeleteSet(folder);
                }
                DeleteIfEmpty(modelFolder);
            }
        }
        catch (Exception)
        {
            // Cleaning up is a courtesy; a folder that can't be read or removed now goes next time.
        }
    }

    // ---------- shared with Autosaver ----------

    /// <summary>Taken to commit a set and to discard or clear one, so the two never cross.</summary>
    internal static readonly object DiskLock = new();
    // Goes up whenever a model's sets are discarded or cleared: a write that started before then is dropped.
    private static readonly ConcurrentDictionary<string, int> generations = new(StringComparer.OrdinalIgnoreCase);
    // Sets applied to a model: removed once the autosaver has written that model's state, or it is cleared.
    private static readonly HashSet<string> adopted = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly int ProcessId = Environment.ProcessId;
    internal static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>
    /// For a key as <see cref="KeyOf"/> gives it. Read without the lock, so a frame never waits on a commit; it only
    /// changes under the lock.
    /// </summary>
    internal static int Generation(string modelKey) => generations.GetValueOrDefault(modelKey);

    /// <summary>Call with <see cref="DiskLock"/> held.</summary>
    internal static void Bump(string modelKey)
    {
        string key = Path.GetFullPath(modelKey);
        generations[key] = generations.GetValueOrDefault(key) + 1;
    }

    /// <summary>
    /// The sets to remove once a model's newer state is on disk (or nothing of it is unsaved): this process's own
    /// earlier sets for it, and sets applied to it. Taken off the adopted list. Call with <see cref="DiskLock"/> held.
    /// </summary>
    internal static List<string> Superseded(string modelKey, string? keep)
    {
        string modelFolder = FolderFor(modelKey);
        var folders = SetFolders(modelFolder).Where(f => !string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)
            && ReadSession(f) is { } s && SameKey(s.ModelKey, modelKey) && s.ProcessId == ProcessId
            && Math.Abs((s.ProcessStart - ProcessStart).TotalSeconds) < 1).ToList();
        foreach (string folder in adopted.Where(f => SameKey(Path.GetDirectoryName(f)!, modelFolder)).ToList())
        {
            adopted.Remove(folder);
            if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
        }
        return folders;
    }

    internal static void DeleteSet(string folder)
    {
        try
        {
            // The session file first: without it the rest no longer counts as a set, whatever fails after.
            string session = Path.Combine(folder, SessionFileName);
            if (File.Exists(session)) Retry(() => File.Delete(session));
            if (Directory.Exists(folder)) Retry(() => Directory.Delete(folder, recursive: true));
            DeleteIfEmpty(Path.GetDirectoryName(folder)!);
        }
        catch (Exception)
        {
            // Left for CleanUp.
        }
    }

    /// <summary>Retries a file operation a few times: virus scanners and the indexer briefly open new files.</summary>
    internal static void Retry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 5) { Thread.Sleep(20 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(20 * (attempt + 1)); }
        }
    }

    internal const string SessionFileName = "session.json";
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal sealed class SessionFile
    {
        public int Version { get; set; } = 1;
        public string ModelKey { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string? LinkPath { get; set; }
        public DateTime SavedAt { get; set; }
        public int ProcessId { get; set; }
        public DateTime ProcessStart { get; set; }
        public List<TextureEntry> Textures { get; set; } = new();
    }

    internal sealed class TextureEntry
    {
        public string Name { get; set; } = "";
        public string? FilePath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ActiveLayer { get; set; }
        public int Layers { get; set; }
        public string Zip { get; set; } = "";
        public List<string> Materials { get; set; } = new();
    }

    // ---------- reading ----------

    private static IEnumerable<string> SetFolders(string modelFolder) =>
        Directory.Exists(modelFolder) ? Directory.EnumerateDirectories(modelFolder) : [];

    private static IEnumerable<RecoverySet> SetsIn(string modelFolder)
    {
        foreach (string folder in SetFolders(modelFolder))
        {
            if (ReadSession(folder) is not { } s) continue;
            var textures = s.Textures.Select(t => new RecoveredTexture(t.Name, t.FilePath, t.Width, t.Height, t.ActiveLayer,
                t.Layers, Path.Combine(folder, t.Zip), t.Materials)).ToList();
            if (textures.Any(t => !File.Exists(t.ZipPath))) continue;
            bool mine = s.ProcessId == ProcessId && Math.Abs((s.ProcessStart - ProcessStart).TotalSeconds) < 1;
            yield return new RecoverySet(folder, s.ModelKey, s.SourcePath, s.LinkPath, s.SavedAt, IsOtherLive(s), mine, textures);
        }
    }

    private static SessionFile? ReadSession(string folder)
    {
        try
        {
            string path = Path.Combine(folder, SessionFileName);
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var session = JsonSerializer.Deserialize<SessionFile>(stream, Json);
            return session is { ModelKey.Length: > 0 } ? session : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Written by another Sable that is still running (so still autosaving it).</summary>
    private static bool IsOtherLive(SessionFile session)
    {
        if (session.ProcessId == ProcessId && Math.Abs((session.ProcessStart - ProcessStart).TotalSeconds) < 1) return false;
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - session.ProcessStart).TotalSeconds) < 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SameKey(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception)
        {
            // Something was just written into it.
        }
    }
}

/// <summary>
/// One autosaved set: the model it belongs to, when it was saved (UTC), and its textures.
/// <see cref="InUse"/>: another running Sable is still writing it. <see cref="FromThisSession"/>: this process wrote
/// it, before the model was reloaded.
/// </summary>
public sealed record RecoverySet(string Folder, string ModelKey, string SourcePath, string? LinkPath, DateTime SavedAt,
    bool InUse, bool FromThisSession, IReadOnlyList<RecoveredTexture> Textures);

/// <summary>
/// One texture of a <see cref="RecoverySet"/>: where it saves (null for embedded or new textures), its size and
/// layers, its layer zip, and the materials that showed it.
/// </summary>
public sealed record RecoveredTexture(string Name, string? FilePath, int Width, int Height, int ActiveLayer, int LayerCount,
    string ZipPath, IReadOnlyList<string> Materials);

/// <summary>
/// Autosaves a model's unsaved textures for <see cref="Recovery"/>. Call <see cref="Tick"/> every frame: once
/// something has been unsaved for <see cref="Interval"/> seconds and nothing changed for <see cref="IdleDelay"/>
/// seconds (or it has been twice the interval), it copies the unsaved textures' layers, a slice of
/// <see cref="CopyBudget"/> bytes per frame so no frame stalls (starting over if a texture changes meanwhile), then
/// encodes and writes them on a background thread, one write at a time. A texture unchanged since the last set is
/// copied over from it rather than encoded again. Nothing it does throws into the caller: failures go to
/// <see cref="LastError"/> and are retried a full interval later. Main thread only.
/// </summary>
public sealed class Autosaver
{
    /// <summary>Seconds a change waits before it is autosaved.</summary>
    public double Interval { get; set; } = 60;
    /// <summary>Seconds without changes before a snapshot is taken, so it doesn't land mid-stroke.</summary>
    public double IdleDelay { get; set; } = 3;
    /// <summary>When the last set was written (UTC), or null.</summary>
    public DateTime? LastSaved { get; private set; }
    /// <summary>Why the last write failed; null once one succeeds.</summary>
    public string? LastError { get; private set; }
    /// <summary>Bytes of layers copied per frame while taking a snapshot: about 2 ms.</summary>
    public const int CopyBudget = 16 << 20;
    /// <summary>A snapshot is being taken, or a set written in the background.</summary>
    public bool Writing => capture != null || write != null;
    /// <summary>Main-thread time of the last snapshot (copying the layers, over all its frames), in milliseconds.</summary>
    public double LastSnapshotMs { get; private set; }
    /// <summary>The most main-thread time the last snapshot took in one frame, in milliseconds.</summary>
    public double LastSnapshotFrameMs { get; private set; }
    /// <summary>Background time of the last write (encoding and writing the set), in milliseconds.</summary>
    public double LastWriteMs { get; private set; }

    /// <summary>For a status line: "Autosaving...", "Autosaved 14:02", "Autosave failed: ...", or empty.</summary>
    public string Status => Writing ? "Autosaving..."
        : LastError != null ? $"Autosave failed: {LastError}"
        : LastSaved is { } saved ? $"Autosaved {saved.ToLocalTime():HH:mm}"
        : "";

    private readonly record struct Mark(PaintTexture Texture, int Version, int Active);

    private sealed record Snapshot(string Key, int Generation, string SourcePath, string? LinkPath, DateTime SavedAt,
        List<Mark> Marks, List<SnapshotTexture> Textures);

    /// <summary>Either the layers copied, or the zip of an unchanged texture to copy over.</summary>
    private sealed record SnapshotTexture(Mark Mark, string Name, string? FilePath, int Width, int Height, int LayerCount,
        List<string> Materials, LayerData[]? Layers, string? ReuseZip);

    /// <summary>A snapshot being copied, a slice per frame.</summary>
    private sealed class Capture(Snapshot snapshot, List<(Color[] From, Color[] To)> copies)
    {
        public Snapshot Snapshot { get; } = snapshot;
        public List<(Color[] From, Color[] To)> Copies { get; } = copies;
        public int Next { get; set; }
        public int Offset { get; set; }
        public double Ms { get; set; }
        public double FrameMs { get; set; }
    }

    private sealed record WriteResult(string Key, int Generation, bool Committed, string? Error, double Ms, DateTime SavedAt,
        Dictionary<PaintTexture, (int Version, string Zip)> Zips);

    private string? key, sourceKey;
    private LoadedModel? source;
    private int generation;
    // What the newest set holds (or the one being written), and what Tick saw last frame.
    private List<Mark> saved = new(), seen = new();
    private Dictionary<PaintTexture, (int Version, string Zip)> zips = new();
    private double lastChange, pendingSince = -1, lastNow;
    private Capture? capture;
    private Task<WriteResult>? write;
    private static int folderCounter;
    // Tests set this through reflection: called after each zip with the folder and the count so far, it can look at
    // a half-written set and throw, as a crash mid-write would.
    private static Action<string, int>? afterZip = null;

    /// <param name="now">Seconds, from any steady clock (Raylib.GetTime()).</param>
    /// <param name="busy">A stroke or drag is in progress: never snapshot then.</param>
    public void Tick(GpuModel? model, double now, bool busy = false)
    {
        lastNow = now;
        Collect();
        if (model == null)
        {
            capture = null;
            return;
        }
        Follow(model);

        if (!Matches(seen, model))
        {
            // Every frame while painting: refilled in place rather than reallocated.
            seen.Clear();
            foreach (var t in model.Textures)
                if (t.Dirty) seen.Add(new Mark(t, t.Version, t.ActiveLayerIndex));
            lastChange = now;
        }
        if (busy) lastChange = now;
        if (capture != null)
        {
            // Changed before the copy was done: drop it, and take a new one once things are quiet again.
            if (busy || !Matches(capture.Snapshot.Marks, model)) capture = null;
            else Continue(CopyBudget);
            return;
        }
        if (Matches(saved, model))
        {
            pendingSince = -1;
            return;
        }
        if (pendingSince < 0) pendingSince = now;
        if (write != null || busy || now - pendingSince < Interval) return;
        if (now - lastChange < IdleDelay && now - pendingSince < Interval * 2) return;
        Begin(model);
        Continue(CopyBudget);
    }

    /// <summary>
    /// Writes whatever is unsaved now and waits until it (and any write already running) is on disk: for closing
    /// the window or opening another model with unsaved changes. Blocks for the encoding time. True when it worked.
    /// </summary>
    public bool Flush(GpuModel? model)
    {
        if (capture != null && model != null && Matches(capture.Snapshot.Marks, model)) Continue(int.MaxValue);
        capture = null;
        write?.Wait();
        Collect();
        if (model == null) return LastError == null;
        Follow(model);
        if (!Matches(saved, model))
        {
            Begin(model);
            Continue(int.MaxValue);
        }
        write?.Wait();
        Collect();
        return LastError == null;
    }

    /// <summary>
    /// Removes this session's sets for a model (and sets applied to it): call after saving leaves nothing unsaved,
    /// and on a clean close. Sets from crashed sessions stay until discarded. A write in progress is dropped.
    /// </summary>
    public void Clear(string modelKey)
    {
        List<string> folders;
        lock (Recovery.DiskLock)
        {
            Recovery.Bump(modelKey);
            folders = Recovery.Superseded(modelKey, keep: null);
        }
        if (folders.Count > 0) Task.Run(() => folders.ForEach(Recovery.DeleteSet));
        if (key != null && string.Equals(Path.GetFullPath(modelKey), key, StringComparison.OrdinalIgnoreCase))
        {
            generation = Recovery.Generation(key);
            zips = new();
            saved = new();
            pendingSince = -1;
            capture = null;
        }
    }

    /// <summary>Takes in a finished write.</summary>
    private void Collect()
    {
        if (write is not { IsCompleted: true } done) return;
        write = null;
        var result = done.IsCompletedSuccessfully
            ? done.Result
            : new WriteResult(key ?? "", generation, false, done.Exception?.InnerException?.Message ?? "unknown error", 0, default, new());
        LastWriteMs = result.Ms;
        bool current = result.Key == key && result.Generation == generation;
        if (result.Committed)
        {
            if (result.Zips.Count > 0) LastSaved = result.SavedAt;
            LastError = null;
            if (current) zips = result.Zips;
        }
        else if (result.Error != null)
        {
            LastError = result.Error;
            if (current)
            {
                // The older set (and its zips) is still there; try again a full interval from now.
                saved = new();
                pendingSince = lastNow;
            }
        }
    }

    /// <summary>Keeps up with which model is open, and with its sets being discarded or cleared elsewhere.</summary>
    private void Follow(GpuModel model)
    {
        if (model.Source != source)
        {
            source = model.Source;
            sourceKey = Recovery.KeyOf(source);
        }
        string modelKey = sourceKey!;
        int now = Recovery.Generation(modelKey);
        if (modelKey == key && now == generation) return;
        if (modelKey != key) seen = new();
        capture = null;
        key = modelKey;
        generation = now;
        zips = new();
        saved = new();
        pendingSince = -1;
    }

    private static bool Matches(List<Mark> marks, GpuModel model)
    {
        int n = 0;
        foreach (var texture in model.Textures)
        {
            if (!texture.Dirty) continue;
            if (n >= marks.Count) return false;
            var mark = marks[n++];
            if (mark.Texture != texture || mark.Version != texture.Version || mark.Active != texture.ActiveLayerIndex) return false;
        }
        return n == marks.Count;
    }

    private static List<Mark> Marks(GpuModel model) =>
        model.Textures.Where(t => t.Dirty).Select(t => new Mark(t, t.Version, t.ActiveLayerIndex)).ToList();

    /// <summary>Sets up a snapshot of the unsaved textures: their settings now, and room for their layers' pixels.</summary>
    private void Begin(GpuModel model)
    {
        var clock = Stopwatch.StartNew();
        var marks = Marks(model);
        var textures = new List<SnapshotTexture>();
        var copies = new List<(Color[] From, Color[] To)>();
        foreach (var mark in marks)
        {
            var t = mark.Texture;
            int index = model.Textures.IndexOf(t);
            var materials = model.Source.Materials.Where(m => m.TextureIndex == index).Select(m => m.Name).ToList();
            LayerData[]? layers = null;
            bool unchanged = zips.TryGetValue(t, out var zip) && zip.Version == t.Version;
            if (!unchanged)
            {
                // Not zeroed first: the copy overwrites all of it.
                layers = t.Layers.Select(l => new LayerData(l.Name, l.Visible, l.Opacity, l.Blend,
                    GC.AllocateUninitializedArray<Color>(l.Pixels.Length), l.Kind)).ToArray();
                for (int i = 0; i < layers.Length; i++) copies.Add((t.Layers[i].Pixels, layers[i].Pixels));
            }
            textures.Add(new SnapshotTexture(mark, t.Name, t.FilePath, t.Width, t.Height, t.Layers.Count, materials,
                layers, unchanged ? zip.Zip : null));
        }
        var snapshot = new Snapshot(key!, generation, model.Source.SourcePath, model.Source.LinkPath, DateTime.UtcNow, marks, textures);
        capture = new Capture(snapshot, copies) { Ms = clock.Elapsed.TotalMilliseconds };
    }

    /// <summary>Copies up to <paramref name="budget"/> more bytes of the snapshot; once it has everything, starts the write.</summary>
    private void Continue(int budget)
    {
        var c = capture!;
        var clock = Stopwatch.StartNew();
        long left = budget / 4;
        while (c.Next < c.Copies.Count && left > 0)
        {
            var (from, to) = c.Copies[c.Next];
            int count = (int)Math.Min(left, from.Length - c.Offset);
            from.AsSpan(c.Offset, count).CopyTo(to.AsSpan(c.Offset));
            left -= count;
            c.Offset += count;
            if (c.Offset == from.Length)
            {
                c.Next++;
                c.Offset = 0;
            }
        }
        double ms = clock.Elapsed.TotalMilliseconds;
        c.Ms += ms;
        c.FrameMs = Math.Max(c.FrameMs, ms);
        if (c.Next < c.Copies.Count) return;

        capture = null;
        LastSnapshotMs = c.Ms;
        LastSnapshotFrameMs = c.FrameMs;
        saved = c.Snapshot.Marks;
        pendingSince = -1;
        var snapshot = c.Snapshot;
        write = Task.Factory.StartNew(() => Write(snapshot), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Background: writes the set into a new folder, commits it with its session file, then removes older ones.</summary>
    private static WriteResult Write(Snapshot snapshot)
    {
        var clock = Stopwatch.StartNew();
        var written = new Dictionary<PaintTexture, (int Version, string Zip)>();
        string modelFolder = Recovery.FolderFor(snapshot.Key);
        string folder = Path.Combine(modelFolder,
            $"{snapshot.SavedAt:yyyyMMdd-HHmmss-fff}-{Recovery.ProcessId}-{Interlocked.Increment(ref folderCounter)}");
        try
        {
            List<string> superseded;
            if (snapshot.Textures.Count == 0)
            {
                // Nothing unsaved any more: only the older sets go.
                lock (Recovery.DiskLock)
                {
                    if (Recovery.Generation(snapshot.Key) != snapshot.Generation) return Dropped();
                    superseded = Recovery.Superseded(snapshot.Key, keep: null);
                }
                superseded.ForEach(Recovery.DeleteSet);
                return new WriteResult(snapshot.Key, snapshot.Generation, true, null, clock.Elapsed.TotalMilliseconds, snapshot.SavedAt, written);
            }

            Directory.CreateDirectory(folder);
            var session = new Recovery.SessionFile
            {
                ModelKey = snapshot.Key,
                SourcePath = snapshot.SourcePath,
                LinkPath = snapshot.LinkPath,
                SavedAt = snapshot.SavedAt,
                ProcessId = Recovery.ProcessId,
                ProcessStart = Recovery.ProcessStart,
            };
            for (int i = 0; i < snapshot.Textures.Count; i++)
            {
                var t = snapshot.Textures[i];
                string zip = $"texture{i}.zip";
                string path = Path.Combine(folder, zip);
                if (t.ReuseZip != null) File.Copy(t.ReuseZip, path);
                else LayerFile.WriteZip(path, t.Width, t.Height, t.Mark.Active, t.Layers!, fast: true);
                written[t.Mark.Texture] = (t.Mark.Version, path);
                afterZip?.Invoke(folder, i + 1);
                session.Textures.Add(new Recovery.TextureEntry
                {
                    Name = t.Name, FilePath = t.FilePath, Width = t.Width, Height = t.Height, ActiveLayer = t.Mark.Active,
                    Layers = t.LayerCount, Zip = zip, Materials = t.Materials,
                });
            }
            string temp = Path.Combine(folder, Recovery.SessionFileName + ".tmp");
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, session, Recovery.Json);
                stream.Flush(flushToDisk: true);
            }

            lock (Recovery.DiskLock)
            {
                // Discarded or cleared while this was being written: it's out of date.
                if (Recovery.Generation(snapshot.Key) != snapshot.Generation)
                {
                    Recovery.DeleteSet(folder);
                    return Dropped();
                }
                Recovery.Retry(() => File.Move(temp, Path.Combine(folder, Recovery.SessionFileName)));
                superseded = Recovery.Superseded(snapshot.Key, keep: folder);
            }
            superseded.ForEach(Recovery.DeleteSet);
            return new WriteResult(snapshot.Key, snapshot.Generation, true, null, clock.Elapsed.TotalMilliseconds, snapshot.SavedAt, written);
        }
        catch (Exception e)
        {
            Recovery.DeleteSet(folder);
            if (Recovery.Generation(snapshot.Key) != snapshot.Generation) return Dropped();
            return new WriteResult(snapshot.Key, snapshot.Generation, false, e.Message, clock.Elapsed.TotalMilliseconds, snapshot.SavedAt, new());
        }

        WriteResult Dropped() => new(snapshot.Key, snapshot.Generation, false, null, clock.Elapsed.TotalMilliseconds, snapshot.SavedAt, new());
    }
}
