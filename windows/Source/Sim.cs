using System;
using System.Collections.Generic;
using System.Linq;

namespace RoboDust;

/// On Windows nothing is ever deleted. Every outcome can be undone.
public enum CleanMode { Tidy = 0, Move = 1, Hide = 2, Sort = 3 }

/// How "In Ordner" groups things.
public enum SortBy { Type = 0, Alphabet = 1, Date = 2 }

public static class CleanModeText
{
    public static string Title(this CleanMode m) => m switch { CleanMode.Tidy => "Ordnen", CleanMode.Move => "Wegräumen", CleanMode.Sort => "In Ordner", _ => "Ausblenden" };
}

public enum Phase { Dropping, Undocking, Scanning, Cleaning, Hunting, Returning, Docking, Emptying, Done, Tidying, Tidied, PuttingBack, Leaving, Finished }

/// One moving sheet of paper: an icon, or the label that tears off it.
public struct Body
{
    public V2 Pos, Vel;
    public float Ang, AngV;
    public float Agit;      // 0 calm ... 1 being tugged hard
    public float Pull;      // 0...1 through the intake
    public bool Ingesting, Gone;
    public float Seed;
}

public enum Flight { None, ToBag, ToHome, ToTidy }

/// Icon and label sizes, in points. Set from Explorer's real icon size before a run.
public static class Metrics
{
    public static float IconHalf = 24;
    public static V2 LabelHalf = new(38, 19);
    public static float LabelOffset => IconHalf + 3 + LabelHalf.Y;
}

public sealed class Item
{
    public readonly int Index;
    public readonly DesktopEntry Entry;
    public readonly V2 Home;
    public readonly float Mass, IngestTime;
    public V3 Color = new(0.6f, 0.6f, 0.6f);
    public Body Icon, Label;
    public bool LabelAttached = true;
    public float LabelLost, Ping;
    public bool Seen, InBin, InDock, Grabbed;
    public Flight Flight = Flight.None;
    public V2 FlightFrom, FlightTo;
    public float FlightT, FlightDelay, FlightDur = 1, FlightScale = 1, FlightSpin;
    public float Land;
    public V2 Rest;
    public bool Protected;

    public Item(int index, DesktopEntry entry)
    {
        Index = index; Entry = entry; Home = entry.Center; Rest = entry.Center;
        var mb = Math.Max(entry.Bytes, 1) / 1_000_000f;
        Mass = 1 + 5 * M.Clamp(MathF.Log10(1 + mb) / 4, 0, 1);
        IngestTime = 0.42f + 0.22f * (Mass - 1);
        Icon = new Body { Pos = entry.Center, Seed = index * 1.618f };
        Label = new Body { Pos = entry.Center + new V2(0, Metrics.LabelOffset), Seed = index * 2.414f };
    }
    public bool Cleared => Icon.Gone && (Label.Gone || LabelAttached);
    public bool OnFloor => !InBin && !InDock && Flight == Flight.None && !Icon.Gone && !Protected;
}

public struct Particle
{
    public V2 Pos, Vel;
    public float Life, MaxLife, Size;
    public float R, G, B, A;
    public float Chip, Ang;
}

public interface ISimAudio
{
    void SetMotor(float level, float load);
    void SetRoar(float level);
    void Ingest(float mass);
    void Tick();
    void Chirp(params (float f, float d)[] notes);
    void Thud(float amount);
}

/// A straight port of the Mac simulation. Only the outcomes differ: nothing is deleted.
public sealed class Sim
{
    public readonly V2 Size;
    public readonly V2 WallMin, WallMax;
    public readonly float R = 92;
    public float SpeedScale = 1;

    public V2 Pos; public float Heading = -M.Pi / 2;
    public float V, W;
    public float BrushAngle, BrushSpeed, LidarAngle, LidarSpeed;
    public float Bumper, Lift; public bool Picked;
    public float Shake, Heavy;
    public V3 Led = new(0.2f, 0.85f, 1.0f);
    public float LedPulse;

    public readonly V2 DockPos;
    public readonly V2 DockOut = new(0, -1);
    public float DockDrop, DockAlpha = 1, RobotAlpha = 1, BagFill, BagGlow;

    public readonly List<Item> Items;
    public readonly List<Particle> Particles = new(2500);
    public Phase Phase = Phase.Dropping;
    public CleanMode Mode = CleanMode.Move;
    public bool SkipFinalClick, RealRun;
    public CleanMode? Discarded;
    public bool Draining, Paused;
    public float T, PhaseT, ScanSweep, ScanActive;
    public int BinCount; public long BinBytes;
    public int Collected; public long CollectedBytes, TotalBytes;
    public int BinCapacity;
    public float Battery = 1, Distance, RunTime;
    public string Status = "Warm-up";
    public V2? Mouse;
    public readonly List<V2> Trail = new();
    public float LastBump = -10;
    (float reverse, float turn, float dir)? maneuver;

    List<V2> path = new();
    public int PathI;
    float waypointT;
    Item huntTarget;
    float huntT;
    bool finishing, wasEmptyingMidRun, firstStop = true, dockAligned;
    public int ClogEvents, Swallowing;

    public ISimAudio Audio;
    readonly SeededRng rng = new(7);
    float tickBudget;
    public Action OnDoneOnce;

    /// The rooms the robot can drive through: one per display, joined by doors.
    public readonly Floorplan Plan;

    public Sim(V2 size, V2 visibleMin, V2 visibleMax, List<DesktopEntry> entries)
        : this(new Floorplan(new List<RectF> { new(visibleMin.X, visibleMin.Y, visibleMax.X - visibleMin.X, visibleMax.Y - visibleMin.Y) }), entries) { }

