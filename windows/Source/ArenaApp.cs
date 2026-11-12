using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;

namespace RoboDust;

/// What the arena page shows: who is out there, who wants to fight, and how a fight is going.
public sealed class ArenaModel
{
    public List<LanPeer> Peers = new();
    public LanPeer Incoming; public string IncomingSid; public DateTime IncomingAt;
    public LanPeer Pending; public string PendingSid; public DateTime PendingAt;
    public LanPeer Accepted; public string AcceptedSid; public DateTime AcceptedAt;
    public bool Fighting;
    public string Message;
    public Action<LanPeer> Challenge;
    public Action Accept, Decline, CancelChallenge, Practice, Forfeit;

    public string Shape => string.Join("|", string.Join(",", Peers.Select(p => p.Id + p.Name + p.Busy + (int)p.Look.Skin + p.Look.Weapon)),
        Incoming?.Id, Pending?.Id, Accepted?.Id, Fighting, Message);
}

/// The robot fight against other people on the same network, and the practice fight.
public sealed partial class AppController
{
    Lan lan; bool lanFailed;
    DispatcherTimer lobbyTimer;
    Sim arena;
    Overlay arenaOverlay;
    LanPeer foe; string sid; bool arenaHost, arenaPractice;
    double lastHeard, netAcc, arenaLast; float arenaAcc;
    public int? LanTestPort, LanTestPeer;   // dev only: two copies on one PC, over loopback
    public string ArenaAuto;                // dev only: "host" or "guest"; the computer plays, then it quits
    public bool StartInTrayForTest;

    ArenaModel A => Model.Arena;
    string MyName => Model.Settings.PlayerName ?? "";
    Look MyLook() => Look.Of(Model.Settings);

    /// The arena's display: where the dock stands, else the main one. The arena fits on it with room to spare.
    Screen ArenaScreen() { var all = Screen.All(); Floorplan.Layout(all); return all.FirstOrDefault(s => s.Name == Model.DockScreen) ?? all[0]; }
    (float w, float h) MaxArena() { var s = ArenaScreen().SizePts; return (M.Clamp(s.X - 120, 700, 1300), M.Clamp(s.Y - 150, 420, 760)); }

    void WireArena()
    {
        A.Challenge = Challenge;
        A.Accept = AcceptChallenge;
        A.Decline = () => { if (A.Incoming != null) lan?.Send(A.Incoming.End, new { t = "ans", id = lan.Id, sid = A.IncomingSid, ok = false }); A.Incoming = null; Model.Changed(); };
        A.CancelChallenge = () => { if (A.Pending != null) lan?.Send(A.Pending.End, new { t = "bye", id = lan.Id, sid = A.PendingSid }); A.Pending = null; Model.Changed(); };
        A.Practice = () =>
        {
            var pick = new[] { 1, 2, 3, 4, 5 }[Random.Shared.Next(5)];
            var bot = new LanPeer { Name = "Trainingsbot", Look = new Look { Skin = (RobotSkin)new[] { 6, 8, 9, 16, 18 }[Random.Shared.Next(5)], Weapon = pick, Armor = Random.Shared.Next(3) == 0 } };
            var (w, h) = MaxArena();
            StartMatch(true, bot, null, w, h, practice: true);
        };
        A.Forfeit = () => { if (arena != null) { if (!arenaPractice) lan?.Send(foe.End, new { t = "bye", id = lan.Id, sid }); EndMatch("Du hast aufgegeben.", 0); } };
    }

    /// After every change: the network is open while the arena page is (with a name), or a fight is on.
    void ArenaSync()
    {
        if (A.Challenge == null) WireArena();
        var want = arena != null || (Model.Page == Page.Arena && Model.Stage == Stage.Ready && MyName != "");
        if (want && lan == null && !lanFailed) StartLan();
        else if (!want && lan != null) StopLan();
        if (!want) lanFailed = false;
    }

