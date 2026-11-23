using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DungeonMapper.Controls;
using DungeonMapper.Models;
using DungeonMapper.Net;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// multiplayer sessions (see Agent.md "Multiplayer Design", Net/). the DM
// hosts: a timer sends players the PlayerView package of the map picked to
// show, only when it changed. a player joins: this window switches to the
// received map, read only, with the measure tool and the move tool for their
// own heroes (sent to the DM, who applies them); their own campaign is left
// alone and comes back on leaving.
public partial class MainWindow
{
    private SessionHost? _host;
    private MapEntry? _shownMap;        // the map players see, picked apart from the open one
    private DispatcherTimer? _publishTimer;
    private SessionClient? _client;
    private SessionBrowser? _browser;
    private DispatcherTimer? _browseTimer;
    private string _joinedMap = "";
    private bool _fitOnNextState;
    // remembered while the app runs
    private string _hostName = "Dungeon Mapper session", _hostCode = "", _playerName = "player",
        _joinCode = "", _joinAddress = "", _joinPort = NetProtocol.DefaultPort.ToString();
    private int _hostPort = NetProtocol.DefaultPort;

    private void InitSession()
    {
        MenuHost.Click += (_, _) => { CloseModals(); ShowHostSession(); };
        MenuJoin.Click += (_, _) => { CloseModals(); ShowJoinSession(); };
        MenuLeave.Click += (_, _) => { if (_client is not null) LeavePlayerMode("left the session"); else if (_host is not null) StopHosting(); };
        Closed += (_, _) => { _host?.Dispose(); _client?.Dispose(); _browser?.Dispose(); };
        Canvas.RemoteLabelChanged += SendLabelToHost;
        Canvas.PlayerMoveRequested += (from, to) =>
        {
            if (_client is not { } cl) return;
            _ = System.Threading.Tasks.Task.Run(() => cl.SendPawnMove(from, to));
            StatusText.Text = $"moving {PlayerPawnName(Canvas.RemoteDocument?.Pawns.GetValueOrDefault(from)?.TemplateId)}";
        };
        Canvas.PlayerPlaceRequested += (id, at) =>
        {
            if (_client is not { } cl) return;
            _ = System.Threading.Tasks.Task.Run(() => cl.SendPawnPlace(id, at));
            StatusText.Text = $"placing {PlayerPawnName(id)}";
        };
        Canvas.PlayerDeployRequested += pawns =>
        {
            if (_client is not { } cl) return;
            _ = System.Threading.Tasks.Task.Run(() => cl.SendDeploy(pawns));
        };
    }

    private void RefreshSessionUi()
    {
        bool hosting = _host is not null, joined = _client is not null;
        MenuHost.IsEnabled = !joined;
        MenuJoin.IsEnabled = !hosting && !joined;
        MenuLeave.IsEnabled = hosting || joined;
        MenuLeave.Header = hosting ? "_Stop hosting" : "_Leave session";
        SessionStatus.IsVisible = hosting || joined;
        if (hosting)
        {
            int n = _host!.Players.Count;
            SessionStatus.Text = $"hosting · {Plural(n, "player")} · showing {ShownMap()?.Name ?? "nothing"}";
        }
        else if (joined)
            SessionStatus.Text = $"joined {_client!.SessionName}" + (_joinedMap.Length > 0 ? $" · {_joinedMap}" : " · waiting for the DM");
    }

    // ---------- host ----------

    // the picked map, or the open one when the pick is gone (deleted, another campaign opened)
    private MapEntry? ShownMap() =>
        _shownMap is not null && _project.Maps.Contains(_shownMap) ? _shownMap : _project.Maps.ElementAtOrDefault(_project.ActiveIndex);

