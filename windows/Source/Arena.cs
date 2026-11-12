using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RoboDust;

public enum ArenaPhase { Countdown, Fight, RoundOver, MatchOver }

/// A robot fight in an arena: best of three, driven with the arrow keys. One side (the host, or
/// this PC in a practice fight) works the fight out; the other side only shows what it's sent.
public sealed class ArenaInfo
{
    public bool Host = true, Practice, CpuA;   // CpuA: dev only, the computer drives the host's robot too
    public float W, H;
    public string NameA = "Du", NameB = "Gegner";   // A is the host's robot, on the left
    public int RoundsA, RoundsB, Round = 1;
    public ArenaPhase Phase = ArenaPhase.Countdown;
    public float PhaseT;
    public string Banner = "";
    public float BannerT;
    public int Winner = -1;   // 0 A, 1 B
    // Inputs: throttle, steer, turbo.
    public float ThA, StA, ThB, StB; public bool BoA, BoB;
    public float BoostCdA, BoostCdB, BoostTA, BoostTB;
    /// What happened this frame, for the other side to show too: kind, x, y, amount.
    public readonly List<float[]> Events = new();
    // The guest's latest picture of the fight.
    public JsonElement? Latest; public double LatestAt;
    public const int RoundsToWin = 2;
}

public sealed partial class Sim
{
    public ArenaInfo Arena;

    /// A fresh arena of this size, with the two robots facing each other.
    public static Sim MakeArena(float w, float h, ArenaInfo info, Look a, Look b, V3 ledA, V3 ledB)
    {
        var s = new Sim(new V2(w, h), V2.Zero, new V2(w, h), new List<DesktopEntry>())
        {
            Arena = info, Fun = FunMode.Duel, DockDrop = 1, DockAlpha = 0, Phase = Phase.Cleaning,
        };
        info.W = w; info.H = h;
        s.Look = a;
        s.Me = new Fighter { Name = info.NameA, R = s.R, Look = a, Led = ledA };
        s.Foe = new Fighter { Name = info.NameB, R = s.R, Look = b, Led = ledB };
        if (s.Me.Look.Weapon == 0) s.Me.Look.Weapon = 5;
        if (s.Foe.Look.Weapon == 0) s.Foe.Look.Weapon = 5;
        s.ResetRound();
        s.Banner("3");
        return s;
    }

    void Banner(string text) { Arena.Banner = text; Arena.BannerT = 0; }

    void ResetRound()
    {
        var a = Arena;
        foreach (var (f, x, h) in new[] { (Me, a.W * 0.2f, 0f), (Foe, a.W * 0.8f, MathF.PI) })
        {
            f.Pos = new V2(x, a.H * 0.5f); f.Heading = h; f.V = 0; f.W = 0; f.Knock = V2.Zero;
            f.Hp = 1; f.Down = false; f.KoT = 0; f.Stun = 0; f.Air = 0; f.Strike = -1; f.Swing = 0; f.Cool = 0.5f; f.Alpha = 1; f.Grace = 0;
        }
        Pos = Me.Pos; Heading = Me.Heading; V = 0;
        a.Phase = ArenaPhase.Countdown; a.PhaseT = 0;
        a.BoostCdA = a.BoostCdB = 0; a.BoostTA = a.BoostTB = 0;
    }

    /// The local player's keys (and, in a practice fight, the computer's).
    public void ArenaInput(float throttle, float steer, bool turbo)
    {
        if (Arena == null) return;
        if (Arena.Host) { Arena.ThA = throttle; Arena.StA = steer; Arena.BoA = turbo; }
    }

