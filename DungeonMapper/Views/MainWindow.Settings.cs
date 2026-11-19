using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DungeonMapper.Controls;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// settings modal. every row is fixed in MainWindow.axaml and styled by
// Styles/Ui.axaml. this file fills in the values, wires the boxes and puts a
// color button into each color host. changes apply live and are written to
// disk on close (FlushSettings), like every other settings change.
public partial class MainWindow
{
    private static readonly AppSettings SettingDefaults = new();
    private const int MaxGridSpacing = AppSettings.MaxGridSpacing, MaxAutosaveMinutes = AppSettings.MaxAutosaveMinutes;

    private sealed record ColorSetting(ContentControl Host, Button Reset,
        Func<AppSettings, (int r, int g, int b)> Get, Action<AppSettings, (int r, int g, int b)> Set);

    private sealed record NumberSetting(TextBox Box, TextBlock Error, Button Reset, int Min, int Max,
        Func<AppSettings, int> Get, Action<AppSettings, int> Set);

    private ColorSetting[] _colorSettings = [];
    private NumberSetting[] _numberSettings = [];
    private bool _settingsLoading;

    // wired once from the constructor
    private void InitSettings()
    {
        _colorSettings =
        [
            new(SetMajorColor, ResetMajorColor, s => (s.MajorGridR, s.MajorGridG, s.MajorGridB),
                (s, c) => (s.MajorGridR, s.MajorGridG, s.MajorGridB) = c),
            new(SetMinorColor, ResetMinorColor, s => (s.MinorGridR, s.MinorGridG, s.MinorGridB),
                (s, c) => (s.MinorGridR, s.MinorGridG, s.MinorGridB) = c),
            new(SetDotColor, ResetDotColor, s => (s.GridDotR, s.GridDotG, s.GridDotB),
                (s, c) => (s.GridDotR, s.GridDotG, s.GridDotB) = c),
            new(SetBackgroundColor, ResetBackgroundColor, s => (s.BackgroundR, s.BackgroundG, s.BackgroundB),
                (s, c) => (s.BackgroundR, s.BackgroundG, s.BackgroundB) = c),
            new(SetCellColor, ResetCellColor, s => (s.CellFillR, s.CellFillG, s.CellFillB),
                (s, c) => (s.CellFillR, s.CellFillG, s.CellFillB) = c),
            new(SetWallColor, ResetWallColor, s => (s.WallR, s.WallG, s.WallB),
                (s, c) => (s.WallR, s.WallG, s.WallB) = c),
            new(SetStairColor, ResetStairColor, s => (s.StairColorR, s.StairColorG, s.StairColorB),
                (s, c) => (s.StairColorR, s.StairColorG, s.StairColorB) = c),
            new(SetLabelColor, ResetLabelColor, s => (s.LabelR, s.LabelG, s.LabelB),
                (s, c) => (s.LabelR, s.LabelG, s.LabelB) = c),
        ];
        _numberSettings =
        [
            new(SetGridSpacing, SetGridSpacingError, ResetGridSpacing, 1, MaxGridSpacing,
                s => s.GridDrawSpacing, (s, v) => s.GridDrawSpacing = v),
            new(SetWallThickness, SetWallThicknessError, ResetWallThickness,
                AppSettings.MinWallThickness, AppSettings.MaxWallThickness,
                s => s.WallThickness, (s, v) => s.WallThickness = v),
            new(SetAutosave, SetAutosaveError, ResetAutosave, 0, MaxAutosaveMinutes,
                s => s.AutosaveMinutes, (s, v) => s.AutosaveMinutes = v),
            new(SetFloorGridOpacity, SetFloorGridOpacityError, ResetFloorGridOpacity, 0, AppSettings.MaxFloorGridOpacity,
                s => s.FloorGridOpacity, (s, v) => s.FloorGridOpacity = v),
            new(SetDoorWallGap, SetDoorWallGapError, ResetDoorWallGap, 0, AppSettings.MaxDoorWallGap,
                s => s.DoorWallGap, (s, v) => s.DoorWallGap = v),
        ];
        SetWallThicknessHint.Text = $"pixels, {AppSettings.MinWallThickness} to {AppSettings.MaxWallThickness}.";
        SetAutosaveHint.Text = $"minutes, 0 to {MaxAutosaveMinutes}. 0 turns autosave off.";

        foreach (var c in _colorSettings)
            c.Reset.Click += (_, _) =>
            {
                c.Set(_settings, c.Get(SettingDefaults));
                SettingChanged();
                RefreshSettings();
            };

        // numbers apply on enter or when the box loses focus, so a half typed
        // value never reaches the map. esc puts the saved value back.
        foreach (var n in _numberSettings)
        {
            n.Box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { ApplyNumber(n); e.Handled = true; }
                else if (e.Key == Key.Escape) { RefreshSettings(); FocusCanvas(); e.Handled = true; }
            };
            n.Box.LostFocus += (_, _) => ApplyNumber(n);
            n.Reset.Click += (_, _) =>
            {
                n.Set(_settings, n.Get(SettingDefaults));
                SettingChanged();
                RefreshSettings();
            };
        }