    private void ShowHostSession()
    {
        var body = new StackPanel { Spacing = 6, Margin = new(16, 12, 16, 16) };
        var refusal = RefusalLine();
        if (_host is null)
        {
            body.Children.Add(Tx("players on the same network join and see only what you reveal on the map you pick. only you can change or save anything.", "hint"));
            body.Children.Add(FieldLabel("session name"));
            var name = new TextBox { Text = _hostName, Classes = { "field" } };
            body.Children.Add(name);
            body.Children.Add(FieldLabel("session code (optional, players must type it)"));
            var code = new TextBox { Text = _hostCode, Classes = { "field" } };
            body.Children.Add(code);
            body.Children.Add(FieldLabel("port"));
            var port = new TextBox { Text = _hostPort.ToString(), Classes = { "field", "number" }, HorizontalAlignment = HorizontalAlignment.Left };
            body.Children.Add(port);
            AddShownMapChips(body);
            body.Children.Add(refusal);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
            var go = Btn("start hosting", primary: true);
            var cancel = Btn("cancel");
            go.Click += (_, _) =>
            {
                if (!int.TryParse(port.Text, out int p) || p < 1 || p > 65535) { SetRefusal(refusal, "the port is a number from 1 to 65535"); return; }
                _hostName = string.IsNullOrWhiteSpace(name.Text) ? "Dungeon Mapper session" : name.Text.Trim();
                _hostCode = code.Text?.Trim() ?? "";
                _hostPort = p;
                if (StartHosting() is { } err) { SetRefusal(refusal, err); return; }
                ShowHostSession();
            };
            cancel.Click += (_, _) => { CloseModals(); FocusCanvas(); };
            row.Children.Add(go);
            row.Children.Add(cancel);
            body.Children.Add(row);
        }
        else
        {
            var addrs = NetProtocol.LocalAddresses();
            string where = addrs.Count == 0 ? $"port {_host.Port} (no network address found)" : string.Join("\n", addrs.Select(a => $"{a}:{_host.Port}"));
            body.Children.Add(FieldLabel("players join at"));
            body.Children.Add(new Border { Classes = { "infobox" }, Child = Tx(where + (string.IsNullOrEmpty(_host.Code) ? "" : $"\ncode: {_host.Code}"), "body") });
            body.Children.Add(Tx("players on this network also see the session in their join list.", "hint"));
            AddShownMapChips(body);
            var players = _host.Players;
            body.Children.Add(FieldLabel("players"));
            body.Children.Add(new Border { Classes = { "infobox" }, Child = Tx(players.Count == 0 ? "nobody yet" : string.Join(", ", players), "body") });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
            var stop = Btn("stop hosting", danger: true);
            var close = Btn("close");
            stop.Click += (_, _) => { StopHosting(); CloseModals(); FocusCanvas(); };
            close.Click += (_, _) => { CloseModals(); FocusCanvas(); };
            row.Children.Add(stop);
            row.Children.Add(close);
            body.Children.Add(row);
        }
        ShowModal(460, _host is null ? 470 : 440, _host is null ? "Host a session" : "Hosting", _host is null ? null : _host.SessionName,
            new ScrollViewer { Content = body });
        _modalKind = "host";
    }

    private void AddShownMapChips(StackPanel body)
    {
        body.Children.Add(FieldLabel("map shown to players"));
        var chips = new WrapPanel();
        var shown = ShownMap();
        foreach (var m in _project.Maps)
        {
            var entry = m;
            chips.Children.Add(Chip(m.Name, m == shown, null, () =>
            {
                _shownMap = entry;
                PublishNow();
                RefreshSessionUi();
                ShowHostSession();
            }));
        }
        body.Children.Add(chips);
    }

