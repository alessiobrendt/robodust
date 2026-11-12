using System;
using System.Collections.Generic;
using System.Linq;

namespace RoboDust;

/// More gadgets: the laser, the tornado, the snowplow, and driving it yourself. Like the rest of the
/// fun mode, only a picture: no file is touched by any of it.
public sealed partial class Sim
{
    bool HasLaser => Fun == FunMode.Laser;
    bool HasTornado => Fun == FunMode.Tornado;
    bool HasPlow => Fun == FunMode.Plow;
    bool Working => Phase is Phase.Cleaning or Phase.Hunting && !Picked && !Paused;

    // MARK: Driving it yourself

    public bool RemoteControl;
    float manualThrottle, manualSteer, lastManual = -10;
    bool manualWas;

    /// Arrow keys, read by the app each frame: throttle and steering, each -1...1.
    public void SetManual(float throttle, float steer)
    {
        if (!RemoteControl) return;
        if (throttle != 0 || steer != 0) { lastManual = T; manualWas = true; }
        manualThrottle = throttle; manualSteer = steer;
    }

    public bool ManualActive => RemoteControl && T - lastManual < 2.5f && Phase is Phase.Cleaning or Phase.Hunting && !Picked;

    void DriveManual(float dt)
    {
        Status = "Du steuerst";
        DriveRaw(manualThrottle * 340 * SpeedScale, manualSteer * 3.0f, dt);
    }

    void UpdateManual()
    {
        // Let go of the keys and it carries on by itself, from wherever you left it.
        if (manualWas && !ManualActive)
        {
            manualWas = false;
            if (Phase == Phase.Cleaning) { PlanLanes(); PhaseT = 0; }
            if (Phase == Phase.Hunting) huntTarget = null;
        }
    }

    // MARK: Laser

    public Item LaserTarget;
    float zapBudget;

    void UpdateLaser(float dt, bool working)
    {
        LaserTarget = null;
        if (!working) return;
        // Locks on to the nearest thing ahead, within reach of the beam.
        float best = R * 6;
        foreach (var it in Items)
        {
            if (!it.OnFloor || it.Icon.Ingesting || it.Grabbed) continue;
            var d = it.Icon.Pos - Nozzle; var l = d.Len;
            if (l < best && l > 1 && d.Unit.Dot(Fwd) > 0.72f) { best = l; LaserTarget = it; }
        }
        if (LaserTarget == null) return;
        var t = LaserTarget;
        if (!t.Burning) StartBurn(t);
        // The beam: a hot core and a few sparks where it lands.
        var from = Nozzle; var to = t.Icon.Pos;
        var seg = to - from; var n = (int)MathF.Max(6, seg.Len / 9);
        for (int k = 0; k <= n; k++)
        {
            var p = from + seg * (k / (float)n) + seg.Perp.Unit * Rand(-1.2f, 1.2f);
            SpawnFun(PartKind.Glow, p, V2.Zero, 0.05f, Rand(1.6f, 2.6f), 1, 0.25f, 0.35f, 0.9f);
        }
        if (Rand() < dt * 40)
        {
            var a = Rand(0, 6.283f);
            SpawnFun(PartKind.Glow, to, new V2(MathF.Cos(a), MathF.Sin(a)) * Rand(60, 200), Rand(0.15f, 0.3f), Rand(1.2f, 2), 0.6f, 0.95f, 1, 1);
        }
        zapBudget += dt * 8;
        if (zapBudget > 1) { zapBudget = 0; Audio?.Pop(); }
    }

    // MARK: Tornado

    void UpdateTornado(float dt, bool working)
    {
        if (!working) return;
        var reach = R * 3.6f;
        foreach (var it in Items)
        {
            if (!it.OnFloor || it.Icon.Ingesting || it.Burning || it.Grabbed || it.Carried) continue;
            var d = it.Icon.Pos - Pos; var l = d.Len;
            if (l > reach || l < 1) continue;
            var k = 1 - l / reach;
            // Round and round, and slowly in.
            it.Icon.Vel += (d.Unit.Perp * (950 * k) - d.Unit * (160 * k)) * dt / it.Mass;
            it.Icon.AngV += 9 * k * dt;
            it.Icon.Agit = MathF.Max(it.Icon.Agit, 0.6f * k);
        }
        // Dust caught in the wind.
        for (int i = 0; i < 6; i++)
        {
            var a = Rand(0, 6.283f); var r = R * Rand(1.1f, 2.6f);
            var p = Pos + new V2(MathF.Cos(a), MathF.Sin(a)) * r;
            var g = Rand(0.65f, 0.9f);
            SpawnFun(PartKind.Swirl, p, V2.Zero, Rand(0.5f, 1.1f), Rand(1.5f, 3.5f), g, g, g * 1.02f, 0.4f);
        }
    }

    // MARK: Snowplow

    int plowStage;            // 0 pushing everything into a heap, 1 vacuuming up the heap and the snow
    bool toPile;
    public V2 PilePoint;
    bool pileSet;
    float snowAcc;
    const int CarryCap = 28;

    public bool Plowing => HasPlow && plowStage == 0;
    bool InPileNow(Item it) => Plowing && (it.Icon.Pos - PilePoint).Len < R * 2.4f;

