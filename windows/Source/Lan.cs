using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace RoboDust;

/// Another RoboDust on the same network, with its arena page open.
public sealed class LanPeer
{
    public string Id, Name;
    public IPEndPoint End;
    public Look Look;
    public bool Busy;
    public DateTime Seen;
}

/// The robot fight over the local network: small JSON messages over UDP. Nothing leaves the local
/// network, and only what's needed for a fight is sent: the name the player typed in, the robot's
/// looks, and during a fight the positions and key presses. No file name, no path, nothing else.
public sealed class Lan : IDisposable
{
    public const int Port = 47820;
    public readonly string Id = Guid.NewGuid().ToString("N")[..12];
    UdpClient listen, game;
    readonly Dispatcher ui;
    volatile bool closed;
    readonly int listenPort;
    readonly IPAddress bindTo;
    readonly List<IPEndPoint> testTargets;

    /// type, message, sender. Always on the UI thread.
    public Action<string, JsonElement, IPEndPoint> OnMessage;

    /// Normal: listens on the LAN and says hello by broadcast. Test: loopback only, between two fixed ports.
    public Lan(Dispatcher ui, int? testPort = null, int? testPeerPort = null)
    {
        this.ui = ui;
        if (testPort is { } tp)
        {
            listenPort = tp; bindTo = IPAddress.Loopback;
            testTargets = new() { new IPEndPoint(IPAddress.Loopback, testPeerPort ?? Port) };
        }
        else { listenPort = Port; bindTo = IPAddress.Any; }
    }

    public void Start()
    {
        listen = new UdpClient(AddressFamily.InterNetwork);
        listen.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listen.Client.Bind(new IPEndPoint(bindTo, listenPort));
        game = new UdpClient(new IPEndPoint(bindTo, 0)) { EnableBroadcast = true };
        // A peer that vanishes mid-fight makes Windows report the next receive as an error: ignore that.
        const int SIO_UDP_CONNRESET = -1744830452;
        try { game.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); listen.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
        Loop(listen); Loop(game);
    }

    void Loop(UdpClient c)
    {
        var t = new Thread(() =>
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (!closed)
            {
                byte[] data;
                try { data = c.Receive(ref from); }
                catch { if (closed) return; continue; }
                if (data.Length > 8192) continue;
                JsonElement msg;
                try { using var doc = JsonDocument.Parse(data); msg = doc.RootElement.Clone(); }
                catch { continue; }
                if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("t", out var tp) || tp.ValueKind != JsonValueKind.String) continue;
                if (msg.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.String && idp.GetString() == Id) continue;   // our own hello
                var sender = new IPEndPoint(from.Address, from.Port);
                var type = tp.GetString();
                ui.BeginInvoke(() => { if (!closed) OnMessage?.Invoke(type, msg, sender); });
            }
        }) { IsBackground = true, Name = "RoboDust LAN" };
        t.Start();
    }

    /// Hello to everyone on every local network this PC is on. Sent from the game socket, so
    /// replies come straight back to it.
    public void Broadcast(object msg)
    {
        if (closed || game == null) return;
        var bytes = Encode(msg);
        foreach (var to in Targets())
            try { game.Send(bytes, bytes.Length, to); } catch { }
    }

    public void Send(IPEndPoint to, object msg)
    {
        if (closed || game == null || to == null) return;
        var bytes = Encode(msg);
        try { game.Send(bytes, bytes.Length, to); } catch { }
    }

    static byte[] Encode(object msg) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));

    IEnumerable<IPEndPoint> Targets()
    {
        if (testTargets != null) { foreach (var t in testTargets) yield return t; yield break; }
        var seen = new HashSet<string>();
        IEnumerable<IPAddress> bcasts;
        try
        {
            bcasts = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                .Select(u =>
                {
                    var a = u.Address.GetAddressBytes(); var m = u.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) a[i] = (byte)(a[i] | ~m[i]);
                    return new IPAddress(a);
                }).ToList();
        }
        catch { bcasts = Array.Empty<IPAddress>(); }
        foreach (var b in bcasts.Append(IPAddress.Broadcast))
            if (seen.Add(b.ToString())) yield return new IPEndPoint(b, Port);
    }

    public void Dispose()
    {
        closed = true;
        try { listen?.Close(); } catch { }
        try { game?.Close(); } catch { }
    }

    // MARK: Reading messages safely: everything from the network is checked and clamped.

    public static string Str(JsonElement m, string key, int max = 40)
    {
        if (!m.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString() ?? "";
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }

    public static float Num(JsonElement m, string key, float min, float max, float fallback = 0)
    {
        if (!m.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d)) return fallback;
        return (float)Math.Clamp(d, min, max);
    }

    public static bool Bool(JsonElement m, string key) => m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    public static object LookMsg(Look l) => new { sk = (int)l.Skin, ht = l.Hat, wp = l.Weapon, ar = l.Armor, sp = l.Spikes, spo = l.Spoiler, gl = l.Glow, gc = l.GlowColor, dc = l.Decal, lb = l.Lightbar };

    public static Look ReadLook(JsonElement m, string key)
    {
        var l = new Look();
        if (!m.TryGetProperty(key, out var o) || o.ValueKind != JsonValueKind.Object) return l;
        l.Skin = (RobotSkin)(int)Num(o, "sk", 0, 18);
        l.Hat = (int)Num(o, "ht", 0, 12);
        l.Weapon = (int)Num(o, "wp", 0, 5);
        l.Armor = Bool(o, "ar"); l.Spikes = Bool(o, "sp"); l.Spoiler = Bool(o, "spo"); l.Glow = Bool(o, "gl");
        l.GlowColor = (int)Num(o, "gc", 0, 3); l.Decal = (int)Num(o, "dc", 0, 2); l.Lightbar = Bool(o, "lb");
        return l;
    }

    /// A name someone typed in: one line, at most 16 characters.
    public static string CleanName(string s)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > 16 ? s[..16] : s;
    }
}