    public Sim(Floorplan plan, List<DesktopEntry> entries)
    {
        Plan = plan;
        // The dock's room: the main display. Lanes, tidying and the dock all start from here.
        var main = plan.Rooms[plan.Main];
        Size = new V2(main.Right, main.Bottom); WallMin = new V2(main.X, main.Y); WallMax = new V2(main.Right, main.Bottom);
        Items = entries.Select((e, i) => new Item(i, e)).ToList();
        TotalBytes = entries.Sum(e => e.Bytes);
        var dx = DockXOverride ?? BestDockX(Items.Where(i => main.Contains(i.Home)).ToList(), main.X, main.W);
        DockPos = new V2(dx, main.Bottom - R * 2.08f - 6);
        Pos = DockPos;
        BinCapacity = Items.Count > 40 ? (int)MathF.Ceiling(Items.Count * 0.55f) : Items.Count + 1;
        SpeedScale = 1 + MathF.Min(Items.Count / 300f, 0.45f);
    }

    public static float? DockXOverride;

    /// Puts the dock on the emptiest stretch of the bottom wall, near the middle.
    static float BestDockX(List<Item> items, float left, float width)
    {
        float best = left + width / 2, bestScore = float.PositiveInfinity;
        for (float x = left + 300; x < left + width - 300; x += 60)
        {
            var score = MathF.Abs(x - (left + width / 2)) * 0.002f;
            foreach (var it in items) if (MathF.Abs(it.Home.X - x) < 170) score += 1;
            if (score < bestScore) { bestScore = score; best = x; }
        }
        return best;
    }

    public V2 Fwd => new(MathF.Cos(Heading), MathF.Sin(Heading));
    public V2 Side => Fwd.Perp;
    public V2 Intake => Pos + Fwd * (R * 0.80f);
    public (V2 l, V2 r) BrushCenters { get { var a = Fwd * (R * 0.62f); return (Pos + a + Side * (R * 0.72f), Pos + a - Side * (R * 0.72f)); } }
    public int Remaining => Items.Count(i => !i.Protected && !i.Icon.Gone && !i.InBin && !i.InDock);

    float Rand(float a = 0, float b = 1) => rng.Range(a, b);
    void SetPhase(Phase p) { Phase = p; PhaseT = 0; }

    // MARK: - Step

    public void Step(float dt)
    {
        T += dt;
        if (Paused)
        {
            BrushSpeed *= MathF.Exp(-dt * 6); V = 0; W = 0;
            Audio?.SetMotor(0.04f, 0);
            Status = "Pausiert";
            return;
        }
        PhaseT += dt;
        if (Phase is not (Phase.Done or Phase.Tidied or Phase.Finished)) RunTime += dt;
        UpdatePhase(dt);
        IntegrateRobot(dt);
        UpdateItems(dt);
        UpdateParticles(dt);
        Heavy *= MathF.Exp(-dt * 3);
        Bumper *= MathF.Exp(-dt * 7);
        Shake *= MathF.Exp(-dt * 8);
        BagGlow *= MathF.Exp(-dt * 2);
        if (Draining)
        {
            BagFill = MathF.Max(0, BagFill - dt / 1.4f);
            BagGlow = 1;
            Shake = MathF.Max(Shake, 0.08f);
            if (BagFill <= 0)
            {
                Draining = false;
                Audio?.SetRoar(0);
                Audio?.Chirp((659, 0.08f), (523, 0.16f));
            }
        }
        LedPulse += dt;
        var suck = SuctionOn;
        BrushAngle += BrushSpeed * dt;
        LidarAngle += LidarSpeed * dt;
        BrushSpeed += ((suck ? 26 : 0) * (1 - Heavy * 0.7f) - BrushSpeed) * MathF.Min(1, dt * 3);
        LidarSpeed += ((Phase is Phase.Dropping or Phase.Done or Phase.Tidied ? 0 : 16) - LidarSpeed) * MathF.Min(1, dt * 2);
        float level = suck ? 1 : (Phase is Phase.Undocking or Phase.Scanning or Phase.Returning or Phase.Docking ? 0.3f : 0);
        Audio?.SetMotor(Picked ? level * 0.6f : level, Heavy);
        Battery = M.Clamp(Battery - (suck ? 0.0016f : 0) * dt, 0, 1);
        if (Phase is Phase.Emptying or Phase.Done) Battery = MathF.Min(1, Battery + dt * 0.01f);
    }

    public bool SuctionOn => Phase is Phase.Cleaning or Phase.Hunting || (Phase == Phase.Returning && !finishing && Remaining > 0);

    // MARK: - Phases