    void UpdatePlow(float dt)
    {
        if (!pileSet)
        {
            // The heap goes in front of the dock, off to one side, where it won't block the way home.
            PilePoint = Plan.Clamp(DockPos + DockOut * (R * 3.4f) + DockOut.Perp * (R * 3.4f), R * 2);
            pileSet = true;
        }
        Snowfall(dt);
        if (!Plowing || !(Phase is Phase.Cleaning or Phase.Hunting)) return;
        if (huntTarget != null && huntTarget.Carried) huntTarget = null;
        // The blade picks up whatever it meets.
        int carried = 0;
        foreach (var it in Items) if (it.Carried) carried++;
        foreach (var it in Items)
        {
            if (carried >= CarryCap) break;
            if (!it.OnFloor || it.Carried || it.Grabbed || InPileNow(it)) continue;
            var rel = it.Icon.Pos - Pos;
            float f = rel.Dot(Fwd), s = rel.Dot(Side);
            if (f > R * 0.7f && f < R * 1.45f && MathF.Abs(s) < R * 0.95f) { Capture(it, s, carried); carried++; }
        }
        int free = 0;
        foreach (var it in Items) if (it.OnFloor && !it.Carried && !InPileNow(it)) free++;
        if (toPile)
        {
            Status = "Schiebt alles zum Haufen";
            if ((Pos - PilePoint).Len < R * 1.4f || PathI >= path.Count)
            {
                Dump();
                toPile = false;
                NextPlowStep(free);
            }
        }
        else if (carried >= CarryCap || (carried > 0 && free == 0) || (Phase == Phase.Cleaning && PathI >= path.Count && carried > 0))
        {
            toPile = true;
            path = Plan.Route(Pos, PilePoint, R + 2); PathI = 0;
            if (Phase != Phase.Cleaning) SetPhase(Phase.Cleaning);
        }
        else if (carried == 0 && free == 0) NextPlowStep(0);
        else Status = "Schiebt mit dem Schneepflug";
    }

    void NextPlowStep(int free)
    {
        if (free > 0) { PlanLanes(); SetPhase(Phase.Cleaning); return; }
        // Everything is on the heap: now the suction comes on and it all goes.
        plowStage = 1;
        Audio?.Chirp((523, 0.08f), (784, 0.12f));
        PlanLanes();
        SetPhase(Phase.Cleaning);
    }

    void Capture(Item it, float side, int count)
    {
        it.Carried = true;
        it.CarryOff = new V2(R * 1.18f + Rand(0, R * 0.25f) * MathF.Min(1, count / 8f), M.Clamp(side, -R * 0.8f, R * 0.8f));
        Audio?.Tick();
    }

    void CarryItem(Item it, float dt)
    {
        var want = Pos + Fwd * it.CarryOff.X + Side * it.CarryOff.Y;
        it.Icon.Pos += (want - it.Icon.Pos) * MathF.Min(1, dt * 14);
        it.Icon.Vel = Fwd * V;
        it.Icon.Agit = MathF.Max(it.Icon.Agit, 0.15f);
        it.Icon.Ang += (M.Wrap(Heading + M.Pi / 2) * 0.1f - it.Icon.Ang) * MathF.Min(1, dt * 3);
        if (!Plowing) it.Carried = false;
    }

    /// Drops the load: it slides off the blade into the heap.
    void Dump()
    {
        foreach (var it in Items)
        {
            if (!it.Carried) continue;
            it.Carried = false;
            var away = (it.Icon.Pos - PilePoint).Unit;
            it.Icon.Vel = away * Rand(30, 110) + new V2(Rand(-40, 40), Rand(-40, 40));
            it.Icon.AngV = Rand(-3, 3);
            it.Icon.Agit = 0.5f;
        }
        for (int k = 0; k < 40; k++)
        {
            var a = Rand(0, 6.283f);
            SpawnFun(PartKind.Snow, PilePoint + new V2(MathF.Cos(a), MathF.Sin(a)) * Rand(0, R * 1.2f), new V2(MathF.Cos(a), MathF.Sin(a)) * Rand(30, 140), Rand(20, 40), Rand(2, 4), 1, 1, 1, 0.9f);
        }
        Audio?.Thud(0.3f);
    }

    /// It snows on the desktop until the heap is done, and the plow pushes the snow along too.
    void Snowfall(float dt)
    {
        if (!Plowing || Phase is Phase.Done or Phase.Tidying or Phase.Tidied or Phase.PuttingBack or Phase.Leaving or Phase.Finished) return;
        int snow = 0;
        foreach (var p in Particles) if (p.Kind == PartKind.Snow) snow++;
        if (snow > 900) return;
        snowAcc += dt * 60;
        while (snowAcc >= 1)
        {
            snowAcc -= 1;
            var room = Plan.Rooms[(int)(Rand() * Plan.Rooms.Count) % Plan.Rooms.Count];
            var p = new V2(room.X + Rand() * room.W, room.Y + Rand() * room.H);
            SpawnFun(PartKind.Snow, p, new V2(Rand(-8, 8), Rand(4, 14)), Rand(40, 70), Rand(1.8f, 3.8f), 1, 1, 1, 0.85f);
        }
    }
}
