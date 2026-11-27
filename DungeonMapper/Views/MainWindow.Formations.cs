using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DungeonMapper.Controls;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// formations in the pawn panel: capture from a map area, deploy (with a
// member checklist), rename, update from area, delete. the canvas side is
// Controls/MapCanvas.Formations.cs. see Agent.md "Formation Manager Design".
public partial class MainWindow
{
    private StackPanel? _formationHost;
    // the open formation in the list (shows its members and actions)
    private string? _formationOpen;
    // the formation whose name is being edited
    private string? _formationRenaming;
    // the last formation deployed, for the deploy hotkey
    private string? _lastFormationId;
    // unticked members per formation, by member index. session only
    private readonly Dictionary<string, HashSet<int>> _formationUnticked = new();
    private readonly Dictionary<string, string> _formationSearch = new();

    private HashSet<int> Unticked(Formation f)
    {
        if (!_formationUnticked.TryGetValue(f.Id, out var s)) _formationUnticked[f.Id] = s = new();
        s.RemoveWhere(i => i >= f.Members.Count);
        return s;
    }

    // the formations section near the top of the pawn panel
    private void AddFormationSection()
    {
        _formationHost = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 4) };
        ToolPanel.Target.Children.Add(_formationHost);
        RefreshFormations();
    }

    private void RefreshFormations()
    {
        if (_formationHost is null || !ToolPanel.Target.Children.Contains(_formationHost)) return;
        _formationHost.Children.Clear();
        var forms = PanelLibrary.Formations;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Classes = { "grouphead" } };
        head.Children.Add(Tx("formations", "body", "strong"));
        var add = Btn("+ new");
        add.Padding = new Thickness(6, 2);
        ToolTip.SetTip(add, "drag a box around pawns on the map to save them as a formation");
        add.Click += (_, _) => CaptureFormation(null);
        AddCell(head, add, 1);
        _formationHost.Children.Add(head);

        if (forms.Count == 0)
        {
            _formationHost.Children.Add(new TextBlock
            {
                Text = "no formations yet. use + new and drag a box around your party on the map.",
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var f in forms) _formationHost.Children.Add(FormationRow(f));
    }

    private Control FormationRow(Formation f)
    {
        bool open = _formationOpen == f.Id;
        var sp = new StackPanel { Spacing = 2 };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*,Auto,Auto"), ColumnSpacing = 4 };
        g.Children.Add(Tx(open ? "▾" : "▸", "small"));
        int ticked = f.Members.Count - Unticked(f).Count;
        Control nameCtl;
        if (_formationRenaming == f.Id)
        {
            var box = new TextBox { Text = f.Name, Classes = { "field" } };
            bool done = false;
            void Finish(bool keep)
            {
                if (done) return;
                done = true;
                var n = (box.Text ?? "").Trim();
                if (keep && n.Length > 0 && n != f.Name) { f.Name = n; SendFormation(f); SetStatus($"formation renamed to {n}"); }
                _formationRenaming = null;
                RefreshFormations();
                FocusCanvas();
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
                else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
            };
            box.LostFocus += (_, _) => Finish(true);
            box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
            nameCtl = box;
        }
        else
        {
            var name = Tx(f.Name.Length > 0 ? f.Name : "formation", "body");
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            nameCtl = name;
        }
        AddCell(g, nameCtl, 1);
        var cnt = Tx(ticked == f.Members.Count ? $"{f.Members.Count}" : $"{ticked}/{f.Members.Count}", "count");
        cnt.Classes.Set("zero", ticked == 0);
        ToolTip.SetTip(cnt, "members that deploy");
        AddCell(g, cnt, 2);
        var deploy = Btn("deploy", primary: true);
        deploy.Padding = new Thickness(6, 2);
        deploy.Click += (_, _) => DeployFormation(f);
        AddCell(g, deploy, 3);
        var row = new Border { Child = g, Classes = { "row", "compact" } };
        row.Classes.Set("selected", open);
        row.PointerPressed += (_, e) =>
        {
            if (_formationRenaming == f.Id) return;
            if (e.ClickCount >= 2) { _formationRenaming = f.Id; _formationOpen = f.Id; RefreshFormations(); return; }
            _formationOpen = open ? null : f.Id;
            RefreshFormations();
        };
        sp.Children.Add(row);
        if (open) sp.Children.Add(FormationDetails(f));
        return sp;
    }

    // the member checklist and the actions of the open formation
    private Control FormationDetails(Formation f)
    {
        var sp = new StackPanel { Spacing = 3, Margin = new Thickness(14, 2, 0, 6) };
        var off = Unticked(f);
        void Changed() => RefreshFormations();

        // quick picks
        var chips = new WrapPanel();
        chips.Children.Add(Chip("all", off.Count == 0, null, () => { off.Clear(); Changed(); }));
        chips.Children.Add(Chip("none", off.Count == f.Members.Count, null, () => { for (int i = 0; i < f.Members.Count; i++) off.Add(i); Changed(); }));
        chips.Children.Add(Chip("heroes", false, null, () =>
        {
            off.Clear();
            for (int i = 0; i < f.Members.Count; i++)
                if (PanelLibrary.Find(f.Members[i].TemplateId)?.Hero != true) off.Add(i);
            Changed();
        }));
        sp.Children.Add(chips);

        // skip by status: unticks every member that has it (a member with
        // several statuses is skipped by any of them). only statuses members
        // have are listed
        var statuses = Enumerable.Range(0, f.Members.Count)
            .SelectMany(i => MemberStatuses(f.Members[i].TemplateId).Select(st => (i, st))).ToList();
        if (statuses.Count > 0)
        {
            var skip = new WrapPanel();
            var dots = PanelPresets.PawnStatuses.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First());
            foreach (var name in statuses.Select(x => x.st).Distinct())
            {
                var n = name;
                skip.Children.Add(Chip($"skip {n}", false,
                    dots.TryGetValue(n, out var ps) ? new Ellipse { Width = 9, Height = 9, Fill = Rgb((ps.R, ps.G, ps.B)) } : null,
                    () => { foreach (var x in statuses.Where(x => x.st == n)) off.Add(x.i); Changed(); }));
            }
            sp.Children.Add(skip);
        }

        // a search box once the list gets long
        string q = _formationSearch.GetValueOrDefault(f.Id, "");
        var list = new StackPanel { Spacing = 0 };
        if (f.Members.Count > 10)
        {
            var search = SearchBox("search members", q);
            search.TextChanged += (_, _) => { _formationSearch[f.Id] = search.Text ?? ""; BuildMemberList(f, list, search.Text ?? ""); };
            sp.Children.Add(search);
        }
        BuildMemberList(f, list, q);
        sp.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        var acts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var rename = Btn("rename");
        rename.Click += (_, _) => { _formationRenaming = f.Id; RefreshFormations(); };
        var update = Btn("update from area");
        ToolTip.SetTip(update, "drag a box on the map to replace this formation's members");
        update.Click += (_, _) => CaptureFormation(f);
        var del = Btn("delete", danger: true);
        del.Click += (_, _) => ConfirmFlyout(del, $"delete the formation {f.Name}? the pawns on the map are not touched.", () =>
        {
            PanelLibrary.Formations.Remove(f);
            if (_client is { } cl) { var fid = f.Id; _ = System.Threading.Tasks.Task.Run(() => cl.SendFormationDelete(fid)); }
            _formationUnticked.Remove(f.Id);
            if (_lastFormationId == f.Id) _lastFormationId = null;
            RefreshFormations();
            SetStatus($"deleted formation {f.Name}");
        });
        foreach (var b in new[] { rename, update, del }) { b.Padding = new Thickness(6, 2); b.Margin = new Thickness(0, 0, 4, 4); acts.Children.Add(b); }
        sp.Children.Add(acts);
        return sp;
    }

    // members grouped by affiliation, each group with a tick for all of it
    private void BuildMemberList(Formation f, StackPanel list, string query)
    {
        list.Children.Clear();
        var off = Unticked(f);
        var q = query.Trim();
        var shown = Enumerable.Range(0, f.Members.Count)
            .Select(i => (i, t: PanelLibrary.Find(f.Members[i].TemplateId)))
            .Where(x => q.Length == 0 || (x.t is null ? "missing pawn" : PawnName(x.t)).Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (shown.Count == 0) { list.Children.Add(Tx("no members match", "muted")); return; }
        var groups = shown.GroupBy(x => x.t is null ? "missing" : PawnTemplate.NormalizeAffiliation(x.t.Affiliation));
        bool many = shown.Select(x => x.t?.Affiliation).Distinct().Count() > 1;
        foreach (var grp in groups)
        {
            var members = grp.ToList();
            if (many)
            {
                int on = members.Count(x => !off.Contains(x.i));
                var gh = new CheckBox { IsChecked = on == members.Count ? true : on == 0 ? false : null, Classes = { "toggle" }, IsThreeState = false };
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                if (PanelPresets.AffiliationColors.FirstOrDefault(a => a.Name == grp.Key) is { } ac) label.Children.Add(AffDot(ac, 10));
                label.Children.Add(Tx(grp.Key, "body", "strong"));
                label.Children.Add(Tx($"{on} of {members.Count}", "colhead"));
                gh.Content = label;
                gh.Click += (_, _) =>
                {
                    bool all = on < members.Count;
                    foreach (var x in members) { if (all) off.Remove(x.i); else off.Add(x.i); }
                    RefreshFormations();
                };
                list.Children.Add(gh);
            }
            foreach (var (i, t) in members)
            {
                var cb = new CheckBox { IsChecked = !off.Contains(i), Classes = { "toggle" }, Margin = new Thickness(many ? 14 : 0, 0, 0, 0) };
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                if (t is not null) label.Children.Add(PawnIcon(t, 14));
                var n = Tx(t is null ? "missing pawn" : PawnName(t), t is null ? "muted" : "body");
                n.TextTrimming = TextTrimming.CharacterEllipsis;
                label.Children.Add(n);
                if (t?.Hero == true) label.Children.Add(Tx("hero", "tag"));
                cb.Content = label;
                int idx = i;
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (cb.IsChecked == true) off.Remove(idx); else off.Add(idx);
                    RefreshFormations();
                };
                list.Children.Add(cb);
            }
        }
    }

    // what a member has now: a placed hero's statuses (they move with the
    // hero); other members deploy as new pawns with none
    private IEnumerable<string> MemberStatuses(string templateId)
    {
        var t = PanelLibrary.Find(templateId);
        if (t is null || !t.Hero) return Enumerable.Empty<string>();
        foreach (var d in Joined ? new[] { PanelDoc } : _project.Maps.Select(m => m.Doc))
            foreach (var p in d.Pawns.Values)
                if (p.TemplateId == templateId) return p.Statuses.Select(s => s.Name).ToList();
        return Enumerable.Empty<string>();
    }

    // new (target null) or update: the next drag on the map picks the pawns
    private void CaptureFormation(Formation? target)
    {
        if (Canvas.Mode != ToolMode.Pawn) SetTool(ToolMode.Pawn);
        SetStatus(target is null
            ? "new formation: drag a box around the pawns on the map. esc cancels"
            : $"update {target.Name}: drag a box around the pawns on the map. esc cancels");
        Canvas.BeginAreaPick((x0, y0, x1, y1) => FormationFromArea(target, x0, y0, x1, y1));
    }

    /// saves the pawns in an area as a new formation (target null) or as
    /// target's members (the area pick, the region's right-click menu)
    private void FormationFromArea(Formation? target, int x0, int y0, int x1, int y1)
    {
        {
            var pawns = PanelDoc.Pawns.Values.Where(p => p.Cell.X >= x0 && p.Cell.X <= x1 && p.Cell.Y >= y0 && p.Cell.Y <= y1)
                .Where(p => !Joined || PlayerPawns.Controlled(PanelLibrary.Find(p.TemplateId))).ToList();
            if (pawns.Count == 0) { SetStatus(Joined ? "no player pawns in that area, nothing saved" : "no pawns in that area, nothing saved", StatusKind.Warning); return; }
            var members = Formation.Capture(pawns);
            if (target is null)
            {
                var f = new Formation { Id = Formation.NewId(), Name = Formation.UniqueName(PanelLibrary.Formations), Members = members };
                PanelLibrary.Formations.Add(f);
                SendFormation(f);
                _formationOpen = f.Id;
                _formationRenaming = f.Id; // name it right away
                SetStatus($"saved a formation of {Plural(members.Count, "pawn")}. type a name, enter keeps it");
            }
            else if (PanelLibrary.Formations.Contains(target))
            {
                target.Members = members;
                SendFormation(target);
                _formationUnticked.Remove(target.Id);
                _formationOpen = target.Id;
                SetStatus($"updated {target.Name}: {Plural(members.Count, "pawn")}");
            }
            if (Canvas.Mode == ToolMode.Pawn) RefreshPawnPanel();
        }
    }

    private void DeployFormation(Formation f)
    {
        var off = Unticked(f);
        var members = f.Members.Select((m, i) => (m, i))
            .Where(x => !off.Contains(x.i) && PanelLibrary.Find(x.m.TemplateId) is not null)
            .Select(x => (x.m.TemplateId, x.m.Dx, x.m.Dy)).ToList();
        int missing = f.Members.Count(m => PanelLibrary.Find(m.TemplateId) is null);
        if (members.Count == 0)
        {
            SetStatus(missing == f.Members.Count ? $"{f.Name} has no pawns left in the library" : $"no members of {f.Name} are ticked", StatusKind.Warning);
            return;
        }
        if (Canvas.Mode != ToolMode.Pawn) SetTool(ToolMode.Pawn);
        _lastFormationId = f.Id;
        Canvas.ArmFormation(f.Name, members);
        if (missing > 0) SetStatus(StatusText.Text + $" ({missing} missing from the library, skipped)", StatusKind.Warning);
        FocusCanvas();
    }

    // while joined a formation change goes to the DM, who keeps it in the campaign
    private void SendFormation(Formation f)
    {
        if (_client is not { } cl) return;
        var copy = new Formation { Id = f.Id, Name = f.Name, Members = f.Members.Select(m => new FormationMember { TemplateId = m.TemplateId, Dx = m.Dx, Dy = m.Dy }).ToList() };
        _ = System.Threading.Tasks.Task.Run(() => cl.SendFormation(copy));
    }

    // the deploy hotkey: the last formation deployed this session
    private void DeployLastFormation()
    {
        var f = _lastFormationId is null ? null : PanelLibrary.Formations.Find(x => x.Id == _lastFormationId);
        if (f is null) { SetStatus("no formation deployed yet. pick one in the pawn panel's formations", StatusKind.Warning); return; }
        DeployFormation(f);
    }
}