    void UpdatePhase(float dt)
    {
        switch (Phase)
        {
            case Phase.Dropping:
                Status = "Wird abgesetzt";
                DockDrop = MathF.Min(1, PhaseT / 0.42f);
                if (PhaseT >= 0.42f && PhaseT - dt < 0.42f) LandDock();
                if (PhaseT > 1.3f) { Audio?.Chirp((659, 0.09f), (880, 0.09f), (1175, 0.16f)); SetPhase(Phase.Undocking); }
                break;
            case Phase.Undocking:
                Status = wasEmptyingMidRun ? "Zurück an die Arbeit" : "Verlässt die Station";
                DriveRaw(150 * SpeedScale, 0, dt);
                if ((Pos - DockPos).Len > R * 1.9f)
                {
                    if (wasEmptyingMidRun || !firstStop) { wasEmptyingMidRun = false; BeginCleaning(); }
                    else SetPhase(Phase.Scanning);
                    firstStop = false;
                }
                break;
            case Phase.Scanning:
            {
                Status = "Kartiert deinen Desktop";
                ScanActive = MathF.Min(1, ScanActive + dt * 3);
                const float rate = 2.1f;
                var swept = ScanSweep > 2 * M.Pi;
                DriveRaw(0, swept ? 0 : rate, dt);
                var prev = ScanSweep;
                if (!swept) ScanSweep += rate * dt;
                foreach (var it in Items)
                {
                    if (it.Seen) continue;
                    var d = it.Icon.Pos - Pos;
                    var a = MathF.Atan2(d.Y, d.X) - (-M.Pi / 2);
                    while (a < 0) a += 2 * M.Pi;
                    if (a <= ScanSweep && a > prev - 0.001f) { it.Seen = true; it.Ping = 1; tickBudget += 0.3f; }
                }
                if (tickBudget > 1) { tickBudget = 0; Audio?.Tick(); }
                if (ScanSweep > 2 * M.Pi)
                {
                    foreach (var it in Items) if (!it.Seen) { it.Seen = true; it.Ping = 1; }
                    Audio?.Chirp((880, 0.08f), (1319, 0.14f));
                    BeginCleaning();
                }
                break;
            }
            case Phase.Cleaning:
                ScanActive = MathF.Max(0, ScanActive - dt * 2);
                Status = Heavy > 0.35f ? "Große Datei. Puh…" : "Saugt";
                FollowPath(dt);
                if (BinCount >= BinCapacity && Remaining > 0) { GoDock(true); break; }
                if (PathI >= path.Count)
                {
                    if (Remaining == 0) GoDock(false); else { SetPhase(Phase.Hunting); huntTarget = null; }
                }
                break;
            case Phase.Hunting:
                Status = "Sammelt die Nachzügler ein";
                if (BinCount >= BinCapacity && Remaining > 0) { GoDock(true); break; }
                if (huntTarget == null || huntTarget.Icon.Gone || huntTarget.Icon.Ingesting)
                {
                    huntTarget = NearestRemaining();
                    huntT = 0;
                    if (huntTarget != null) { path = Plan.Route(Pos, huntTarget.Icon.Pos, R + 2); PathI = 0; }
                    else { GoDock(false); break; }
                }
                huntT += dt;
                if (huntTarget != null)
                {
                    // Through a door if it's on another display, straight there if not.
                    path = Plan.Route(Pos, huntTarget.Icon.Pos, R + 2); PathI = 0;
                    // Something wedged out of reach: reel it in from a distance rather than circle forever.
                    if (huntT > 7) StartIngest(huntTarget, true);
                }
                FollowPath(dt);
                break;
            case Phase.Returning:
                Status = finishing ? "Fährt nach Hause" : "Behälter voll, fährt zur Station";
                Led = new V3(1, 0.68f, 0.2f);
                FollowPath(dt);
                if (PathI >= path.Count) { SetPhase(Phase.Docking); Audio?.Chirp((740, 0.08f), (587, 0.12f)); }
                break;
            case Phase.Docking:
            {
                Status = "Fährt rückwärts in die Station";
                var outA = MathF.Atan2(DockOut.Y, DockOut.X);
                var along = (Pos - DockPos).Dot(DockOut);
                var toDock = DockPos - Pos;
                var aimTail = MathF.Atan2(-toDock.Y, -toDock.X);
                var want = along > R * 0.7f ? aimTail : outA;
                var err = M.Wrap(want - Heading);
                if (!dockAligned)
                {
                    DriveRaw(0, M.Clamp(err * 3.0f, -2.2f, 2.2f), dt);
                    if (MathF.Abs(err) < 0.06f) dockAligned = true;
                }
                else
                {
                    DriveRaw(-M.Clamp(along * 1.5f + 34, 34, 135) * SpeedScale, M.Clamp(err * 2.6f, -0.9f, 0.9f), dt);
                    // Last centimetres: the ramp's guide rails centre it.
                    if (along < R * 0.5f)
                    {
                        var lateral = (Pos - DockPos).Dot(DockOut.Perp);
                        Pos -= DockOut.Perp * (lateral * MathF.Min(1, dt * 5));
                    }
                    if (along <= 0.5f)
                    {
                        Pos = DockPos; Heading = outA; V = 0; W = 0;
                        dockAligned = false;
                        Shake = 0.35f; Audio?.Thud(0.35f);
                        Audio?.Chirp((523, 0.09f), (659, 0.09f), (784, 0.09f), (1047, 0.2f));
                        Led = new V3(0.35f, 1, 0.5f);
                        SetPhase(Phase.Emptying);
                        PrepareEmptying();
                    }
                }
                break;
            }
            case Phase.Emptying:
            {
                Status = "Entleert sich selbst";
                var roarOn = PhaseT > 0.7f && PhaseT < EmptyDuration + 0.7f;
                Audio?.SetRoar(roarOn ? 1 : 0);
                if (roarOn) { Shake = MathF.Max(Shake, 0.09f); BagGlow = 1; }
                if (PhaseT > 0.7f) FlyEmptying(dt);
                if (PhaseT > EmptyDuration + 1.6f)
                {
                    if (finishing)
                    {
                        Audio?.Chirp((784, 0.1f), (988, 0.1f), (1175, 0.1f), (1568, 0.3f));
                        SetPhase(Phase.Done);
                        OnDoneOnce?.Invoke(); OnDoneOnce = null;
                        if (Mode == CleanMode.Tidy) StartTidy();
                        else if (SkipFinalClick) Discard(Mode);
                    }
                    else
                    {
                        Led = new V3(0.2f, 0.85f, 1);
                        wasEmptyingMidRun = true;
                        Audio?.Chirp((659, 0.09f), (880, 0.14f));
                        SetPhase(Phase.Undocking);
                    }
                }
                break;
            }
            case Phase.Done:
                Status = Discarded == null ? "Alles sauber" : DoneStatus;
                break;
            case Phase.Tidying:
                Status = "Sortiert in Spalten";
                if (PhaseT > 3.8f) SetPhase(Phase.Tidied);
                break;
            case Phase.Tidied:
                Status = "Vorschau: geordnet";
                break;
            case Phase.PuttingBack:
                Status = "Legt alles zurück";
                if (PhaseT > 2.4f) Audio?.SetRoar(0);
                if (PhaseT > 3.9f) { SetPhase(Phase.Leaving); Audio?.Chirp((1047, 0.08f), (784, 0.08f), (523, 0.14f)); }
                break;
            case Phase.Leaving:
            {
                Status = "Alles wieder da";
                var k = M.Clamp(PhaseT / 0.5f, 0, 1);
                DockAlpha = 1 - k; RobotAlpha = 1 - k;
                if (PhaseT > 0.6f) SetPhase(Phase.Finished);
                break;
            }
            case Phase.Finished: break;
        }
    }

