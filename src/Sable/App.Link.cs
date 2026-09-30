using Raylib_cs;
using Sable.Model;
using Sable.Paint;
using Sable.Rendering;

namespace Sable;

/// <summary>
/// The live link from Blender (<c>Sable --link link.json</c>, started by the Sable Link add-on): when Blender writes
/// a newer revision of the linked objects, load it in the background and swap the geometry in, keeping every
/// texture, its layers and the paint undo history.
/// </summary>
internal sealed partial class App
{
    private Task<LoadedModel>? linkRefresh;
    /// <summary>A loaded update waiting for the stroke (or selection drag) in progress to finish.</summary>
    private LoadedModel? pendingLink;
    private double linkCheckedAt;
    private int linkFailedRevision = -1;
    private bool linkEndedShown;

    /// <summary>Per frame: look for a newer revision a few times a second, and apply one when it's ready.</summary>
    private void UpdateLink()
    {
        if (Model?.Source.LinkPath is not { } path) return;
        RunLinkTest();
        bool busy = stroke != null || lassoDrag != LassoDrag.None || zoomDrag != ZoomDrag.None;

        if (linkRefresh is { IsCompleted: true } task)
        {
            linkRefresh = null;
            if (task.IsFaulted)
            {
                var error = task.Exception!.InnerException ?? task.Exception;
                linkFailedRevision = BlenderLink.TryRead(path)?.Revision ?? -1;
                SetStatus($"Couldn't update from Blender: {error.Message}", error: true);
            }
            else pendingLink = task.Result;
        }
        if (pendingLink != null)
        {
            if (busy) return;
            var source = pendingLink;
            pendingLink = null;
            ApplyLinkUpdate(source);
            return;
        }
        if (linkRefresh != null || loading != null) return;

        double now = Raylib.GetTime();
        if (now - linkCheckedAt < 0.25) return;
        linkCheckedAt = now;
        if (!File.Exists(path))
        {
            if (!linkEndedShown) SetStatus("The Blender link was stopped; Blender changes no longer come through. Painting and saving still work.", error: false);
            linkEndedShown = true;
            return;
        }
        linkEndedShown = false;
        if (BlenderLink.TryRead(path) is not { } link) return;
        if (link.Revision > Model.Source.LinkRevision && link.Revision != linkFailedRevision) StartLinkRefresh(force: false);
    }

    // --linktest: paint, bump the link's revision as Blender would, and check the paint survives the update.
    private int linkTestStep = -1;
    private PaintTexture? linkTestTexture;

    private void RunLinkTest()
    {
        if (!options.LinkTest || Model?.Source.LinkPath is not { } path || linkTestStep >= 2) return;
        if (linkTestStep < 0)
        {
            linkTestStep = 0;
            var materials = string.Join(", ", Model.Source.Materials.Select(m => $"{m.Name}->{(m.TextureIndex >= 0 ? Model.Textures[m.TextureIndex].Name : "none")}"));
            Console.WriteLine($"[linktest] loaded revision {Model.Source.LinkRevision} from {Model.Source.Name}; materials: {materials}");
            state.ActiveTexture = Model.Source.Materials.FirstOrDefault(m => m.TextureIndex >= 0)?.TextureIndex ?? -1;
            if (state.ActiveTexture < 0) { Console.WriteLine("[linktest] no texture to paint"); quit = true; return; }
            linkTestTexture = Model.Textures[state.ActiveTexture];
            var paint = new Stroke(linkTestTexture, new Color(255, 0, 255, 255));
            paint.Apply(1, 1, 1f);
            undo.Push(paint.Finish()!);
            string json = File.ReadAllText(path);
            var link = BlenderLink.TryRead(path)!;
            File.WriteAllText(path, json.Replace($"\"revision\": {link.Revision}", $"\"revision\": {link.Revision + 1}"));
            return;
        }
        if (linkTestStep == 0 && Model.Source.LinkRevision > 1)
        {
            linkTestStep = 2;
            bool same = Model.Textures.Contains(linkTestTexture!);
            var texel = linkTestTexture!.Pixels[1 * linkTestTexture.Width + 1];
            bool active = state.ActiveTexture >= 0 && Model.Textures[state.ActiveTexture] == linkTestTexture;
            undo.Undo();
            var undone = linkTestTexture.Pixels[1 * linkTestTexture.Width + 1];
            Console.WriteLine($"[linktest] updated to revision {Model.Source.LinkRevision}: same texture object {same}, still active {active}, "
                              + $"painted texel ({texel.R},{texel.G},{texel.B}) (expect 255,0,255), after undo ({undone.R},{undone.G},{undone.B})");
            quit = true;
        }
    }

    /// <summary>Loads the link's current revision in the background (Ctrl+R forces it).</summary>
    private void StartLinkRefresh(bool force)
    {
        if (Model?.Source.LinkPath is not { } path || linkRefresh != null) return;
        if (force) linkFailedRevision = -1;
        linkRefresh = Task.Run(() => ModelLoader.Load(path, _ => { }));
    }

    /// <summary>
    /// Replaces the model with the updated geometry. Textures carry over (see <see cref="GpuModel"/>); the active
    /// object, hidden objects, local view and the camera stay as they were, matched by object name.
    /// </summary>
    private void ApplyLinkUpdate(LoadedModel source)
    {
        var old = Model!;
        string? activeName = state.ActiveObject >= 0 && state.ActiveObject < old.Source.Objects.Count ? old.Source.Objects[state.ActiveObject].Name : null;
        string? isolatedName = state.Isolated is int isolated && isolated < old.Source.Objects.Count ? old.Source.Objects[isolated].Name : null;
        var hidden = old.Source.Objects.Where(o => o.Hidden).Select(o => o.Name).ToHashSet();
        var activeTexture = state.ActiveTexture >= 0 && state.ActiveTexture < old.Textures.Count ? old.Textures[state.ActiveTexture] : null;
        var selectionTexture = state.Selection != null && state.Selection.Texture < old.Textures.Count ? old.Textures[state.Selection.Texture] : null;

        profiler.Skip();
        var fresh = new GpuModel(source, shader!.Shader, old);
        var added = fresh.Textures.Where(t => !old.Textures.Contains(t)).ToList();
        foreach (var obj in source.Objects) obj.Hidden = hidden.Contains(obj.Name);
        state.Model = fresh;
        // Hide/reveal steps point at the old model's parts; paint, layer and resize steps point at textures, which stay.
        undo.RemoveAll(step => step is VisibilityStep);
        old.Dispose();

        state.ActiveObject = activeName != null ? source.Objects.FindIndex(o => o.Name == activeName) : -1;
        int isolatedIndex = isolatedName != null ? source.Objects.FindIndex(o => o.Name == isolatedName) : -1;
        state.Isolated = isolatedIndex >= 0 ? isolatedIndex : null;
        state.Submesh = null;
        state.ActiveTexture = activeTexture != null ? fresh.Textures.IndexOf(activeTexture) : fresh.Textures.Count > 0 ? 0 : -1;
        if (state.Selection != null)
        {
            int index = selectionTexture != null ? fresh.Textures.IndexOf(selectionTexture) : -1;
            if (index >= 0) state.Selection.Texture = index;
            else state.Selection = null;
        }
        LoadLayers(added);

        SetStatus($"Updated from Blender (revision {source.LinkRevision}): {source.Objects.Count} object{(source.Objects.Count == 1 ? "" : "s")}, "
                  + $"{source.Parts.Sum(p => p.TriangleCount)} tris", error: false);
    }
}
