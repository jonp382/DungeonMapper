using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// "has anything changed" for the autosave skip and the save prompt on exit.
// the check compares what a save would write (maps, pawn library, campaign
// presets) with what was last written or loaded, so pawn and preset edits
// count too, not just map undo steps. the active map is left out: switching
// maps is not a change.
public partial class MainWindow
{
    // campaign content at the last save, save as or load (startup: the empty campaign)
    private string? _savedContent;
    // campaign content at the last autosave, save or load
    private string? _autosavedContent;
    // set once the exit prompt has been answered, so the next close goes through
    private bool _exitConfirmed;

    private string ProjectContent() =>
        MapSerializer.ProjectContentKey(_project, ActiveLibrary, ActivePresets, ActiveObjects);

    // after a save, save as or load: nothing is unsaved and nothing needs an autosave
    private void MarkSaved()
    {
        _savedContent = ProjectContent();
        _autosavedContent = _savedContent;
    }

    private bool HasUnsavedChanges() => ProjectContent() != _savedContent;

    // the autosave tick once the interval has passed
    private void RunAutosave()
    {
        _lastAutosave = DateTime.Now;
        FlushPendingEdits();
        var content = ProjectContent();
        if (content == _autosavedContent)
        {
            SetStatus("skipped autosave: no changes have been made", StatusKind.Warning);
            return;
        }
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "maps", "autosaves");
        System.IO.Directory.CreateDirectory(dir);
        var name = $"autosave_{_project.ActiveIndex}_{Stamp()}.json";
        MapSerializer.SaveProject(_project, System.IO.Path.Combine(dir, name), ActiveLibrary, ActivePresets, ActiveObjects);
        _autosavedContent = content;
        SetStatus($"autosaved {name}", StatusKind.Success);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_exitConfirmed)
        {
            FlushPendingEdits();
            FlushSettings();
            if (HasUnsavedChanges())
            {
                e.Cancel = true;
                ShowExitPrompt();
            }
        }
        base.OnClosing(e);
    }

    // yes saves (the picker for a campaign that was never saved) then closes,
    // no closes without saving, cancel and esc go back to the map. no button is
    // the default, enter does not pick one.
    private void ShowExitPrompt()
    {
        CloseModals();
        Canvas.CancelMove();
        var body = new StackPanel { Spacing = 14, Margin = new(16, 12, 16, 16) };
        body.Children.Add(new TextBlock
        {
            Text = "there are changes that have not been saved.",
            Classes = { "body" },
            TextWrapping = TextWrapping.Wrap,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var yes = Btn("yes, save", primary: true);
        var no = Btn("no, discard", danger: true);
        var cancel = Btn("cancel");
        yes.Click += async (_, _) =>
        {
            CloseExitPrompt();
            if (await SaveCurrentAsync()) ConfirmedClose();
            else SetStatus("not saved, exit cancelled", StatusKind.Warning);
        };
        no.Click += (_, _) => { CloseExitPrompt(); ConfirmedClose(); };
        cancel.Click += (_, _) => CloseExitPrompt();
        row.Children.Add(yes);
        row.Children.Add(no);
        row.Children.Add(cancel);
        body.Children.Add(row);
        ShowModal(400, 170, "Save before exiting?", null, body);
        _modalKind = "exit";
        FocusCanvas(); // no button holds focus, so enter or space cannot answer
    }

    private void CloseExitPrompt()
    {
        if (_modalKind != "exit") return;
        ModalOverlay.IsVisible = false;
        _modalKind = null;
        FocusCanvas();
    }

    private void ConfirmedClose()
    {
        _exitConfirmed = true;
        Close();
    }
}