    void LandDock()
    {
        Audio?.Thud(1);
        Shake = 1;
        // The air it pushes out shoves nearby icons away.
        foreach (var it in Items)
        {
            var d = it.Icon.Pos - (DockPos + DockOut * (-R * 0.5f));
            var l = d.Len;
            if (l < R * 3.4f)
            {
                var k = 1 - l / (R * 3.4f);
                it.Icon.Vel += d.Unit * (520 * k * k) / it.Mass;
                it.Icon.AngV += Rand(-5, 5) * k;
                it.Icon.Agit = MathF.Max(it.Icon.Agit, k);
            }
        }
        for (int i = 0; i < 70; i++)
        {
            var a = Rand(0, 6.283f);
            var p = DockPos + new V2(MathF.Cos(a) * R * 1.3f, MathF.Sin(a) * R * 1.0f);
            Spawn(p, new V2(MathF.Cos(a), MathF.Sin(a)) * Rand(120, 420), Rand(0.3f, 0.8f), Rand(1.5f, 4), 1, 1, 1, 0.3f, 0);
        }
    }

    void BeginCleaning() { Led = new V3(0.2f, 0.85f, 1); PlanLanes(); SetPhase(Phase.Cleaning); }

    public void GoDock(bool mid)
    {
        finishing = !mid;
        if (!mid) huntTarget = null;
        path = Plan.Route(Pos, DockPos + DockOut * (R * 3.0f), R + 2);
        PathI = 0;
        SetPhase(Phase.Returning);
    }

    Item NearestRemaining()
    {
        Item best = null; float bd = float.PositiveInfinity;
        foreach (var it in Items)
        {
            if (!it.OnFloor || it.Icon.Ingesting || it.Grabbed) continue;
            var d = (it.Icon.Pos - Pos).Len;
            if (d < bd) { bd = d; best = it; }
        }
        return best;
    }

    V2 ClampToWalk(V2 p) => Plan.Clamp(p, R + 2);

    /// Lawn-mower rows over where the mess actually is, starting from the nearest end. With several
    /// displays it mows one room at a time, nearest first, and drives through the door to the next.
    void PlanLanes()
    {
        path = new(); PathI = 0;
        var at = Pos;
        foreach (var room in Plan.VisitOrder(Pos))
        {
            var live = Items.Where(i => i.OnFloor && Plan.RoomOf(i.Icon.Pos) == room).Select(i => i.Icon.Pos).ToList();
            if (live.Count == 0) continue;
            var lanes = LanesIn(Plan.Rooms[room], live, at);
            if (lanes.Count == 0) continue;
            if (path.Count > 0 || Plan.RoomOf(at) != room)
            {
                var route = Plan.Route(at, lanes[0], R + 2);
                path.AddRange(route.Take(route.Count - 1));
            }
            path.AddRange(lanes);
            at = lanes[^1];
        }
        waypointT = 0;
    }

    List<V2> LanesIn(RectF room, List<V2> live, V2 from)
    {
        var stopsOut = new List<V2>();
        var step = R * LaneSpacing;
        var minY = MathF.Max(live.Min(p => p.Y), room.Y + R + 2);
        var maxY = MathF.Min(live.Max(p => p.Y), room.Bottom - R - 2);
        var lanes = new List<(float y, float a, float b)>();
        for (var y = minY; y <= maxY + step * 0.5f; y += step)
        {
            var yy = MathF.Min(y, maxY);
            var band = live.Where(p => MathF.Abs(p.Y - yy) < step * 0.62f).Select(p => p.X).ToList();
            if (band.Count > 0) lanes.Add((yy, MathF.Max(band.Min() - R * 0.5f, room.X + R + 2), MathF.Min(band.Max() + R * 0.5f, room.Right - R - 2)));
        }
        if (lanes.Count > 0 && MathF.Abs(lanes[0].y - from.Y) > MathF.Abs(lanes[^1].y - from.Y)) lanes.Reverse();
        var x = from.X;
        foreach (var l in lanes)
        {
            var near = MathF.Abs(l.a - x) < MathF.Abs(l.b - x) ? l.a : l.b;
            var far = near == l.a ? l.b : l.a;
            // A row that crosses the dock goes around it, rather than pressing against it until it times out.
            float sideL = DockPos.X - R * 2.35f, sideR = DockPos.X + R * 2.35f;
            var crossesDock = InsideKeepOut(new V2(DockPos.X, l.y)) && MathF.Min(near, far) < sideL && MathF.Max(near, far) > sideR;
            var stops = new List<V2> { new(near, l.y), new(far, l.y) };
            if (crossesDock)
            {
                var (first, second) = near < far ? (sideL, sideR) : (sideR, sideL);
                var over = DockPos.Y + DockOut.Y * R * 2.2f;
                stops = new() { new(near, l.y), new(first, l.y), new(first, over), new(second, over), new(second, l.y), new(far, l.y) };
            }
            foreach (var p in stops) { var q = room.Inset(R + 2).Clamp(p); if (!InsideKeepOut(q)) stopsOut.Add(q); }
            x = far;
        }
        return stopsOut;
    }

    bool InsideKeepOut(V2 p)
    {
        var d = p - DockPos;
        float u = d.Dot(DockOut), s = MathF.Abs(d.Dot(DockOut.Perp));
        return u < R * 2.0f && s < R * 2.3f;
    }

    /// Slides the robot along the dock's outline so it never drives over it.
    void PushOutOfDock()
    {
        if (!InsideKeepOut(Pos)) return;
        var d = Pos - DockPos;
        float u = d.Dot(DockOut), s = d.Dot(DockOut.Perp);
        float toFront = R * 2.0f - u, toSide = R * 2.3f - MathF.Abs(s);
        if (toFront < toSide) Pos += DockOut * toFront;
        else Pos += DockOut.Perp * (s < 0 ? -toSide : toSide);
    }

    // MARK: - Motion

