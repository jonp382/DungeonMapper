using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DungeonMapper.Models;

namespace DungeonMapper.Net;

public sealed class SessionRefusedException(string reason) : Exception(reason);

/// A player's side of a session: joins a host, then receives what the DM
/// shows. read only: the hello and pings are all it sends. Events fire on a
/// background thread.
public sealed class SessionClient : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, string> _pictures = new(); // every picture received so far
    private bool _closed;

    public string SessionName { get; }

    /// the map name and package, each time the host sends one
    public event Action<string, PlayerPackage>? StateReceived;
    /// the connection ended (reason for the status line). not raised by Dispose
    public event Action<string>? Disconnected;
    /// someone pinged a cell (name, x, y): the DM or another player
    public event Action<string, int, int>? PingReceived;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// pings a cell on the map the DM shows
    public async Task SendPing(int x, int y)
    {
        await _writeLock.WaitAsync();
        try { await NetProtocol.WriteAsync(_stream, new NetMessage { Type = "ping", X = x, Y = y }); }
        catch (Exception) { } // a lost connection is reported by the read loop
        finally { _writeLock.Release(); }
    }

    private SessionClient(TcpClient c, string session)
    {
        _client = c;
        _stream = c.GetStream();
        SessionName = session;
    }

    /// connects and says hello. throws SessionRefusedException when the host
    /// refuses (version, code), or a socket / timeout error
    public static async Task<SessionClient> ConnectAsync(string host, int port, string code, string name, int timeoutMillis = 8000)
    {
        var c = new TcpClient { NoDelay = true };
        using var t = new CancellationTokenSource(timeoutMillis);
        try
        {
            await c.ConnectAsync(host, port, t.Token);
            var s = c.GetStream();
            await NetProtocol.WriteAsync(s, new NetMessage { Type = "hello", App = NetProtocol.AppVersion, Code = code, Name = name }, t.Token);
            var reply = await NetProtocol.ReadAsync(s, t.Token);
            if (reply is null) throw new SessionRefusedException("the host closed the connection");
            if (reply.Type == "refuse") throw new SessionRefusedException(reply.Reason ?? "refused");
            if (reply.Type != "welcome") throw new SessionRefusedException("not a Dungeon Mapper host");
            return new SessionClient(c, reply.Session ?? "session");
        }
        catch (OperationCanceledException)
        {
            c.Dispose();
            throw new TimeoutException("no answer from the host");
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    /// starts receiving. wire the events first
    public void Start() => _ = Task.Run(ReadLoop);

    private async Task ReadLoop()
    {
        string reason = "the host ended the session";
        try
        {
            while (await NetProtocol.ReadAsync(_stream, _cts.Token) is { } m)
            {
                if (m.Type == "ping" && m.X is int px && m.Y is int py) { PingReceived?.Invoke(m.Name ?? "someone", px, py); continue; }
                if (m.Type != "state" || m.Package is null) continue;
                foreach (var (k, v) in m.Package.ObjectPictures ?? new()) _pictures[k] = v;
                var pkg = MapSerializer.PackageFromWire(m.Package);
                pkg.Objects.AddPictures(_pictures);
                StateReceived?.Invoke(m.MapName ?? "map", pkg);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { reason = $"connection lost ({ex.Message})"; }
        if (_closed) return;
        _closed = true;
        _client.Dispose();
        Disconnected?.Invoke(reason);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        _client.Dispose();
    }
}
