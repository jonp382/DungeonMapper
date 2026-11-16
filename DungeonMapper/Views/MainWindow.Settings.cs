using System;
using Avalonia.Controls;
using Avalonia.Input;
using DungeonMapper.Models;

namespace DungeonMapper.Views;

// settings modal. every row is fixed in MainWindow.axaml and styled by
// Styles/Ui.axaml. this file fills in the values, wires the boxes and puts a
// color button into each color host. changes apply live and are written to
// disk on close (FlushSettings), like every other settings change.
public partial class MainWindow
{
    private static readonly AppSettings SettingDefaults = new();
    private const int MaxGridSpacing = 100, MaxAutosaveMinutes = 999;

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
}
