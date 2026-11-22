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
                Package = new PackageWire { Map = w.Map, PawnLibrary = w.PawnLibrary, ObjectTypes = w.ObjectTypes, ObjectPictures = pics, Presets = w.Presets },
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
            // players only send pings; read until they leave
            while (await NetProtocol.ReadAsync(s, ct) is { } m)
                if (m.Type == "ping") OnPing(peer, m);
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
