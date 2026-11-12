using System;
using System.Collections.Generic;
using System.Linq;

namespace RoboDust;

/// A rival robot for the race. Simpler than RoboDust: it chases the nearest icon and swallows it.
/// What it collects goes into RoboDust's dock with everything else, so the real outcome is the same.
public sealed class Rival
{
    public string Name;
    public V2 Pos, Home;
    public float Heading = -MathF.PI / 2, V, W, R;
    public float BrushAngle, BrushSpeed, LidarAngle, LedPulse;
    public V3 Led;
    public RobotSkin Skin;
    public int Score;
    public Item Target;
    public V2 Fwd => new(MathF.Cos(Heading), MathF.Sin(Heading));
    public V2 Side => Fwd.Perp;
    public V2 Intake => Pos + Fwd * (R * 0.8f);
    public (V2 l, V2 r) BrushCenters { get { var a = Fwd * (R * 0.62f); return (Pos + a + Side * (R * 0.72f), Pos + a - Side * (R * 0.72f)); } }
}

public sealed partial class Sim
{
    public readonly List<Rival> Rivals = new();
    public RobotSkin Skin;    // RoboDust's own look, so the rivals can pick different ones
    bool raceAnnounced;

    public V2 IntakeFor(Item it) => it.TakenBy >= 0 && it.TakenBy < Rivals.Count ? Rivals[it.TakenBy].Intake : Intake;

    public int OwnScore => Collected - Rivals.Sum(r => r.Score);

    /// "Du 12 · Blitz 9 · Turbo 7", for the status line.
    public string RaceLine => Rivals.Count == 0 ? null : "RoboDust " + OwnScore + " · " + string.Join(" · ", Rivals.Select(r => $"{r.Name} {r.Score}"));

    public string Winner
    {
        get
        {
            var best = ("RoboDust", OwnScore);
            foreach (var r in Rivals) if (r.Score > best.Item2) best = (r.Name, r.Score);
            return best.Item1;
        }
    }

    void SetupRace()
    {
        var skins = Enum.GetValues<RobotSkin>().Where(s => s != Skin).ToList();
        var specs = new[] { ("Blitz", new V3(1, 0.32f, 0.28f), skins[0]), ("Turbo", new V3(0.4f, 1, 0.45f), skins[Math.Min(1, skins.Count - 1)]) };
        int side = -1;
        foreach (var (name, led, skin) in specs)
        {
            // Parked either side of the dock, facing the room.
            var home = Plan.Clamp(DockPos + DockOut.Perp * (R * 3.3f * side) + DockOut * (R * 0.6f), R * 0.9f);
            Rivals.Add(new Rival { Name = name, Led = led, Skin = skin, R = R * 0.85f, Pos = home, Home = home, Heading = MathF.Atan2(DockOut.Y, DockOut.X) });
            side = 1;
        }
    }

    void UpdateRace(float dt)
    {
        if (Rivals.Count == 0) SetupRace();
        var racing = Phase is Phase.Cleaning or Phase.Hunting or Phase.Returning or Phase.Docking or Phase.Emptying && !Paused;
        if (Phase == Phase.Cleaning && !raceAnnounced) { raceAnnounced = true; Audio?.Chirp((392, 0.1f), (392, 0.1f), (392, 0.1f), (784, 0.3f)); }
        for (int i = 0; i < Rivals.Count; i++)
        {
            var r = Rivals[i];
            r.LedPulse += dt;
            r.LidarAngle += 14 * dt;
            r.BrushSpeed += ((racing ? 26 : 0) - r.BrushSpeed) * MathF.Min(1, dt * 3);
            r.BrushAngle += r.BrushSpeed * dt;
            if (!racing) { Drive(r, r.Home, dt, park: true); continue; }
            // A target nobody else is after.
            if (r.Target == null || !r.Target.OnFloor || r.Target.Icon.Ingesting || r.Target.Burning || r.Target.Carried || r.Target.Grabbed)
            {
                r.Target = null; float bd = float.PositiveInfinity;
                foreach (var it in Items)
                {
                    if (!it.OnFloor || it.Icon.Ingesting || it.Burning || it.Carried || it.Grabbed || it == huntTarget) continue;
                    if (Rivals.Any(o => o != r && o.Target == it)) continue;
                    var d = (it.Icon.Pos - r.Pos).Len;
                    if (d < bd) { bd = d; r.Target = it; }
                }
            }
            if (r.Target == null) { Drive(r, r.Home, dt, park: true); continue; }
            Drive(r, r.Target.Icon.Pos, dt, park: false);
            if ((r.Target.Icon.Pos - r.Intake).Len < r.R * 0.8f) RivalTake(r.Target, i);
        }
    }