    void FollowPath(float dt)
    {
        if (PathI >= path.Count) { DriveRaw(0, 0, dt); return; }
        // Mouse in the way: bump, back off, swing round, like a real one meeting a foot.
        if (maneuver is { } m)
        {
            DriveRaw(m.reverse > 0 ? -110 : 0, m.reverse > 0 ? 0 : m.dir * 2.6f, dt);
            var n = m;
            if (n.reverse > 0) n.reverse -= dt; else n.turn -= dt;
            maneuver = n.turn > 0 ? n : null;
            return;
        }
        if (Mouse is { } mp)
        {
            var d = mp - Pos; var l = d.Len;
            if (l < R + 20 && d.Dot(Fwd) > 0 && T - LastBump > 1.2f)
            {
                LastBump = T;
                Bumper = 1; Audio?.Thud(0.3f); Audio?.Chirp((494, 0.07f), (392, 0.09f));
                maneuver = (0.32f, 0.45f, d.Dot(Side) > 0 ? -1 : 1);
                return;
            }
        }
        var target = path[PathI];
        waypointT += dt;
        var reach = (PathI == path.Count - 1 ? R * 0.28f : R * 0.45f) * MathF.Max(1, SpeedScale);
        if ((target - Pos).Len < reach || waypointT > 14)
        {
            PathI += 1; waypointT = 0;
            if (PathI >= path.Count) return;
            target = path[PathI];
        }
        // Carrot: blend towards the next waypoint when close, which rounds the corners.
        var toT = target - Pos;
        var aim = target;
        if (PathI + 1 < path.Count && toT.Len < R * 1.4f)
        {
            var k = 1 - toT.Len / (R * 1.4f);
            aim = target + (path[PathI + 1] - target).Unit * (R * 0.9f * k);
        }
        var err = M.Wrap(MathF.Atan2(aim.Y - Pos.Y, aim.X - Pos.X) - Heading);
        var wCmd = M.Clamp(err * 4.2f * SpeedScale, -3.1f * SpeedScale, 3.1f * SpeedScale);
        var align = M.Clamp(1 - MathF.Abs(err) / 1.15f, 0, 1);
        var vCmd = 360 * align * align;
        if (Swallowing > 0) vCmd *= MathF.Max(0.38f, 1 - 0.22f * Swallowing);
        if (PathI == path.Count - 1) vCmd = MathF.Min(vCmd, MathF.Max(70, toT.Len * 2.2f));
        // Eases off before a corner: a fast robot can otherwise overshoot and circle the waypoint.
        else vCmd = MathF.Min(vCmd, 170 + toT.Len * 2.2f);
        vCmd *= 1 - 0.68f * M.Clamp(Heavy, 0, 1);
        DriveRaw(vCmd * SpeedScale, wCmd, dt);
    }

    void DriveRaw(float vCmd, float wCmd, float dt)
    {
        var a = 720 * SpeedScale;
        V += M.Clamp(vCmd - V, -a * dt, a * dt);
        var aw = 12 * SpeedScale;
        W += M.Clamp(wCmd - W, -aw * dt, aw * dt);
    }

    void IntegrateRobot(float dt)
    {
        if (Picked) { V = 0; W = 0; return; }
        Heading = M.Wrap(Heading + W * dt);
        Pos += Fwd * (V * dt);
        Distance += MathF.Abs(V * dt);
        // Walls: bumper compresses, a small knock.
        var walled = ClampToWalk(Pos);
        if (Phase is Phase.Cleaning or Phase.Hunting or Phase.Returning)
        {
            if ((walled - Pos).Len > 0.3f)
            {
                if (T - LastBump > 0.9f && MathF.Abs(V) > 90) { LastBump = T; Bumper = 0.8f; Audio?.Thud(0.16f); V *= 0.4f; }
                Pos = walled;
            }
            PushOutOfDock();
        }
        if (Trail.Count == 0 || (Pos - Trail[^1]).Len > 26) { Trail.Add(Pos); if (Trail.Count > 1600) Trail.RemoveRange(0, 200); }
    }

    // MARK: - Items

    void StartIngest(Item it, bool icon)
    {
        ref var b = ref (icon ? ref it.Icon : ref it.Label);
        if (b.Ingesting || it.Protected) return;
        b.Ingesting = true;
        b.Pull = 0.001f;
        if (icon)
        {
            if (it.LabelAttached) DetachLabel(it);
            Audio?.Ingest(it.Mass);
            if (it.Mass > 3.4f) ClogEvents++;
            var c = it.Color;
            for (int i = 0; i < 6 + (int)(it.Mass * 4); i++)
            {
                var dir = (Intake - it.Icon.Pos).Unit.Rotated(Rand(-1.2f, 1.2f));
                Spawn(it.Icon.Pos + new V2(Rand(-24, 24), Rand(-24, 24)), dir * Rand(80, 300), Rand(0.2f, 0.5f), Rand(2, 5), c.X, c.Y, c.Z, 0.95f, 1);
            }
        }
    }

    void DetachLabel(Item it)
    {
        it.LabelAttached = false;
        it.Label.Pos = it.Icon.Pos + new V2(0, Metrics.LabelOffset).Rotated(it.Icon.Ang);
        it.Label.Vel = it.Icon.Vel;
        it.Label.Ang = it.Icon.Ang;
        it.Label.AngV = Rand(-5, 5);
    }

    bool ApplyField(ref Body b, float mass, bool on, float dt)
    {
        var rel = b.Pos - Pos;
        float f = rel.Dot(Fwd), s = rel.Dot(Side);
        var toIntake = Intake - b.Pos;
        var d = toIntake.Len;
        if (on)
        {
            // Suction: only reaches forward, and falls off fast.
            var reach = R * 2.5f;
            if (d < reach)
            {
                var k = 1 - d / reach;
                var front = M.Smooth(-R * 0.3f, R * 0.5f, f);
                var accel = 3300 * k * k * (0.2f + 0.8f * front) / mass;
                b.Vel += toIntake.Unit * accel * dt;
                b.Agit = MathF.Max(b.Agit, k * front * 1.15f);
            }
            // Side brushes whirl things across the front of the robot and inwards.
            var (bl, br) = BrushCenters;
            for (int i = 0; i < 2; i++)
            {
                var c = i == 0 ? bl : br; float sign = i == 0 ? -1 : 1;
                var e = b.Pos - c; var el = e.Len;
                var brr = R * 0.62f;
                if (el < brr)
                {
                    var k = 1 - el / brr;
                    var tangent = e.Unit.Perp * sign;
                    b.Vel += (tangent * 1300 + (Intake - b.Pos).Unit * 800) * k * dt / mass;
                    b.AngV += sign * 14 * k * dt / mass * 6;
                    b.Agit = MathF.Max(b.Agit, 0.5f + k * 0.5f);
                }
            }
        }
        // Robot body: shoves what it doesn't swallow.
        if (!Picked)
        {
            var l = rel.Len;
            var keep = R + 16;
            if (on && f > R * 0.1f && MathF.Abs(s) < R * IntakeWidth && l < R + 44) return true;  // into the intake
            if (l < keep)
            {
                var n = rel.Unit;
                b.Pos = Pos + n * keep;
                var vr = (Fwd * V).Dot(n);
                if (vr > 0) b.Vel += n * vr * 1.1f;
                b.AngV += n.Perp.Dot(Fwd) * V * 0.012f;
                b.Agit = MathF.Max(b.Agit, 0.5f);
            }
        }
        return false;
    }

