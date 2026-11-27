using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DungeonMapper.Controls;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// map objects: the object tool's side panel (legend), the view for one placed
// object, change type, the look editor shared with the objects menu, and the
// objects menu itself. laid out like the pawn panel and pawn management.
// see Agent.md "Object System Design".
public partial class MainWindow
{
    // ---------- object bits ----------

    private static string ObjectName(ObjectType t) => string.IsNullOrWhiteSpace(t.Name) ? "object" : t.Name;

    // rendered swatches, keyed by everything that changes how they look
    private readonly Dictionary<string, Avalonia.Media.Imaging.RenderTargetBitmap> _objectIconCache = new();

    // the look as drawn on the map, in a square of the given size
    private Control ObjectIcon(ObjectLook look, double size, string caption = "")
    {
        int px = (int)Math.Ceiling(size * 2);
        string key = $"{look.Fill}|{look.ColorA}|{look.ColorB}|{look.Direction}|{look.Border}|{look.BorderColor}|{look.PictureStretch}|{look.PictureBacking}|{look.PictureId}|{look.BorderWidth}|{caption}|{look.Caption}|{px}";
        if (!_objectIconCache.TryGetValue(key, out var bmp))
        {
            if (_objectIconCache.Count > 400) _objectIconCache.Clear();
            var shown = look.Clone();
            if (caption.Length == 0) shown.Caption = ObjectCaption.None;
            bmp = Canvas.ObjectSwatch(shown, caption, px, px);
            _objectIconCache[key] = bmp;
        }
        return new Image { Source = bmp, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
    }

    private Dictionary<string, int> ObjectMapCounts(MapDocument doc)
    {
        var d = new Dictionary<string, int>();
        foreach (var o in doc.Objects) d[o.TypeId] = Get(d, o.TypeId) + 1;
        return d;
    }

    private Dictionary<string, int> ObjectCampaignCounts()
    {
        var d = new Dictionary<string, int>();
        foreach (var m in _project.Maps)
            foreach (var o in m.Doc.Objects) d[o.TypeId] = Get(d, o.TypeId) + 1;
        return d;
    }

    private void ArmObject(ObjectType t)
    {
        Canvas.ArmedObject = t;
        SetStatus($"armed {ObjectName(t)} - drag on the map to place it");
        RefreshObjectPanel();
        FocusCanvas();
    }

    // each click centers the next placed one on this map, like pawns
    private string? _locateObjectType;
    private int _locateObjectIdx;
    private void LocateObject(ObjectType t)
    {
        var list = _doc.Objects.Where(o => o.TypeId == t.Id).OrderBy(o => o.Y).ThenBy(o => o.X).ToList();
        if (list.Count == 0)
        {
            var others = _project.Maps
                .Select(m => (m.Name, n: m.Doc.Objects.Count(o => o.TypeId == t.Id)))
                .Where(x => x.n > 0).Select(x => $"{x.Name} ({x.n})").ToList();
            SetStatus(others.Count == 0
                ? $"{ObjectName(t)} is not placed on any map"
                : $"{ObjectName(t)} is not on this map. placed on: {string.Join(", ", others)}", StatusKind.Warning);
            _locateObjectType = null;
            return;
        }
        _locateObjectIdx = _locateObjectType == t.Id ? (_locateObjectIdx + 1) % list.Count : 0;
        _locateObjectType = t.Id;
        var o = list[_locateObjectIdx];
        Canvas.CenterOn(new CellKey(o.X + (o.W - 1) / 2, o.Y + (o.H - 1) / 2));
        SetStatus($"{ObjectName(t)} {_locateObjectIdx + 1} / {list.Count} on this map");
    }

    // after objects or object types change: map, legend, open menu
    private void ObjectViewsChanged()
    {
        Canvas.Refresh();
        RefreshObjectPanel();
        RefreshObjectManager();
    }

    // ---------- side panel: the legend ----------

    private string _objectSearch = "";
    private string _objectFilter = "all"; // all, on map, favorites
    private readonly HashSet<string> _objectCollapsed = new();
    private StackPanel? _objectListHost;
    private WrapPanel? _objectChipHost;

    private void BuildObjectPanel()
    {
        ToolPanel.Clear("object tool");
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        var search = SearchBox("search objects", _objectSearch);
        search.TextChanged += (_, _) => { _objectSearch = search.Text ?? ""; RefreshObjectList(); };
        var manage = Btn("manage");
        manage.Padding = new Thickness(6, 3);
        manage.Click += (_, _) => { CloseModals(); ShowObjectManagement(); };
        Grid.SetColumn(manage, 1);
        top.Children.Add(search);
        top.Children.Add(manage);
        ToolPanel.Target.Children.Add(top);

        ToolPanel.AddHint("pick a type, then drag on the map to place it. click an object to configure it and drag its handles to resize. shift+drag places over another object.");

        _objectChipHost = new WrapPanel();
        ToolPanel.Target.Children.Add(_objectChipHost);
        _objectListHost = new StackPanel { Spacing = 1 };
        ToolPanel.Target.Children.Add(_objectListHost);
        RefreshObjectPanel();
    }

    // in place, only while the legend is showing
    private void RefreshObjectPanel()
    {
        if (_objectListHost is null || !ToolPanel.Target.Children.Contains(_objectListHost)) return;
        _objectChipHost!.Children.Clear();
        foreach (var f in new[] { "all", "on map", "favorites" })
        {
            var k = f;
            _objectChipHost.Children.Add(Chip(k, _objectFilter == k, null, () => { _objectFilter = k; RefreshObjectPanel(); }));
        }
        RefreshObjectList();
    }

    private void RefreshObjectList()
    {
        if (_objectListHost is null) return;
        _objectListHost.Children.Clear();
        if (ActiveObjects.Types.Count == 0)
        {
            _objectListHost.Children.Add(new TextBlock { Text = "no object types yet. use manage to add some.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
            return;
        }
        var counts = ObjectMapCounts(_doc);
        var q = _objectSearch.Trim();
        var shown = ActiveObjects.Types.Where(t =>
                (q.Length == 0 || ObjectName(t).Contains(q, StringComparison.OrdinalIgnoreCase))
                && (_objectFilter == "all"
                    || (_objectFilter == "on map" && Get(counts, t.Id) > 0)
                    || (_objectFilter == "favorites" && t.Favorite)))
            .OrderBy(t => ObjectName(t), StringComparer.OrdinalIgnoreCase).ToList();
        if (shown.Count == 0)
        {
            _objectListHost.Children.Add(Tx("no objects match", "muted"));
            return;
        }
        var favs = shown.Where(t => t.Favorite).ToList();
        if (favs.Count > 0)
            AddObjectGroup("★ favorites", new Ellipse { Width = 8, Height = 8, Classes = { "fav" } }, favs, counts);
        AddObjectGroup("objects", null, shown.Where(t => !t.Favorite).ToList(), counts);
    }

    private void AddObjectGroup(string title, Control? dot, List<ObjectType> list, Dictionary<string, int> counts)
    {
        if (list.Count == 0) return;
        bool collapsed = _objectCollapsed.Contains(title);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("14,Auto,*,Auto"), ColumnSpacing = 4, Classes = { "grouphead" } };
        head.Children.Add(Tx(collapsed ? "▸" : "▾", "small"));
        var tsp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (dot is not null) tsp.Children.Add(dot);
        tsp.Children.Add(Tx(title, "body", "strong"));
        Grid.SetColumn(tsp, 1);
        head.Children.Add(tsp);
        var sum = Tx($"{list.Count} · {list.Sum(t => Get(counts, t.Id))} on map", "colhead");
        sum.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(sum, 3);
        head.Children.Add(sum);
        head.PointerPressed += (_, _) =>
        {
            if (!_objectCollapsed.Add(title)) _objectCollapsed.Remove(title);
            RefreshObjectList();
        };
        _objectListHost!.Children.Add(head);
        if (collapsed) return;
        foreach (var t in list) _objectListHost.Children.Add(ObjectPanelRow(t, Get(counts, t.Id)));
    }

    private Control ObjectPanelRow(ObjectType t, int onMap)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto,Auto,Auto"), ColumnSpacing = 5 };
        g.Children.Add(ObjectIcon(t.Look, 16));
        var name = Tx(ObjectName(t), "body");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        AddCell(g, name, 1);
        var star = StarButton(t.Favorite, () => { t.Favorite = !t.Favorite; RefreshObjectList(); RefreshObjectManager(); }, 11);
        star.IsVisible = t.Favorite;
        AddCell(g, star, 2);
        var cnt = Tx($"×{onMap}", "count");
        if (onMap == 0) cnt.Classes.Add("zero");
        cnt.MinWidth = 26;
        AddCell(g, cnt, 3);
        AddCell(g, LocateButton("find on map", () => LocateObject(t)), 4);

        var row = new Border { Child = g, Classes = { "row", "compact" } };
        if (Canvas.ArmedObject == t) row.Classes.Add("selected");
        ToolTip.SetTip(row, ObjectName(t));
        row.PointerEntered += (_, _) => star.IsVisible = true;
        row.PointerExited += (_, _) => star.IsVisible = t.Favorite;
        row.PointerPressed += (_, _) => ArmObject(t);
        return row;
    }

    // ---------- one placed object ----------

    private void ShowObjectInfo(MapObject obj)
    {
        string id = obj.Id;
        Canvas.SelectedObjectId = id;
        Canvas.Refresh();
        var t = ActiveObjects.Find(obj.TypeId);
        ToolPanel.ClearItem($"object at {obj.X},{obj.Y}");

        var look = obj.EffectiveLook(ActiveObjects);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        var icon = new Border { Child = ObjectIcon(look, 40) };
        head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var bigName = new TextBlock { Text = obj.DisplayName(ActiveObjects) is { Length: > 0 } dn ? dn : "object", Classes = { "strong" }, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        titles.Children.Add(bigName);
        titles.Children.Add(Tx($"{(t is null ? "missing type" : ObjectName(t))} · {obj.W * 5}x{obj.H * 5} ft", "muted"));
        AddCell(head, titles, 1);
        ToolPanel.AddButtonRaw(head);

        if (t is null)
            ToolPanel.AddHint("broken reference: this object's type is missing from the library, it draws in a plain look. change its type or delete it.");

        // own name: shown instead of the type name. one undo step when the edit ends
        bool nameDirty = false;
        void CommitName()
        {
            if (!nameDirty) return;
            nameDirty = false;
            if (_doc.Objects.Contains(obj)) { ActiveHistory.Commit(_doc); ObjectViewsChanged(); }
        }
        var nameBox = ToolPanel.AddText("name", obj.Name ?? "", v =>
        {
            var n = string.IsNullOrWhiteSpace(v) ? null : v;
            if (n == obj.Name) return;
            obj.Name = n;
            nameDirty = true;
            bigName.Text = obj.DisplayName(ActiveObjects) is { Length: > 0 } d ? d : "object";
            Canvas.Refresh();
        }, onDone: CommitName);
        nameBox.PlaceholderText = t is null ? "object" : ObjectName(t);
        nameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; FocusCanvas(); } };
        ToolPanel.Target.Children.Add(new TextBlock { Text = "leave empty to show the type name", Classes = { "hint" } });

        ToolPanel.AddCheck("use the type's look", obj.Look is null, on =>
        {
            if (on == (obj.Look is null)) return;
            obj.Look = on ? null : (t?.Look ?? new ObjectLook()).Clone();
            ActiveHistory.Commit(_doc);
            ObjectViewsChanged();
            ShowObjectInfo(obj);
        }, "off gives this object its own look. type look changes then leave it alone.");

        if (obj.Look is { } own)
        {
            var host = new StackPanel { Spacing = 4 };
            ToolPanel.AddButtonRaw(host);
            BuildLookEditor(host, own, obj.DisplayName(ActiveObjects),
                changed: () => { icon.Child = ObjectIcon(own, 40); Canvas.Refresh(); },
                commit: () => { if (_doc.Objects.Contains(obj)) { ActiveHistory.Commit(_doc); ObjectViewsChanged(); } });
        }

        AddDmOnlyCheck(() => obj.DmOnly, v => obj.DmOnly = v);
        int count = _doc.Objects.Count(o => o.TypeId == obj.TypeId);
        ToolPanel.AddInfo(t is null ? "type missing" : $"{Plural(count, ObjectName(t))} on this map");

        AddActions(
            ("change type", false, () => ShowChangeObjectType(obj)),
            ("delete", true, () =>
            {
                if (_doc.Objects.Remove(obj))
                {
                    Canvas.SelectedObjectId = null;
                    AfterDelete("object");
                    ObjectViewsChanged();
                }
            }));

        SetReopen(() =>
        {
            if (_doc.FindObject(id) is { } again) ShowObjectInfo(again);
            else BackToToolView();
        });
        _flushConfig = CommitName;
    }

