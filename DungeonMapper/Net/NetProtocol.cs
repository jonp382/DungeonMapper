using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DungeonMapper.Models;

namespace DungeonMapper.Net;

// multiplayer wire protocol (see Agent.md "Multiplayer Design"). LAN only.
// every message is a 4 byte big endian length then that many bytes of utf-8
// json, one NetMessage. the conversation:
//   player -> host  hello   (protocol, app version, code, name)
//   host -> player  welcome (session name) or refuse (reason), then closes
//   host -> player  state   (map name + player package) after the welcome
//                           and whenever what players see changes
//   either way      ping    (cell; the host fills in who and passes a
//                           player's ping on to the other players)
//   player -> host  pawn_move (x,y -> to_x,to_y), pawn_place (template id,
//                           x,y), pawn_status (x,y, status), pawn_deploy
//                           (placements), formation_set (formation) and
//                           formation_delete (id): Player pawns only. the DM's
//                           app checks and applies them
//   player -> host  label_set (a label with its id: new or changed) and
//                           label_delete (id): labels players may edit
//   host -> player  notice  (reason) when it refuses one
// discovery: the host broadcasts a beacon over udp every couple of seconds.
public static class NetProtocol
{
    /// bump when the messages change in a way older apps can not read
    public const int Version = 2;
    public const int DefaultPort = 47320;
    public const int DiscoveryPort = 47321;
    public const string AppTag = "DungeonMapper";
    /// a message bigger than this is refused (a map with pictures is far smaller)
    public const int MaxMessage = 64 * 1024 * 1024;

    /// the app build, compared on join: both sides must run the same version.
    /// release builds stamp the tag (release.yml), the sdk adds "+<commit>",
    /// so two builds of different commits never match
    public static string AppVersion =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(NetProtocol).Assembly)?.InformationalVersion
        ?? typeof(NetProtocol).Assembly.GetName().Version?.ToString() ?? "0";

    public static byte[] Encode(NetMessage m)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(m, DmJson.Default.NetMessage);
        var buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, body.Length);
        body.CopyTo(buf, 4);
        return buf;
    }

    public static async Task WriteAsync(Stream s, NetMessage m, CancellationToken ct = default)
    {
        await s.WriteAsync(Encode(m), ct);
        await s.FlushAsync(ct);
    }

    /// the next message, null when the other side closed the connection
    public static async Task<NetMessage?> ReadAsync(Stream s, CancellationToken ct = default)
    {
        var head = new byte[4];
        if (!await ReadExact(s, head, ct)) return null;
        int len = BinaryPrimitives.ReadInt32BigEndian(head);
        if (len < 0 || len > MaxMessage) throw new InvalidDataException($"message of {len} bytes refused");
        var body = new byte[len];
        if (!await ReadExact(s, body, ct)) return null;
        return JsonSerializer.Deserialize(body, DmJson.Default.NetMessage)
            ?? throw new InvalidDataException("empty message");
    }

    private static async Task<bool> ReadExact(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    /// why a hello is refused, null when it is accepted
    public static string? CheckHello(NetMessage hello, string code)
    {
        if (hello.Type != "hello") return "expected a hello";
        if (hello.Protocol != Version || hello.App != AppVersion)
            return $"version mismatch: the host runs {AppVersion}, you run {hello.App ?? "an unknown version"}. both need the same Dungeon Mapper version";
        if (!string.IsNullOrEmpty(code) && hello.Code != code) return "wrong session code";
        return null;
    }

    /// this machine's IPv4 addresses on up, non loopback interfaces, for the
    /// host to show ("players join at ...")
    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(a.Address.ToString());
            }
        }
        catch (NetworkInformationException) { }
        return list.Distinct().ToList();
    }
}

/// one message of any kind; fields not used by a kind stay null
public sealed class NetMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("app_tag")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AppTag { get; set; }
    [JsonPropertyName("protocol")] public int Protocol { get; set; } = NetProtocol.Version;
    [JsonPropertyName("app")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? App { get; set; }
    [JsonPropertyName("code")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Code { get; set; }
    [JsonPropertyName("name")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonPropertyName("reason")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Reason { get; set; }
    [JsonPropertyName("session")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Session { get; set; }
    [JsonPropertyName("port")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Port { get; set; }
    [JsonPropertyName("needs_code")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool NeedsCode { get; set; }
    [JsonPropertyName("map_name")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MapName { get; set; }
    // a ping: the cell, on the map shown to players
    [JsonPropertyName("x")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? X { get; set; }
    [JsonPropertyName("y")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Y { get; set; }
    // a pawn move's target cell, a pawn place's template
    [JsonPropertyName("to_x")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ToX { get; set; }
    [JsonPropertyName("to_y")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ToY { get; set; }
    [JsonPropertyName("template_id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TemplateId { get; set; }
    // a label a player adds or changes, a label id to delete
    [JsonPropertyName("label")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public LabelDto? Label { get; set; }
    [JsonPropertyName("id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Id { get; set; }
    // a pawn's status (pawn_status; null = the template's), a deploy, a formation
    [JsonPropertyName("status")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Status { get; set; }
    [JsonPropertyName("placements")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<PawnPlacement>? Placements { get; set; }
    [JsonPropertyName("formation")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Formation? Formation { get; set; }
    [JsonPropertyName("package")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PackageWire? Package { get; set; }
}

/// one pawn of a deploy: template and cell
public sealed class PawnPlacement
{
    [JsonPropertyName("template_id")] public string TemplateId { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
}