    readonly List<int> awake = new();

    void UpdateItems(float dt)
    {
        var on = SuctionOn;
        awake.Clear();
        Swallowing = 0;
        foreach (var it in Items) if (it.Icon.Ingesting) Swallowing++;
        foreach (var it in Items)
        {
            it.Ping = MathF.Max(0, it.Ping - dt * 1.3f);
            it.Land = MathF.Max(0, it.Land - dt * 4);
            if (it.Flight != Flight.None) { UpdateFlight(it, dt); continue; }
            if (it.InBin || it.InDock) continue;
            if (it.Protected)
            {
                // Not for collecting: can be nudged by the robot, but always settles back on its spot.
                ApplyField(ref it.Icon, it.Mass, false, dt);
                it.Icon.Vel += (it.Rest - it.Icon.Pos) * 26 * dt;
                Integrate(ref it.Icon, dt, 7);
                continue;
            }
            // ICON
            if (!it.Icon.Gone)
            {
                if (it.Grabbed) it.Icon.Agit = MathF.Max(it.Icon.Agit, 0.25f);
                else if (it.Icon.Ingesting)
                {
                    AdvanceIngest(ref it.Icon, it.IngestTime, dt);
                    Heavy = MathF.Max(Heavy, M.Clamp((it.Mass - 1.8f) / 3.2f, 0, 1));
                    if (it.Mass > 3) Shake = MathF.Max(Shake, 0.28f * it.Mass / 6);
                    if (it.Icon.Gone)
                    {
                        it.InBin = true; BinCount++; BinBytes += it.Entry.Bytes;
                        Collected++; CollectedBytes += it.Entry.Bytes;
                        if (it.Mass > 3) Audio?.Chirp((392, 0.05f));
                    }
                }
                else
                {
                    if (ApplyField(ref it.Icon, it.Mass, on, dt)) StartIngest(it, true);
                    Integrate(ref it.Icon, dt, 5.5f);
                    PushOutOfDockItem(ref it.Icon);
                    ClampItem(ref it.Icon, 26);
                }
                if (it.Icon.Agit > 0.02f) awake.Add(it.Index);
            }
            // LABEL
            if (it.LabelAttached && it.Icon.Agit > 0.62f + 0.25f * MathF.Sin(it.Icon.Seed * 13)) DetachLabel(it);
            if (!it.LabelAttached && !it.Label.Gone)
            {
                if (it.Label.Ingesting) AdvanceIngest(ref it.Label, 0.3f, dt);
                else
                {
                    if (ApplyField(ref it.Label, 0.32f, on, dt)) StartIngest(it, false);
                    if (it.Icon.Gone)
                    {
                        // Its icon is gone: a label left behind flutters a bit, then gets taken.
                        it.LabelLost += dt;
                        if (it.LabelLost > 0.8f || (Intake - it.Label.Pos).Len < R * 2.6f) StartIngest(it, false);
                    }
                    Integrate(ref it.Label, dt, 3.6f);
                    it.Label.Vel += new V2(MathF.Sin(T * 7 + it.Label.Seed), MathF.Cos(T * 5.3f + it.Label.Seed * 2)) * 60 * it.Label.Agit * dt;
                    ClampItem(ref it.Label, 30);
                }
            }
        }
        // Stirred-up icons shoulder each other aside; calm ones keep their exact spot.
        var spacing = Metrics.IconHalf * 1.7f;
        if (awake.Count > 1 && awake.Count < 120)
        {
            for (int ii = 0; ii < awake.Count; ii++)
                for (int jj = ii + 1; jj < awake.Count; jj++)
                {
                    Item a = Items[awake[ii]], b = Items[awake[jj]];
                    if (a.Icon.Ingesting || b.Icon.Ingesting || a.Grabbed || b.Grabbed) continue;
                    var d = b.Icon.Pos - a.Icon.Pos; var l = d.Len;
                    if (l < spacing && l > 0.01f)
                    {
                        var p = d.Unit * ((spacing - l) * 7 * dt);
                        a.Icon.Pos -= p / a.Mass; b.Icon.Pos += p / b.Mass;
                    }
                }
        }
    }

    void AdvanceIngest(ref Body b, float duration, float dt)
    {
        b.Pull = MathF.Min(1, b.Pull + dt / duration);
        var k = 1 - MathF.Exp(-dt * (2.5f + 9 * b.Pull * b.Pull));
        b.Pos += (Intake - b.Pos) * k;
        b.Agit = 1;
        b.Ang += b.AngV * dt;
        if (b.Pull >= 1) { b.Gone = true; b.Ingesting = false; }
    }

    static void Integrate(ref Body b, float dt, float damping)
    {
        b.Pos += b.Vel * dt;
        b.Vel *= MathF.Exp(-dt * damping);
        b.Ang += b.AngV * dt;
        b.AngV *= MathF.Exp(-dt * 3.2f);
        // Paper settles flat again, keeping any new position.
        b.Ang += (0 - b.Ang) * (1 - MathF.Exp(-dt * 0.6f * (1 - b.Agit)));
        b.Agit = MathF.Max(0, b.Agit - dt * 1.7f);
    }