        SetLabelText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ApplyLabelText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { RefreshSettings(); FocusCanvas(); e.Handled = true; }
        };
        SetLabelText.LostFocus += (_, _) => ApplyLabelText();
        ResetLabelText.Click += (_, _) =>
        {
            _settings.DefaultLabelText = SettingDefaults.DefaultLabelText;
            MarkSetting();
            RefreshSettings();
        };

        // the slider applies live while dragging and keeps the box in step.
        // the box is the opacity NumberSetting above, RefreshSettings moves
        // the slider to whatever the box applied.
        SetFloorGridOpacitySlider.ValueChanged += (_, _) =>
        {
            if (_settingsLoading) return;
            int v = (int)Math.Round(SetFloorGridOpacitySlider.Value);
            SetFloorGridOpacity.Text = v.ToString();
            SetFloorGridOpacity.Classes.Remove("invalid");
            SetFloorGridOpacityError.IsVisible = false;
            if (v == _settings.FloorGridOpacity) return;
            _settings.FloorGridOpacity = v;
            SettingChanged();
            RefreshSettingResets();
        };

        SetShowMinor.IsCheckedChanged += (_, _) =>
        {
            if (_settingsLoading) return;
            _settings.ShowMinorGrid = SetShowMinor.IsChecked == true;
            SettingChanged();
            RefreshSettings();
        };

        SettingsClose.Click += (_, _) => CloseSettings();
        SettingsResetAll.Click += (_, _) => ConfirmFlyout(SettingsResetAll,
            "put every setting on this page back to its default? door presets and hotkeys are not touched.",
            ResetAllSettings, "reset all");
        SettingsExport.Click += async (_, _) => await ExportSettings();
        SettingsImport.Click += async (_, _) => await ImportSettings();
    }

    private void ShowSettings()
    {
        FlushSettings();
        SettingsOverlay.IsVisible = true;
        RefreshSettings();
    }

    private void CloseSettings()
    {
        foreach (var n in _numberSettings) ApplyNumber(n);
        ApplyLabelText();
        FlushSettings();
        SettingsOverlay.IsVisible = false;
        FocusCanvas();
    }

    // puts every value from _settings into the controls and clears refusals
    private void RefreshSettings()
    {
        _settingsLoading = true;
        foreach (var n in _numberSettings)
        {
            n.Box.Text = n.Get(_settings).ToString();
            n.Box.Classes.Remove("invalid");
            n.Error.IsVisible = false;
        }
        foreach (var c in _colorSettings)
        {
            // minor color is greyed out while minor lines are hidden
            bool enabled = c.Host != SetMinorColor || _settings.ShowMinorGrid;
            var b = ColorButton(c.Get(_settings), v =>
            {
                c.Set(_settings, v);
                SettingChanged();
                RefreshSettingResets();
            }, enabled);
            // fixed width so the swatches line up whatever the hex text is
            b.Width = 92;
            b.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            c.Host.Content = b;
        }
        SetShowMinor.IsChecked = _settings.ShowMinorGrid;
        SetFloorGridOpacitySlider.Value = _settings.FloorGridOpacity;
        SetLabelText.Text = _settings.DefaultLabelText;
        SetLabelText.Classes.Remove("invalid");
        SetLabelTextError.IsVisible = false;
        RefreshSettingResets();
        _settingsLoading = false;
    }

    // a reset button only shows on rows that differ from the default
    private void RefreshSettingResets()
    {
        foreach (var n in _numberSettings) n.Reset.IsVisible = n.Get(_settings) != n.Get(SettingDefaults);
        foreach (var c in _colorSettings) c.Reset.IsVisible = c.Get(_settings) != c.Get(SettingDefaults);
        ResetLabelText.IsVisible = _settings.DefaultLabelText != SettingDefaults.DefaultLabelText;
    }

    private void SettingChanged()
    {
        Canvas.Refresh();
        MarkSetting();
    }

    private void ApplyNumber(NumberSetting n)
    {
        if (_settingsLoading) return;
        string text = (n.Box.Text ?? "").Trim();
        if (!int.TryParse(text, out int v) || v < n.Min || v > n.Max)
        {
            n.Box.Classes.Add("invalid");
            n.Error.Text = $"use a whole number from {n.Min} to {n.Max}.";
            n.Error.IsVisible = true;
            return;
        }
        n.Box.Classes.Remove("invalid");
        n.Error.IsVisible = false;
        n.Box.Text = v.ToString();
        if (v == n.Get(_settings)) return;
        n.Set(_settings, v);
        if (n.Box == SetFloorGridOpacity)
        {
            _settingsLoading = true;
            SetFloorGridOpacitySlider.Value = v;
            _settingsLoading = false;
        }
        SettingChanged();
        RefreshSettingResets();
    }

    private void ApplyLabelText()
    {
        if (_settingsLoading) return;
        string text = SetLabelText.Text ?? "";
        if (text.Trim().Length == 0)
        {
            SetLabelText.Classes.Add("invalid");
            SetLabelTextError.Text = "new labels need some text.";
            SetLabelTextError.IsVisible = true;
            return;
        }
        SetLabelText.Classes.Remove("invalid");
        SetLabelTextError.IsVisible = false;
        if (text == _settings.DefaultLabelText) return;
        _settings.DefaultLabelText = text;
        MarkSetting();
        RefreshSettingResets();
    }

    // only the settings on this page. door presets, hotkeys and pawn data
    // live in the same file but have their own menus.
    private void ResetAllSettings()
    {
        foreach (var c in _colorSettings) c.Set(_settings, c.Get(SettingDefaults));
        foreach (var n in _numberSettings) n.Set(_settings, n.Get(SettingDefaults));
        _settings.ShowMinorGrid = SettingDefaults.ShowMinorGrid;
        _settings.DefaultLabelText = SettingDefaults.DefaultLabelText;
        SettingChanged();
        RefreshSettings();
        StatusText.Text = "settings reset to defaults";
    }

    // every app wide setting: this page, the default label text and the
    // hotkeys. door presets, pawn statuses and affiliation colors live in the
    // campaign file and have their own import and export.
    private async Task ExportSettings()
    {
        foreach (var n in _numberSettings) ApplyNumber(n);
        ApplyLabelText();
        FlushSettings();
        var dir = Path.GetDirectoryName(_settings.SettingsPath())!;
        Directory.CreateDirectory(dir);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export settings",
            SuggestedFileName = "dungeon mapper settings.json",
            DefaultExtension = "json",
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(dir + "/")),
            FileTypeChoices = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;
        try
        {
            File.WriteAllText(file.Path.LocalPath, _settings.ExportJson());
            StatusText.Text = $"settings exported to {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex) { StatusText.Text = $"settings export failed: {ex.Message}"; }
    }

    private async Task ImportSettings()
    {
        var dir = Path.GetDirectoryName(_settings.SettingsPath())!;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import settings",
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(dir)
                ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(dir + "/")) : null,
            FileTypeFilter = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        var name = Path.GetFileName(path);
        AppSettings? read;
        try { read = AppSettings.ParseImport(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            StatusText.Text = $"could not read {name}: {ex.Message}";
            return;
        }
        if (read is null)
        {
            StatusText.Text = $"{name} is not a settings file";
            return;
        }
        ConfirmFlyout(SettingsImport,
            $"replace the app settings and hotkeys with the ones in {name}? door presets, pawn statuses and affiliation colors are not touched. the current settings are kept as settings before import.json.",
            () => ApplyImportedSettings(read, name), "import");
    }

    private void ApplyImportedSettings(AppSettings read, string name)
    {
        try
        {
            FlushSettings();
            var current = _settings.SettingsPath();
            if (File.Exists(current))
                File.Copy(current, Path.Combine(Path.GetDirectoryName(current)!, "settings before import.json"), true);
        }
        catch (Exception ex)
        {
            // the confirm promised a backup, so no backup means no import
            StatusText.Text = $"settings not imported, could not keep the current ones: {ex.Message}";
            return;
        }
        _settings.CopyAppWideFrom(read);
        MarkSetting();
        FlushSettings();
        Canvas.Refresh();
        RefreshToolLabels();
        RefreshSettings();
        if (Canvas.Mode == ToolMode.Pawn) BuildPawnPanel(); else PopulatePanel(Canvas.Mode);
        StatusText.Text = $"settings imported from {name}";
    }
}
