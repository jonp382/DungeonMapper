using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DungeonMapper.Models;

namespace DungeonMapper.Net;

public sealed class SessionRefusedException(string reason) : Exception(reason);

/// A player's side of a session: joins a host, then receives what the DM
/// shows. it sends the hello, pings and requests to move or place the
/// player's own heroes (the host decides). Events fire on a background thread.
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

    /// the host refused a request (the reason)
    public event Action<string>? NoticeReceived;

    /// pings a cell on the map the DM shows
    public Task SendPing(int x, int y) => Send(new NetMessage { Type = "ping", X = x, Y = y });

    /// asks the DM to move the player's pawn at from to to
    public Task SendPawnMove(CellKey from, CellKey to) =>
        Send(new NetMessage { Type = "pawn_move", X = from.X, Y = from.Y, ToX = to.X, ToY = to.Y });

    /// asks the DM to place a Player pawn at the cell
    public Task SendPawnPlace(string templateId, CellKey at) =>
        Send(new NetMessage { Type = "pawn_place", TemplateId = templateId, X = at.X, Y = at.Y });

    /// asks the DM to set the status of the player's pawn (null = the template's)
    public Task SendPawnStatus(CellKey at, string? status) =>
        Send(new NetMessage { Type = "pawn_status", X = at.X, Y = at.Y, Status = status });

    /// asks the DM to place several pawns at once (a formation)
    public Task SendDeploy(IEnumerable<(string id, CellKey cell)> pawns) =>
        Send(new NetMessage { Type = "pawn_deploy", Placements = pawns.Select(p => new PawnPlacement { TemplateId = p.id, X = p.cell.X, Y = p.cell.Y }).ToList() });

    /// asks the DM to keep a formation (new or changed, by id)
    public Task SendFormation(Formation f) => Send(new NetMessage { Type = "formation_set", Formation = f });

    /// asks the DM to delete a formation
    public Task SendFormationDelete(string id) => Send(new NetMessage { Type = "formation_delete", Id = id });

    /// asks the DM to add or change a label (by its id)
    public Task SendLabel(MapLabel l) => Send(new NetMessage { Type = "label_set", Label = MapSerializer.LabelToDto(l) });

    /// asks the DM to add or change an image (by its id)
    public Task SendImage(MapImage im) => Send(new NetMessage { Type = "image_set", Image = MapSerializer.ImageToDto(im) });

    /// asks the DM to delete an image
    public Task SendImageDelete(string id) => Send(new NetMessage { Type = "image_delete", Id = id });

    /// asks the DM to delete a label
    public Task SendLabelDelete(string id) => Send(new NetMessage { Type = "label_delete", Id = id });

    private async Task Send(NetMessage m)
    {
        await _writeLock.WaitAsync();
        try { await NetProtocol.WriteAsync(_stream, m); }
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
                if (m.Type == "notice") { NoticeReceived?.Invoke(m.Reason ?? "refused"); continue; }
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
