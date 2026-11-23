using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DungeonMapper.Models;

namespace DungeonMapper.Net;

/// The DM's side of a session: accepts players, checks their hello, sends
/// them what they may see, and broadcasts a discovery beacon. Publish is the
/// only way map data leaves; it takes a PlayerPackage, which is already cut
/// down by PlayerView. Events fire on background threads.
public enum PawnAction { Move, Place, Status, Deploy }

/// a player asks to move (From -> To), place (TemplateId at To), set the
/// status of (To, Status) or deploy (Placements) Player pawns. the DM's app
/// checks it against the real map
public sealed class PawnRequest
{
    public required string Name { get; init; }
    public PawnAction Action { get; init; }
    public CellKey From { get; init; }
    public CellKey To { get; init; }
    public string TemplateId { get; init; } = "";
    public string? Status { get; init; }
    public List<(string id, CellKey cell)> Placements { get; init; } = new();
    internal object? Peer { get; init; }
}

/// a player keeps (Formation) or deletes (DeleteId) a formation
public sealed class FormationRequest
{
    public required string Name { get; init; }
    public Formation? Formation { get; init; }
    public string DeleteId { get; init; } = "";
    internal object? Peer { get; init; }
}

/// a player adds or changes a label (Label, carrying its id) or deletes one
/// (Label null, DeleteId). the DM's app checks it against the real map
public sealed class LabelRequest
{
    public required string Name { get; init; }
    public MapLabel? Label { get; init; }
    public string DeleteId { get; init; } = "";
    internal object? Peer { get; init; }
}

public sealed class SessionHost : IDisposable
{
    private sealed class Peer
    {
        public required TcpClient Client;
        public required NetworkStream Stream;
        public string Name = "player";
        public readonly HashSet<string> Pictures = new(); // picture keys this player has
        public readonly SemaphoreSlim Lock = new(1, 1);
        public DateTime LastPing = DateTime.MinValue;
    }

    /// the shortest gap between two pings from one player; faster ones are dropped
    public const int PingGapMillis = 250;

    public string SessionName { get; set; } = "Dungeon Mapper session";
    /// empty = no code needed
    public string Code { get; set; } = "";
    public int Port { get; private set; }
    public bool Running => _listener is not null;
    /// where beacons go. default: the broadcast address of each interface and 255.255.255.255
    public List<IPEndPoint>? BeaconTargets { get; set; }
    public int BeaconMillis { get; set; } = 2000;

    /// a player joined or left
    public event Action? PlayersChanged;
    /// a player pinged a cell (name, x, y)
    public event Action<string, int, int>? PingReceived;
    /// a player asked to move or place one of their heroes
    public event Action<PawnRequest>? PawnRequested;
    /// a player added, changed or deleted a label
    public event Action<LabelRequest>? LabelRequested;
    /// a player kept or deleted a formation
    public event Action<FormationRequest>? FormationRequested;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<Peer> _peers = new();
    private NetMessage? _state;
    private string? _stateKey;
    private readonly object _gate = new();

    public List<string> Players { get { lock (_gate) return _peers.Select(p => p.Name).ToList(); } }

    /// starts listening (port 0 picks a free one). throws SocketException when the port is taken
    public void Start(int port = NetProtocol.DefaultPort, bool beacon = true)
    {
        if (Running) return;
        var l = new TcpListener(IPAddress.Any, port);
        l.Start();
        _listener = l;
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        // network work runs on the thread pool, never on the caller's (ui) thread
        var ct = _cts.Token;
        _ = Task.Run(() => AcceptLoop(l, ct));
        if (beacon) _ = Task.Run(() => BeaconLoop(ct));
    }

    public void Stop()
    {
        if (!Running) return;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        List<Peer> peers;
        lock (_gate) { peers = _peers.ToList(); _peers.Clear(); }
        foreach (var p in peers) p.Client.Dispose();
        _state = null;
        _stateKey = null;
        if (peers.Count > 0) PlayersChanged?.Invoke();
    }

    public void Dispose() => Stop();