    void ArenaStep(float dt)
    {
        var a = Arena;
        T += dt; PhaseT += dt; a.PhaseT += dt; a.BannerT += dt;
        if (a.Host) HostStep(a, dt); else GuestStep(a, dt);
        // What both sides do for themselves: brushes, lidar, smoke from a beaten robot.
        foreach (var f in new[] { Me, Foe })
        {
            f.LedPulse += dt; f.LidarAngle += 14 * dt;
            f.BrushSpeed += ((f.Down ? 0 : 26) - f.BrushSpeed) * MathF.Min(1, dt * 3);
            f.BrushAngle += f.BrushSpeed * dt;
            if (f.Down && Rand() < dt * 20) SpawnFun(PartKind.Smoke, f.Pos + new V2(Rand(-f.R * 0.5f, f.R * 0.5f), Rand(-f.R * 0.5f, f.R * 0.5f)), new V2(Rand(-20, 20), Rand(-70, -25)), Rand(1.2f, 2), Rand(14, 22), 0.22f, 0.22f, 0.24f, 0.75f);
        }
        Pos = Me.Pos; Heading = Me.Heading; V = Me.V;
        BrushAngle = Me.BrushAngle; BrushSpeed = Me.BrushSpeed; LidarAngle = Me.LidarAngle; LedPulse = Me.LedPulse;
        Led = Me.Down ? new V3(1, 0.12f, 0.1f) : Me.Led;
        UpdateParticles(dt);
        Bumper *= MathF.Exp(-dt * 7); Shake *= MathF.Exp(-dt * 8);
        Audio?.SetMotor(a.Phase == ArenaPhase.Fight ? 0.5f + 0.5f * MathF.Min(1, MathF.Abs(V) / 380) : 0.15f, 0);
    }

    void HostStep(ArenaInfo a, float dt)
    {
        if (a.Practice) CpuDrive(Foe, Me, out a.ThB, out a.StB, out a.BoB);
        if (a.CpuA) CpuDrive(Me, Foe, out a.ThA, out a.StA, out a.BoA);
        var fighting = a.Phase == ArenaPhase.Fight;
        a.BoostCdA -= dt; a.BoostCdB -= dt;
        DriveFighter(Me, fighting ? a.ThA : 0, fighting ? a.StA : 0, fighting && a.BoA, ref a.BoostCdA, ref a.BoostTA, dt);
        DriveFighter(Foe, fighting ? a.ThB : 0, fighting ? a.StB : 0, fighting && a.BoB, ref a.BoostCdB, ref a.BoostTB, dt);
        Tick(Me, dt, true); Tick(Foe, dt, true);
        switch (a.Phase)
        {
            case ArenaPhase.Countdown:
                var n = 3 - (int)a.PhaseT;
                if (n > 0 && a.Banner != n.ToString()) { Banner(n.ToString()); Audio?.Chirp((440, 0.12f)); }
                if (a.PhaseT >= 3) { a.Phase = ArenaPhase.Fight; a.PhaseT = 0; Banner("KAMPF!"); Audio?.Chirp((880, 0.25f)); Event(5, 0, 0, 0); }
                break;
            case ArenaPhase.Fight:
                if (a.BannerT > 1.2f && a.Banner != "") Banner("");
                Attack(Me, Foe, dt);
                Attack(Foe, Me, dt);
                SeparateFighters();
                if (Me.Down || Foe.Down)
                {
                    var aWon = Foe.Down;
                    if (aWon) a.RoundsA++; else a.RoundsB++;
                    a.Phase = ArenaPhase.RoundOver; a.PhaseT = 0;
                    var done = a.RoundsA >= ArenaInfo.RoundsToWin || a.RoundsB >= ArenaInfo.RoundsToWin;
                    if (done) { a.Winner = aWon ? 0 : 1; Banner((aWon ? a.NameA : a.NameB) + " gewinnt!"); }
                    else Banner("Runde für " + (aWon ? a.NameA : a.NameB));
                }
                break;
            case ArenaPhase.RoundOver:
                if (a.PhaseT >= 3)
                {
                    if (a.Winner >= 0) { a.Phase = ArenaPhase.MatchOver; a.PhaseT = 0; }
                    else { a.Round++; ResetRound(); Banner("3"); }
                }
                break;
        }
    }