    void ClampItem(ref Body b, float pad)
    {
        // Paper bounces softly off the outer walls, and slides through doors like the robot does.
        var q = Plan.Clamp(b.Pos, pad);
        var d = q - b.Pos;
        if (d.Len < 0.01f) return;
        var n = d.Unit;
        b.Pos = q;
        var into = b.Vel.Dot(n);
        if (into < 0) b.Vel -= n * (into * 1.4f);
    }

    void PushOutOfDockItem(ref Body b)
    {
        // Keep icons off the tower so nothing gets lost behind it.
        var d = b.Pos - DockPos;
        float u = d.Dot(DockOut), s = d.Dot(DockOut.Perp);
        if (u < R * 1.0f && u > -R * 2.3f && MathF.Abs(s) < R * 1.45f)
        {
            b.Pos += DockOut * (R * 1.0f - u);
            b.Vel += DockOut * 60;
        }
    }

    // MARK: - Emptying and putting back

    public float EmptyDuration = 3.6f, EmptyCap = 5, IntakeWidth = 0.98f, LaneSpacing = 1.6f;
    public V2 BagPos => DockPos - DockOut * (R * 1.5f);
    List<Item> emptyQueue = new();
    float emptyAcc, emptyRate = 1;

    void PrepareEmptying()
    {
        emptyQueue = Items.Where(i => i.InBin).ToList();
        EmptyDuration = M.Clamp(emptyQueue.Count * 0.09f, MathF.Min(2.2f, EmptyCap), EmptyCap);
        emptyRate = emptyQueue.Count / (EmptyDuration * 0.9f);
        emptyAcc = 0;
    }

    void FlyEmptying(float dt)
    {
        if (emptyQueue.Count == 0) return;
        emptyAcc += dt * emptyRate;
        while (emptyAcc >= 1 && emptyQueue.Count > 0)
        {
            var it = emptyQueue[^1]; emptyQueue.RemoveAt(emptyQueue.Count - 1);
            emptyAcc -= 1;
            it.InBin = false;
            it.Flight = Flight.ToBag;
            it.FlightFrom = Pos + new V2(Rand(-18, 18), Rand(-18, 18));
            it.FlightTo = BagPos + new V2(Rand(-R * 0.5f, R * 0.5f), Rand(-R * 0.16f, R * 0.16f));
            it.FlightT = 0; it.FlightDelay = 0; it.FlightDur = 0.42f;
            it.FlightSpin = Rand(-9, 9);
            it.FlightScale = 0.34f;
            it.Icon.Gone = false; it.Icon.Pos = it.FlightFrom; it.Icon.Pull = 0; it.Icon.Agit = 0; it.Icon.Ang = Rand(-1, 1); it.Icon.Ingesting = false;
        }
    }

    void UpdateFlight(Item it, float dt)
    {
        if (it.FlightDelay > 0) { it.FlightDelay -= dt; return; }
        it.FlightT = MathF.Min(1, it.FlightT + dt / it.FlightDur);
        var k = it.FlightT;
        var e = it.Flight == Flight.ToBag ? k * k : 1 - MathF.Pow(1 - k, 3);
        var p = it.FlightFrom + (it.FlightTo - it.FlightFrom) * e;
        // Airborne arc, seen from above as a slight sideways bow.
        var span = it.FlightTo - it.FlightFrom;
        var bow = span.Perp.Unit * (MathF.Sin(k * M.Pi) * MathF.Min(span.Len * 0.12f, 90) * (it.Index % 2 == 0 ? 1 : -1));
        if (it.Flight != Flight.ToBag) p += bow;
        it.Icon.Pos = p;
        switch (it.Flight)
        {
            case Flight.ToBag:
                it.FlightScale = 0.34f * (1 - k * 0.65f);
                it.Icon.Ang += it.FlightSpin * dt;
                if (k >= 1)
                {
                    it.Flight = Flight.None; it.InDock = true; it.Icon.Gone = true;
                    BinCount--; BinBytes -= it.Entry.Bytes;
                    BagFill = M.Clamp(BagFill + 1f / Math.Max(Items.Count, 1), 0, 1);
                    tickBudget += 0.45f;
                    if (tickBudget > 1) { tickBudget = 0; Audio?.Tick(); }
                }
                break;
            case Flight.ToHome:
            case Flight.ToTidy:
                it.FlightScale = 0.18f + (1 - 0.18f) * M.Smooth(0, 0.8f, k);
                it.Icon.Ang = it.FlightSpin * (1 - e);
                if (k >= 1)
                {
                    it.Flight = Flight.None; it.InDock = false; it.InBin = false;
                    it.Icon.Pos = it.FlightTo; it.Icon.Ang = 0; it.Icon.Vel = V2.Zero; it.Icon.Gone = false; it.Icon.Agit = 0; it.Icon.AngV = 0;
                    it.LabelAttached = true; it.Label.Gone = false; it.Label.Ingesting = false; it.Label.Pull = 0; it.LabelLost = 0;
                    it.FlightScale = 1; it.Land = 1;
                    it.Rest = it.FlightTo;
                    tickBudget += 0.35f;
                    if (tickBudget > 1) { tickBudget = 0; Audio?.Tick(); }
                }
                break;
        }
    }

    /// Fans the same sprites out into columns by kind, from the top-left, the way Windows arranges a desktop.
    void StartTidy()
    {
        SetPhase(Phase.Tidying);
        var grouped = Enum.GetValues<Kind>().SelectMany(k => Items.Where(i => i.Entry.Kind == k && !i.Protected)).ToList();
        var protectedSpots = Items.Where(i => i.Protected).Select(i => i.Home).ToList();
        var rowPitch = MathF.Max(Metrics.IconHalf * 2 + Metrics.LabelHalf.Y * 2 + 14, 90);
        var colPitchWanted = MathF.Max(Metrics.LabelHalf.X * 2 + 12, 90);
        var rows = Math.Max(1, (int)((WallMax.Y - WallMin.Y - 30) / rowPitch));
        var slotsNeeded = grouped.Count + protectedSpots.Count;
        var cols = (slotsNeeded + rows - 1) / rows;
        // A very full desktop tightens the columns so that everything stays on screen.
        var pitch = MathF.Min(colPitchWanted, (WallMax.X - WallMin.X - 120) / Math.Max(cols - 1, 1));
        var slots = new List<(Item, V2)>();
        int slot = 0;
        foreach (var it in grouped)
        {
            V2 spot;
            // Skip spots that something left alone is sitting on.
            do
            {
                int col = slot / rows, row = slot % rows;
                spot = new V2(WallMin.X + 50 + col * pitch, WallMin.Y + 12 + Metrics.IconHalf + row * rowPitch);
                slot++;
            } while (protectedSpots.Any(p => (p - spot).Len < Metrics.IconHalf * 1.5f));
            slots.Add((it, spot));
        }
        Launch(slots, _ => BagPos, Flight.ToTidy, 3.0f);
        Audio?.SetRoar(0);
    }

