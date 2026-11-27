using System.Collections.Generic;
using System.Linq;
using DungeonMapper.Controls;
using DungeonMapper.Models;

namespace DungeonMapper.Views;

// reveal tool side panel (multiplayer): what it changes (cells or items),
// show or hide, the shape for cells or the item kinds for items, counts, and
// whole map buttons. the canvas half is Controls/MapCanvas.Reveal.cs.
public partial class MainWindow
{
    // wired once from the constructor
    private void InitReveal()
    {
        Canvas.RevealChanged += RefreshToolInfo;
    }

    private void BuildRevealPanel()
    {
        ToolPanel.Clear("reveal tool");
        bool cells = Canvas.RevealTarget == RevealTarget.Cells;
        ToolPanel.AddHint(cells
            ? "choose what players see. revealed floor shows to players, the rest stays hidden. while this tool is selected, hidden floor is dimmed."
            : "items marked DM only are never shown to players, even on revealed floor. new items start DM only. they have a dashed pink outline while this tool is selected.");
        ToolPanel.AddChips(new[] { "cells", "items" }, cells ? "cells" : "items",
            v => { Canvas.RevealTarget = v == "cells" ? RevealTarget.Cells : RevealTarget.Items; Canvas.ResetReveal(); BuildRevealPanel(); Canvas.Refresh(); FocusCanvas(); },
            "changes");
        string[] actions = cells ? new[] { "reveal", "hide" } : new[] { "visible", "DM only" };
        ToolPanel.AddChips(actions, Canvas.RevealShow ? actions[0] : actions[1],
            v => { Canvas.RevealShow = v == actions[0]; Canvas.Refresh(); FocusCanvas(); },
            "action");
        if (cells)
        {
            ToolPanel.AddChips(new[] { "box", "room", "circle" }, Canvas.RevealShape.ToString().ToLowerInvariant(),
                v =>
                {
                    Canvas.RevealShape = v switch { "room" => RevealShape.Room, "circle" => RevealShape.Circle, _ => RevealShape.Box };
                    Canvas.ResetReveal(); Canvas.Refresh(); FocusCanvas();
                },
                "shape");
            _toolInfo = ToolPanel.AddInfo("");
            ToolPanel.AddButton("reveal whole map", () => { int n = Canvas.RevealAll(true); SetStatus($"revealed {Plural(n, "cell")}"); FocusCanvas(); });
            ToolPanel.AddButton("hide whole map", () => { int n = Canvas.RevealAll(false); SetStatus($"hid {Plural(n, "cell")}"); FocusCanvas(); });
        }
        else
        {
            void Kind(string label, RevealItems k) =>
                ToolPanel.AddCheck(label, Canvas.RevealKinds.HasFlag(k), v =>
                {
                    Canvas.RevealKinds = v ? Canvas.RevealKinds | k : Canvas.RevealKinds & ~k;
                    FocusCanvas();
                });
            ToolPanel.AddHint("the box changes these:");
            Kind("labels", RevealItems.Labels);
            Kind("doors", RevealItems.Doors);
            Kind("objects", RevealItems.Objects);
            Kind("pawns", RevealItems.Pawns);
            Kind("images", RevealItems.Images);
            _toolInfo = ToolPanel.AddInfo("");
            ToolPanel.AddButton("make all ticked visible", () => { int n = Canvas.SetAllDmOnly(false); SetStatus($"{Plural(n, "item")} now visible to players"); FocusCanvas(); });
            ToolPanel.AddButton("make all ticked DM only", () => { int n = Canvas.SetAllDmOnly(true); SetStatus($"{Plural(n, "item")} now DM only"); FocusCanvas(); });
        }
        ToolPanel.AddButton("preview player view", () => SetPlayerPreview(true));
        RefreshToolInfo();
    }

    /// the reveal panel's info box: revealed floor and DM-only counts
    public static string RevealInfo(MapDocument doc)
    {
        var floor = doc.FloorCells();
        int shown = floor.Count(doc.Revealed.Contains);
        var (l, d, o, p, im) = doc.DmOnlyCounts();
        return $"{shown} of {Plural(floor.Count, "floor cell")} revealed\n"
             + $"DM only: {Plural(l, "label")}, {Plural(d, "door")}, {Plural(o, "object")}, {Plural(p, "pawn")}, {Plural(im, "image")}";
    }
}

// player view preview: the canvas draws the players' copy of the map (see
// Models/PlayerView.cs), read only. View menu or the reveal panel turns it on,
// esc or picking a tool turns it off
public partial class MainWindow
{
    private void SetPlayerPreview(bool on)
    {
        if (Canvas.PlayerPreview == on) { MenuPlayerPreview.IsChecked = on; return; }
        if (on) { Canvas.CancelMove(); Canvas.CancelFormation(); Canvas.CancelAreaPick(); Canvas.CancelLine(); }
        Canvas.PlayerPreview = on;
        MenuPlayerPreview.IsChecked = on;
        ToolPanel.IsEnabled = !on; // panel buttons would act on the preview copy
        SetStatus(on
            ? "player view preview: this is what players are sent. editing is off, esc returns"
            : "back to editing");
        FocusCanvas();
    }

    // keys while previewing: esc leaves, saving, undo and redo, zoom to fit
    // and tool letters (which leave through SetTool) still work. everything
    // else that would edit is swallowed. true when the key was handled here
    private bool PreviewKey(Avalonia.Input.KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers == Avalonia.Input.KeyModifiers.Control;
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            if (ModalOverlay.IsVisible || SettingsOverlay.IsVisible || HotkeysOverlay.IsVisible
                || DoorPresetsOverlay.IsVisible || MapsOverlay.IsVisible) return false;
            SetPlayerPreview(false);
            return true;
        }
        if (IsPingKey(e.Key) && e.KeyModifiers == Avalonia.Input.KeyModifiers.None) return false;
        if (ctrl) return e.Key is not (Avalonia.Input.Key.S or Avalonia.Input.Key.Z or Avalonia.Input.Key.Y);
        if (e.Key == Avalonia.Input.Key.Delete) return true;
        if (e.KeyModifiers != Avalonia.Input.KeyModifiers.None) return true; // shift+r and friends
        if (System.Enum.TryParse<Avalonia.Input.Key>(_settings.ToolHotkeys.GetValueOrDefault(AppSettings.FormationHotkey, ""), out var form) && form == e.Key)
            return true;
        return false;
    }
}