    /// sends the package to every player when it differs from the last one.
    /// returns true when it was new
    public bool Publish(string mapName, PlayerPackage package)
    {
        var msg = new NetMessage { Type = "state", MapName = mapName, Package = MapSerializer.PackageToWire(package) };
        string key = JsonSerializer.Serialize(msg, DmJson.Default.NetMessage);
        lock (_gate)
        {
            if (key == _stateKey) return false;
            _state = msg;
            _stateKey = key;
        }
        List<Peer> peers;
        lock (_gate) peers = _peers.ToList();
        foreach (var p in peers) _ = Task.Run(() => SendState(p, msg));
        return true;
    }

    /// the DM pings a cell: every player sees it
    public void Ping(string name, int x, int y)
    {
        List<Peer> peers;
        lock (_gate) peers = _peers.ToList();
        var msg = new NetMessage { Type = "ping", Name = name, X = x, Y = y };
        foreach (var p in peers) _ = Task.Run(() => Send(p, msg));
    }

    private async Task Send(Peer p, NetMessage msg)
    {
        await p.Lock.WaitAsync();
        try { await NetProtocol.WriteAsync(p.Stream, msg); }
        catch (Exception) { Drop(p); }
        finally { p.Lock.Release(); }
    }

    // a player's ping: shown to the DM and passed on to the other players,
    // always under the name the player joined with
    private void OnPing(Peer from, NetMessage m)
    {
        if (m.X is not int x || m.Y is not int y) return;
        var now = DateTime.UtcNow;
        if ((now - from.LastPing).TotalMilliseconds < PingGapMillis) return;
        from.LastPing = now;
        PingReceived?.Invoke(from.Name, x, y);
        List<Peer> others;
        lock (_gate) others = _peers.Where(p => p != from).ToList();
        var msg = new NetMessage { Type = "ping", Name = from.Name, X = x, Y = y };
        foreach (var p in others) _ = Task.Run(() => Send(p, msg));
    }

    /// tells the player who sent the request why it was refused
    public void Notice(PawnRequest r, string reason) => Notice(r.Peer, reason);
    public void Notice(LabelRequest r, string reason) => Notice(r.Peer, reason);
    public void Notice(FormationRequest r, string reason) => Notice(r.Peer, reason);

    private void Notice(object? peer, string reason)
    {
        if (peer is Peer p) _ = Task.Run(() => Send(p, new NetMessage { Type = "notice", Reason = reason }));
    }

    private void OnLabel(Peer from, NetMessage m)
    {
        if (m.Type == "label_delete")
        {
            if (!string.IsNullOrEmpty(m.Id)) LabelRequested?.Invoke(new LabelRequest { Name = from.Name, DeleteId = m.Id, Peer = from });
            return;
        }
        if (m.Label is null || MapSerializer.LabelFromDto(m.Label) is not { } l) return;
        LabelRequested?.Invoke(new LabelRequest { Name = from.Name, Label = l, Peer = from });
    }

    private void OnPawn(Peer from, NetMessage m)
    {
        PawnRequest? r = null;
        if (m.Type == "pawn_deploy")
        {
            var list = (m.Placements ?? new()).Where(p => !string.IsNullOrEmpty(p.TemplateId)).Select(p => (p.TemplateId, new CellKey(p.X, p.Y))).ToList();
            if (list.Count == 0 || list.Count > 500) return;
            r = new PawnRequest { Name = from.Name, Action = PawnAction.Deploy, Placements = list, Peer = from };
        }
        else if (m.X is int x && m.Y is int y)
        {
            if (m.Type == "pawn_place" && !string.IsNullOrEmpty(m.TemplateId))
                r = new PawnRequest { Name = from.Name, Action = PawnAction.Place, TemplateId = m.TemplateId, To = new CellKey(x, y), Peer = from };
            else if (m.Type == "pawn_status")
                r = new PawnRequest { Name = from.Name, Action = PawnAction.Status, To = new CellKey(x, y), Status = m.Status, Peer = from };
            else if (m.Type == "pawn_move" && m.ToX is int tx && m.ToY is int ty)
                r = new PawnRequest { Name = from.Name, Action = PawnAction.Move, From = new CellKey(x, y), To = new CellKey(tx, ty), Peer = from };
        }
        if (r is not null) PawnRequested?.Invoke(r);
    }