    void DriveFighter(Fighter f, float th, float st, bool turbo, ref float boostCd, ref float boostT, float dt)
    {
        if (f.Down) { th = 0; st = MathF.Max(0, 3 - f.KoT); f.KoT += dt; }
        else if (f.Stun > 0 || f.Air > 0) { th = 0; st = 0; }
        boostT -= dt;
        if (turbo && boostCd <= 0 && !f.Down) { boostT = 0.35f; boostCd = 3; Event(4, f.Pos.X, f.Pos.Y, 0); }
        var vCmd = th * 380;
        if (boostT > 0) vCmd = 780;
        f.V += M.Clamp(vCmd - f.V, -950 * dt, 950 * dt);
        f.W += M.Clamp(st * 3.3f - f.W, -15 * dt, 15 * dt);
        f.Heading = M.Wrap(f.Heading + f.W * dt);
        f.Pos += f.Fwd * (f.V * dt) + f.Knock * dt;
        f.Knock *= MathF.Exp(-dt * 5);
        // Arena walls: bounce off with a thud.
        var c = Plan.Clamp(f.Pos, f.R + 4);
        if ((c - f.Pos).Len > 0.5f)
        {
            if (MathF.Abs(f.V) > 160 || f.Knock.Len > 160) { Event(3, c.X, c.Y, 0.3f); Audio?.Thud(0.3f); }
            f.V *= -0.25f; f.Knock = V2.Zero;
            f.Pos = c;
        }
    }

    void SeparateFighters()
    {
        var e = Foe.Pos - Me.Pos; var min = Foe.R + Me.R + 2;
        if (e.Len >= min || e.Len < 0.01f) return;
        var push = e.Unit * ((min - e.Len) * 0.5f);
        Foe.Pos = Plan.Clamp(Foe.Pos + push, Foe.R + 4);
        Me.Pos = Plan.Clamp(Me.Pos - push, Me.R + 4);
    }

    /// The computer opponent: heads for you, and goes for it with the turbo when it has a clear run.
    void CpuDrive(Fighter self, Fighter target, out float th, out float st, out bool turbo)
    {
        var d = target.Pos - self.Pos;
        var want = MathF.Atan2(d.Y, d.X);
        // Saws and wedges want to meet you head on; it circles a little so it isn't a sitting duck.
        if (d.Len > self.R * 3) want += MathF.Sin(T * 0.9f) * 0.5f;
        var err = M.Wrap(want - self.Heading);
        st = M.Clamp(err * 2.2f, -1, 1);
        th = MathF.Abs(err) < 1.1f ? 1 : 0.2f;
        if (self.Hp < 0.3f && target.Hp > 0.5f && d.Len < self.R * 3) th = -0.6f;   // backs off when it's losing
        turbo = d.Len < self.R * 4.5f && MathF.Abs(err) < 0.2f && Rand() < 0.05f;
    }

    public void CpuForMe(out float th, out float st, out bool turbo) => CpuDrive(Me, Foe, out th, out st, out turbo);

    /// Recorded on the host so the guest sees the same sparks: 1 sparks, 2 dust, 3 thud, 4 turbo, 5 go, 6 K.o.
    void Event(int kind, float x, float y, float amount)
    {
        if (Arena is { Host: true } a && a.Events.Count < 60) a.Events.Add(new[] { kind, x, y, amount });
    }

    // MARK: Over the network

    static object FighterMsg(Fighter f) => new
    {
        x = MathF.Round(f.Pos.X, 1), y = MathF.Round(f.Pos.Y, 1), h = MathF.Round(f.Heading, 3), v = MathF.Round(f.V), w = MathF.Round(f.W, 2),
        hp = MathF.Round(f.Hp, 3), sp = MathF.Round(f.Spin % 6.2832f, 2), sw = MathF.Round(f.Swing, 2), air = MathF.Round(f.Air, 2), st = MathF.Round(f.Stun, 2),
        dn = f.Down, fl = MathF.Round(f.Flash, 2),
    };

    /// The host's picture of the fight, ~30 times a second.
    public object Snapshot(string sid, string id)
    {
        var a = Arena;
        var ev = a.Events.ToArray(); a.Events.Clear();
        return new { t = "st", id, sid, w = a.W, h = a.H, ph = (int)a.Phase, pt = MathF.Round(a.PhaseT, 2), r = a.Round, ra = a.RoundsA, rb = a.RoundsB, win = a.Winner, bn = a.Banner, a = FighterMsg(Me), b = FighterMsg(Foe), ev };
    }