    public string DoneStatus
    {
        get
        {
            var tail = RealRun ? "" : " (Vorschau)";
            return (Discarded switch { CleanMode.Hide => "Ausgeblendet", CleanMode.Sort => "In Ordner sortiert", _ => "Weggeräumt" }) + tail;
        }
    }

    /// The real action finished: anything left alone flies home, then the dock leaves.
    public void FinishAfterAction(List<Item> list)
    {
        Draining = false;
        BagFill = 0;
        Audio?.SetRoar(0);
        if (list.Count == 0) { SetPhase(Phase.Leaving); return; }
        Launch(list.Select(i => (i, i.Home)).ToList(), _ => BagPos, Flight.ToHome, 1.4f);
        SetPhase(Phase.PuttingBack);
    }

    /// Drains the bag for the chosen outcome. Only a picture of it: no file is touched here.
    public void Discard(CleanMode choice)
    {
        if (Phase != Phase.Done || Discarded != null || choice == CleanMode.Tidy) return;
        Discarded = choice;
        Draining = true;
        Audio?.SetRoar(0.7f);
        Status = DoneStatus;
    }

    public void PutBack()
    {
        if (Phase is not (Phase.Done or Phase.Tidied or Phase.Tidying)) return;
        Draining = false;
        SetPhase(Phase.PuttingBack);
        Led = new V3(0.5f, 0.75f, 1);
        Launch(Items.Select(i => (i, i.Home)).ToList(), it => it.InDock ? BagPos : it.Icon.Pos, Flight.ToHome, 2.9f);
        Audio?.SetRoar(0.55f);
        BagFill = 0;
    }

    void Launch(List<(Item it, V2 to)> slots, Func<Item, V2> from, Flight flight, float spread)
    {
        var n = Math.Max(slots.Count, 1);
        for (int i = 0; i < slots.Count; i++)
        {
            var (it, to) = slots[i];
            it.FlightFrom = from(it);
            it.Flight = flight;
            it.FlightTo = to;
            it.Icon.Gone = false; it.Icon.Ingesting = false; it.Icon.Pos = it.FlightFrom; it.Icon.Pull = 0; it.Icon.Agit = 0;
            it.FlightT = 0;
            it.FlightDelay = spread * 0.72f * i / n;
            it.FlightDur = 0.75f + Rand(0, 0.25f);
            it.FlightSpin = Rand(-2.5f, 2.5f);
            it.FlightScale = 0.18f;
            it.LabelAttached = true; it.Label.Gone = false; it.Label.Pull = 0; it.Label.Ingesting = false;
        }
    }

    // MARK: - Pointer

    public bool HitRobot(V2 p) => (p - Pos).Len < R && Phase is not (Phase.Docking or Phase.Emptying or Phase.Dropping or Phase.PuttingBack or Phase.Leaving);

    public Item HitItem(V2 p)
    {
        Item best = null; float bd = Metrics.IconHalf * 1.3f;
        foreach (var it in Items)
        {
            if (!it.OnFloor) continue;
            var d = (it.Icon.Pos - p).Len;
            if (d < bd) { bd = d; best = it; }
        }
        return best;
    }

    public void PickUp() { Picked = true; Audio?.Chirp((988, 0.1f), (784, 0.1f), (988, 0.1f), (784, 0.16f)); Status = "Lass mich runter!"; }
    public void DragRobot(V2 p) => Pos = ClampToWalk(p);
    public void DropRobot()
    {
        Picked = false; Audio?.Thud(0.55f); Shake = 0.6f;
        PushOutOfDock();
        if (Phase == Phase.Cleaning) { PlanLanes(); PhaseT = 0; }
    }

    // MARK: - Particles

    void Spawn(V2 p, V2 vel, float life, float size, float r, float g, float b, float a, float chip)
    {
        if (Particles.Count > 2300) Particles.RemoveRange(0, 100);
        Particles.Add(new Particle { Pos = p, Vel = vel, Life = life, MaxLife = life, Size = size, R = r, G = g, B = b, A = a, Chip = chip, Ang = Rand(0, 6.28f) });
    }

    float airAcc;

    void UpdateParticles(float dt)
    {
        var on = SuctionOn;
        // Air the robot is drawing in, visible as a few fast specks.
        if (on && !Picked)
        {
            airAcc += 44 * dt;
            while (airAcc >= 1)
            {
                airAcc -= 1;
                var a = Rand(-1.25f, 1.25f);
                var dir = Fwd.Rotated(a);
                var p = Pos + dir * (R * Rand(1.1f, 1.95f));
                Spawn(p, -dir * Rand(60, 140), Rand(0.25f, 0.5f), Rand(0.8f, 2), 1, 1, 1, 0.34f, 0);
            }
        }
        var intake = Intake;
        int i = 0;
        while (i < Particles.Count)
        {
            var q = Particles[i];
            q.Life -= dt;
            if (q.Life <= 0) { Particles[i] = Particles[^1]; Particles.RemoveAt(Particles.Count - 1); continue; }
            if (on)
            {
                var d = intake - q.Pos; var l = d.Len;
                if (l < R * 3) q.Vel += d.Unit * (2800 * (1 - l / (R * 3))) * dt;
                if (l < R * 0.5f) q.Life = 0;
            }
            q.Vel *= MathF.Exp(-dt * 2.4f);
            q.Pos += q.Vel * dt;
            q.Ang += dt * 9 * q.Chip;
            Particles[i] = q;
            i++;
        }
    }
}
