using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using Sable.Paint;

namespace Sable;

/// <summary>
/// Autosave and crash recovery: unsaved textures (every layer) are written to %APPDATA%\Sable\recovery about once a
/// minute while painting pauses, and when a model with a recovery set is opened, Sable offers to restore it.
/// </summary>
internal sealed partial class App
{
    private readonly Autosaver autosaver = new();
    private RecoverySet? recoveryOffer;
    private bool openRecoveryPrompt;

    /// <summary>Recovery only runs for real sessions, not the scripted checks.</summary>
    private bool RecoveryEnabled => KeepsSettings && !options.LinkTest;

    private void StartRecovery()
    {
        if (RecoveryEnabled) Task.Run(Recovery.CleanUp);
    }

    /// <summary>Per frame: autosave once things are quiet.</summary>
    private void TickAutosave()
    {
        if (!RecoveryEnabled) return;
        RunRecoveryTest();
        bool busy = stroke != null || lassoDrag != LassoDrag.None || layerDragBefore != null || zoomDrag != ZoomDrag.None;
        autosaver.Tick(Model, Raylib.GetTime(), busy);
    }

    /// <summary>Before the model is replaced or the window closes: keep unsaved work in a recovery set, or clear it.</summary>
    private void FlushRecovery()
    {
        if (!RecoveryEnabled || Model == null) return;
        if (Model.Textures.Any(t => t.Dirty)) autosaver.Flush(Model);
        else autosaver.Clear(Recovery.KeyOf(Model.Source));
    }

    /// <summary>After a save leaves nothing unsaved, the recovery set has nothing left to keep.</summary>
    private void ClearRecoveryIfSaved()
    {
        if (RecoveryEnabled && Model != null && !Model.Textures.Any(t => t.Dirty)) autosaver.Clear(Recovery.KeyOf(Model.Source));
    }

    /// <summary>After a model opens: offer what a crash (or closing without saving) left behind.</summary>
    private void OfferRecovery()
    {
        if (!RecoveryEnabled || Model == null) return;
        recoveryOffer = Recovery.Find(Recovery.KeyOf(Model.Source));
        openRecoveryPrompt = recoveryOffer != null;
    }

    private void DrawRecoveryPrompt()
    {
        if (openRecoveryPrompt)
        {
            ImGui.OpenPopup("Recover unsaved work");
            openRecoveryPrompt = false;
        }
        var screen = new Vector2(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
        ImGui.SetNextWindowPos(screen * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.BeginPopupModal("Recover unsaved work", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) return;
        if (recoveryOffer is not { } set || Model == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"{Model.Source.Name} has unsaved changes from {set.SavedAt.ToLocalTime():ddd d MMM, HH:mm}{(set.FromThisSession ? " (this session)" : "")}:");
        foreach (var texture in set.Textures)
            ImGui.BulletText($"{texture.Name}  {texture.Width}x{texture.Height}, {texture.LayerCount} layer{(texture.LayerCount == 1 ? "" : "s")}");
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted("Recover replaces those textures with the recovered ones (unsaved; Ctrl+S saves them).\nUndo history starts fresh.");
        ImGui.PopStyleColor();

        if (ImGui.Button("Recover", new Vector2(110, 0)))
        {
            EndStroke();
            int restored = Recovery.Apply(set, Model, out var notes);
            undo.Clear();
            state.Selection = null;
            uvView.RequestFit();
            foreach (string note in notes) Model.Source.Warnings.Add(note);
            SetStatus($"Recovered {restored} texture{(restored == 1 ? "" : "s")} from {set.SavedAt.ToLocalTime():HH:mm}"
                      + (notes.Count > 0 ? $"; {notes[0]}" : ". Ctrl+S saves them."), error: false);
            recoveryOffer = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Discard", new Vector2(110, 0)))
        {
            Recovery.Discard(set);
            SetStatus("Discarded the recovered changes.", error: false);
            recoveryOffer = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Later", new Vector2(110, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            recoveryOffer = null;
            ImGui.CloseCurrentPopup();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Keep the recovered changes on disk and decide next time the model is opened.");
        ImGui.EndPopup();
    }

    // --recoverytest (run twice; set SABLE_RECOVERY and SABLE_SETTINGS to scratch paths): with no recovery set,
    // paint a texel, autosave and exit without cleaning up, as a crash would; with one, restore it and check.
    private int recoveryTestStep = -1;

    private void RunRecoveryTest()
    {
        if (!options.RecoveryTest || Model == null || recoveryTestStep >= 1) return;
        recoveryTestStep = 1;
        var texture = Model.Textures.FirstOrDefault();
        if (texture == null) { Console.WriteLine("[recoverytest] no texture"); quit = true; return; }
        if (recoveryOffer == null)
        {
            texture.Pixels[0] = new Color(1, 2, 3, 255);
            texture.Touch(0, 0);
            Console.WriteLine($"[recoverytest] crash run: painted {texture.Name}, flushed {autosaver.Flush(Model)}; exiting without cleanup");
            Environment.Exit(0);
        }
        var set = recoveryOffer;
        int restored = Recovery.Apply(set, Model, out var notes);
        var texel = Model.Textures[0].Pixels[0];
        Console.WriteLine($"[recoverytest] recovery run: offered set from {set.SavedAt.ToLocalTime():HH:mm:ss} with {set.Textures.Count} texture(s), restored {restored}, "
                          + $"texel ({texel.R},{texel.G},{texel.B}) (expect 1,2,3), dirty {Model.Textures[0].Dirty}{(notes.Count > 0 ? ", notes: " + string.Join("; ", notes) : "")}");
        Recovery.Discard(set);
        recoveryOffer = null;
        Console.WriteLine($"[recoverytest] after discard, set found: {Recovery.Find(Recovery.KeyOf(Model.Source)) != null} (expect False)");
        quit = true;
    }
}