    void StartLan()
    {
        try
        {
            lan = new Lan(ui, LanTestPort, LanTestPeer);
            lan.OnMessage = OnLan;
            lan.Start();
        }
        catch (Exception e)
        {
            lan?.Dispose(); lan = null; lanFailed = true;
            A.Message = "Das Netzwerk ist gerade nicht erreichbar (" + e.Message + "). Üben gegen den Computer geht trotzdem.";
            Panel?.Refresh();
            return;
        }
        lobbyTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.2), DispatcherPriority.Normal, (_, _) => LobbyTick(), ui);
        lobbyTimer.Start();
        SayHello();
    }

    void StopLan()
    {
        lobbyTimer?.Stop(); lobbyTimer = null;
        lan?.Dispose(); lan = null;
        A.Peers.Clear(); A.Incoming = null; A.Pending = null; A.Accepted = null;
    }

    void SayHello() => lan?.Broadcast(new { t = "hello", id = lan.Id, name = MyName, busy = arena != null || A.Pending != null || A.Accepted != null, look = Lan.LookMsg(MyLook()), v = 1 });

    void LobbyTick()
    {
        SayHello();
        var now = DateTime.Now;
        var before = A.Shape;
        A.Peers.RemoveAll(p => (now - p.Seen).TotalSeconds > 4.5);
        if (A.Incoming != null && (now - A.IncomingAt).TotalSeconds > 20) A.Incoming = null;
        if (A.Pending != null && (now - A.PendingAt).TotalSeconds > 20) { A.Message = A.Pending.Name + " hat nicht geantwortet."; A.Pending = null; }
        if (A.Accepted != null && arena == null && (now - A.AcceptedAt).TotalSeconds > 8) { A.Message = "Der Kampf mit " + A.Accepted.Name + " kam nicht zustande."; A.Accepted = null; }
        // Dev: the computer challenges whoever it finds.
        if (ArenaAuto == "host" && arena == null && A.Pending == null && A.Peers.FirstOrDefault(p => !p.Busy) is { } first) Challenge(first);
        if (A.Shape != before) Panel?.Refresh();
    }

    void Challenge(LanPeer p)
    {
        if (lan == null || arena != null || p == null) return;
        A.Pending = p; A.PendingSid = Guid.NewGuid().ToString("N")[..12]; A.PendingAt = DateTime.Now; A.Message = null;
        var (w, h) = MaxArena();
        lan.Send(p.End, new { t = "chal", id = lan.Id, name = MyName, look = Lan.LookMsg(MyLook()), sid = A.PendingSid, w, h });
        Model.Changed();
    }

    void AcceptChallenge()
    {
        if (lan == null || A.Incoming == null) return;
        var (w, h) = MaxArena();
        A.Accepted = A.Incoming; A.AcceptedSid = A.IncomingSid; A.AcceptedAt = DateTime.Now;
        lan.Send(A.Incoming.End, new { t = "ans", id = lan.Id, sid = A.IncomingSid, ok = true, name = MyName, look = Lan.LookMsg(MyLook()), w, h });
        A.Incoming = null; A.Message = null;
        Model.Changed();
    }

    void OnLan(string type, JsonElement msg, IPEndPoint from)
    {
        if (lan == null) return;
        var id = Lan.Str(msg, "id", 20);
        if (string.IsNullOrEmpty(id)) return;
        var msid = Lan.Str(msg, "sid", 20);
        var now = clock.Elapsed.TotalSeconds;
        switch (type)
        {
            case "hello":
            {
                var name = Lan.CleanName(Lan.Str(msg, "name"));
                if (name == "") return;
                var p = A.Peers.FirstOrDefault(x => x.Id == id);
                if (p == null) { p = new LanPeer { Id = id }; A.Peers.Add(p); }
                var changed = p.Name != name || p.Busy != Lan.Bool(msg, "busy");
                p.Name = name; p.End = from; p.Look = Lan.ReadLook(msg, "look"); p.Busy = Lan.Bool(msg, "busy"); p.Seen = DateTime.Now;
                if (changed || A.Peers.Count == 1) Panel?.Refresh();
                break;
            }
            case "chal":
            {
                var name = Lan.CleanName(Lan.Str(msg, "name"));
                if (name == "" || msid == null) return;
                if (arena != null || A.Incoming != null || A.Pending != null || A.Accepted != null || Model.Stage != Stage.Ready || Model.Page != Page.Arena)
                {
                    lan.Send(from, new { t = "ans", id = lan.Id, sid = msid, ok = false, why = "busy" });
                    return;
                }
                A.Incoming = new LanPeer { Id = id, Name = name, End = from, Look = Lan.ReadLook(msg, "look"), Seen = DateTime.Now };
                A.IncomingSid = msid; A.IncomingAt = DateTime.Now;
                if (ArenaAuto == "guest") { AcceptChallenge(); return; }
                if (ArenaAuto == null) { Panel.Show(); Panel.Activate(); System.Media.SystemSounds.Exclamation.Play(); }
                Model.Changed();
                break;
            }
            case "ans":
            {
                if (A.Pending == null || msid != A.PendingSid || id != A.Pending.Id) return;
                var p = A.Pending; A.Pending = null;
                if (!Lan.Bool(msg, "ok")) { A.Message = p.Name + (Lan.Str(msg, "why") == "busy" ? " ist gerade beschäftigt." : " hat abgelehnt."); Model.Changed(); return; }
                p.End = from; p.Look = Lan.ReadLook(msg, "look");
                var n = Lan.CleanName(Lan.Str(msg, "name")); if (n != "") p.Name = n;
                var (w, h) = MaxArena();
                w = MathF.Min(w, Lan.Num(msg, "w", 700, 1300, w)); h = MathF.Min(h, Lan.Num(msg, "h", 420, 760, h));
                StartMatch(true, p, msid, w, h);
                break;
            }
            case "st":
            {
                if (arena == null)
                {
                    // The host has started: join in.
                    if (A.Accepted == null || msid != A.AcceptedSid || id != A.Accepted.Id) return;
                    var p = A.Accepted; A.Accepted = null; p.End = from;
                    StartMatch(false, p, msid, Lan.Num(msg, "w", 700, 1300, 1000), Lan.Num(msg, "h", 420, 760, 600));
                }
                if (arena != null && !arenaHost && msid == sid && id == foe.Id) { arena.ApplySnapshot(msg, now); lastHeard = now; }
                break;
            }
            case "in":
                if (arena != null && arenaHost && msid == sid && id == foe.Id)
                {
                    var a = arena.Arena;
                    a.ThB = Lan.Num(msg, "th", -1, 1); a.StB = Lan.Num(msg, "st", -1, 1); a.BoB = Lan.Bool(msg, "bo");
                    lastHeard = now;
                }
                break;
            case "bye":
                if (arena != null && msid == sid && id == foe.Id) EndMatch(foe.Name + " hat den Kampf verlassen. Du gewinnst!", 10);
                else if (A.Incoming != null && msid == A.IncomingSid) { A.Incoming = null; Model.Changed(); }
                break;
        }
    }

    // MARK: The fight

    void StartMatch(bool host, LanPeer other, string session, float w, float h, bool practice = false)
    {
        if (arena != null) return;
        var m = Model;
        try
        {
            foe = other; sid = session; arenaHost = host; arenaPractice = practice;
            var info = new ArenaInfo { Host = host, Practice = practice, NameA = host ? MyName : other.Name, NameB = host ? other.Name : MyName };
            if (info.NameA == "") info.NameA = "Du";
            if (info.NameB == "") info.NameB = "Du";
            var mine = MyLook();
            var s = Sim.MakeArena(w, h, info, host ? mine : other.Look, host ? other.Look : mine, new V3(0.2f, 0.85f, 1), new V3(1, 0.3f, 0.25f));
            if (!host)
            {
                // The guest's own robot is B: it is the one drawn as "this" robot here.
                (s.Me, s.Foe) = (s.Foe, s.Me);
                s.Me.Led = new V3(0.2f, 0.85f, 1); s.Foe.Led = new V3(1, 0.3f, 0.25f);
            }
            s.Look = s.Me.Look;
            if (ArenaAuto == "host") info.CpuA = true;
            s.Audio = synth; synth.Muted = m.Muted; synth.StartLive();
            var screen = ArenaScreen();
            var o = new Overlay(screen) { Screens = new() { screen } };
            o.Prepare(); o.MakeCurrent();
            using (var wall = SafeWallpaper(screen)) o.Renderer.SetWallpaper(wall);
            o.Renderer.SetSprites(new List<Sprite>());
            o.Renderer.Look = s.Me.Look; o.Renderer.Skin = s.Me.Look.Skin;
            var size = screen.SizePts;
            o.Renderer.Origin = -new V2((size.X - w) / 2, (size.Y - h) / 2 + 20);
            o.Sim = s;
            o.OnEscape = () => A.Forfeit();
            o.OnSpace = null;
            arena = s; arenaOverlay = o;
            A.Fighting = true; A.Pending = null; A.Incoming = null; A.Accepted = null; A.Message = null;
            if (m.ClearWindows && ArenaAuto == null) MinimizeWindows();
            o.ShowAboveDesktop();
            if (ArenaAuto == null) { Panel.Hide(); o.Activate(); }
            lastHeard = clock.Elapsed.TotalSeconds; netAcc = 0; arenaLast = clock.Elapsed.TotalSeconds; arenaAcc = 0;
            System.Windows.Media.CompositionTarget.Rendering += OnArenaFrame;
            SayHello();
            m.Changed();
        }
        catch (Exception e)
        {
            arena = null;
            CloseArena();
            A.Message = "Der Kampf konnte nicht starten: " + e.Message;
            m.Changed();
        }
    }

    void OnArenaFrame(object sender, EventArgs e)
    {
        var s = arena; var o = arenaOverlay;
        if (s == null || o == null) return;
        var a = s.Arena;
        float th, st; bool bo;
        if (ArenaAuto != null) s.CpuForMe(out th, out st, out bo);
        else { th = ArrowKeys.Throttle(); st = ArrowKeys.Steer(); bo = ArrowKeys.Turbo(); }
        if (arenaHost) s.ArenaInput(th, st, bo);
        var dt = Overlay.Step(s, new List<Overlay> { o }, clock, ref arenaLast, ref arenaAcc);
        o.Render(dt);
        var now = clock.Elapsed.TotalSeconds;
        if (!arenaPractice)
        {
            netAcc += dt;
            if (netAcc >= 1 / 30.0)
            {
                netAcc = 0;
                if (arenaHost) lan?.Send(foe.End, s.Snapshot(sid, lan?.Id));
                else lan?.Send(foe.End, new { t = "in", id = lan?.Id, sid, th, st, bo });
            }
            if (now - lastHeard > 6) { EndMatch("Die Verbindung zu " + foe.Name + " ist abgerissen.", 0); return; }
        }
        if (a.Phase == ArenaPhase.MatchOver && a.PhaseT > 2.5f)
        {
            var iWon = a.Winner == (arenaHost ? 0 : 1);
            var coins = arenaPractice ? (iWon ? 3 : 1) : (iWon ? 20 : 5);
            EndMatch(iWon ? $"Du hast {foe.Name} besiegt, {a.RoundsA}:{a.RoundsB}!" : $"{foe.Name} hat gewonnen, {a.RoundsA}:{a.RoundsB}.", coins);
        }
    }

    void EndMatch(string message, int coins)
    {
        if (arena == null) return;
        var s = arena;
        if (coins > 0) { Model.Settings.Coins += coins; Model.Settings.Save(); message += $" +{coins} Münzen."; }
        if (ArenaAuto != null)
        {
            Console.WriteLine($"ARENA {ArenaAuto}: {message} rounds {s.Arena.RoundsA}:{s.Arena.RoundsB} round {s.Arena.Round} me {s.Me.Name} hp {s.Me.Hp:0.00} foe {s.Foe.Name} hp {s.Foe.Hp:0.00}");
            Delay(1.5, () => Quit());
        }
        CloseArena();
        A.Message = message;
        A.Fighting = false;
        if (ArenaAuto == null) { Panel.Show(); Panel.Activate(); }
        SayHello();
        Model.Changed();
    }

    void CloseArena()
    {
        System.Windows.Media.CompositionTarget.Rendering -= OnArenaFrame;
        synth.SetMotor(0, 0); synth.StopLive();
        if (arenaOverlay != null) { arenaOverlay.Sim = null; arenaOverlay.Hide(); arenaOverlay.Dispose(); arenaOverlay = null; }
        arena = null;
        RestoreWindows();
    }
}
