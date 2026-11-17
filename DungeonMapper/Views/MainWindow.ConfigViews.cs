using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DungeonMapper.Controls;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// the side panel views for one placed element: door, stair, label, pawn info and
// change pawn. same layout as the tool views: title, field labels with chips or
// boxes, an info box, then an actions row with delete last
public partial class MainWindow
{
    // undo and redo swap in new objects, so an open view would edit a detached
    // copy. each view leaves a way to find its element again by where it sits,
    // and the first control of the view marks that it is still showing
    private Action? _reopenConfig;
    private Control? _configMarker;
    // the open view's pending edit, taken as its own undo step before an undo
    // or redo runs so the undo steps back from it instead of skipping it
    private Action? _flushConfig;

    private void FlushPendingEdits()
    {
        EndLabelEdit(true);
        if (_configMarker is not null && ToolPanel.Target.Children.Contains(_configMarker)) _flushConfig?.Invoke();
    }

    private void SetReopen(Action reopen)
    {
        _reopenConfig = reopen;
        _flushConfig = null;
        _configMarker = ToolPanel.Target.Children.FirstOrDefault();
    }

    // called after undo and redo: rebuilds the open view for the element at the
    // same spot, or goes back to the tool view when it is gone
    private void ReopenConfigView()
    {
        if (_reopenConfig is null || _configMarker is null || !ToolPanel.Target.Children.Contains(_configMarker)) return;
        _reopenConfig();
    }

    private void BackToToolView()
    {
        _reopenConfig = null;
        PopulatePanel(Canvas.Mode);
    }

