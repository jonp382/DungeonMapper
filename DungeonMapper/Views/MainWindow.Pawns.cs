using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DungeonMapper.Controls;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// pawn ui: the pawn tool side panel, the pawn management modal (pawns,
// affiliations and status styles tabs) and the import picker.
public partial class MainWindow
{
    // ---------- pawn bits (the shared builders live in Ui.cs) ----------

    private static string PawnName(PawnTemplate t) => string.IsNullOrWhiteSpace(t.Name) ? "pawn" : t.Name;
    private static bool IsDefaultAffiliation(string n) => n is "Player" or "Ally" or "Enemy" or "Neutral";

    // the pawn as drawn on the map: outer circle (or its image), inner circle,
    // status ring. ownColors draws the template's stored colors instead of the
    // effective ones (the import picker shows a file's pawns before they exist).
    private Control PawnIcon(PawnTemplate t, double size, bool ownColors = false)
    {
        var (eo, ei) = ownColors && t.ColorOverride ? ((t.OuterR, t.OuterG, t.OuterB), (t.InnerR, t.InnerG, t.InnerB)) : t.Effective(ActivePresets);
        var st = ActivePresets.FindPawnStatus(t.Status);
        var bmp = ownColors || string.IsNullOrEmpty(t.ImageB64) ? null : Canvas.LoadPawnImage(t.Id);
        IBrush outerFill = Rgb(eo);
        if (bmp is not null)
        {
            double side = Math.Min(bmp.PixelSize.Width, bmp.PixelSize.Height);
            outerFill = new ImageBrush
            {
                Source = bmp,
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                SourceRect = new RelativeRect((bmp.PixelSize.Width - side) / (2.0 * bmp.PixelSize.Width),
                                              (bmp.PixelSize.Height - side) / (2.0 * bmp.PixelSize.Height),
                                              side / bmp.PixelSize.Width, side / bmp.PixelSize.Height,
                                              RelativeUnit.Relative),
            };
        }
        var g = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(new Ellipse { Width = size, Height = size, Fill = outerFill });
        if (bmp is null)
            g.Children.Add(new Ellipse { Width = size / 2, Height = size / 2, Fill = Rgb(ei), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (st.Thickness > 0)
            g.Children.Add(new Ellipse { Width = size, Height = size, Stroke = Rgb((st.R, st.G, st.B)), StrokeThickness = Math.Min(st.Thickness, size / 6) });
        return g;
    }

    // per-template counts, computed once per refresh (cheap for 100+ pawns)
    private Dictionary<string, int> MapCounts(MapDocument doc)
    {
        var d = new Dictionary<string, int>();
        foreach (var p in doc.Pawns.Values) d[p.TemplateId] = d.TryGetValue(p.TemplateId, out var n) ? n + 1 : 1;
        return d;
    }

    private Dictionary<string, int> CampaignCounts()
    {
        var d = new Dictionary<string, int>();
        foreach (var m in _project.Maps)
            foreach (var p in m.Doc.Pawns.Values) d[p.TemplateId] = d.TryGetValue(p.TemplateId, out var n) ? n + 1 : 1;
        return d;
    }

    private static int Get(Dictionary<string, int> d, string id) => d.TryGetValue(id, out var n) ? n : 0;

    private void ArmPawn(PawnTemplate t)
    {
        Canvas.ArmedPawn = t;
        StatusText.Text = $"armed {PawnName(t)} - click a cell to place";
        RefreshPawnPanel();
        FocusCanvas();
    }

    // locate: each click centers the next placed copy on this map. when there
    // are none here, the status bar lists the maps that do have it.
    private string? _locateId;
    private int _locateIdx;
    private void LocatePawn(PawnTemplate t)
    {
        var cells = _doc.Pawns.Values.Where(p => p.TemplateId == t.Id).Select(p => p.Cell)
            .OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
        if (cells.Count == 0)
        {
            var others = _project.Maps
                .Select(m => (m.Name, n: m.Doc.Pawns.Values.Count(p => p.TemplateId == t.Id)))
                .Where(x => x.n > 0).Select(x => $"{x.Name} ({x.n})").ToList();
            StatusText.Text = others.Count == 0
                ? $"{PawnName(t)} is not placed on any map"
                : $"{PawnName(t)} is not on this map. placed on: {string.Join(", ", others)}";
            _locateId = null;
            return;
        }
        _locateIdx = _locateId == t.Id ? (_locateIdx + 1) % cells.Count : 0;
        _locateId = t.Id;
        Canvas.CenterOn(cells[_locateIdx]);
        StatusText.Text = $"{PawnName(t)} {_locateIdx + 1} / {cells.Count} on this map";
    }

    // ---------- side panel ----------

    private string _pawnSearch = "";
    private string _pawnFilter = "all"; // "all", "on map" or an affiliation name
    private readonly HashSet<string> _pawnCollapsed = new();
    private StackPanel? _pawnListHost;
    private WrapPanel? _pawnChipHost;
    private StackPanel? _pawnRecentHost;
    private TextBox? _pawnSearchBox;

    private void BuildPawnPanel()
    {
        ToolPanel.Clear("pawn");
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        _pawnSearchBox = SearchBox("search pawns (ctrl+f)", _pawnSearch);
        _pawnSearchBox.TextChanged += (_, _) => { _pawnSearch = _pawnSearchBox.Text ?? ""; RefreshPawnList(); };
        var manage = Btn("manage");
        manage.Padding = new Thickness(6, 3);
        manage.Click += (_, _) => { CloseModals(); ShowPawnManagement(); };
        Grid.SetColumn(manage, 1);
        top.Children.Add(_pawnSearchBox);
        top.Children.Add(manage);
        ToolPanel.Target.Children.Add(top);

        _pawnChipHost = new WrapPanel();
        ToolPanel.Target.Children.Add(_pawnChipHost);

        var names = new CheckBox { Content = Txt("names on map"), IsChecked = _settings.ShowPawnNames, Padding = new Thickness(4, 0) };
        names.IsCheckedChanged += (_, _) =>
        {
            _settings.ShowPawnNames = names.IsChecked == true;
            MarkSetting();
            Canvas.Refresh();
            FocusCanvas();
        };
        ToolPanel.Target.Children.Add(names);

        _pawnRecentHost = new StackPanel { Spacing = 3 };
        ToolPanel.Target.Children.Add(_pawnRecentHost);

        _pawnListHost = new StackPanel { Spacing = 1 };
        ToolPanel.Target.Children.Add(_pawnListHost);
        RefreshPawnPanel();
    }

    // refreshes chips, recents and the list in place. does nothing when the side
    // panel is showing something else (placed pawn info, change pawn).
    private void RefreshPawnPanel()
    {
        if (_pawnListHost is null || !ToolPanel.Target.Children.Contains(_pawnListHost)) return;

        _pawnChipHost!.Children.Clear();
        _pawnChipHost.Children.Add(Chip("all", _pawnFilter == "all", null, () => SetPawnFilter("all")));
        _pawnChipHost.Children.Add(Chip("on map", _pawnFilter == "on map", null, () => SetPawnFilter("on map")));
        foreach (var ac in ActivePresets.AffiliationColors)
        {
            var name = ac.Name;
            _pawnChipHost.Children.Add(Chip(name, _pawnFilter == name, AffDot(ac, 10), () => SetPawnFilter(name)));
        }

        _pawnRecentHost!.Children.Clear();
        var recent = ActiveLibrary.Recent.Select(ActiveLibrary.Find).Where(t => t is not null).Cast<PawnTemplate>().ToList();
        _pawnRecentHost.IsVisible = recent.Count > 0;
        if (recent.Count > 0)
        {
            _pawnRecentHost.Children.Add(Txt("recently placed", 10, "#8a8a9a"));
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var t in recent)
            {
                var armed = Canvas.ArmedPawn == t;
                var b = new Button
                {
                    Content = PawnIcon(t, 22),
                    Width = 30, Height = 30,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = Hx(armed ? "#34406a" : "#262632"),
                    BorderBrush = Hx(armed ? "#5a7fd0" : "#444450"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                };
                ToolTip.SetTip(b, PawnName(t));
                b.Click += (_, _) => ArmPawn(t);
                strip.Children.Add(b);
            }
            _pawnRecentHost.Children.Add(strip);
        }
        RefreshPawnList();
    }

    private void SetPawnFilter(string f)
    {
        _pawnFilter = f;
        RefreshPawnPanel();
    }

    private void RefreshPawnList()
    {
        if (_pawnListHost is null) return;
        _pawnListHost.Children.Clear();
        if (ActiveLibrary.Pawns.Count == 0)
        {
            _pawnListHost.Children.Add(new TextBlock { Text = "no pawns yet. use manage to add some.", FontSize = 12, Foreground = Hx("#9a9aaa"), TextWrapping = TextWrapping.Wrap });
            return;
        }
        var counts = MapCounts(_doc);
        var q = _pawnSearch.Trim();
        var shown = ActiveLibrary.Pawns.Where(t =>
                (q.Length == 0 || PawnName(t).Contains(q, StringComparison.OrdinalIgnoreCase))
                && (_pawnFilter == "all"
                    || (_pawnFilter == "on map" && Get(counts, t.Id) > 0)
                    || PawnTemplate.NormalizeAffiliation(t.Affiliation) == _pawnFilter))
            .OrderBy(t => PawnName(t), StringComparer.OrdinalIgnoreCase).ToList();
        if (shown.Count == 0)
        {
            _pawnListHost.Children.Add(Txt("no pawns match", 12, "#9a9aaa"));
            return;
        }

        var favs = shown.Where(t => t.Favorite).ToList();
        if (favs.Count > 0)
            AddPawnGroup("★ favorites", new Ellipse { Width = 8, Height = 8, Fill = Hx("#ffcc33") }, $"{favs.Count}", favs, counts);

        var known = new HashSet<string>(ActivePresets.AffiliationColors.Select(a => a.Name));
        foreach (var ac in ActivePresets.AffiliationColors)
        {
            var list = shown.Where(t => PawnTemplate.NormalizeAffiliation(t.Affiliation) == ac.Name).ToList();
            if (list.Count == 0) continue;
            int onMap = list.Sum(t => Get(counts, t.Id));
            AddPawnGroup(ac.Name, AffDot(ac, 12), $"{list.Count} · {onMap} on map", list, counts);
        }
        var orphans = shown.Where(t => !known.Contains(PawnTemplate.NormalizeAffiliation(t.Affiliation))).ToList();
        if (orphans.Count > 0)
            AddPawnGroup("no affiliation", null, $"{orphans.Count}", orphans, counts);
    }

    private void AddPawnGroup(string title, Control? dot, string summary, List<PawnTemplate> list, Dictionary<string, int> counts)
    {
        bool collapsed = _pawnCollapsed.Contains(title);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("14,Auto,*,Auto"), ColumnSpacing = 4, Background = Hx("#00000000"), Margin = new Thickness(0, 6, 0, 2), Cursor = new Cursor(StandardCursorType.Hand) };
        head.Children.Add(Txt(collapsed ? "▸" : "▾", 11, "#8a8a9a"));
        var tsp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (dot is not null) tsp.Children.Add(dot);
        tsp.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Hx("#e6e6ee"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(tsp, 1);
        head.Children.Add(tsp);
        var sum = Txt(summary, 10, "#8a8a9a");
        Grid.SetColumn(sum, 3);
        head.Children.Add(sum);
        head.PointerPressed += (_, _) =>
        {
            if (!_pawnCollapsed.Add(title)) _pawnCollapsed.Remove(title);
            RefreshPawnList();
        };
        _pawnListHost!.Children.Add(head);
        if (collapsed) return;
        foreach (var t in list) _pawnListHost.Children.Add(PawnPanelRow(t, Get(counts, t.Id)));
    }

    private Control PawnPanelRow(PawnTemplate t, int onMap)
    {
        bool armed = Canvas.ArmedPawn == t;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto,Auto,Auto"), ColumnSpacing = 5 };
        g.Children.Add(PawnIcon(t, 16));
        var name = new TextBlock { Text = PawnName(t), FontSize = 12, Foreground = Hx("#e6e6ee"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        var star = StarButton(t.Favorite, () => { t.Favorite = !t.Favorite; RefreshPawnList(); RefreshManageModal(); }, 11);
        star.IsVisible = t.Favorite;
        Grid.SetColumn(star, 2);
        g.Children.Add(star);
        var cnt = Txt($"×{onMap}", 11, onMap > 0 ? "#c8c8d4" : "#8a8a98");
        cnt.MinWidth = 26;
        cnt.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(cnt, 3);
        g.Children.Add(cnt);
        var loc = LocateButton("find on map", () => LocatePawn(t));
        Grid.SetColumn(loc, 4);
        g.Children.Add(loc);

        var row = new Border
        {
            Child = g,
            Height = 24,
            Padding = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Background = Hx(armed ? "#34406a" : "#00000000"),
            BorderBrush = Hx(armed ? "#5a7fd0" : "#00000000"),
        };
        ToolTip.SetTip(row, $"{PawnName(t)} · {PawnTemplate.NormalizeAffiliation(t.Affiliation)}");
        // the star shows on hover so it can be set without opening the manager
        row.PointerEntered += (_, _) => { star.IsVisible = true; if (!armed) row.Background = Hx("#2c2c38"); };
        row.PointerExited += (_, _) => { star.IsVisible = t.Favorite; if (!armed) row.Background = Hx("#00000000"); };
        row.PointerPressed += (_, _) => ArmPawn(t);
        return row;
    }

    // ctrl+f: jump to the pawn search box
    private void FocusPawnSearch()
    {
        if (Canvas.Mode != ToolMode.Pawn) SetTool(ToolMode.Pawn);
        if (_pawnListHost is null || !ToolPanel.Target.Children.Contains(_pawnListHost)) BuildPawnPanel();
        _pawnSearchBox?.Focus();
        _pawnSearchBox?.SelectAll();
    }

    // ---------- management modal ----------

    private string? _modalKind; // "manage" or "import" while ModalOverlay is open
    private string _manageTab = "pawns";
    private string _manageSearch = "";
    private string _manageSort = "name";
    private string _manageFilter = "all";
    private readonly HashSet<string> _manageSelected = new();
    private string? _manageEditId;
    private StackPanel? _manageListHost;
    private StackPanel? _manageChipHost;
    private Border? _manageEditorHost;
    private Border? _manageBulkBar;
    private TextBlock? _manageFooter;
    private CheckBox? _manageAllCheck;

    private static readonly string[] ManageSorts = { "name", "affiliation", "on map", "campaign", "needs attention", "insertion" };

    private void ShowPawnManagement()
    {
        _modalKind = "manage";
        ModalOverlay.Width = 760;
        ModalOverlay.Height = 640;
        _statusCombo = null;
        _statusComboTemplate = null;

        var root = new DockPanel();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(12, 10, 12, 0) };
        var title = new TextBlock { Text = "Pawn management", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Hx("#e6e6ee"), Margin = new Thickness(0, 0, 16, 8), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        int col = 1;
        foreach (var (key, label) in new[] { ("pawns", "Pawns"), ("affiliations", "Affiliations"), ("statuses", "Status styles") })
        {
            var tab = Tab(label, _manageTab == key, () => { _manageTab = key; ShowPawnManagement(); });
            Grid.SetColumn(tab, col++);
            header.Children.Add(tab);
        }
        var close = Btn("close");
        close.Margin = new Thickness(0, 0, 0, 8);
        close.Click += (_, _) => CloseManage();
        Grid.SetColumn(close, 5);
        header.Children.Add(close);
        var headWrap = new Border { Child = header, BorderBrush = Hx("#444450"), BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(headWrap, Dock.Top);
        root.Children.Add(headWrap);

        Control body = _manageTab switch
        {
            "affiliations" => PresetTab(BuildAffiliationsTab(), CampaignPresets.Part.Affiliations),
            "statuses" => PresetTab(BuildStatusesTab(), CampaignPresets.Part.Statuses),
            _ => BuildPawnsTab(),
        };
        root.Children.Add(body);
        ModalOverlay.Child = root;
        ModalOverlay.IsVisible = true;
    }

    // the affiliations and status styles tabs with their import / export footer
    private Control PresetTab(Control tab, CampaignPresets.Part part)
    {
        var dock = new DockPanel();
        var foot = PresetTabFooter(part);
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);
        dock.Children.Add(tab);
        return dock;
    }

    private void CloseManage()
    {
        FlushSettings();
        ModalOverlay.IsVisible = false;
        _modalKind = null;
        _statusCombo = null;
        _statusComboTemplate = null;
        FocusCanvas();
    }

    // rebuilds the open management modal from the live data (after undo, map
    // switch, deletes, side panel favorites). keeps the tab, search and selection.
    private void RefreshManageModal()
    {
        if (!ModalOverlay.IsVisible || _modalKind != "manage") return;
        if (_manageTab == "pawns" && _manageListHost is not null) { RefreshManageList(); RefreshManageEditor(); }
        else ShowPawnManagement();
    }

    // after any edit to pawns: map, side panel and modal list stay in step
    private void PawnsChanged()
    {
        Canvas.Refresh();
        if (Canvas.Mode == ToolMode.Pawn) RefreshPawnPanel();
        if (_manageListHost is not null) RefreshManageList();
    }

    // after the map itself changes (undo, redo, switch, place): counts in the
    // side panel and the modal follow the active map
    private void PawnViewsChanged()
    {
        if (Canvas.Mode == ToolMode.Pawn) RefreshPawnPanel();
        RefreshManageModal();
    }

    private Control BuildPawnsTab()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,300") };

        // left: search, filters, list, bulk bar, footer
        var left = new DockPanel { Margin = new Thickness(12, 12, 12, 10) };
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        var search = SearchBox($"search {ActiveLibrary.Pawns.Count} pawns", _manageSearch);
        search.TextChanged += (_, _) => { _manageSearch = search.Text ?? ""; RefreshManageList(); };
        tools.Children.Add(search);
        var sort = new ComboBox { ItemsSource = ManageSorts.Select(s => "sort: " + s).ToArray(), FontSize = 12, MinWidth = 140, SelectedIndex = Math.Max(0, Array.IndexOf(ManageSorts, _manageSort)) };
        sort.SelectionChanged += (_, _) => { if (sort.SelectedIndex >= 0) { _manageSort = ManageSorts[sort.SelectedIndex]; RefreshManageList(); } };
        Grid.SetColumn(sort, 1);
        tools.Children.Add(sort);
        var add = Btn("+ add pawn", primary: true);
        add.Click += (_, _) =>
        {
            var t = new PawnTemplate { Name = "new pawn", Affiliation = "Neutral", Status = "none" };
            t.ApplyAffiliationDefaults();
            ActiveLibrary.Add(t);
            _manageEditId = t.Id;
            _manageSearch = "";
            search.Text = "";
            PawnsChanged();
            RefreshManageEditor();
            StatusText.Text = "added new pawn";
        };
        Grid.SetColumn(add, 2);
        tools.Children.Add(add);
        DockPanel.SetDock(tools, Dock.Top);
        left.Children.Add(tools);

        _manageChipHost = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_manageChipHost, Dock.Top);
        left.Children.Add(_manageChipHost);

        var colHead = ManageRowGrid();
        colHead.Margin = new Thickness(6, 0, 6, 4);
        _manageAllCheck = new CheckBox { MinWidth = 0, Padding = new Thickness(0) };
        ToolTip.SetTip(_manageAllCheck, "select all shown");
        _manageAllCheck.Click += (_, _) =>
        {
            var shown = ManageShown(CampaignCounts(), MapCounts(_doc));
            if (_manageAllCheck.IsChecked == true) foreach (var t in shown) _manageSelected.Add(t.Id);
            else foreach (var t in shown) _manageSelected.Remove(t.Id);
            RefreshManageList();
        };
        colHead.Children.Add(_manageAllCheck);
        AddCell(colHead, Txt("name", 10, "#8a8a9a"), 2);
        AddCell(colHead, Txt("affiliation", 10, "#8a8a9a"), 3);
        var hm = Txt("map", 10, "#8a8a9a"); hm.HorizontalAlignment = HorizontalAlignment.Right; AddCell(colHead, hm, 4);
        var hc = Txt("campaign", 10, "#8a8a9a"); hc.HorizontalAlignment = HorizontalAlignment.Right; AddCell(colHead, hc, 5);
        var headBorder = new Border { Child = colHead, BorderBrush = Hx("#33333e"), BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(headBorder, Dock.Top);
        left.Children.Add(headBorder);

        // footer: import / export all / totals
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        var imp = Btn("import…");
        imp.Click += (_, _) => ShowImportPicker();
        foot.Children.Add(imp);
        var expAll = Btn("export all");
        expAll.Click += (_, _) => ExportPawns(ActiveLibrary, "all pawns");
        Grid.SetColumn(expAll, 1);
        foot.Children.Add(expAll);
        _manageFooter = Txt("", 11, "#8a8a9a");
        _manageFooter.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_manageFooter, 2);
        foot.Children.Add(_manageFooter);
        var footBorder = new Border { Child = foot, BorderBrush = Hx("#33333e"), BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(footBorder, Dock.Bottom);
        left.Children.Add(footBorder);

        _manageBulkBar = new Border { Background = Hx("#2c2c38"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4), Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_manageBulkBar, Dock.Bottom);
        left.Children.Add(_manageBulkBar);

        _manageListHost = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 0) };
        left.Children.Add(new ScrollViewer { Content = _manageListHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        grid.Children.Add(left);

        var divider = new Border { Background = Hx("#444450") };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        _manageEditorHost = new Border { Padding = new Thickness(14) };
        var editScroll = new ScrollViewer { Content = _manageEditorHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(editScroll, 2);
        grid.Children.Add(editScroll);

        RefreshManageList();
        RefreshManageEditor();
        return grid;
    }

    private static Grid ManageRowGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("22,22,*,78,44,60"),
        ColumnSpacing = 8,
    };

    private static void AddCell(Grid g, Control c, int col)
    {
        Grid.SetColumn(c, col);
        g.Children.Add(c);
    }

    private List<PawnTemplate> ManageShown(Dictionary<string, int> camp, Dictionary<string, int> map)
    {
        var q = _manageSearch.Trim();
        var list = ActiveLibrary.Pawns.Where(t =>
            (q.Length == 0 || PawnName(t).Contains(q, StringComparison.OrdinalIgnoreCase))
            && _manageFilter switch
            {
                "all" => true,
                "favorites" => t.Favorite,
                "on map" => Get(map, t.Id) > 0,
                "unused" => Get(camp, t.Id) == 0,
                _ => PawnTemplate.NormalizeAffiliation(t.Affiliation) == _manageFilter,
            }).ToList();
        IOrderedEnumerable<PawnTemplate>? o = _manageSort switch
        {
            "name" => list.OrderBy(t => PawnName(t), StringComparer.OrdinalIgnoreCase),
            "affiliation" => list.OrderBy(t => PawnTemplate.NormalizeAffiliation(t.Affiliation), StringComparer.OrdinalIgnoreCase).ThenBy(t => PawnName(t), StringComparer.OrdinalIgnoreCase),
            "on map" => list.OrderByDescending(t => Get(map, t.Id)).ThenBy(t => PawnName(t), StringComparer.OrdinalIgnoreCase),
            "campaign" => list.OrderByDescending(t => Get(camp, t.Id)).ThenBy(t => PawnName(t), StringComparer.OrdinalIgnoreCase),
            "needs attention" => list.OrderByDescending(t => IsInvalid(t)).ThenBy(t => PawnName(t), StringComparer.OrdinalIgnoreCase),
            _ => null,
        };
        return o?.ToList() ?? list;
    }

    private void RefreshManageList()
    {
        if (_manageListHost is null || _manageChipHost is null) return;
        var camp = CampaignCounts();
        var map = MapCounts(_doc);
        _manageSelected.RemoveWhere(id => ActiveLibrary.Find(id) is null);

        // view filters on the first line, affiliations on their own line so the
        // row never wraps a single chip however many affiliations there are
        _manageChipHost.Children.Clear();
        var views = new WrapPanel();
        var affs = new WrapPanel();
        _manageChipHost.Children.Add(views);
        _manageChipHost.Children.Add(affs);
        foreach (var f in new[] { "all", "favorites", "on map", "unused" })
            views.Children.Add(Chip(f, _manageFilter == f, null, () => { _manageFilter = f; RefreshManageList(); }));
        foreach (var ac in ActivePresets.AffiliationColors)
        {
            var n = ac.Name;
            affs.Children.Add(Chip(n, _manageFilter == n, AffDot(ac, 10), () => { _manageFilter = n; RefreshManageList(); }));
        }

        var shown = ManageShown(camp, map);
        _manageListHost.Children.Clear();
        foreach (var t in shown) _manageListHost.Children.Add(ManageRow(t, Get(map, t.Id), Get(camp, t.Id)));
        if (shown.Count == 0)
            _manageListHost.Children.Add(new TextBlock { Text = ActiveLibrary.Pawns.Count == 0 ? "no pawns yet. add one or import a file." : "no pawns match", FontSize = 12, Foreground = Hx("#9a9aaa"), Margin = new Thickness(6) });

        if (_manageAllCheck is not null)
            _manageAllCheck.IsChecked = shown.Count > 0 && shown.All(t => _manageSelected.Contains(t.Id));

        int bad = ActiveLibrary.Pawns.Count(IsInvalid);
        if (_manageFooter is not null)
            _manageFooter.Text = $"{ActiveLibrary.Pawns.Count} pawns" + (bad > 0 ? $" · {bad} need attention" : "");

        if (_manageBulkBar is not null)
        {
            _manageBulkBar.IsVisible = _manageSelected.Count > 0;
            var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 6 };
            bar.Children.Add(Txt($"{_manageSelected.Count} selected", 12));
            var exp = Btn("export selected");
            exp.Click += (_, _) => ExportPawns(ActiveLibrary.Subset(_manageSelected), $"{_manageSelected.Count} pawns");
            AddCell(bar, exp, 1);
            var del = Btn("delete selected", danger: true);
            del.Click += (_, _) => DeleteSelectedPawns(del);
            AddCell(bar, del, 2);
            var clr = Btn("clear");
            clr.Click += (_, _) => { _manageSelected.Clear(); RefreshManageList(); };
            AddCell(bar, clr, 3);
            _manageBulkBar.Child = bar;
        }
    }

    private Control ManageRow(PawnTemplate t, int onMap, int inCampaign)
    {
        bool editing = _manageEditId == t.Id;
        var g = ManageRowGrid();
        var check = new CheckBox { IsChecked = _manageSelected.Contains(t.Id), MinWidth = 0, Padding = new Thickness(0) };
        check.Click += (_, _) =>
        {
            if (check.IsChecked == true) _manageSelected.Add(t.Id); else _manageSelected.Remove(t.Id);
            RefreshManageList();
        };
        g.Children.Add(check);
        AddCell(g, PawnIcon(t, 18), 1);
        var nameSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        nameSp.Children.Add(new TextBlock { Text = PawnName(t), FontSize = 12, Foreground = Hx("#e6e6ee"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (t.Favorite) nameSp.Children.Add(Txt("★", 10, "#ffcc33"));
        if (t.Hero) nameSp.Children.Add(Txt("hero", 9, "#9ab8ff"));
        if (IsInvalid(t))
        {
            var warn = new TextBlock { Text = "!", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Hx("#ffcc33"), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(warn, InvalidReason(t));
            nameSp.Children.Add(warn);
        }
        AddCell(g, nameSp, 2);
        AddCell(g, new TextBlock { Text = PawnTemplate.NormalizeAffiliation(t.Affiliation), FontSize = 11, Foreground = Hx("#b0b0be"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 3);
        var m = Txt($"×{onMap}", 11, onMap > 0 ? "#c8c8d4" : "#8a8a98"); m.HorizontalAlignment = HorizontalAlignment.Right; AddCell(g, m, 4);
        var c = Txt($"×{inCampaign}", 11, inCampaign > 0 ? "#c8c8d4" : "#8a8a98"); c.HorizontalAlignment = HorizontalAlignment.Right; AddCell(g, c, 5);

        var row = new Border
        {
            Child = g,
            Height = 27,
            Padding = new Thickness(6, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = Hx(editing ? "#34406a" : "#262632"),
            BorderBrush = Hx(editing ? "#5a7fd0" : "#00000000"),
        };
        row.PointerEntered += (_, _) => { if (!editing) row.Background = Hx("#2c2c38"); };
        row.PointerExited += (_, _) => { if (!editing) row.Background = Hx("#262632"); };
        row.PointerPressed += (_, _) =>
        {
            _manageEditId = t.Id;
            RefreshManageList();
            RefreshManageEditor();
        };
        return row;
    }

    private string InvalidReason(PawnTemplate t)
    {
        var aff = PawnTemplate.NormalizeAffiliation(t.Affiliation);
        if (!ActivePresets.AffiliationColors.Any(a => a.Name == aff)) return $"affiliation \"{aff}\" no longer exists";
        return "another pawn has the same name and affiliation";
    }

    private void DeleteSelectedPawns(Control anchor)
    {
        var ids = _manageSelected.ToList();
        var camp = CampaignCounts();
        int placed = ids.Sum(id => Get(camp, id));
        void doDelete()
        {
            foreach (var id in ids)
            {
                if (Canvas.ArmedPawn?.Id == id) Canvas.ArmedPawn = null;
                ActiveLibrary.Remove(id);
            }
            if (_manageEditId is not null && ids.Contains(_manageEditId)) _manageEditId = null;
            _manageSelected.Clear();
            PawnsChanged();
            RefreshManageEditor();
            StatusText.Text = $"deleted {ids.Count} pawns";
        }
        ConfirmFlyout(anchor, placed == 0
            ? $"delete {ids.Count} pawns from the library?"
            : $"delete {ids.Count} pawns? {placed} placed pawn(s) across the campaign will render as X (broken reference) and can be replaced with Change Pawn.", doDelete);
    }

    private void RefreshManageEditor()
    {
        if (_manageEditorHost is null) return;
        var t = _manageEditId is null ? null : ActiveLibrary.Find(_manageEditId);
        _statusCombo = null;
        _statusComboTemplate = null;
        if (t is null)
        {
            _manageEditId = null;
            _manageEditorHost.Child = new TextBlock { Text = "select a pawn to edit it", FontSize = 12, Foreground = Hx("#9a9aaa") };
            return;
        }

        var sp = new StackPanel { Spacing = 2 };
        _manageEditorHost.Child = sp;

        // header: preview, name, affiliation, favorite
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 6) };
        var preview = new Border { Child = PawnIcon(t, 56) };
        head.Children.Add(preview);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var bigName = new TextBlock { Text = PawnName(t), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Hx("#e6e6ee"), TextTrimming = TextTrimming.CharacterEllipsis };
        titles.Children.Add(bigName);
        titles.Children.Add(Txt(PawnTemplate.NormalizeAffiliation(t.Affiliation), 12, "#9a9aaa"));
        AddCell(head, titles, 1);
        AddCell(head, StarButton(t.Favorite, () => { t.Favorite = !t.Favorite; PawnsChanged(); RefreshManageEditor(); }, 18), 2);
        sp.Children.Add(head);

        void Redraw()
        {
            preview.Child = PawnIcon(t, 56);
            PawnsChanged();
        }

        sp.Children.Add(FieldLabel("name"));
        var name = new TextBox { Text = t.Name, FontSize = 12, Background = Hx("#14141a"), BorderBrush = Hx("#50505c") };
        name.TextChanged += (_, _) =>
        {
            if (t.Name == name.Text) return;
            t.Name = name.Text ?? "";
            bigName.Text = PawnName(t);
            PawnsChanged();
        };
        sp.Children.Add(name);

        var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        var affCol = new StackPanel();
        affCol.Children.Add(FieldLabel("affiliation"));
        var affNames = ActivePresets.AffiliationColors.Select(a => a.Name).ToList();
        var curAff = PawnTemplate.NormalizeAffiliation(t.Affiliation);
        if (!affNames.Contains(curAff)) affNames.Add(curAff);
        var aff = new ComboBox { ItemsSource = affNames, SelectedItem = curAff, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        aff.SelectionChanged += (_, _) =>
        {
            if (aff.SelectedItem is not string a || a == t.Affiliation) return;
            t.Affiliation = a;
            PawnsChanged();
            RefreshManageEditor();
        };
        affCol.Children.Add(aff);
        pair.Children.Add(affCol);
        var stCol = new StackPanel();
        stCol.Children.Add(FieldLabel("status"));
        var stNames = ActivePresets.PawnStatuses.Select(s => s.Name).ToArray();
        var status = new ComboBox { ItemsSource = stNames, SelectedItem = Array.IndexOf(stNames, t.Status) >= 0 ? t.Status : null, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        status.SelectionChanged += (_, _) =>
        {
            if (status.SelectedItem is not string s || s == t.Status) return;
            t.Status = s;
            Redraw();
        };
        _statusCombo = status;
        _statusComboTemplate = t;
        stCol.Children.Add(status);
        AddCell(pair, stCol, 1);
        sp.Children.Add(pair);

        // hero: one copy across the whole campaign. refused while more than one
        // copy is placed, since it could not say which one to keep
        var hero = new CheckBox { Content = Txt("hero (only one can exist, placing it again moves it)"), IsChecked = t.Hero, Margin = new Thickness(0, 6, 0, 0) };
        var heroMsg = new TextBlock { FontSize = 11, Foreground = Hx("#ffb0b0"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        hero.IsCheckedChanged += (_, _) =>
        {
            bool on = hero.IsChecked == true;
            if (on == t.Hero) return;
            if (on)
            {
                int placed = _project.Maps.Sum(m => m.Doc.Pawns.Values.Count(p => p.TemplateId == t.Id));
                if (placed > 1)
                {
                    hero.IsChecked = false;
                    heroMsg.Text = $"there are {placed} of this pawn in the campaign. a hero can only have one, so remove the extras first.";
                    heroMsg.IsVisible = true;
                    return;
                }
            }
            heroMsg.IsVisible = false;
            t.Hero = on;
            PawnsChanged();
        };
        sp.Children.Add(hero);
        sp.Children.Add(heroMsg);

        sp.Children.Add(FieldLabel("colors"));
        var useAff = new CheckBox { Content = Txt("use affiliation colors"), IsChecked = !t.ColorOverride };
        sp.Children.Add(useAff);
        var (eo, ei) = t.Effective(ActivePresets);
        var colors = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 6 };
        colors.Children.Add(Txt("outer", 11, "#9a9aaa"));
        AddCell(colors, ColorButton(eo, c => { t.OuterR = c.r; t.OuterG = c.g; t.OuterB = c.b; Redraw(); }, t.ColorOverride), 1);
        AddCell(colors, Txt("inner", 11, "#9a9aaa"), 2);
        AddCell(colors, ColorButton(ei, c => { t.InnerR = c.r; t.InnerG = c.g; t.InnerB = c.b; Redraw(); }, t.ColorOverride), 3);
        sp.Children.Add(colors);
        useAff.IsCheckedChanged += (_, _) =>
        {
            bool own = useAff.IsChecked != true;
            if (own == t.ColorOverride) return;
            if (own)
            {
                // start the pawn's own colors from what it looks like now
                var (o, i) = t.Effective(ActivePresets);
                t.OuterR = o.r; t.OuterG = o.g; t.OuterB = o.b;
                t.InnerR = i.r; t.InnerG = i.g; t.InnerB = i.b;
            }
            t.ColorOverride = own;
            PawnsChanged();
            RefreshManageEditor();
        };

        sp.Children.Add(FieldLabel("image"));
        var imgRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var choose = Btn(string.IsNullOrEmpty(t.ImageB64) ? "choose image…" : "replace image…");
        choose.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } } },
            });
            if (files.Count == 0) return;
            try
            {
                var png = PawnImage.Compress(System.IO.File.ReadAllBytes(files[0].Path.LocalPath));
                t.ImageB64 = Convert.ToBase64String(png);
                t.ImageExt = ".png";
                Canvas.InvalidateImageCache();
                PawnsChanged();
                RefreshManageEditor();
                StatusText.Text = $"image compressed to 50x50 png ({png.Length / 1024} KB)";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"could not read image: {ex.Message}";
            }
        };
        imgRow.Children.Add(choose);
        if (!string.IsNullOrEmpty(t.ImageB64))
        {
            var rm = Btn("remove");
            rm.Click += (_, _) =>
            {
                t.ImageB64 = "";
                t.ImageExt = "";
                Canvas.InvalidateImageCache();
                PawnsChanged();
                RefreshManageEditor();
            };
            imgRow.Children.Add(rm);
        }
        sp.Children.Add(imgRow);

        // placed counts: this map, campaign, then each map that has it
        sp.Children.Add(FieldLabel("placed"));
        var perMap = _project.Maps.Select(m => (m.Name, n: m.Doc.Pawns.Values.Count(p => p.TemplateId == t.Id))).ToList();
        int here = _doc.Pawns.Values.Count(p => p.TemplateId == t.Id);
        var counts = new Border { Background = Hx("#262632"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6) };
        var cg = new StackPanel { Spacing = 2 };
        cg.Children.Add(CountLine("this map", here, true));
        cg.Children.Add(CountLine("campaign", perMap.Sum(x => x.n), true));
        foreach (var (mn, n) in perMap.Where(x => x.n > 0))
            cg.Children.Add(CountLine("  " + mn, n, false));
        counts.Child = cg;
        sp.Children.Add(counts);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        var loc = Btn("locate");
        loc.IsEnabled = perMap.Any(x => x.n > 0);
        loc.Click += (_, _) => { CloseManage(); LocatePawn(t); };
        acts.Children.Add(loc);
        var dup = Btn("duplicate");
        dup.Click += (_, _) =>
        {
            var c = new PawnTemplate
            {
                Name = PawnName(t) + " copy",
                Affiliation = t.Affiliation,
                ColorOverride = t.ColorOverride,
                InnerR = t.InnerR, InnerG = t.InnerG, InnerB = t.InnerB,
                OuterR = t.OuterR, OuterG = t.OuterG, OuterB = t.OuterB,
                Status = t.Status,
                ImageB64 = t.ImageB64,
                ImageExt = t.ImageExt,
                Favorite = t.Favorite,
                Hero = t.Hero,
            };
            ActiveLibrary.Add(c);
            _manageEditId = c.Id;
            PawnsChanged();
            RefreshManageEditor();
            StatusText.Text = $"duplicated {PawnName(t)}";
        };
        acts.Children.Add(dup);
        var del = Btn("delete", danger: true);
        del.Click += (_, _) => DeleteTemplateWithWarning(t, del);
        acts.Children.Add(del);
        sp.Children.Add(acts);
    }

    private static Control CountLine(string label, int n, bool strong)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Hx(strong ? "#e6e6ee" : "#9a9aaa"), TextTrimming = TextTrimming.CharacterEllipsis });
        var v = Txt($"×{n}", 12, n > 0 ? "#c8c8d4" : "#8a8a98");
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    // ---------- affiliations tab ----------

    private const string AffCols = "40,*,120,120,70,70";

    private Control BuildAffiliationsTab()
    {
        var sp = new StackPanel { Spacing = 6, Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock
        {
            Text = "pawns that use affiliation colors take both rings from here. pawns with their own colors keep them. Player, Ally, Enemy and Neutral cannot be renamed or deleted.",
            FontSize = 12, Foreground = Hx("#9a9aaa"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(AffCols), ColumnSpacing = 10, Margin = new Thickness(8, 0, 8, 4) };
        string[] hs = { "look", "name", "outer", "inner", "pawns", "" };
        for (int i = 0; i < hs.Length; i++)
        {
            var h = Txt(hs[i], 10, "#8a8a9a");
            if (i == 4) h.HorizontalAlignment = HorizontalAlignment.Right;
            AddCell(head, h, i);
        }
        sp.Children.Add(new Border { Child = head, BorderBrush = Hx("#33333e"), BorderThickness = new Thickness(0, 0, 0, 1) });

        var rows = new StackPanel { Spacing = 6 };
        foreach (var ac in ActivePresets.AffiliationColors.ToList())
            rows.Children.Add(AffiliationRow(ac));
        sp.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var add = Btn("+ add affiliation");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 6, 0, 0);
        add.Click += (_, _) =>
        {
            var n = ActivePresets.AffiliationColors.Count;
            var name = $"Affiliation {n + 1}";
            while (ActivePresets.AffiliationColors.Any(a => a.Name == name)) { n++; name = $"Affiliation {n + 1}"; }
            ActivePresets.AffiliationColors.Add(new AffiliationColor(name, (180, 180, 180), (110, 110, 110)));
            PawnsChanged();
            ShowPawnManagement();
        };
        sp.Children.Add(add);
        return sp;
    }

    private Control AffiliationRow(AffiliationColor ac)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(AffCols), ColumnSpacing = 10 };
        var look = new Border { Child = AffDot(ac, 30) };
        g.Children.Add(look);
        void Changed()
        {
            look.Child = AffDot(ac, 30);
            PawnsChanged();
        }

        bool fixedName = IsDefaultAffiliation(ac.Name);
        var name = new TextBox { Text = ac.Name, FontSize = 13, IsReadOnly = fixedName, Background = Hx("#14141a"), BorderBrush = Hx("#50505c"), VerticalAlignment = VerticalAlignment.Center, Opacity = fixedName ? 0.7 : 1 };
        name.TextChanged += (_, _) =>
        {
            var nn = (name.Text ?? "").Trim();
            bool ok = nn.Length > 0 && !IsDefaultAffiliation(nn) && !ActivePresets.AffiliationColors.Any(a => a != ac && a.Name == nn);
            name.BorderBrush = Hx(ok ? "#50505c" : "#d05a5a");
            if (!ok || nn == ac.Name) return;
            // renaming carries the pawns along with it
            foreach (var t in ActiveLibrary.Pawns)
                if (PawnTemplate.NormalizeAffiliation(t.Affiliation) == ac.Name) t.Affiliation = nn;
            _pawnCollapsed.Remove(ac.Name);
            if (_pawnFilter == ac.Name) _pawnFilter = nn;
            if (_manageFilter == ac.Name) _manageFilter = nn;
            ac.Name = nn;
            PawnsChanged();
        };
        AddCell(g, name, 1);
        AddCell(g, ColorButton((ac.OuterR, ac.OuterG, ac.OuterB), c => { ac.OuterR = c.r; ac.OuterG = c.g; ac.OuterB = c.b; Changed(); }), 2);
        AddCell(g, ColorButton((ac.InnerR, ac.InnerG, ac.InnerB), c => { ac.InnerR = c.r; ac.InnerG = c.g; ac.InnerB = c.b; Changed(); }), 3);
        int n = ActiveLibrary.Pawns.Count(t => PawnTemplate.NormalizeAffiliation(t.Affiliation) == ac.Name);
        var cnt = Txt($"{n}", 12, "#c8c8d4");
        cnt.HorizontalAlignment = HorizontalAlignment.Right;
        AddCell(g, cnt, 4);
        if (!fixedName)
        {
            var del = Btn("delete", danger: true);
            del.Click += (_, _) => DeleteAffiliationWithWarning(ac, del);
            AddCell(g, del, 5);
        }
        return new Border { Child = g, Height = 44, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(4), Background = Hx("#262632") };
    }

    // ---------- status styles tab ----------

    private const string StatusCols = "40,*,140,90,70";

    private Control BuildStatusesTab()
    {
        var sp = new StackPanel { Spacing = 6, Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock
        {
            Text = "a status draws a colored ring around the pawn. thickness 0 hides the ring. \"none\" cannot be deleted.",
            FontSize = 12, Foreground = Hx("#9a9aaa"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(StatusCols), ColumnSpacing = 10, Margin = new Thickness(8, 0, 8, 4) };
        string[] hs = { "look", "name", "color", "thickness", "" };
        for (int i = 0; i < hs.Length; i++) AddCell(head, Txt(hs[i], 10, "#8a8a9a"), i);
        sp.Children.Add(new Border { Child = head, BorderBrush = Hx("#33333e"), BorderThickness = new Thickness(0, 0, 0, 1) });

        var rowsHost = new StackPanel { Spacing = 6 };
        var dupMarks = new List<(PawnStatus st, TextBox box)>();
        void UpdateDuplicates()
        {
            var counts = new Dictionary<string, int>();
            foreach (var (st, _) in dupMarks) counts[st.Name] = counts.TryGetValue(st.Name, out var k) ? k + 1 : 1;
            foreach (var (st, box) in dupMarks)
            {
                bool dup = counts[st.Name] > 1;
                box.BorderBrush = Hx(dup ? "#d05a5a" : "#50505c");
                ToolTip.SetTip(box, dup ? "another status has this name" : null);
            }
        }

        foreach (var st in ActivePresets.PawnStatuses.ToList())
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(StatusCols), ColumnSpacing = 10 };
            var look = new Border();
            void Look()
            {
                var gg = new Grid { Width = 30, Height = 30 };
                gg.Children.Add(new Ellipse { Width = 30, Height = 30, Fill = Hx("#8a8a98") });
                if (st.Thickness > 0) gg.Children.Add(new Ellipse { Width = 30, Height = 30, Stroke = Rgb((st.R, st.G, st.B)), StrokeThickness = Math.Min(st.Thickness, 6) });
                look.Child = gg;
            }
            Look();
            g.Children.Add(look);

            bool isNone = st.Name == "none";
            var name = new TextBox { Text = st.Name, FontSize = 13, IsReadOnly = isNone, Background = Hx("#14141a"), BorderBrush = Hx("#50505c"), VerticalAlignment = VerticalAlignment.Center, Opacity = isNone ? 0.7 : 1 };
            name.TextChanged += (_, _) =>
            {
                var old = st.Name;
                var nn = name.Text ?? "";
                if (nn == old) return;
                // templates reference statuses by name, so they follow the rename
                foreach (var t in ActiveLibrary.Pawns) if (t.Status == old) t.Status = nn;
                st.Name = nn;
                PawnsChanged();
                UpdateDuplicates();
            };
            AddCell(g, name, 1);
            AddCell(g, ColorButton((st.R, st.G, st.B), c => { st.R = c.r; st.G = c.g; st.B = c.b; Look(); PawnsChanged(); }), 2);
            var thick = new NumericUpDown { Value = st.Thickness, Minimum = 0, Maximum = 20, Increment = 1, FormatString = "0", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            thick.ValueChanged += (_, _) =>
            {
                st.Thickness = (int)Math.Max(0, thick.Value ?? 0);
                Look();
                PawnsChanged();
            };
            AddCell(g, thick, 3);
            if (!isNone)
            {
                var del = Btn("delete", danger: true);
                del.Click += (_, _) => DeleteStatusWithWarning(st, del);
                AddCell(g, del, 4);
            }
            dupMarks.Add((st, name));
            rowsHost.Children.Add(new Border { Child = g, Height = 44, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(4), Background = Hx("#262632") });
        }
        UpdateDuplicates();
        sp.Children.Add(new ScrollViewer { Content = rowsHost, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var add = Btn("+ add status style");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 6, 0, 0);
        add.Click += (_, _) =>
        {
            string nm = "new";
            int i = 1;
            while (ActivePresets.PawnStatuses.Any(s => s.Name == nm)) nm = "new " + i++;
            ActivePresets.PawnStatuses.Add(new PawnStatus(nm, 245, 220, 60, 2));
            ShowPawnManagement();
        };
        sp.Children.Add(add);
        return sp;
    }

    // ---------- import / export ----------

    private async void ExportPawns(PawnLibrary lib, string label)
    {
        if (lib.Pawns.Count == 0) { StatusText.Text = "no pawns to export"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"Pawns_{Stamp()}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;
        var path = file.Path.LocalPath;
        try
        {
            System.IO.File.WriteAllText(path, MapSerializer.LibraryToJson(lib));
            StatusText.Text = $"exported {label} ({lib.Pawns.Count}) to {path}";
        }
        catch (Exception ex) { StatusText.Text = $"export failed: {ex.Message}"; }
    }

    // import picker: reads a pawn library or campaign file and lets the user tick
    // which pawns to bring in. never overwrites; pawns already in the library
    // (same name and look) start unticked and are reused if imported anyway.
    private async void ShowImportPicker()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        List<PawnTemplate> incoming;
        try { incoming = MapSerializer.LibraryFromDtos(MapSerializer.LibraryFromJson(System.IO.File.ReadAllText(path))); }
        catch (Exception ex) { StatusText.Text = $"import failed: {ex.Message}"; return; }
        if (incoming.Count == 0) { StatusText.Text = "that file has no pawns in it"; return; }

        var kinds = incoming.ToDictionary(t => t, t => ActiveLibrary.Classify(t));
        var picked = new HashSet<PawnTemplate>(incoming.Where(t => kinds[t] != PawnLibrary.ImportKind.Have));
        string search = "", filter = "all";
        bool hideHave = false;

        _modalKind = "import";
        ModalOverlay.Width = 620;
        ModalOverlay.Height = 640;
        var root = new DockPanel { Margin = new Thickness(12) };

        var head = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Import pawns", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Hx("#e6e6ee") });
        head.Children.Add(new TextBlock { Text = $"from {System.IO.Path.GetFileName(path)} · {incoming.Count} pawns in file", FontSize = 12, Foreground = Hx("#9a9aaa"), TextTrimming = TextTrimming.CharacterEllipsis });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 8) };
        var sbox = SearchBox("search", "");
        tools.Children.Add(sbox);
        var hide = new CheckBox { Content = Txt("hide ones I already have", 12, "#c8c8d4") };
        AddCell(tools, hide, 1);
        DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(tools);

        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(chips, Dock.Top);
        root.Children.Add(chips);

        Grid RowGrid() => new() { ColumnDefinitions = new ColumnDefinitions("22,22,*,78,130"), ColumnSpacing = 8 };
        var colHead = RowGrid();
        colHead.Margin = new Thickness(6, 0, 6, 4);
        var all = new CheckBox { MinWidth = 0, Padding = new Thickness(0) };
        ToolTip.SetTip(all, "select all shown");
        colHead.Children.Add(all);
        AddCell(colHead, Txt("name", 10, "#8a8a9a"), 2);
        AddCell(colHead, Txt("affiliation", 10, "#8a8a9a"), 3);
        var headB = new Border { Child = colHead, BorderBrush = Hx("#33333e"), BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(headB, Dock.Top);
        root.Children.Add(headB);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6 };
        var cancel = Btn("cancel");
        cancel.Background = Hx("#00000000");
        buttons.Children.Add(cancel);
        var importAll = Btn($"import all ({incoming.Count})");
        AddCell(buttons, importAll, 2);
        var importSel = Btn("", primary: true);
        AddCell(buttons, importSel, 3);
        var btnBorder = new Border { Child = buttons, BorderBrush = Hx("#444450"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(btnBorder, Dock.Bottom);
        root.Children.Add(btnBorder);
        var note = new TextBlock
        {
            Text = "pawns you already have (same name and look) start unticked. a different look under the same name imports as a separate pawn.",
            FontSize = 11, Foreground = Hx("#8a8a9a"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8),
        };
        DockPanel.SetDock(note, Dock.Bottom);
        root.Children.Add(note);

        var list = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 0) };
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        List<PawnTemplate> Shown() => incoming.Where(t =>
                (search.Length == 0 || PawnName(t).Contains(search, StringComparison.OrdinalIgnoreCase))
                && (!hideHave || kinds[t] != PawnLibrary.ImportKind.Have)
                && (filter == "all" || PawnTemplate.NormalizeAffiliation(t.Affiliation) == filter))
            .OrderBy(t => PawnName(t), StringComparer.OrdinalIgnoreCase).ToList();

        void Rebuild()
        {
            chips.Children.Clear();
            chips.Children.Add(Chip("all", filter == "all", null, () => { filter = "all"; Rebuild(); }));
            var fileAffs = incoming.Select(t => PawnTemplate.NormalizeAffiliation(t.Affiliation)).Distinct().ToList();
            foreach (var ac in ActivePresets.AffiliationColors.Where(a => fileAffs.Contains(a.Name)))
            {
                var n = ac.Name;
                chips.Children.Add(Chip(n, filter == n, AffDot(ac, 10), () => { filter = n; Rebuild(); }));
            }
            foreach (var n in fileAffs.Where(a => !ActivePresets.AffiliationColors.Any(x => x.Name == a)))
            {
                var nn = n;
                chips.Children.Add(Chip(nn, filter == nn, null, () => { filter = nn; Rebuild(); }));
            }

            var shown = Shown();
            list.Children.Clear();
            foreach (var t in shown)
            {
                var kind = kinds[t];
                bool have = kind == PawnLibrary.ImportKind.Have;
                var g = RowGrid();
                var cb = new CheckBox { IsChecked = picked.Contains(t), MinWidth = 0, Padding = new Thickness(0) };
                cb.Click += (_, _) => { if (cb.IsChecked == true) picked.Add(t); else picked.Remove(t); Rebuild(); };
                g.Children.Add(cb);
                var icon = PawnIcon(t, 18, ownColors: true);
                icon.Opacity = have ? 0.45 : 1;
                AddCell(g, icon, 1);
                AddCell(g, new TextBlock { Text = PawnName(t), FontSize = 12, Foreground = Hx(have ? "#7a7a88" : "#e6e6ee"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 2);
                AddCell(g, Txt(PawnTemplate.NormalizeAffiliation(t.Affiliation), 11, "#b0b0be"), 3);
                var (nt, nc) = kind switch
                {
                    PawnLibrary.ImportKind.Have => ("already in library", "#7a7a88"),
                    PawnLibrary.ImportKind.NewLook => ("same name, new look", "#ffcc33"),
                    _ => ("new", "#8fd18f"),
                };
                var noteTb = Txt(nt, 10, nc);
                noteTb.HorizontalAlignment = HorizontalAlignment.Right;
                AddCell(g, noteTb, 4);
                var row = new Border { Child = g, Height = 27, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(4), Background = Hx(have ? "#00000000" : "#262632") };
                row.PointerPressed += (_, _) => { if (!picked.Add(t)) picked.Remove(t); Rebuild(); };
                list.Children.Add(row);
            }
            if (shown.Count == 0) list.Children.Add(new TextBlock { Text = "nothing matches", FontSize = 12, Foreground = Hx("#9a9aaa"), Margin = new Thickness(6) });
            all.IsChecked = shown.Count > 0 && shown.All(picked.Contains);
            importSel.Content = $"import selected ({picked.Count})";
            importSel.IsEnabled = picked.Count > 0;
        }

        void DoImport(List<PawnTemplate> chosen)
        {
            int before = ActiveLibrary.Pawns.Count;
            ActiveLibrary.MergeImport(chosen);
            int added = ActiveLibrary.Pawns.Count - before;
            Canvas.InvalidateImageCache();
            _manageTab = "pawns";
            ShowPawnManagement();
            PawnsChanged();
            StatusText.Text = added == chosen.Count
                ? $"imported {added} pawns"
                : $"imported {added} pawns ({chosen.Count - added} were already in the library)";
        }

        sbox.TextChanged += (_, _) => { search = (sbox.Text ?? "").Trim(); Rebuild(); };
        hide.IsCheckedChanged += (_, _) => { hideHave = hide.IsChecked == true; Rebuild(); };
        all.Click += (_, _) =>
        {
            foreach (var t in Shown()) { if (all.IsChecked == true) picked.Add(t); else picked.Remove(t); }
            Rebuild();
        };
        cancel.Click += (_, _) => ShowPawnManagement();
        importAll.Click += (_, _) => DoImport(incoming);
        importSel.Click += (_, _) => DoImport(incoming.Where(picked.Contains).ToList());
        Rebuild();

        ModalOverlay.Child = root;
        ModalOverlay.IsVisible = true;
    }
}