    void Drive(Rival r, V2 to, float dt, bool park)
    {
        var route = Plan.Route(r.Pos, to, r.R + 2);
        var aim = route[0];
        var d = aim - r.Pos;
        float vCmd, wCmd;
        if (park && d.Len < 6)
        {
            vCmd = 0;
            wCmd = M.Clamp(M.Wrap(MathF.Atan2(DockOut.Y, DockOut.X) - r.Heading) * 3, -2, 2);
        }
        else
        {
            var err = M.Wrap(MathF.Atan2(d.Y, d.X) - r.Heading);
            wCmd = M.Clamp(err * 4.2f, -3.2f, 3.2f);
            var align = M.Clamp(1 - MathF.Abs(err) / 1.2f, 0, 1);
            vCmd = 320 * SpeedScale * 0.94f * align * align;
            if (park) vCmd = MathF.Min(vCmd, d.Len * 3);
        }
        r.V += M.Clamp(vCmd - r.V, -700 * dt, 700 * dt);
        r.W += M.Clamp(wCmd - r.W, -12 * dt, 12 * dt);
        r.Heading = M.Wrap(r.Heading + r.W * dt);
        r.Pos += r.Fwd * (r.V * dt);
        // Robots don't drive through each other, or over the dock.
        var bots = new List<(V2 p, float rr)> { (Pos, R) };
        bots.AddRange(Rivals.Where(o => o != r).Select(o => (o.Pos, o.R)));
        foreach (var (p, rr) in bots)
        {
            var e = r.Pos - p; var min = rr + r.R + 4;
            if (e.Len < min && e.Len > 0.01f) r.Pos = p + e.Unit * min;
        }
        var k = r.Pos - DockPos;
        float u = k.Dot(DockOut), s = k.Dot(DockOut.Perp);
        if (u < R * 1.6f && u > -R * 2.4f && MathF.Abs(s) < R * 1.5f + r.R * 0.5f) r.Pos += DockOut * (R * 1.6f - u);
        r.Pos = Plan.Clamp(r.Pos, r.R + 2);
        // Paper in the way gets shoved, like it does by RoboDust.
        foreach (var it in Items)
        {
            if (!it.OnFloor || it.Icon.Ingesting || it.Burning || it.Carried || it == r.Target) continue;
            var e = it.Icon.Pos - r.Pos;
            if (e.Len < r.R + 14 && e.Len > 0.01f) { it.Icon.Pos = r.Pos + e.Unit * (r.R + 14); it.Icon.Agit = MathF.Max(it.Icon.Agit, 0.4f); }
        }
    }

    void RivalTake(Item it, int who)
    {
        if (it.Icon.Ingesting || it.Protected) return;
        it.TakenBy = who;
        it.Icon.Ingesting = true;
        it.Icon.Pull = 0.001f;
        // Its label goes with it, straight away.
        it.LabelAttached = false; it.Label.Gone = true;
        Audio?.Ingest(it.Mass * 0.7f);
        var r = Rivals[who];
        for (int k = 0; k < 6; k++)
        {
            var dir = (r.Intake - it.Icon.Pos).Unit.Rotated(Rand(-1.2f, 1.2f));
            Spawn(it.Icon.Pos + new V2(Rand(-20, 20), Rand(-20, 20)), dir * Rand(80, 260), Rand(0.2f, 0.45f), Rand(2, 4), it.Color.X, it.Color.Y, it.Color.Z, 0.95f, 1);
        }
    }

    void RivalGot(Item it)
    {
        var r = Rivals[it.TakenBy];
        r.Score++;
        it.TakenBy = -1;
        // Handed over: it rides in RoboDust's bin from here, without filling it.
        it.InBin = true; it.ViaRival = true;
        Collected++; CollectedBytes += it.Entry.Bytes;
    }

    /// At the end: the winner gets the confetti.
    void RaceDone()
    {
        if (Rivals.Count == 0) return;
        var w = Winner;
        var at = w == "RoboDust" ? Pos : Rivals.First(r => r.Name == w).Pos;
        ConfettiBurst(at, 120, 700, DockOut, 3.1f);
        Audio?.Popper();
    }
}