    // a row of buttons at the bottom of a view. each fires the panel button hook first
    private void AddActions(params (string label, bool danger, Action onClick)[] actions)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, danger, onClick) in actions)
        {
            var b = Btn(label, danger: danger);
            b.Margin = new Thickness(0, 0, 6, 6);
            var go = onClick;
            b.Click += (_, _) => { ToolPanel.ButtonHook?.Invoke(); go(); };
            row.Children.Add(b);
        }
        ToolPanel.AddButtonRaw(row);
    }

    // after a delete: one undo step, every count follows, back to the tool view
    private void AfterDelete(string what)
    {
        ActiveHistory.Commit(_doc);
        Canvas.Refresh();
        PawnViewsChanged();
        RefreshDoorPresets();
        RefreshMaps();
        BackToToolView();
        StatusText.Text = $"{what} deleted";
        FocusCanvas();
    }

    // ---------- door ----------

    private void ShowDoorConfig(IDoorConfig door)
    {
        var spot = _doc.DoorSpotOf(door);
        ToolPanel.Clear("door");
        TextBlock? info = null;
        void ShowInfo() { if (info is not null) info.Text = DoorConfigInfo(door.Type, _doc, _project.Maps.Select(m => m.Doc)); }

        var types = ActivePresets.DoorTypes.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        // known issue: one chip per door preset, all shown. with around 90 presets
        // the chips push the door view past the bottom of the screen at 1080p.
        // left as is since a campaign is not expected to have that many door
        // types. if it ever matters, cap the chip area height with its own scroll
        // or switch to a searchable list past a set count.
        ToolPanel.AddChips(ActivePresets.DoorTypes.Select(t => t.Name).ToArray(), door.Type, t =>
            {
                if (t == door.Type) return;
                door.Type = t;
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
                RefreshDoorPresets();
                ShowInfo();
                FocusCanvas();
            },
            "type",
            n => types.TryGetValue(n, out var t)
                ? new Ellipse { Width = 9, Height = 9, Fill = Rgb((t.R, t.G, t.B)) }
                : null);
        ToolPanel.AddChips(new[] { "open", "closed", "locked" }, door.State.ToString().ToLowerInvariant(), s =>
            {
                var st = Enum.Parse<DoorState>(s, ignoreCase: true);
                if (st == door.State) return;
                door.State = st;
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
                FocusCanvas();
            },
            "state");
        info = ToolPanel.AddInfo("");
        ShowInfo();
        AddActions(("delete", true, () =>
        {
            if (_doc.RemoveDoorConfig(door)) AfterDelete("door");
        }));

        SetReopen(() =>
        {
            if (_doc.DoorAtSpot(spot) is { } d) ShowDoorConfig(d);
            else BackToToolView();
        });
    }

    public static string DoorConfigInfo(string type, MapDocument doc, IEnumerable<MapDocument> campaign)
    {
        int here = doc.AllDoors().Count(d => d.Type == type);
        // the open map is part of the campaign, count it once even if it is not listed yet
        var docs = campaign.ToList();
        if (!docs.Contains(doc)) docs.Add(doc);
        int all = docs.Sum(m => m.AllDoors().Count(d => d.Type == type));
        return $"{Plural(here, "door")} of this type on this map, {all} in the campaign";
    }

    // ---------- stair ----------

    private void ShowStairConfig(Stair stair)
    {
        var cell = _doc.Stairs.FirstOrDefault(kv => ReferenceEquals(kv.Value, stair)).Key;
        ToolPanel.Clear("stair");
        // picking a direction sets every connected stair to it, so a group
        // that drifted out of sync re-syncs on one choice
        ToolPanel.AddChips(new[] { "up", "down", "left", "right" }, stair.Direction.ToString().ToLowerInvariant(), v =>
            {
                var target = Enum.Parse<StairDirection>(v, ignoreCase: true);
                var group = _doc.ConnectedStairs(cell);
                if (group.All(s => s.Direction == target)) return;
                foreach (var s in group) s.Direction = target;
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
                FocusCanvas();
            },
            "direction");
        ToolPanel.AddInfo(StairConfigInfo(_doc.ConnectedStairs(cell).Count));
        AddActions(("delete", true, () =>
        {
            if (_doc.Stairs.Remove(cell)) AfterDelete("stair");
        }));

        SetReopen(() =>
        {
            if (_doc.Stairs.TryGetValue(cell, out var s)) ShowStairConfig(s);
            else BackToToolView();
        });
    }

    // connected counts the stair itself
    public static string StairConfigInfo(int connected) => connected <= 1
        ? "a single stair, no others are connected to it"
        : $"{connected} connected stairs, all change direction together";

    // ---------- label ----------

    private void ShowLabelConfig(MapLabel label)
    {
        int index = _doc.Labels.IndexOf(label);
        var cell = label.Cell;
        ToolPanel.Clear("label");
        // the size box shows the size the label draws at. while the label has
        // no size of its own that is its default, which for a range depends on
        // the text, so the box follows the text until a number is typed in.
        TextBox? sizeBox = null;
        bool settingSize = false;
        void ShowSize()
        {
            if (sizeBox is null) return;
            settingSize = true;
            sizeBox.Text = ((int)Math.Round(MapCanvas.LabelSize(label))).ToString();
            settingSize = false;
        }
        // text and size preview live while typing but make one undo step per
        // edit, taken when the edit ends (enter, focus leaving, panel rebuilt,
        // or undo and redo flushing it first). a box raises its text changed
        // event once when it is first shown, so changes only count while the
        // box has focus. only edits typed here count: the on map editor makes
        // its own undo step, and the step is skipped when the text and size
        // ended up back where the view opened with
        string startText = label.Text;
        int startSize = label.FontSize;
        bool dirty = false;
        void Done()
        {
            // the size box also tidies up here: a clamped, negative or
            // unparsable entry goes back to the size the label really has
            ShowSize();
            // undo and redo swap in new label objects, a view left on an old
            // one must not commit the restored map over the redo steps
            if (!dirty || !_doc.Labels.Contains(label)) return;
            dirty = false;
            if (label.Text == startText && label.FontSize == startSize) return;
            startText = label.Text;
            startSize = label.FontSize;
            ActiveHistory.Commit(_doc);
        }
        TextBox? textBox = null;
        textBox = ToolPanel.AddText("text", label.Text, t =>
        {
            if (textBox?.IsKeyboardFocusWithin != true) return;
            t = t.Replace("\r\n", "\n");
            if (t == label.Text) return;
            label.Text = t;
            dirty = true;
            if (label.FontSize == 0) ShowSize();
            Canvas.Refresh();
        }, multiline: true, onDone: Done);
        if (textBox.Parent is Panel textField)
            textField.Children.Add(new TextBlock { Text = "enter confirms, shift+enter starts a new line. double click the label to edit it on the map", Classes = { "hint" }, TextWrapping = TextWrapping.Wrap });
        // wraps only this box so long lines are readable, the label on the map
        // keeps its own line breaks. remembered for the session, not saved.
        textBox.TextWrapping = _labelBoxWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ToolPanel.AddCheck("wrap text in this box", _labelBoxWrap, v =>
        {
            _labelBoxWrap = v;
            textBox.TextWrapping = v ? TextWrapping.Wrap : TextWrapping.NoWrap;
        });
        // font size at 100% zoom, it scales with the map. 0 or less means auto,
        // the model clamps it to 0..MapLabel.MaxFontSize
        sizeBox = ToolPanel.AddNumber("font size", (int)Math.Round(MapCanvas.LabelSize(label)), v =>
        {
            if (settingSize || sizeBox?.IsKeyboardFocusWithin != true) return;
            int before = label.FontSize;
            label.FontSize = v;
            if (label.FontSize == before) return;
            dirty = true;
            Canvas.Refresh();
        }, onDone: Done);
        // enter finishes the edit like on the map: focus goes back to the map,
        // which ends the edit through focus loss. shift+enter or alt+enter
        // starts a new line in the text
        void EnterFinishes(TextBox box, bool newlines) => box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (newlines && (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) != 0) InsertNewline(box);
            else FocusCanvas();
        }, RoutingStrategies.Tunnel);
        EnterFinishes(textBox, true);
        EnterFinishes(sizeBox, false);
        ToolPanel.AddChips(new[] { "left", "center", "right" }, label.Align.ToString().ToLowerInvariant(), v =>
            {
                var a = Enum.Parse<LabelAlign>(v, ignoreCase: true);
                if (a == label.Align) return;
                label.Align = a;
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
                FocusCanvas();
            },
            "alignment");

        // color: off follows the settings label color, so changing that
        // setting recolors this label. on gives the label its own color, which
        // starts as the current settings color so nothing jumps. the picker
        // previews live and makes one undo step when its popup closes
        var colorHost = new ContentControl();
        var colorHint = new TextBlock { Classes = { "hint" }, TextWrapping = TextWrapping.Wrap };
        void ShowColorState()
        {
            colorHint.Text = label.HasOwnColor
                ? "keeps this color when the settings label color changes"
                : "follows the label color in settings";
            colorHost.IsVisible = label.HasOwnColor;
            if (!label.HasOwnColor) { colorHost.Content = null; return; }
            bool colorDirty = false;
            var b = ColorButton((label.R, label.G, label.B), c =>
            {
                if ((label.R, label.G, label.B) == c) return;
                (label.R, label.G, label.B) = c;
                colorDirty = true;
                Canvas.Refresh();
            });
            b.Margin = new Thickness(28, 2, 0, 0);
            if (b.Flyout is Flyout fly)
                fly.Closed += (_, _) =>
                {
                    if (!colorDirty) return;
                    colorDirty = false;
                    if (_doc.Labels.Contains(label)) ActiveHistory.Commit(_doc);
                };
            colorHost.Content = b;
        }
        var own = ToolPanel.AddCheck("own color", label.HasOwnColor, on =>
        {
            if (on == label.HasOwnColor) return;
            if (on) (label.R, label.G, label.B) = (_settings.LabelR, _settings.LabelG, _settings.LabelB);
            else (label.R, label.G, label.B) = (-1, -1, -1);
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
            ShowColorState();
        });
        // the hint and the picker sit tight under the toggle, like AddCheck's own hint
        colorHint.Margin = new Thickness(28, 0, 0, 2);
        ToolPanel.RemoveControl(own);
        var colorGroup = new StackPanel { Spacing = 0 };
        colorGroup.Children.Add(own);
        colorGroup.Children.Add(colorHint);
        colorGroup.Children.Add(colorHost);
        ToolPanel.AddButtonRaw(colorGroup);
        ShowColorState();

        AddActions(
            ("reset to defaults", false, () =>
            {
                label.FontSize = 0; label.R = -1; label.G = -1; label.B = -1;
                label.Align = LabelAlign.Center;
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
                // rebuild so every field shows the defaults
                ShowLabelConfig(label);
            }),
            ("delete", true, () =>
            {
                if (_doc.Labels.Remove(label)) AfterDelete("label");
            }));

        // the same list position on the same cell, or any label on that cell
        SetReopen(() =>
        {
            var labels = _doc.Labels;
            var again = index >= 0 && index < labels.Count && labels[index].Cell == cell
                ? labels[index]
                : labels.FirstOrDefault(l => l.Cell == cell);
            if (again is not null) ShowLabelConfig(again);
            else BackToToolView();
        });
        _flushConfig = Done;
    }

    // ---------- pawn info ----------

    // a placed pawn: header like the pawn editor, status chips, a count, then
    // change and delete. the template itself is edited in pawn management
    private void ShowPawnInfo(Pawn pawn)
    {
        var cell = pawn.Cell;
        var t = ActiveLibrary.Find(pawn.TemplateId);
        ToolPanel.Clear("pawn");

        if (t is null)
        {
            ToolPanel.AddHint("broken reference: this pawn's template is missing from the library, it shows as an x on the map. change it to another pawn or delete it.");
        }
        else
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(PawnIcon(t, 40));
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            titles.Children.Add(new TextBlock { Text = PawnName(t), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Hx("#e6e6ee"), TextTrimming = TextTrimming.CharacterEllipsis });
            var aff = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            var affName = PawnTemplate.NormalizeAffiliation(t.Affiliation);
            if (ActivePresets.AffiliationColors.FirstOrDefault(a => a.Name == affName) is { } ac) aff.Children.Add(AffDot(ac, 10));
            aff.Children.Add(Txt(affName, 12, "#9a9aaa"));
            if (t.Hero) aff.Children.Add(Txt("hero", 10, "#9ab8ff"));
            titles.Children.Add(aff);
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);
            var star = StarButton(t.Favorite, () => { t.Favorite = !t.Favorite; PawnsChanged(); ShowPawnInfo(pawn); }, 16);
            star.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(star, 2);
            head.Children.Add(star);
            ToolPanel.AddButtonRaw(head);

            // the placed pawn's own status overrides its template's. inherit clears it
            var statuses = new List<string> { "inherit" };
            statuses.AddRange(ActivePresets.PawnStatuses.Select(s => s.Name));
            var dots = ActivePresets.PawnStatuses.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First());
            ToolPanel.AddChips(statuses.ToArray(), pawn.Status ?? "inherit", s =>
                {
                    var v = s == "inherit" ? null : s;
                    if (v == pawn.Status) return;
                    pawn.Status = v;
                    ActiveHistory.Commit(_doc);
                    Canvas.Refresh();
                    FocusCanvas();
                },
                "status",
                n => dots.TryGetValue(n, out var st)
                    ? new Ellipse { Width = 9, Height = 9, Fill = Rgb((st.R, st.G, st.B)) }
                    : null);

            int count = _doc.Pawns.Values.Count(p => p.TemplateId == pawn.TemplateId);
            ToolPanel.AddInfo($"{count} on this map");
        }

        AddActions(
            ("change pawn", false, () => ShowChangePawn(pawn)),
            ("delete", true, () =>
            {
                if (_doc.Pawns.TryGetValue(cell, out var p) && ReferenceEquals(p, pawn))
                {
                    _doc.RemovePawn(cell);
                    AfterDelete("pawn");
                }
            }));

        SetReopen(() =>
        {
            if (_doc.Pawns.TryGetValue(cell, out var p)) ShowPawnInfo(p);
            else BackToToolView();
        });
    }

    // ---------- change pawn ----------

    // swaps the template a placed pawn points at. it keeps its cell and status.
    // rows are grouped like the pawn tool panel, the current pawn is highlighted
    private void ShowChangePawn(Pawn pawn)
    {
        var cell = pawn.Cell;
        ToolPanel.Clear("change pawn");
        var search = SearchBox("search pawns", "");
        ToolPanel.AddButtonRaw(search);
        var list = new StackPanel { Spacing = 0 };
        ToolPanel.AddButtonRaw(list);

        void Pick(PawnTemplate t)
        {
            ToolPanel.ButtonHook?.Invoke();
            if (t.Id == pawn.TemplateId) { ShowPawnInfo(pawn); return; }
            pawn.TemplateId = t.Id;
            // a hero stays one of a kind, the copies elsewhere go
            bool moved = t.Hero && Canvas.TakeHeroCopies(cell, t.Id) is not null;
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
            PawnViewsChanged();
            RefreshMaps();
            StatusText.Text = moved ? $"pawn changed to hero {PawnName(t)}, its other copy was removed" : $"pawn changed to {PawnName(t)}";
            ShowPawnInfo(pawn);
            FocusCanvas();
        }

        Control Row(PawnTemplate t, AffiliationColor? ac)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto"), ColumnSpacing = 6 };
            g.Children.Add(PawnIcon(t, 16));
            var name = new TextBlock { Text = PawnName(t), FontSize = 12, Foreground = Hx("#e6e6ee"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            g.Children.Add(name);
            if (ac is not null)
            {
                var dot = AffDot(ac, 9);
                Grid.SetColumn(dot, 2);
                g.Children.Add(dot);
            }
            var row = new Border { Child = g, Classes = { "row" }, MinHeight = 26 };
            if (t.Id == pawn.TemplateId) row.Classes.Add("selected");
            ToolTip.SetTip(row, $"{PawnName(t)} · {PawnTemplate.NormalizeAffiliation(t.Affiliation)}");
            row.PointerPressed += (_, _) => Pick(t);
            return row;
        }

        void Group(string title, Control? dot, IEnumerable<PawnTemplate> pawns)
        {
            var items = pawns.ToList();
            if (items.Count == 0) return;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 6, 0, 3) };
            if (dot is not null) head.Children.Add(dot);
            head.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Hx("#e6e6ee"), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(Txt($"{items.Count}", 10, "#8a8a9a"));
            list.Children.Add(head);
            foreach (var t in items)
            {
                var aff = PawnTemplate.NormalizeAffiliation(t.Affiliation);
                list.Children.Add(Row(t, ActivePresets.AffiliationColors.FirstOrDefault(a => a.Name == aff)));
            }
        }

        void Build()
        {
            list.Children.Clear();
            var q = (search.Text ?? "").Trim();
            var shown = ActiveLibrary.Pawns
                .Where(t => q.Length == 0 || PawnName(t).Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => PawnName(t), StringComparer.OrdinalIgnoreCase).ToList();
            if (ActiveLibrary.Pawns.Count == 0) { list.Children.Add(Txt("no pawns in the library yet", 12, "#9a9aaa")); return; }
            if (shown.Count == 0) { list.Children.Add(Txt("no pawns match", 12, "#9a9aaa")); return; }
            Group("★ favorites", new Ellipse { Width = 8, Height = 8, Fill = Hx("#ffcc33") }, shown.Where(t => t.Favorite));
            var known = new HashSet<string>(ActivePresets.AffiliationColors.Select(a => a.Name));
            foreach (var ac in ActivePresets.AffiliationColors)
                Group(ac.Name, AffDot(ac, 12), shown.Where(t => PawnTemplate.NormalizeAffiliation(t.Affiliation) == ac.Name));
            Group("no affiliation", null, shown.Where(t => !known.Contains(PawnTemplate.NormalizeAffiliation(t.Affiliation))));
        }
        search.TextChanged += (_, _) => Build();
        Build();

        AddActions(("back", false, () => ShowPawnInfo(pawn)));

        SetReopen(() =>
        {
            if (_doc.Pawns.TryGetValue(cell, out var p)) ShowChangePawn(p);
            else BackToToolView();
        });
    }
}
