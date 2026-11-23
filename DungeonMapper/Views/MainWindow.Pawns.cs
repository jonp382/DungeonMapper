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

    // what the pawn panel, formations and icons work from: this campaign, or
    // the DM's package while joined (Player pawns, the shown map)
    private bool Joined => _client is not null;
    private PawnLibrary PanelLibrary => Joined ? Canvas.Library : ActiveLibrary;
    private CampaignPresets PanelPresets => Joined ? Canvas.Presets : ActivePresets;
    private MapDocument PanelDoc => Joined ? Canvas.Document : _doc;

    private static string PawnName(PawnTemplate t) => string.IsNullOrWhiteSpace(t.Name) ? "pawn" : t.Name;
    private static bool IsDefaultAffiliation(string n) => n is "Player" or "Ally" or "Enemy" or "Neutral";

    // the pawn as drawn on the map: outer circle (or its image), inner circle,
    // status ring. ownColors draws the template's stored colors instead of the
    // effective ones (the import picker shows a file's pawns before they exist).
    private Control PawnIcon(PawnTemplate t, double size, bool ownColors = false)
    {
        var (eo, ei) = ownColors && t.ColorOverride ? ((t.OuterR, t.OuterG, t.OuterB), (t.InnerR, t.InnerG, t.InnerB)) : t.Effective(PanelPresets);
        var st = PanelPresets.FindPawnStatus(t.Status);
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
        var cells = PanelDoc.Pawns.Values.Where(p => p.TemplateId == t.Id).Select(p => p.Cell)
            .OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
        if (cells.Count == 0 && Joined)
        {
            StatusText.Text = $"{PawnName(t)} is not on this map";
            _locateId = null;
            return;
        }
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
        top.Children.Add(_pawnSearchBox);
        if (!Joined)
        {
            var manage = Btn("manage");
            manage.Padding = new Thickness(6, 3);
            manage.Click += (_, _) => { CloseModals(); ShowPawnManagement(); };
            Grid.SetColumn(manage, 1);
            top.Children.Add(manage);
        }
        else ToolPanel.AddHint("your party's pawns. click one, then a revealed cell to place it; click a placed one to set its status. it goes on the DM's map for everyone.");
        ToolPanel.Target.Children.Add(top);

        _pawnChipHost = new WrapPanel();
        ToolPanel.Target.Children.Add(_pawnChipHost);

        var names = new CheckBox { Content = "names on map", Classes = { "toggle" }, IsChecked = _settings.ShowPawnNames, Padding = new Thickness(4, 0) };
        names.IsCheckedChanged += (_, _) =>
        {
            _settings.ShowPawnNames = names.IsChecked == true;
            MarkSetting();
            Canvas.Refresh();
            FocusCanvas();
        };
        ToolPanel.Target.Children.Add(names);

        AddFormationSection();

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
        // a player only has Player pawns, so no affiliation chips
        if (!Joined)
            foreach (var ac in ActivePresets.AffiliationColors)
            {
                var name = ac.Name;
                _pawnChipHost.Children.Add(Chip(name, _pawnFilter == name, AffDot(ac, 10), () => SetPawnFilter(name)));
            }

        _pawnRecentHost!.Children.Clear();
        var recent = Joined ? new List<PawnTemplate>() : ActiveLibrary.Recent.Select(ActiveLibrary.Find).Where(t => t is not null).Cast<PawnTemplate>().ToList();
        _pawnRecentHost.IsVisible = recent.Count > 0;
        if (recent.Count > 0)
        {
            _pawnRecentHost.Children.Add(Tx("recently placed", "colhead"));
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var t in recent)
            {
                var b = new Button { Content = PawnIcon(t, 22), Classes = { "recent" } };
                if (Canvas.ArmedPawn == t) b.Classes.Add("armed");
                ToolTip.SetTip(b, PawnName(t));
                b.Click += (_, _) => ArmPawn(t);
                strip.Children.Add(b);
            }
            _pawnRecentHost.Children.Add(strip);
        }
        RefreshFormations();
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
        var pool = Joined ? PanelLibrary.Pawns.Where(PlayerPawns.Controlled).ToList() : ActiveLibrary.Pawns;
        if (pool.Count == 0)
        {
            _pawnListHost.Children.Add(new TextBlock { Text = Joined ? "the DM has no player pawns yet." : "no pawns yet. use manage to add some.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
            return;
        }
        var counts = MapCounts(PanelDoc);
        var q = _pawnSearch.Trim();
        if (Joined && _pawnFilter != "all" && _pawnFilter != "on map") _pawnFilter = "all";
        var shown = pool.Where(t =>
                (q.Length == 0 || PawnName(t).Contains(q, StringComparison.OrdinalIgnoreCase))
                && (_pawnFilter == "all"
                    || (_pawnFilter == "on map" && Get(counts, t.Id) > 0)
                    || PawnTemplate.NormalizeAffiliation(t.Affiliation) == _pawnFilter))
            .OrderBy(t => PawnName(t), StringComparer.OrdinalIgnoreCase).ToList();
        if (shown.Count == 0)
        {
            _pawnListHost.Children.Add(Tx("no pawns match", "muted"));
            return;
        }

        var favs = shown.Where(t => t.Favorite).ToList();
        if (favs.Count > 0)
            AddPawnGroup("★ favorites", new Ellipse { Width = 8, Height = 8, Classes = { "fav" } }, $"{favs.Count}", favs, counts);

        var known = new HashSet<string>(PanelPresets.AffiliationColors.Select(a => a.Name));
        foreach (var ac in PanelPresets.AffiliationColors)
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
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("14,Auto,*,Auto"), ColumnSpacing = 4, Classes = { "grouphead" } };
        head.Children.Add(Tx(collapsed ? "▸" : "▾", "small"));
        var tsp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (dot is not null) tsp.Children.Add(dot);
        tsp.Children.Add(Tx(title, "body", "strong"));
        Grid.SetColumn(tsp, 1);
        head.Children.Add(tsp);
        var sum = Tx(summary, "colhead");
        sum.VerticalAlignment = VerticalAlignment.Center;
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
        var name = Tx(PawnName(t), "body");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        var star = StarButton(t.Favorite, () => { t.Favorite = !t.Favorite; RefreshPawnList(); RefreshManageModal(); }, 11);
        star.IsVisible = t.Favorite && !Joined;
        Grid.SetColumn(star, 2);
        g.Children.Add(star);
        var cnt = Tx($"×{onMap}", "count");
        if (onMap == 0) cnt.Classes.Add("zero");
        cnt.MinWidth = 26;
        Grid.SetColumn(cnt, 3);
        g.Children.Add(cnt);
        var loc = LocateButton("find on map", () => LocatePawn(t));
        Grid.SetColumn(loc, 4);
        g.Children.Add(loc);

        var row = new Border { Child = g, Classes = { "row", "compact" } };
        if (armed) row.Classes.Add("selected");
        ToolTip.SetTip(row, $"{PawnName(t)} · {PawnTemplate.NormalizeAffiliation(t.Affiliation)}");
        // the star shows on hover so it can be set without opening the manager
        row.PointerEntered += (_, _) => star.IsVisible = !Joined;
        row.PointerExited += (_, _) => star.IsVisible = t.Favorite && !Joined;
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
        _statusCombo = null;
        _statusComboTemplate = null;

        Control body = _manageTab switch
        {
            "affiliations" => PresetTab(BuildAffiliationsTab(), CampaignPresets.Part.Affiliations),
            "statuses" => PresetTab(BuildStatusesTab(), CampaignPresets.Part.Statuses),
            _ => BuildPawnsTab(),
        };
        ShowModal(760, 640, "Pawn management", null, body, tabbed: true);
    }

    // the modal frame lives in MainWindow.axaml. management shows the tabs and
    // close, the import pickers show a title and subtitle over their own body.
    private void InitModal()
    {
        ManageTabPawns.Click += (_, _) => { _manageTab = "pawns"; ShowPawnManagement(); };
        ManageTabAffiliations.Click += (_, _) => { _manageTab = "affiliations"; ShowPawnManagement(); };
        ManageTabStatuses.Click += (_, _) => { _manageTab = "statuses"; ShowPawnManagement(); };
        ModalClose.Click += (_, _) => CloseManage();
    }

    private void ShowModal(double width, double height, string title, string? subtitle, Control body, bool tabbed = false)
    {
        ModalOverlay.Width = width;
        ModalOverlay.Height = height;
        ModalTitle.Text = title;
        ModalSubtitle.Text = subtitle ?? "";
        ModalSubtitle.IsVisible = subtitle is not null;
        ModalTabs.IsVisible = tabbed;
        ModalClose.IsVisible = tabbed;
        ModalHeader.Classes.Set("tabbed", tabbed);
        Grid.SetColumnSpan(ModalTitleStack, tabbed ? 1 : 3);
        ManageTabPawns.Classes.Set("on", _manageTab == "pawns");
        ManageTabAffiliations.Classes.Set("on", _manageTab == "affiliations");
        ManageTabStatuses.Classes.Set("on", _manageTab == "statuses");
        ModalBody.Child = body;
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
        // objects ride along: their legend and menu counts follow the map too
        RefreshObjectPanel();
        RefreshObjectManager();
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
        AddCell(colHead, Tx("name", "colhead"), 2);
        AddCell(colHead, Tx("affiliation", "colhead"), 3);
        var hm = Tx("map", "colhead"); hm.HorizontalAlignment = HorizontalAlignment.Right; AddCell(colHead, hm, 4);
        var hc = Tx("campaign", "colhead"); hc.HorizontalAlignment = HorizontalAlignment.Right; AddCell(colHead, hc, 5);
        var headBorder = new Border { Child = colHead, Classes = { "colhead" }, Padding = new Thickness(6, 0, 6, 4), Margin = new Thickness(0) };
        DockPanel.SetDock(headBorder, Dock.Top);
        left.Children.Add(headBorder);

        // footer: import / export all / totals
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
        var imp = Btn("import…");
        imp.Click += (_, _) => ShowImportPicker();
        foot.Children.Add(imp);
        var expAll = Btn("export all");
        expAll.Click += (_, _) => ExportPawns(ActiveLibrary, "all pawns");
        Grid.SetColumn(expAll, 1);
        foot.Children.Add(expAll);
        _manageFooter = Tx("", "small");
        _manageFooter.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_manageFooter, 2);
        foot.Children.Add(_manageFooter);
        var footBorder = new Border { Child = foot, Classes = { "footer" } };
        DockPanel.SetDock(footBorder, Dock.Bottom);
        left.Children.Add(footBorder);

        _manageBulkBar = new Border { Classes = { "bulkbar" } };
        DockPanel.SetDock(_manageBulkBar, Dock.Bottom);
        left.Children.Add(_manageBulkBar);

        _manageListHost = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 0) };
        left.Children.Add(new ScrollViewer { Content = _manageListHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        grid.Children.Add(left);

        var divider = new Border { Classes = { "divider" } };
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
            {
            var none = Tx(ActiveLibrary.Pawns.Count == 0 ? "no pawns yet. add one or import a file." : "no pawns match", "muted");
            none.Margin = new Thickness(6);
            _manageListHost.Children.Add(none);
        }

        if (_manageAllCheck is not null)
            _manageAllCheck.IsChecked = shown.Count > 0 && shown.All(t => _manageSelected.Contains(t.Id));

        int bad = ActiveLibrary.Pawns.Count(IsInvalid);
        if (_manageFooter is not null)
            _manageFooter.Text = $"{ActiveLibrary.Pawns.Count} pawns" + (bad > 0 ? $" · {bad} need attention" : "");

        if (_manageBulkBar is not null)
        {
            _manageBulkBar.IsVisible = _manageSelected.Count > 0;
            var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 6 };
            bar.Children.Add(Tx($"{_manageSelected.Count} selected", "body"));
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
        var nameTb = Tx(PawnName(t), "body");
        nameTb.TextTrimming = TextTrimming.CharacterEllipsis;
        nameSp.Children.Add(nameTb);
        if (t.Favorite)
        {
            var star = Tx("★", "warn");
            star.FontSize = 10;
            nameSp.Children.Add(star);
        }
        if (t.Hero) nameSp.Children.Add(Tx("hero", "tag"));
        if (IsInvalid(t))
        {
            var warn = Tx("!", "body", "warn");
            warn.FontWeight = FontWeight.Bold;
            ToolTip.SetTip(warn, InvalidReason(t));
            nameSp.Children.Add(warn);
        }
        AddCell(g, nameSp, 2);
        var aff = Tx(PawnTemplate.NormalizeAffiliation(t.Affiliation), "dim");
        aff.TextTrimming = TextTrimming.CharacterEllipsis;
        AddCell(g, aff, 3);
        var m = Tx($"×{onMap}", "count"); m.Classes.Set("zero", onMap == 0); m.HorizontalAlignment = HorizontalAlignment.Right; AddCell(g, m, 4);
        var c = Tx($"×{inCampaign}", "count"); c.Classes.Set("zero", inCampaign == 0); c.HorizontalAlignment = HorizontalAlignment.Right; AddCell(g, c, 5);

        var row = new Border { Child = g, Classes = { "row", "dense" } };
        row.Classes.Set("selected", editing);
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
            _manageEditorHost.Child = Tx("select a pawn to edit it", "muted");
            return;
        }

        var sp = new StackPanel { Spacing = 2 };
        _manageEditorHost.Child = sp;

        // header: preview, name, affiliation, favorite
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 6) };
        var preview = new Border { Child = PawnIcon(t, 56) };
        head.Children.Add(preview);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var bigName = Tx(PawnName(t), "title");
        bigName.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(bigName);
        titles.Children.Add(Tx(PawnTemplate.NormalizeAffiliation(t.Affiliation), "muted"));
        AddCell(head, titles, 1);
        AddCell(head, StarButton(t.Favorite, () => { t.Favorite = !t.Favorite; PawnsChanged(); RefreshManageEditor(); }, 18), 2);
        sp.Children.Add(head);

        void Redraw()
        {
            preview.Child = PawnIcon(t, 56);
            PawnsChanged();
        }

        sp.Children.Add(FieldLabel("name"));
        var name = new TextBox { Text = t.Name, Classes = { "field" } };
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
        var hero = new CheckBox { Content = "hero (only one can exist, placing it again moves it)", Classes = { "toggle" }, IsChecked = t.Hero, Margin = new Thickness(0, 6, 0, 0) };
        var heroMsg = new TextBlock { Classes = { "refusal" }, IsVisible = false };
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
        var useAff = new CheckBox { Content = "use affiliation colors", Classes = { "toggle" }, IsChecked = !t.ColorOverride };
        sp.Children.Add(useAff);
        var (eo, ei) = t.Effective(ActivePresets);
        var colors = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 6 };
        colors.Children.Add(Tx("outer", "hint"));
        AddCell(colors, ColorButton(eo, c => { t.OuterR = c.r; t.OuterG = c.g; t.OuterB = c.b; Redraw(); }, t.ColorOverride), 1);
        AddCell(colors, Tx("inner", "hint"), 2);
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
                FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp" } } },
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
        var counts = new Border { Classes = { "infobox" } };
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
        var l = Tx(label, strong ? "body" : "muted");
        l.TextWrapping = TextWrapping.NoWrap;
        l.TextTrimming = TextTrimming.CharacterEllipsis;
        g.Children.Add(l);
        var v = Tx($"×{n}", "count");
        v.Classes.Set("zero", n == 0);
        v.FontSize = 12;
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
            Classes = { "muted" }, Margin = new Thickness(0, 0, 0, 6),
        });
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(AffCols), ColumnSpacing = 10 };
        string[] hs = { "look", "name", "outer", "inner", "pawns", "" };
        for (int i = 0; i < hs.Length; i++)
        {
            var h = Tx(hs[i], "colhead");
            if (i == 4) h.HorizontalAlignment = HorizontalAlignment.Right;
            AddCell(head, h, i);
        }
        sp.Children.Add(new Border { Child = head, Classes = { "colhead" }, Margin = new Thickness(0) });

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
        var name = new TextBox { Text = ac.Name, Classes = { "field" }, FontSize = 13, IsReadOnly = fixedName, VerticalAlignment = VerticalAlignment.Center, Opacity = fixedName ? 0.7 : 1 };
        name.TextChanged += (_, _) =>
        {
            var nn = (name.Text ?? "").Trim();
            bool ok = nn.Length > 0 && !IsDefaultAffiliation(nn) && !ActivePresets.AffiliationColors.Any(a => a != ac && a.Name == nn);
            name.Classes.Set("clash", !ok);
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
        var cnt = Tx($"{n}", "body");
        cnt.HorizontalAlignment = HorizontalAlignment.Right;
        AddCell(g, cnt, 4);
        if (!fixedName)
        {
            var del = Btn("delete", danger: true);
            del.Click += (_, _) => DeleteAffiliationWithWarning(ac, del);
            AddCell(g, del, 5);
        }
        return new Border { Child = g, Classes = { "row", "tall" } };
    }

    // ---------- status styles tab ----------

    private const string StatusCols = "40,*,140,90,70";

    private Control BuildStatusesTab()
    {
        var sp = new StackPanel { Spacing = 6, Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock
        {
            Text = "a status draws a colored ring around the pawn. thickness 0 hides the ring. \"none\" cannot be deleted.",
            Classes = { "muted" }, Margin = new Thickness(0, 0, 0, 6),
        });
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(StatusCols), ColumnSpacing = 10 };
        string[] hs = { "look", "name", "color", "thickness", "" };
        for (int i = 0; i < hs.Length; i++) AddCell(head, Tx(hs[i], "colhead"), i);
        sp.Children.Add(new Border { Child = head, Classes = { "colhead" }, Margin = new Thickness(0) });

        var rowsHost = new StackPanel { Spacing = 6 };
        var dupMarks = new List<(PawnStatus st, TextBox box)>();
        void UpdateDuplicates()
        {
            var counts = new Dictionary<string, int>();
            foreach (var (st, _) in dupMarks) counts[st.Name] = counts.TryGetValue(st.Name, out var k) ? k + 1 : 1;
            foreach (var (st, box) in dupMarks)
            {
                bool dup = counts[st.Name] > 1;
                box.Classes.Set("clash", dup);
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
                gg.Children.Add(new Ellipse { Width = 30, Height = 30, Classes = { "look" } });
                if (st.Thickness > 0) gg.Children.Add(new Ellipse { Width = 30, Height = 30, Stroke = Rgb((st.R, st.G, st.B)), StrokeThickness = Math.Min(st.Thickness, 6) });
                look.Child = gg;
            }
            Look();
            g.Children.Add(look);

            bool isNone = st.Name == "none";
            var name = new TextBox { Text = st.Name, Classes = { "field" }, FontSize = 13, IsReadOnly = isNone, VerticalAlignment = VerticalAlignment.Center, Opacity = isNone ? 0.7 : 1 };
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
            rowsHost.Children.Add(new Border { Child = g, Classes = { "row", "tall" } });
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
        var root = new DockPanel { Margin = new Thickness(12) };

        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 8) };
        var sbox = SearchBox("search", "");
        tools.Children.Add(sbox);
        var hide = new CheckBox { Content = "hide ones I already have", Classes = { "toggle" } };
        AddCell(tools, hide, 1);
        DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(tools);

        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(chips, Dock.Top);
        root.Children.Add(chips);

        Grid RowGrid() => new() { ColumnDefinitions = new ColumnDefinitions("22,22,*,78,130"), ColumnSpacing = 8 };
        var colHead = RowGrid();
        var all = new CheckBox { MinWidth = 0, Padding = new Thickness(0) };
        ToolTip.SetTip(all, "select all shown");
        colHead.Children.Add(all);
        AddCell(colHead, Tx("name", "colhead"), 2);
        AddCell(colHead, Tx("affiliation", "colhead"), 3);
        var headB = new Border { Child = colHead, Classes = { "colhead" }, Padding = new Thickness(6, 0, 6, 4), Margin = new Thickness(0) };
        DockPanel.SetDock(headB, Dock.Top);
        root.Children.Add(headB);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6 };
        var cancel = Btn("cancel");
        cancel.Background = Brushes.Transparent;
        buttons.Children.Add(cancel);
        var importAll = Btn($"import all ({incoming.Count})");
        AddCell(buttons, importAll, 2);
        var importSel = Btn("", primary: true);
        AddCell(buttons, importSel, 3);
        var btnBorder = new Border { Child = buttons, Classes = { "footer" }, Padding = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(btnBorder, Dock.Bottom);
        root.Children.Add(btnBorder);
        var note = new TextBlock
        {
            Text = "pawns you already have (same name and look) start unticked. a different look under the same name imports as a separate pawn.",
            Classes = { "small" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8),
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
                var nameTb = Tx(PawnName(t), "body");
                nameTb.Classes.Set("faint", have);
                nameTb.TextTrimming = TextTrimming.CharacterEllipsis;
                AddCell(g, nameTb, 2);
                AddCell(g, Tx(PawnTemplate.NormalizeAffiliation(t.Affiliation), "dim"), 3);
                var (nt, nc) = kind switch
                {
                    PawnLibrary.ImportKind.Have => ("already in library", "faint"),
                    PawnLibrary.ImportKind.NewLook => ("same name, new look", "warn"),
                    _ => ("new", "ok"),
                };
                var noteTb = Tx(nt, nc);
                noteTb.FontSize = 10;
                noteTb.HorizontalAlignment = HorizontalAlignment.Right;
                AddCell(g, noteTb, 4);
                var row = new Border { Child = g, Classes = { "row", "dense" } };
                row.Classes.Set("faded", have);
                row.PointerPressed += (_, _) => { if (!picked.Add(t)) picked.Remove(t); Rebuild(); };
                list.Children.Add(row);
            }
            if (shown.Count == 0)
            {
                var none = Tx("nothing matches", "muted");
                none.Margin = new Thickness(6);
                list.Children.Add(none);
            }
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

        ShowModal(620, 640, "Import pawns", $"from {System.IO.Path.GetFileName(path)} · {incoming.Count} pawns in file", root);
    }
}