    /// null when hosting started, else why not
    private string? StartHosting()
    {
        var host = new SessionHost { SessionName = _hostName, Code = _hostCode };
        try { host.Start(_hostPort); }
        catch (SocketException ex) { return $"can not host on port {_hostPort}: {ex.Message}"; }
        _host = host;
        _shownMap ??= _project.Maps.ElementAtOrDefault(_project.ActiveIndex);
        host.PingReceived += (n, x, y) => Dispatcher.UIThread.Post(() => { if (_host == host) OnHostPing(n, x, y); });
        host.LabelRequested += r => Dispatcher.UIThread.Post(() => { if (_host == host) OnLabelRequest(host, r); });
        host.FormationRequested += r => Dispatcher.UIThread.Post(() => { if (_host == host) OnFormationRequest(host, r); });
        host.PawnRequested += r => Dispatcher.UIThread.Post(() => { if (_host == host) OnPawnRequest(host, r); });
        host.PlayersChanged += () => Dispatcher.UIThread.Post(() =>
        {
            RefreshSessionUi();
            if (ModalOverlay.IsVisible && _modalKind == "host") ShowHostSession();
        });
        _publishTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => PublishNow());
        _publishTimer.Start();
        PublishNow();
        RefreshSessionUi();
        StatusText.Text = $"hosting {_hostName} on port {host.Port}";
        return null;
    }

    private void StopHosting()
    {
        _publishTimer?.Stop();
        _host?.Dispose();
        _host = null;
        RefreshSessionUi();
        StatusText.Text = "stopped hosting";
    }

    // sends the shown map's player package. the host skips it when nothing changed
    private void PublishNow()
    {
        if (_host is null) return;
        var shown = ShownMap();
        var pkg = shown is null
            ? new PlayerPackage()
            : PlayerView.Package(shown.Doc, ActiveLibrary, ActiveObjects, ActivePresets);
        if (_host.Publish(shown?.Name ?? "", pkg)) RefreshSessionUi();
    }

    // a player moves, places, sets the status of or deploys Player pawns on
    // the map shown to players. checked again here against the real map and
    // library, applied as one undo step of that map and sent back out
    private void OnPawnRequest(SessionHost host, PawnRequest r)
    {
        var shown = ShownMap();
        if (shown is null) { host.Notice(r, "the DM is not showing a map"); return; }
        var doc = shown.Doc;
        var lib = ActiveLibrary;
        bool OnOtherMap(string id) => _project.Maps.Any(m => m.Doc != doc && m.Doc.Pawns.Values.Any(p => p.TemplateId == id));
        string Name(string id) => lib.Find(id) is { } tpl ? PawnName(tpl) : "pawn";
        string? why = r.Action switch
        {
            PawnAction.Move => PlayerPawns.CheckMove(doc, lib, r.From, r.To),
            PawnAction.Place => PlayerPawns.CheckPlace(doc, lib, r.TemplateId, r.To, OnOtherMap(r.TemplateId)),
            PawnAction.Status => PlayerPawns.CheckStatus(doc, lib, ActivePresets, r.To, r.Status),
            _ => PlayerPawns.CheckDeploy(doc, lib, r.Placements, OnOtherMap),
        };
        if (why is not null) { host.Notice(r, why); return; }
        switch (r.Action)
        {
            case PawnAction.Move:
                string moving = Name(doc.Pawns[r.From].TemplateId);
                PlayerPawns.ApplyMove(doc, r.From, r.To);
                StatusText.Text = $"{r.Name} moved {moving} to {r.To.X},{r.To.Y}";
                break;
            case PawnAction.Place:
                bool moved = PlayerPawns.ApplyPlace(doc, lib.Find(r.TemplateId)!, r.To);
                StatusText.Text = $"{r.Name} {(moved ? "moved" : "placed")} {Name(r.TemplateId)} to {r.To.X},{r.To.Y}";
                break;
            case PawnAction.Status:
                var pawn = doc.Pawns[r.To];
                pawn.Status = r.Status;
                StatusText.Text = $"{r.Name} set {Name(pawn.TemplateId)} to {r.Status ?? "its usual status"}";
                break;
            default:
                var templates = r.Placements.Select(p => lib.Find(p.id)!).Distinct().ToList();
                PlayerPawns.ApplyDeploy(doc, templates, r.Placements);
                StatusText.Text = $"{r.Name} deployed {Plural(r.Placements.Count, "pawn")}";
                break;
        }
        HistoryFor(shown).Commit(doc);
        if (doc == _doc)
        {
            Canvas.Refresh();
            PawnViewsChanged();
            RefreshToolInfo();
        }
        RefreshMaps();
        PublishNow();
    }

    // a player keeps or deletes a formation in the DM's campaign. only
    // formations of Player pawns, new or already only Player pawns
    private void OnFormationRequest(SessionHost host, FormationRequest r)
    {
        var lib = ActiveLibrary;
        string id = r.Formation?.Id ?? r.DeleteId;
        var existing = lib.Formations.Find(f => f.Id == id);
        if (existing is not null && PlayerPawns.CheckFormation(lib, existing) is not null) { host.Notice(r, "only the DM changes that formation"); return; }
        if (r.Formation is { } sent)
        {
            sent.Tidy();
            if (PlayerPawns.CheckFormation(lib, sent) is { } why) { host.Notice(r, why); return; }
            var name = sent.Name.Trim();
            if (name.Length == 0) name = Formation.UniqueName(lib.Formations);
            if (name.Length > 100) name = name[..100];
            if (existing is null)
            {
                lib.Formations.Add(new Formation { Id = sent.Id, Name = name, Members = sent.Members });
                StatusText.Text = $"{r.Name} saved the formation {name}";
            }
            else
            {
                existing.Name = name;
                existing.Members = sent.Members;
                StatusText.Text = $"{r.Name} changed the formation {name}";
            }
        }
        else
        {
            if (existing is null) { host.Notice(r, "that formation is gone"); return; }
            lib.Formations.Remove(existing);
            StatusText.Text = $"{r.Name} deleted the formation {existing.Name}";
        }
        if (Canvas.Mode == ToolMode.Pawn) RefreshPawnPanel();
        PublishNow();
    }

    // a player adds, changes or deletes a label on the map shown to players.
    // one undo step of that map, then sent back out
    private void OnLabelRequest(SessionHost host, LabelRequest r)
    {
        var shown = ShownMap();
        if (shown is null) { host.Notice(r, "the DM is not showing a map"); return; }
        var doc = shown.Doc;
        var before = r.Label is null ? PlayerLabels.Find(doc, r.DeleteId) : PlayerLabels.Find(doc, r.Label.Id);
        string? why = r.Label is null ? PlayerLabels.Delete(doc, r.DeleteId) : PlayerLabels.Set(doc, r.Label, r.Name);
        if (why is not null) { host.Notice(r, why); return; }
        HistoryFor(shown).Commit(doc);
        StatusText.Text = r.Label is null ? $"{r.Name} deleted a label" : before is null ? $"{r.Name} added a label" : $"{r.Name} changed a label";
        if (doc == _doc)
        {
            Canvas.Refresh();
            // the DM's open view of that label follows (or closes when it went)
            if (before is not null && Canvas.SelectedLabel == before && _editingLabel != before)
            {
                if (doc.Labels.Contains(before)) ShowLabelConfig(before); else BackToToolView();
            }
        }
        RefreshMaps();
        PublishNow();
    }

    // ---------- join ----------

    private void ShowJoinSession(string? error = null)
    {
        if (_browser is null)
        {
            _browser = new SessionBrowser();
            if (!_browser.Start()) error ??= "can not listen for sessions on this network (port in use?). you can still join by address.";
            _browser.Changed += () => Dispatcher.UIThread.Post(RefreshJoinList);
            _browseTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => RefreshJoinList());
            _browseTimer.Start();
        }
        var body = new StackPanel { Spacing = 6, Margin = new(16, 12, 16, 16) };
        body.Children.Add(Tx("join the DM's session on this network. you see what the DM reveals and can measure, nothing is saved.", "hint"));
        body.Children.Add(FieldLabel("your name"));
        var name = new TextBox { Text = _playerName, Classes = { "field" } };
        name.TextChanged += (_, _) => _playerName = name.Text ?? "";
        body.Children.Add(name);
        body.Children.Add(FieldLabel("session code (if the DM set one)"));
        var code = new TextBox { Text = _joinCode, Classes = { "field" } };
        code.TextChanged += (_, _) => _joinCode = code.Text ?? "";
        body.Children.Add(code);

        body.Children.Add(FieldLabel("sessions on this network"));
        _joinList = new StackPanel { Spacing = 4 };
        body.Children.Add(_joinList);

        body.Children.Add(FieldLabel("or join by address"));
        var addrRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90"), ColumnSpacing = 6 };
        var addr = new TextBox { Text = _joinAddress, Classes = { "field" }, Watermark = "192.168.1.20" };
        var port = new TextBox { Text = _joinPort, Classes = { "field", "number" } };
        addr.TextChanged += (_, _) => _joinAddress = addr.Text ?? "";
        port.TextChanged += (_, _) => _joinPort = port.Text ?? "";
        Grid.SetColumn(port, 1);
        addrRow.Children.Add(addr);
        addrRow.Children.Add(port);
        body.Children.Add(addrRow);
        _joinRefusal = RefusalLine();
        body.Children.Add(_joinRefusal);
        if (error is not null) SetRefusal(_joinRefusal, error);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var go = Btn("join", primary: true);
        var cancel = Btn("cancel");
        go.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_joinAddress)) { SetRefusal(_joinRefusal, "type the address the DM's app shows, or pick a session above"); return; }
            if (!int.TryParse(_joinPort, out int p) || p < 1 || p > 65535) { SetRefusal(_joinRefusal, "the port is a number from 1 to 65535"); return; }
            Join(_joinAddress.Trim(), p);
        };
        cancel.Click += (_, _) => { CloseModals(); FocusCanvas(); };
        row.Children.Add(go);
        row.Children.Add(cancel);
        body.Children.Add(row);

        ShowModal(460, 560, "Join a session", null, new ScrollViewer { Content = body });
        _modalKind = "join";
        RefreshJoinList();
    }

    private StackPanel? _joinList;
    private TextBlock? _joinRefusal;
    private bool _joining;

    private void RefreshJoinList()
    {
        if (_joinList is null || _browser is null || !ModalOverlay.IsVisible || _modalKind != "join") return;
        _joinList.Children.Clear();
        var found = _browser.Sessions;
        if (found.Count == 0)
        {
            _joinList.Children.Add(Tx("looking... none found yet", "faint"));
            return;
        }
        foreach (var f in found)
        {
            var s = f;
            string extra = (s.NeedsCode ? " · code" : "") + (s.SameVersion ? "" : $" · version {s.HostVersion}, can not join");
            var b = Btn($"{s.Session}  ({s.Address}:{s.Port}){extra}");
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.IsEnabled = s.SameVersion && !_joining;
            b.Click += (_, _) => Join(s.Address, s.Port);
            _joinList.Children.Add(b);
        }
    }

    private void StopBrowsing()
    {
        _browseTimer?.Stop();
        _browser?.Dispose();
        _browser = null;
        _joinList = null;
    }

    private async void Join(string address, int port)
    {
        if (_joining) return;
        _joining = true;
        if (_joinRefusal is not null) SetRefusal(_joinRefusal, null);
        StatusText.Text = $"joining {address}:{port}...";
        try
        {
            var client = await SessionClient.ConnectAsync(address, port, _joinCode.Trim(), string.IsNullOrWhiteSpace(_playerName) ? "player" : _playerName.Trim());
            _joining = false;
            EnterPlayerMode(client);
        }
        catch (Exception ex)
        {
            _joining = false;
            string why = ex switch
            {
                SessionRefusedException r => $"refused: {r.Message}",
                TimeoutException => "no answer from that address",
                SocketException s => $"could not connect: {s.Message}",
                _ => $"could not join: {ex.Message}",
            };
            StatusText.Text = why;
            if (_joinRefusal is not null && _modalKind == "join") SetRefusal(_joinRefusal, why);
        }
    }

    // ---------- player mode ----------

    private void EnterPlayerMode(SessionClient client)
    {
        StopBrowsing();
        CloseModals();
        SetPlayerPreview(false);
        _client = client;
        _joinedMap = "";
        _fitOnNextState = true;
        SetTool(ToolMode.Measure);
        Canvas.RemoteDocument = new MapDocument();
        SetEditingUi(false);
        client.StateReceived += (map, pkg) => Dispatcher.UIThread.Post(() => ApplyRemote(client, map, pkg));
        client.PingReceived += (n, x, y) => Dispatcher.UIThread.Post(() => { if (_client == client) OnClientPing(n, x, y); });
        client.NoticeReceived += why => Dispatcher.UIThread.Post(() =>
        {
            if (_client != client) return;
            _pendingLabels.Clear(); // a refused label is not coming back
            StatusText.Text = $"the DM's map refused: {why}";
        });
        client.Disconnected += reason => Dispatcher.UIThread.Post(() => { if (_client == client) LeavePlayerMode(reason); });
        client.Start();
        RefreshSessionUi();
        StatusText.Text = $"joined {client.SessionName}. you see what the DM reveals. Session > Leave session to go back to your own maps";
    }

    private void ApplyRemote(SessionClient from, string map, PlayerPackage pkg)
    {
        if (_client != from) return;
        Canvas.Library = pkg.Library;
        Canvas.Presets = pkg.Presets;
        Canvas.ObjectTypes = pkg.Objects;
        Canvas.InvalidateImageCache(); // template images are cached by id, the host may have changed one
        KeepOpenLabel(pkg.Map);
        Canvas.RemoteDocument = pkg.Map;
        // the armed pawn and the panels follow the new library and map
        if (Canvas.ArmedPawn is { } armed)
            Canvas.ArmedPawn = pkg.Library.Find(armed.Id) is { } again && PlayerPawns.Controlled(again) ? again : null;
        if (Canvas.Mode == ToolMode.Pawn)
        {
            if (_playerPawnInfoCell is CellKey c)
            {
                if (pkg.Map.Pawns.TryGetValue(c, out var p) && PlayerPawns.Controlled(pkg.Library.Find(p.TemplateId))) ShowPlayerPawnInfo(p);
                else BackToToolView();
            }
            else if (_formationRenaming is null) RefreshPawnPanel();
        }
        if (map != _joinedMap) _fitOnNextState = true;
        _joinedMap = map;
        if (_fitOnNextState && pkg.Map.Cells.Count + pkg.Map.FloodRegions.Count > 0) { Canvas.AutoFit(); _fitOnNextState = false; }
        RefreshSessionUi();
    }

    // labels a player sent that the DM's map has not shown back yet
    private readonly HashSet<string> _pendingLabels = new();

    /// the map label views and commits work on: the DM's open map, or the
    /// received map while joined
    private MapDocument LabelDoc => _client is not null ? Canvas.Document : _doc;

    /// after a label edit: an undo step for the DM, sent to the DM for a player
    private void CommitLabel(MapLabel label)
    {
        if (_client is not null) SendLabelToHost(label);
        else ActiveHistory.Commit(_doc);
    }

    private void SendLabelToHost(MapLabel label)
    {
        if (_client is not { } cl || !PlayerLabels.Editable(label)) return;
        _pendingLabels.Add(label.Id);
        var copy = label.CopyAt(label.Cell); // the wire copy is taken off the ui thread
        _ = System.Threading.Tasks.Task.Run(() => cl.SendLabel(copy));
    }

    // a new map from the DM replaces every label object. the label the player
    // has open (view or on map editor) keeps its object, so typing goes on;
    // a label the DM removed or took back closes
    private void KeepOpenLabel(MapDocument incoming)
    {
        foreach (var l in incoming.Labels) _pendingLabels.Remove(l.Id);
        var open = _editingLabel ?? Canvas.SelectedLabel;
        if (open is null || open.Id.Length == 0 || !(Canvas.RemoteDocument?.Labels.Contains(open) ?? false)) return;
        int i = incoming.Labels.FindIndex(l => l.Id == open.Id);
        if (i >= 0 && PlayerLabels.Editable(incoming.Labels[i])) { incoming.Labels[i] = open; return; }
        if (i < 0 && _pendingLabels.Contains(open.Id)) { incoming.Labels.Add(open); return; }
        if (_editingLabel == open) EndLabelEdit(false);
        Canvas.SelectedLabel = null;
        if (Canvas.Mode == ToolMode.Label) BackToToolView();
        StatusText.Text = "the DM took that label back";
    }

    private void LeavePlayerMode(string reason)
    {
        _pendingLabels.Clear();
        var c = _client;
        _client = null;
        c?.Dispose();
        Canvas.RemoteDocument = null;
        SyncCampaign();
        Canvas.InvalidateImageCache();
        SetEditingUi(true);
        SetTool(ToolMode.Select);
        Canvas.AutoFit();
        RefreshSessionUi();
        StatusText.Text = $"{reason}. back to your own maps";
    }

    // player mode turns off everything that edits or saves
    private void SetEditingUi(bool on)
    {
        foreach (var m in Enum.GetValues<ToolMode>())
            ToolButton(m).IsEnabled = on || m is ToolMode.Measure or ToolMode.Move or ToolMode.Label or ToolMode.Pawn;
        MenuFile.IsEnabled = on;
        MenuEdit.IsEnabled = on;
        MenuManage.IsEnabled = on;
        MenuPlayerPreview.IsEnabled = on;
        MapList.IsEnabled = on;
        BtnManageMaps.IsEnabled = on;
    }

    // keys while joined: esc, zoom to fit, measure, move, label, pawn, deploy
    // formation (r rotates it) and ping. everything
    // else is swallowed. true when handled here
    private bool PlayerKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) return false;
        if (e.Key == Key.R && Canvas.FormationArmed && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift) return false;
        if (e.KeyModifiers != KeyModifiers.None) return true;
        foreach (var name in new[] { AppSettings.FitHotkey, nameof(ToolMode.Measure), nameof(ToolMode.Move), nameof(ToolMode.Label), nameof(ToolMode.Pawn), AppSettings.FormationHotkey, AppSettings.PingHotkey })
            if (Enum.TryParse<Key>(_settings.ToolHotkeys.GetValueOrDefault(name, ""), out var k) && k == e.Key) return false;
        return true;
    }
}

