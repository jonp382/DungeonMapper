using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DungeonMapper.Models;

namespace DungeonMapper.Net;

/// a session heard on the network
public sealed record FoundSession(string Session, string Address, int Port, bool NeedsCode, bool SameVersion, string HostVersion, DateTime Seen);

/// Listens for host beacons so players can pick a session instead of typing
/// an address. Sessions not heard for a while drop off. Changed fires on a
/// background thread.
public sealed class SessionBrowser : IDisposable
{
    public const int ForgetMillis = 7000;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, FoundSession> _found = new();
    private readonly object _gate = new();

    public event Action? Changed;

    /// starts listening. false when the discovery port can not be opened
    /// (joining by address still works)
    public bool Start(int port = NetProtocol.DiscoveryPort)
    {
        if (_udp is not null) return true;
        try
        {
            var u = new UdpClient();
            u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            u.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _udp = u;
        }
        catch (SocketException) { return false; }
        _cts = new CancellationTokenSource();
        var udp = _udp; var ct = _cts.Token;
        _ = Task.Run(() => Listen(udp, ct));
        return true;
    }

    public List<FoundSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                foreach (var k in _found.Where(kv => (now - kv.Value.Seen).TotalMilliseconds > ForgetMillis).Select(kv => kv.Key).ToList())
                    _found.Remove(k);
                return _found.Values.OrderBy(f => f.Session).ThenBy(f => f.Address).ToList();
            }
        }
    }

    private async Task Listen(UdpClient u, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await u.ReceiveAsync(ct); }
            catch (Exception) { return; }
            NetMessage? m;
            try { m = JsonSerializer.Deserialize(r.Buffer, DmJson.Default.NetMessage); }
            catch (JsonException) { continue; }
            if (m is null || m.Type != "beacon" || m.AppTag != NetProtocol.AppTag || m.Port <= 0) continue;
            var f = new FoundSession(m.Session ?? "session", r.RemoteEndPoint.Address.ToString(), m.Port, m.NeedsCode,
                m.Protocol == NetProtocol.Version && m.App == NetProtocol.AppVersion, m.App ?? "?", DateTime.UtcNow);
            bool isNew;
            lock (_gate)
            {
                string key = $"{f.Address}:{f.Port}";
                isNew = !_found.TryGetValue(key, out var old) || old with { Seen = f.Seen } != f;
                _found[key] = f;
            }
            if (isNew) Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
    }
}