    /// The guest takes the host's picture. Its own robot is B; the host's is A.
    public void ApplySnapshot(JsonElement m, double now)
    {
        var a = Arena;
        a.Phase = (ArenaPhase)(int)Lan.Num(m, "ph", 0, 3);
        a.PhaseT = Lan.Num(m, "pt", 0, 1000);
        a.Round = (int)Lan.Num(m, "r", 1, 9, 1);
        a.RoundsA = (int)Lan.Num(m, "ra", 0, 9); a.RoundsB = (int)Lan.Num(m, "rb", 0, 9);
        a.Winner = (int)Lan.Num(m, "win", -1, 1, -1);
        var bn = Lan.Str(m, "bn", 40) ?? "";
        if (bn != a.Banner) Banner(bn);
        a.Latest = m; a.LatestAt = now;
        if (m.TryGetProperty("ev", out var ev) && ev.ValueKind == JsonValueKind.Array)
        {
            int n = 0;
            foreach (var e in ev.EnumerateArray())
            {
                if (++n > 60 || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 4) break;
                float F(int i) { var x = e[i]; return x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var d) && double.IsFinite(d) ? (float)Math.Clamp(d, -10000, 10000) : 0; }
                var at = new V2(F(1), F(2));
                switch ((int)F(0))
                {
                    case 1: SparksLocal(at, (int)M.Clamp(F(3), 1, 30), 380); break;
                    case 2: DustLocal(at, 40, (int)M.Clamp(F(3), 1, 24)); break;
                    case 3: Audio?.Thud(M.Clamp(F(3), 0, 1)); break;
                    case 4: DustLocal(at, 50, 8); break;
                    case 5: Audio?.Chirp((880, 0.25f)); break;
                    case 6: SparksLocal(at, 30, 520); Audio?.Popper(); Audio?.Thud(0.9f); break;
                }
            }
        }
    }

    void GuestStep(ArenaInfo a, float dt)
    {
        if (a.Latest is not { } m) return;
        Follow(Me, m, "b", dt);
        Follow(Foe, m, "a", dt);
    }

    /// Glides towards where the host says the robot is, a little ahead by its speed, so it moves smoothly.
    void Follow(Fighter f, JsonElement m, string key, float dt)
    {
        if (!m.TryGetProperty(key, out var o) || o.ValueKind != JsonValueKind.Object) return;
        var target = new V2(Lan.Num(o, "x", -100, 5000), Lan.Num(o, "y", -100, 5000));
        var h = Lan.Num(o, "h", -10, 10);
        f.V = Lan.Num(o, "v", -2000, 2000); f.W = Lan.Num(o, "w", -50, 50);
        var k = MathF.Min(1, dt * 16);
        if ((target - f.Pos).Len > f.R * 3) f.Pos = target; else f.Pos += (target - f.Pos) * k + new V2(MathF.Cos(f.Heading), MathF.Sin(f.Heading)) * (f.V * dt * (1 - k));
        f.Heading = M.Wrap(f.Heading + M.Wrap(h - f.Heading) * k);
        var hp = Lan.Num(o, "hp", 0, 1, 1);
        if (hp < f.Hp - 0.001f) f.Flash = 1;
        f.Hp = hp;
        f.Spin += (M.Wrap(Lan.Num(o, "sp", -10, 10) - f.Spin % 6.2832f)) * k;
        f.Swing = Lan.Num(o, "sw", 0, 1); f.Air = Lan.Num(o, "air", 0, 2); f.Stun = Lan.Num(o, "st", 0, 5);
        var down = Lan.Bool(o, "dn");
        if (down && !f.Down) f.KoT = 0;
        f.Down = down;
        f.Flash *= MathF.Exp(-dt * 6);
    }

    void SparksLocal(V2 at, int n, float speed)
    {
        for (int i = 0; i < n; i++)
        {
            var ang = Rand(0, 6.283f); var hot = Rand();
            SpawnFun(PartKind.Glow, at, new V2(MathF.Cos(ang), MathF.Sin(ang)) * Rand(speed * 0.3f, speed), Rand(0.15f, 0.4f), Rand(1.4f, 2.6f), 1, 0.75f + hot * 0.25f, 0.3f + hot * 0.5f, 1);
        }
    }

    void DustLocal(V2 at, float r, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var ang = Rand(0, 6.283f);
            SpawnFun(PartKind.Smoke, at + new V2(MathF.Cos(ang), MathF.Sin(ang)) * r, new V2(MathF.Cos(ang), MathF.Sin(ang)) * Rand(40, 120), Rand(0.5f, 0.9f), Rand(8, 13), 0.7f, 0.68f, 0.64f, 0.45f);
        }
    }
}