    // swaps the type a placed object points at. it keeps its place, size, name
    // and own look
    private void ShowChangeObjectType(MapObject obj)
    {
        string id = obj.Id;
        ToolPanel.ClearItem("change type");
        var search = SearchBox("search objects", "");
        ToolPanel.AddButtonRaw(search);
        var list = new StackPanel { Spacing = 1 };
        ToolPanel.AddButtonRaw(list);
        void Build()
        {
            list.Children.Clear();
            var q = (search.Text ?? "").Trim();
            var shown = ActiveObjects.Types
                .Where(t => q.Length == 0 || ObjectName(t).Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Favorite).ThenBy(t => ObjectName(t), StringComparer.OrdinalIgnoreCase).ToList();
            if (shown.Count == 0) list.Children.Add(Tx(ActiveObjects.Types.Count == 0 ? "no object types yet" : "no objects match", "muted"));
            foreach (var t in shown)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto"), ColumnSpacing = 5 };
                g.Children.Add(ObjectIcon(t.Look, 16));
                var n = Tx(ObjectName(t), "body");
                n.TextTrimming = TextTrimming.CharacterEllipsis;
                AddCell(g, n, 1);
                if (t.Favorite) { var s = Tx("★", "warn"); s.FontSize = 10; AddCell(g, s, 2); }
                var row = new Border { Child = g, Classes = { "row", "compact" } };
                if (t.Id == obj.TypeId) row.Classes.Add("selected");
                var pick = t;
                row.PointerPressed += (_, _) =>
                {
                    if (pick.Id != obj.TypeId && _doc.Objects.Contains(obj))
                    {
                        obj.TypeId = pick.Id;
                        ActiveHistory.Commit(_doc);
                        ObjectViewsChanged();
                        SetStatus($"changed to {ObjectName(pick)}");
                    }
                    ShowObjectInfo(obj);
                };
                list.Children.Add(row);
            }
        }
        search.TextChanged += (_, _) => Build();
        Build();
        AddActions(("back", false, () => ShowObjectInfo(obj)));
        SetReopen(() =>
        {
            if (_doc.FindObject(id) is { } again) ShowChangeObjectType(again);
            else BackToToolView();
        });
    }

    // ---------- look editor ----------

    private static readonly (ObjectFill fill, string label)[] FillChoices =
    [
        (ObjectFill.Solid, "solid"), (ObjectFill.Linear, "gradient"), (ObjectFill.Radial, "radial"),
        (ObjectFill.Stripes, "stripes"), (ObjectFill.Checker, "checker"), (ObjectFill.Outline, "outline"),
        (ObjectFill.Picture, "picture"),
    ];

    /// Fills host with the controls for one look. changed fires on every edit
    /// for a live preview, commit when an edit is finished (a chip or toggle
    /// click, a color picker closing, a picture chosen) so a placed object's
    /// edit is one undo step.
    private void BuildLookEditor(StackPanel host, ObjectLook look, string name, Action changed, Action commit)
    {
        void Rebuild() => BuildLookEditor(host, look, name, changed, commit);
        void Set(Action edit) { edit(); changed(); commit(); Rebuild(); }
        host.Children.Clear();

        host.Children.Add(FieldLabel("fill"));
        var fills = new WrapPanel();
        foreach (var (f, label) in FillChoices)
        {
            var ff = f;
            fills.Children.Add(Chip(label, look.Fill == ff, null, () => Set(() => look.Fill = ff)));
        }
        host.Children.Add(fills);

        // colors, named for what they do in this fill
        (string a, string? b) names = look.Fill switch
        {
            ObjectFill.Linear => ("start", "end"),
            ObjectFill.Radial => ("center", "edge"),
            ObjectFill.Stripes => ("stripes", "between"),
            ObjectFill.Checker => ("squares", "between"),
            ObjectFill.Picture => ("", null), // its backing color sits with the picture options
            ObjectFill.Outline => ("", null),
            _ => ("color", null),
        };
        if (names.a.Length > 0) host.Children.Add(LookColor(names.a, look.ColorA, v => look.ColorA = v, changed, commit));
        if (names.b is not null) host.Children.Add(LookColor(names.b, look.ColorB, v => look.ColorB = v, changed, commit));

        if (look.Fill == ObjectFill.Linear)
        {
            host.Children.Add(FieldLabel("direction"));
            var dirs = new WrapPanel();
            foreach (var d in Enum.GetValues<GradientDirection>())
            {
                var dd = d;
                dirs.Children.Add(Chip(d.ToString().ToLowerInvariant(), look.Direction == dd, null, () => Set(() => look.Direction = dd)));
            }
            host.Children.Add(dirs);
        }

        if (look.Fill == ObjectFill.Picture)
        {
            host.Children.Add(FieldLabel("picture"));
            var row = new WrapPanel();
            var choose = Btn(string.IsNullOrEmpty(look.PictureId) ? "choose picture…" : "replace…");
            choose.Margin = new Thickness(0, 0, 6, 4);
            choose.Click += async (_, _) =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp" } } },
                });
                if (files.Count == 0) return;
                try
                {
                    // the same 50x50 png as pawn pictures, whatever the object's size
                    var png = PawnImage.Compress(System.IO.File.ReadAllBytes(files[0].Path.LocalPath));
                    // stored once in the campaign's picture pool, the look keeps its key
                    Set(() => look.PictureId = ActiveObjects.AddPicture(Convert.ToBase64String(png)));
                    SetStatus($"picture compressed to 50x50 png ({png.Length / 1024} KB)");
                }
                catch (Exception ex) { SetStatus($"could not read picture: {ex.Message}", StatusKind.Error); }
            };
            row.Children.Add(choose);
            if (!string.IsNullOrEmpty(look.PictureId))
            {
                var rm = Btn("remove");
                rm.Margin = new Thickness(0, 0, 6, 4);
                rm.Click += (_, _) => Set(() => look.PictureId = "");
                row.Children.Add(rm);
            }
            host.Children.Add(row);
            var fit = new WrapPanel();
            fit.Children.Add(Chip("stretch", look.PictureStretch, null, () => Set(() => look.PictureStretch = true)));
            fit.Children.Add(Chip("keep square", !look.PictureStretch, null, () => Set(() => look.PictureStretch = false)));
            host.Children.Add(fit);
            host.Children.Add(FieldLabel("behind the picture"));
            var back = new WrapPanel();
            back.Children.Add(Chip("see through", !look.PictureBacking, null, () => Set(() => look.PictureBacking = false)));
            back.Children.Add(Chip("fill color", look.PictureBacking, null, () => Set(() => look.PictureBacking = true)));
            host.Children.Add(back);
            if (look.PictureBacking)
                host.Children.Add(LookColor("color", look.ColorA, v => look.ColorA = v, changed, commit));
        }

        host.Children.Add(FieldLabel(look.Fill == ObjectFill.Outline ? "outline" : "border"));
        if (look.Fill != ObjectFill.Outline)
        {
            var border = new CheckBox { Content = "show border", Classes = { "toggle" }, IsChecked = look.Border };
            border.IsCheckedChanged += (_, _) => { if (look.Border != (border.IsChecked == true)) Set(() => look.Border = border.IsChecked == true); };
            host.Children.Add(border);
        }
        if (look.Fill == ObjectFill.Outline || look.Border)
        {
            host.Children.Add(LookColor("color", look.BorderColor, v => look.BorderColor = v, changed, commit));
            host.Children.Add(BorderWidthRow(look, changed, commit));
        }

        host.Children.Add(FieldLabel("caption"));
        var caps = new WrapPanel();
        foreach (var c in Enum.GetValues<ObjectCaption>())
        {
            var cc = c;
            caps.Children.Add(Chip(c.ToString().ToLowerInvariant(), look.Caption == cc, null, () => Set(() => look.Caption = cc)));
        }
        host.Children.Add(caps);
        if (look.Caption != ObjectCaption.None && name.Length > 0)
            host.Children.Add(new TextBlock
            {
                Text = $"shows \"{(look.Caption == ObjectCaption.Name ? name : ObjectLook.Initials(name))}\"",
                Classes = { "hint" },
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
    }

    // thickness 1 to 10: applies on enter or when the box loses focus, a value
    // out of range or not a number is refused with a red line, like the
    // settings boxes
    private static Control BorderWidthRow(ObjectLook look, Action changed, Action commit)
    {
        var sp = new StackPanel { Spacing = 2 };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        g.Children.Add(Tx($"thickness, {ObjectLook.MinBorderWidth} to {ObjectLook.MaxBorderWidth}", "hint"));
        var box = new TextBox { Text = look.BorderWidth.ToString(), Classes = { "field", "number" } };
        AddCell(g, box, 1);
        sp.Children.Add(g);
        var refusal = RefusalLine();
        sp.Children.Add(refusal);
        void Apply()
        {
            var text = (box.Text ?? "").Trim();
            if (!int.TryParse(text, out int v) || v < ObjectLook.MinBorderWidth || v > ObjectLook.MaxBorderWidth)
            {
                box.Classes.Add("invalid");
                SetRefusal(refusal, $"use a whole number from {ObjectLook.MinBorderWidth} to {ObjectLook.MaxBorderWidth}.");
                return;
            }
            box.Classes.Remove("invalid");
            SetRefusal(refusal, null);
            box.Text = v.ToString();
            if (v == look.BorderWidth) return;
            look.BorderWidth = v;
            changed();
            commit();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { Apply(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { box.Text = look.BorderWidth.ToString(); box.Classes.Remove("invalid"); SetRefusal(refusal, null); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Apply();
        return sp;
    }

    // a hint label, then the shared color button. live while picking, commit
    // once when the picker closes
    private static Control LookColor(string label, string hex, Action<string> set, Action changed, Action commit)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        g.Children.Add(Tx(label, "hint"));
        bool dirty = false;
        var b = ColorButton(ObjectLook.Rgb(hex), c =>
        {
            var v = ObjectLook.Hex(c);
            set(v);
            dirty = true;
            changed();
        });
        if (b.Flyout is Flyout fly)
            fly.Closed += (_, _) => { if (dirty) { dirty = false; commit(); } };
        AddCell(g, b, 1);
        return g;
    }

    // ---------- objects menu ----------

    private string _objManageSearch = "";
    private string _objManageFilter = "all";
    private string? _objEditId;
    private StackPanel? _objManageList;
    private WrapPanel? _objManageChips;
    private Border? _objEditorHost;
    private TextBlock? _objManageFooter;

    private void ShowObjectManagement()
    {
        _modalKind = "objects";
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,300") };

        var left = new DockPanel { Margin = new Thickness(12, 12, 12, 10) };
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        var search = SearchBox($"search {ActiveObjects.Types.Count} objects", _objManageSearch);
        search.TextChanged += (_, _) => { _objManageSearch = search.Text ?? ""; RefreshObjectManager(); };
        tools.Children.Add(search);
        var add = Btn("+ add object", primary: true);
        add.Click += (_, _) =>
        {
            var t = ActiveObjects.Add(new ObjectType { Name = "new object" });
            _objEditId = t.Id;
            _objManageSearch = "";
            search.Text = "";
            ObjectViewsChanged();
            SetStatus("added new object type");
        };
        AddCell(tools, add, 1);
        DockPanel.SetDock(tools, Dock.Top);
        left.Children.Add(tools);

        _objManageChips = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_objManageChips, Dock.Top);
        left.Children.Add(_objManageChips);

        var colHead = ObjectRowGrid();
        AddCell(colHead, Tx("name", "colhead"), 1);
        var hm = Tx("map", "colhead"); hm.HorizontalAlignment = HorizontalAlignment.Right; AddCell(colHead, hm, 2);
        var hc = Tx("campaign", "colhead"); hc.HorizontalAlignment = HorizontalAlignment.Right; AddCell(colHead, hc, 3);
        var headBorder = new Border { Child = colHead, Classes = { "colhead" }, Padding = new Thickness(6, 0, 6, 4) };
        DockPanel.SetDock(headBorder, Dock.Top);
        left.Children.Add(headBorder);

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
        var imp = Btn("import…");
        imp.Click += (_, _) => ImportObjectTypes();
        foot.Children.Add(imp);
        var exp = Btn("export all");
        exp.Click += (_, _) => ExportObjectTypes();
        AddCell(foot, exp, 1);
        _objManageFooter = Tx("", "small");
        _objManageFooter.HorizontalAlignment = HorizontalAlignment.Right;
        AddCell(foot, _objManageFooter, 2);
        var footBorder = new Border { Child = foot, Classes = { "footer" } };
        DockPanel.SetDock(footBorder, Dock.Bottom);
        left.Children.Add(footBorder);

        _objManageList = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 0) };
        left.Children.Add(new ScrollViewer { Content = _objManageList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        grid.Children.Add(left);

        var divider = new Border { Classes = { "divider" } };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        _objEditorHost = new Border { Padding = new Thickness(14) };
        var editScroll = new ScrollViewer { Content = _objEditorHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(editScroll, 2);
        grid.Children.Add(editScroll);

        ShowModal(760, 640, "Object management", null, grid);
        _modalKind = "objects";
        // the frame only shows close for tabbed menus, this one needs it too
        ModalClose.IsVisible = true;
        Grid.SetColumnSpan(ModalTitleStack, 1);
        RefreshObjectManager();
    }

    private bool ObjectManagerOpen => ModalOverlay.IsVisible && _modalKind == "objects" && _objManageList is not null;

    private static Grid ObjectRowGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("22,*,44,60"),
        ColumnSpacing = 8,
    };

    private void RefreshObjectManager()
    {
        if (!ObjectManagerOpen) return;
        var camp = ObjectCampaignCounts();
        var map = ObjectMapCounts(_doc);

        _objManageChips!.Children.Clear();
        foreach (var f in new[] { "all", "favorites", "on map", "unused" })
        {
            var k = f;
            _objManageChips.Children.Add(Chip(k, _objManageFilter == k, null, () => { _objManageFilter = k; RefreshObjectManager(); }));
        }

        var q = _objManageSearch.Trim();
        var shown = ActiveObjects.Types.Where(t =>
                (q.Length == 0 || ObjectName(t).Contains(q, StringComparison.OrdinalIgnoreCase))
                && _objManageFilter switch
                {
                    "favorites" => t.Favorite,
                    "on map" => Get(map, t.Id) > 0,
                    "unused" => Get(camp, t.Id) == 0,
                    _ => true,
                })
            .OrderBy(t => ObjectName(t), StringComparer.OrdinalIgnoreCase).ToList();

        _objManageList!.Children.Clear();
        foreach (var t in shown)
        {
            var g = ObjectRowGrid();
            g.Children.Add(ObjectIcon(t.Look, 18));
            var nameSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var nameTb = Tx(ObjectName(t), "body");
            nameTb.TextTrimming = TextTrimming.CharacterEllipsis;
            nameSp.Children.Add(nameTb);
            if (t.Favorite) { var star = Tx("★", "warn"); star.FontSize = 10; nameSp.Children.Add(star); }
            AddCell(g, nameSp, 1);
            int onMap = Get(map, t.Id), inCamp = Get(camp, t.Id);
            var m = Tx($"×{onMap}", "count"); m.Classes.Set("zero", onMap == 0); m.HorizontalAlignment = HorizontalAlignment.Right; AddCell(g, m, 2);
            var c = Tx($"×{inCamp}", "count"); c.Classes.Set("zero", inCamp == 0); c.HorizontalAlignment = HorizontalAlignment.Right; AddCell(g, c, 3);
            var row = new Border { Child = g, Classes = { "row", "dense" } };
            row.Classes.Set("selected", _objEditId == t.Id);
            var pick = t;
            row.PointerPressed += (_, _) => { _objEditId = pick.Id; RefreshObjectManager(); };
            _objManageList.Children.Add(row);
        }
        if (shown.Count == 0)
        {
            var none = Tx(ActiveObjects.Types.Count == 0 ? "no object types yet. add one or import a file." : "no objects match", "muted");
            none.Margin = new Thickness(6);
            _objManageList.Children.Add(none);
        }
        if (_objManageFooter is not null) _objManageFooter.Text = Plural(ActiveObjects.Types.Count, "object type");
        RefreshObjectEditor();
    }

    private void RefreshObjectEditor()
    {
        if (_objEditorHost is null) return;
        var t = _objEditId is null ? null : ActiveObjects.Find(_objEditId);
        if (t is null)
        {
            _objEditId = null;
            _objEditorHost.Child = Tx("select an object type to edit it", "muted");
            return;
        }
        var sp = new StackPanel { Spacing = 2 };
        _objEditorHost.Child = sp;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 6) };
        var preview = new Border { Child = ObjectIcon(t.Look, 56, ObjectName(t)) };
        head.Children.Add(preview);
        var bigName = Tx(ObjectName(t), "title");
        bigName.TextTrimming = TextTrimming.CharacterEllipsis;
        AddCell(head, bigName, 1);
        AddCell(head, StarButton(t.Favorite, () => { t.Favorite = !t.Favorite; ObjectViewsChanged(); }, 18), 2);
        sp.Children.Add(head);

        sp.Children.Add(FieldLabel("name"));
        var name = new TextBox { Text = t.Name, Classes = { "field" } };
        name.TextChanged += (_, _) =>
        {
            if (t.Name == name.Text) return;
            t.Name = name.Text ?? "";
            bigName.Text = ObjectName(t);
            preview.Child = ObjectIcon(t.Look, 56, ObjectName(t));
            Canvas.Refresh();
            RefreshObjectPanel();
        };
        // the list re-sorts once the name is done, not on every key
        name.LostFocus += (_, _) => RefreshObjectManager();
        sp.Children.Add(name);

        sp.Children.Add(FieldLabel("default look"));
        sp.Children.Add(new TextBlock
        {
            Text = "objects of this type use this look unless they have their own.",
            Classes = { "hint" },
            TextWrapping = TextWrapping.Wrap,
        });
        var lookHost = new StackPanel { Spacing = 4 };
        sp.Children.Add(lookHost);
        BuildLookEditor(lookHost, t.Look, ObjectName(t),
            changed: () => { preview.Child = ObjectIcon(t.Look, 56, ObjectName(t)); Canvas.Refresh(); },
            commit: () => { RefreshObjectPanel(); RefreshObjectList(); });

        sp.Children.Add(FieldLabel("placed"));
        var perMap = _project.Maps.Select(m => (m.Name, n: m.Doc.Objects.Count(o => o.TypeId == t.Id))).ToList();
        var counts = new Border { Classes = { "infobox" } };
        var cg = new StackPanel { Spacing = 2 };
        cg.Children.Add(CountLine("this map", _doc.Objects.Count(o => o.TypeId == t.Id), true));
        cg.Children.Add(CountLine("campaign", perMap.Sum(x => x.n), true));
        foreach (var (mn, n) in perMap.Where(x => x.n > 0)) cg.Children.Add(CountLine("  " + mn, n, false));
        int ownLooks = _project.Maps.Sum(m => m.Doc.Objects.Count(o => o.TypeId == t.Id && o.Look is not null));
        if (ownLooks > 0) cg.Children.Add(Tx($"{ownLooks} with their own look", "muted"));
        counts.Child = cg;
        sp.Children.Add(counts);

        var acts = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var place = Btn("place", primary: true);
        place.Click += (_, _) => { CloseManage(); if (Canvas.Mode != ToolMode.Object) SetTool(ToolMode.Object); ArmObject(t); };
        var loc = Btn("locate");
        loc.IsEnabled = perMap.Any(x => x.n > 0);
        loc.Click += (_, _) => { CloseManage(); LocateObject(t); };
        var dup = Btn("duplicate");
        dup.Click += (_, _) =>
        {
            var c = t.Clone();
            c.Id = "";
            c.Name = ObjectName(t) + " copy";
            ActiveObjects.Add(c);
            _objEditId = c.Id;
            ObjectViewsChanged();
            SetStatus($"duplicated {ObjectName(t)}");
        };
        var del = Btn("delete", danger: true);
        int placed = perMap.Sum(x => x.n);
        del.Click += (_, _) =>
        {
            void doDelete()
            {
                ActiveObjects.Remove(t.Id);
                if (Canvas.ArmedObject == t) Canvas.ArmedObject = null;
                _objEditId = null;
                ObjectViewsChanged();
                SetStatus($"deleted {ObjectName(t)}");
            }
            if (placed == 0) doDelete();
            else ConfirmFlyout(del, $"{Plural(placed, "placed object")} in the campaign use this type. they stay on the map in a plain look and can be given another type with change type. delete?", doDelete);
        };
        foreach (var b in new[] { place, loc, dup, del }) { b.Margin = new Thickness(0, 0, 6, 6); acts.Children.Add(b); }
        sp.Children.Add(acts);
    }

    // ---------- import / export ----------

    private async void ExportObjectTypes()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export object types",
            SuggestedFileName = "object types.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;
        try
        {
            System.IO.File.WriteAllText(file.Path.LocalPath, MapSerializer.ObjectTypesToJson(ActiveObjects));
            SetStatus($"exported {Plural(ActiveObjects.Types.Count, "object type")}", StatusKind.Success);
        }
        catch (Exception ex) { SetStatus($"export failed: {ex.Message}", StatusKind.Error); }
    }

    // takes the object types of an export, a campaign or a map file. types
    // already here (same name and look) are skipped
    private async void ImportObjectTypes()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import object types",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;
        try
        {
            var text = System.IO.File.ReadAllText(files[0].Path.LocalPath);
            var dto = System.Text.Json.JsonSerializer.Deserialize(text, DmJson.Default.MapExportDto);
            ActiveObjects.AddPictures(dto?.ObjectPictures);
            var types = MapSerializer.TidyObjectTypes(dto?.ObjectLibrary);
            ActiveObjects.AdoptLegacy(types.Select(t => t.Look));
            int before = ActiveObjects.Types.Count;
            ActiveObjects.MergeAppend(types);
            int added = ActiveObjects.Types.Count - before;
            ObjectViewsChanged();
            SetStatus(types.Count == 0 ? "no object types in that file" : $"imported {Plural(added, "object type")}, {types.Count - added} already here", types.Count == 0 ? StatusKind.Warning : StatusKind.Success);
        }
        catch (Exception ex) { SetStatus($"import failed: {ex.Message}", StatusKind.Error); }
    }
}