    private void OnFormation(Peer from, NetMessage m)
    {
        if (m.Type == "formation_delete")
        {
            if (!string.IsNullOrEmpty(m.Id)) FormationRequested?.Invoke(new FormationRequest { Name = from.Name, DeleteId = m.Id, Peer = from });
        }
        else if (m.Formation is { } f) FormationRequested?.Invoke(new FormationRequest { Name = from.Name, Formation = f, Peer = from });
    }

    // a player gets only the pictures it does not have yet
    private async Task SendState(Peer p, NetMessage full)
    {
        await p.Lock.WaitAsync();
        try
        {
            var w = full.Package!;
            var pics = (w.ObjectPictures ?? new()).Where(kv => !p.Pictures.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var msg = new NetMessage
            {
                Type = "state", MapName = full.MapName,
                Package = new PackageWire { Map = w.Map, PawnLibrary = w.PawnLibrary, ObjectTypes = w.ObjectTypes, ObjectPictures = pics, Presets = w.Presets, Light = w.Light, Formations = w.Formations },
            };
            await NetProtocol.WriteAsync(p.Stream, msg);
            foreach (var k in pics.Keys) p.Pictures.Add(k);
        }
        catch (Exception) { Drop(p); }
        finally { p.Lock.Release(); }
    }

    private void Drop(Peer p)
    {
        bool had;
        lock (_gate) had = _peers.Remove(p);
        p.Client.Dispose();
        if (had) PlayersChanged?.Invoke();
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch (Exception) { return; }
            _ = Serve(c, ct);
        }
    }

    private async Task Serve(TcpClient c, CancellationToken ct)
    {
        Peer? peer = null;
        try
        {
            c.NoDelay = true;
            var s = c.GetStream();
            NetMessage? hello;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(10000); // a silent connection is dropped
                hello = await NetProtocol.ReadAsync(s, t.Token);
            }
            if (hello is null) { c.Dispose(); return; }
            if (NetProtocol.CheckHello(hello, Code) is { } why)
            {
                await NetProtocol.WriteAsync(s, new NetMessage { Type = "refuse", Reason = why }, ct);
                c.Dispose();
                return;
            }
            peer = new Peer { Client = c, Stream = s, Name = string.IsNullOrWhiteSpace(hello.Name) ? "player" : hello.Name.Trim() };
            await NetProtocol.WriteAsync(s, new NetMessage { Type = "welcome", Session = SessionName, App = NetProtocol.AppVersion }, ct);
            lock (_gate) _peers.Add(peer);
            PlayersChanged?.Invoke();
            NetMessage? state;
            lock (_gate) state = _state;
            if (state is not null) await SendState(peer, state);
            // players send pings, pawn and label requests; read until they leave
            while (await NetProtocol.ReadAsync(s, ct) is { } m)
            {
                if (m.Type == "ping") OnPing(peer, m);
                else if (m.Type is "pawn_move" or "pawn_place" or "pawn_status" or "pawn_deploy") OnPawn(peer, m);
                else if (m.Type is "formation_set" or "formation_delete") OnFormation(peer, m);
                else if (m.Type is "label_set" or "label_delete") OnLabel(peer, m);
            }
        }
        catch (Exception) { }
        if (peer is not null) Drop(peer); else c.Dispose();
    }

    private List<IPEndPoint> DefaultBeaconTargets()
    {
        var list = new List<IPEndPoint> { new(IPAddress.Broadcast, NetProtocol.DiscoveryPort) };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask is null) continue;
                    var ip = a.Address.GetAddressBytes();
                    var mask = a.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPEndPoint(new IPAddress(ip), NetProtocol.DiscoveryPort));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list.DistinctBy(e => e.ToString()).ToList();
    }

    private async Task BeaconLoop(CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var targets = BeaconTargets ?? DefaultBeaconTargets();
        while (!ct.IsCancellationRequested)
        {
            var beacon = new NetMessage
            {
                Type = "beacon", AppTag = NetProtocol.AppTag, App = NetProtocol.AppVersion,
                Session = SessionName, Port = Port, NeedsCode = !string.IsNullOrEmpty(Code),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(beacon, DmJson.Default.NetMessage);
            foreach (var t in targets)
            {
                try { await udp.SendAsync(bytes, t, ct); }
                catch (Exception) { } // a network that refuses broadcast still allows joining by address
            }
            try { await Task.Delay(BeaconMillis, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