// the players' pawns while joined: the move tool hint and a placed pawn's view
// (the pawn tool panel is the DM's, cut down: MainWindow.Pawns.cs)
public partial class MainWindow
{
    // the cell of the placed pawn whose view is open, null when none
    private CellKey? _playerPawnInfoCell;

    private void BuildPlayerMovePanel()
    {
        ToolPanel.Clear("move");
        ToolPanel.AddHint("drag one of your party's pawns (Player pawns) to an empty revealed cell to move it. it moves on the DM's map for everyone. other pawns are moved by the DM. the pawn tool places pawns and deploys formations.");
    }

    private string PlayerPawnName(string? id) => Canvas.Library.Find(id ?? "") is { } t ? PawnName(t) : "pawn";

    // a placed Player pawn: header, status chips (sent to the DM), count
    private void ShowPlayerPawnInfo(Pawn pawn)
    {
        var cell = pawn.Cell;
        var t = PanelLibrary.Find(pawn.TemplateId);
        ToolPanel.Clear("pawn");
        _playerPawnInfoCell = cell;
        if (t is null) { ToolPanel.AddHint("this pawn's template is missing"); return; }
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*"), ColumnSpacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 4) };
        head.Children.Add(PawnIcon(t, 40));
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        titles.Children.Add(new TextBlock { Text = PawnName(t), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Hx("#e6e6ee"), TextTrimming = TextTrimming.CharacterEllipsis });
        var aff = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var affName = PawnTemplate.NormalizeAffiliation(t.Affiliation);
        if (PanelPresets.AffiliationColors.FirstOrDefault(a => a.Name == affName) is { } ac) aff.Children.Add(AffDot(ac, 10));
        aff.Children.Add(Txt(affName, 12, "#9a9aaa"));
        if (t.Hero) aff.Children.Add(Txt("hero", 10, "#9ab8ff"));
        titles.Children.Add(aff);
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        ToolPanel.AddButtonRaw(head);

        var statuses = new List<string> { "inherit" };
        statuses.AddRange(PanelPresets.PawnStatuses.Select(x => x.Name));
        var dots = PanelPresets.PawnStatuses.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First());
        ToolPanel.AddChips(statuses.ToArray(), pawn.Status ?? "inherit", v =>
            {
                var status = v == "inherit" ? null : v;
                if (status == pawn.Status || _client is not { } cl) return;
                pawn.Status = status; // shown at once, the DM's map confirms it
                Canvas.Refresh();
                _ = System.Threading.Tasks.Task.Run(() => cl.SendPawnStatus(cell, status));
                StatusText.Text = $"{PawnName(t)}: {v}";
                FocusCanvas();
            },
            "status",
            n => dots.TryGetValue(n, out var st)
                ? new Avalonia.Controls.Shapes.Ellipse { Width = 9, Height = 9, Fill = Rgb((st.R, st.G, st.B)) }
                : null);
        ToolPanel.AddInfo($"{PanelDoc.Pawns.Values.Count(p => p.TemplateId == pawn.TemplateId)} on this map");
        ToolPanel.AddButton("back to pawns", BackToToolView);
    }
}
