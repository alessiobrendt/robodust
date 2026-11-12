using System;
using System.Collections.Generic;

namespace RoboDust;

/// Spaßmodus: gadgets bolted onto the robot. Purely a picture: nothing here touches a file, and a
/// "burnt" icon is back to normal the moment it lands where it was.
public enum FunMode { Off = 0, Flames = 1, Bubbles = 2, Confetti = 3, Disco = 4, All = 5, Laser = 6, Tornado = 7, Plow = 8, Race = 9, Squirrels = 10, Duel = 11 }

public sealed partial class Sim
{
    public FunMode Fun = FunMode.Off;
    bool HasFlames => Fun is FunMode.Flames or FunMode.All;
    bool HasBubbles => Fun is FunMode.Bubbles or FunMode.All;
    bool HasConfetti => Fun is FunMode.Confetti or FunMode.All;
    bool HasDisco => Fun is FunMode.Disco or FunMode.All;

    float flameT, flameCool = 2.5f, flameAcc, bubbleAcc, discoAcc, popBudget;
    public float FlameLevel;     // 0...1, how hard the flamethrower is going right now
    readonly List<Particle> popQueue = new();

    /// The nozzle sits under the front bumper; the bubble gun under the bin lid at the back.
    V2 Nozzle => Pos + Fwd * (R * 1.02f);
    V2 BubbleGun => Pos - Fwd * (R * 0.95f);

    void UpdateFun(float dt)
    {
        UpdateManual();
        if (Fun == FunMode.Off || Paused) { FlameLevel = 0; return; }
        popBudget = MathF.Min(4, popBudget + dt * 7);
        var working = Phase is Phase.Cleaning or Phase.Hunting && !Picked;
        if (HasFlames) UpdateFlames(dt, working);
        if (HasBubbles && (working || Phase is Phase.Scanning or Phase.Returning)) UpdateBubbles(dt);
        if (HasDisco) UpdateDisco(dt);
        if (HasLaser) UpdateLaser(dt, working); else LaserTarget = null;
        if (HasTornado) UpdateTornado(dt, working);
        if (HasPlow) UpdatePlow(dt);
        if (Fun == FunMode.Race) UpdateRace(dt);
        if (Fun == FunMode.Duel) UpdateDuel(dt);
        if (HasSquirrels) UpdateSquirrels(dt);
    }

    // MARK: Flamethrower

    void UpdateFlames(float dt, bool working)
    {
        // Bursts: a second or so of fire, then a breather. Something right ahead sets it off early.
        flameCool -= dt;
        var target = false;
        foreach (var it in Items)
        {
            if (!it.OnFloor) continue;
            var d = it.Icon.Pos - Nozzle;
            if (d.Len < R * 2.6f && d.Unit.Dot(Fwd) > 0.85f) { target = true; break; }
        }
        if (working && flameT <= 0 && (flameCool <= 0 || (target && flameCool < 1.6f)))
        {
            flameT = Rand(0.8f, 1.5f);
            flameCool = Rand(2.2f, 4.2f);
            Audio?.Flame(flameT);
        }
        if (flameT > 0 && working) flameT -= dt; else flameT = 0;
        FlameLevel += ((flameT > 0 ? 1 : 0) - FlameLevel) * MathF.Min(1, dt * 12);
        if (FlameLevel < 0.05f) return;

        var sway = MathF.Sin(T * 5.3f) * 0.18f;
        var dir = Fwd.Rotated(sway);
        flameAcc += dt * 220 * FlameLevel;
        while (flameAcc >= 1)
        {
            flameAcc -= 1;
            var v = dir.Rotated(Rand(-0.2f, 0.2f)) * Rand(560, 860) + Fwd * V;
            SpawnFun(PartKind.Fire, Nozzle + dir.Perp * Rand(-6, 6), v, Rand(0.45f, 0.75f), Rand(7, 12), 1, 0.85f, 0.4f, 1);
        }
        // Whatever is in the cone gets singed: darker, jittery, smoking.
        foreach (var it in Items)
        {
            if (!it.OnFloor || it.Icon.Ingesting) continue;
            var d = it.Icon.Pos - Nozzle; var l = d.Len;
            if (l > R * 4.4f || l < 1) continue;
            var cone = d.Unit.Dot(dir);
            if (cone < 0.88f) continue;
            var k = FlameLevel * (1 - l / (R * 4.4f));
            it.Scorch = MathF.Min(1, it.Scorch + dt * 1.8f * k);
            if (it.Scorch >= 1) StartBurn(it);   // too much: it catches fire
            it.Icon.Agit = MathF.Max(it.Icon.Agit, 0.35f * k);
            if (Rand() < dt * 10 * k) SpawnFun(PartKind.Smoke, it.Icon.Pos + new V2(Rand(-14, 14), Rand(-14, 14)), new V2(Rand(-20, 20), Rand(-40, -10)), Rand(0.9f, 1.6f), Rand(8, 14), 0.2f, 0.19f, 0.18f, 0.5f);
        }
    }

    // MARK: Burning to ash

    /// Taking something in. Normally it's swallowed; with the flamethrower on it burns to ash first,
    /// and the robot vacuums up the ash.
    void Take(Item it, bool icon)
    {
        if (it.Buried) Unbury(it);
        TakeNow(it, icon);
    }

    void TakeNow(Item it, bool icon)
    {
        // The snowplow pushes things rather than taking them in, until it is time to vacuum the heap.
        if (Plowing) { if (icon && !it.Carried) Capture(it, (it.Icon.Pos - Pos).Dot(Side), 0); return; }
        if (!HasFlames && !HasLaser) { StartIngest(it, icon); return; }
        if (icon) StartBurn(it); else StartLabelBurn(it);
    }

    float burnSound, ashTick;

    /// Something on fire right in front: the robot waits for it to burn down, so the ash goes straight in.
    bool BurningAhead
    {
        get
        {
            if (!HasFlames && !HasLaser) return false;
            foreach (var it in Items) if (it.Burning && (it.Icon.Pos - Intake).Len < R * 1.6f) return true;
            return false;
        }
    }

    void StartBurn(Item it)
    {
        if (it.Burning || it.Icon.Ingesting || it.Protected || it.Icon.Gone) return;
        it.Burning = true;
        it.BurnStyle = HasLaser && !HasFlames ? 1 : 0;
        if (it.BurnStyle == 1) return;   // the laser does its own thing
        it.Scorch = 1;
        // The flamethrower gives it a proper blast.
        if (Phase is Phase.Cleaning or Phase.Hunting && !Picked) flameT = MathF.Max(flameT, 0.6f);
        if (T - burnSound > 0.5f) { burnSound = T; Audio?.Flame(0.7f); }
    }

    void StartLabelBurn(Item it)
    {
        if (it.LabelBurning || it.Label.Gone || it.Protected) return;
        it.LabelBurning = true;
    }

    void UpdateBurn(Item it, float dt)
    {
        var laser = it.BurnStyle == 1;
        // Big files take a little longer to go.
        it.Burn = MathF.Min(1, it.Burn + dt / ((laser ? 0.45f : 0.55f) + 0.08f * (it.Mass - 1)));
        if (!laser) it.Scorch = 1;
        it.Icon.Vel *= MathF.Exp(-dt * 6);
        it.Icon.Pos += it.Icon.Vel * dt;
        it.Icon.Agit = 0.18f;
        var half = Metrics.IconHalf;
        var c = it.Color;
        if (laser)
        {
            // Falls apart into its own coloured pixels.
            if (Rand() < dt * 60) Pixels(it.Icon.Pos + new V2(Rand(-half, half), Rand(-half, half)), 1, c);
            if (Rand() < dt * 30) SpawnFun(PartKind.Glow, it.Icon.Pos + new V2(Rand(-half, half), Rand(-half, half)), new V2(Rand(-40, 40), Rand(-40, 40)), Rand(0.2f, 0.4f), Rand(1, 2), 0.5f, 0.95f, 1, 1);
        }
        else
        {
            // Embers fly off the burning edge, flakes of ash drop where it stood.
            if (Rand() < dt * 45)
                SpawnFun(PartKind.Glow, it.Icon.Pos + new V2(Rand(-half, half), Rand(-half, half)), new V2(Rand(-30, 30), Rand(-70, -20)), Rand(0.35f, 0.8f), Rand(1.2f, 2.6f), 1, Rand(0.35f, 0.6f), 0.08f, 1);
            if (Rand() < dt * 30) Ash(it.Icon.Pos + new V2(Rand(-half, half), Rand(-half, half)), 1);
            if (Rand() < dt * 8) SpawnFun(PartKind.Smoke, it.Icon.Pos + new V2(Rand(-half, half), Rand(-half, half)), new V2(Rand(-15, 15), Rand(-35, -10)), Rand(1, 1.8f), Rand(8, 13), 0.2f, 0.19f, 0.18f, 0.45f);
        }
        if (it.Burn < 1) return;
        // Gone: what's left is a little heap of ash (or pixels) for the robot to pick up.
        it.Burning = false;
        it.Icon.Gone = true;
        if (it.LabelAttached) it.Label.Gone = true;
        it.InBin = true; BinCount++; BinBytes += it.Entry.Bytes;
        Collected++; CollectedBytes += it.Entry.Bytes;
        if (laser) Pixels(it.Icon.Pos, 22 + (int)(it.Mass * 4), c); else Ash(it.Icon.Pos, 18 + (int)(it.Mass * 4));
    }

    void UpdateLabelBurn(Item it, float dt)
    {
        it.LabelBurn = MathF.Min(1, it.LabelBurn + dt / 0.5f);
        it.Label.Vel *= MathF.Exp(-dt * 5);
        it.Label.Pos += it.Label.Vel * dt;
        if (Rand() < dt * 25) SpawnFun(PartKind.Glow, it.Label.Pos + new V2(Rand(-Metrics.LabelHalf.X, Metrics.LabelHalf.X) * 0.7f, Rand(-6, 6)), new V2(Rand(-20, 20), Rand(-50, -15)), Rand(0.3f, 0.6f), Rand(1, 2), 1, 0.5f, 0.1f, 1);
        if (it.LabelBurn < 1) return;
        it.LabelBurning = false;
        it.Label.Gone = true;
        Ash(it.Label.Pos, 6);
    }

    /// Grey flakes that settle on the floor until the robot's suction finds them.
    void Ash(V2 at, int n)
    {
        for (int k = 0; k < n; k++)
        {
            var g = Rand(0.16f, 0.36f);
            SpawnFun(PartKind.Ash, at + new V2(Rand(-14, 14), Rand(-14, 14)), new V2(Rand(-60, 60), Rand(-60, 60)), Rand(7, 11), Rand(2.2f, 4.5f), g, g * 0.97f, g * 0.94f, 0.95f, chip: 1);
        }
    }

    /// Square pixels in the colours of what the laser took apart. Vacuumed up like ash.
    void Pixels(V2 at, int n, V3 c)
    {
        for (int k = 0; k < n; k++)
        {
            var j = Rand(0.7f, 1.3f);
            SpawnFun(PartKind.Ash, at + new V2(Rand(-14, 14), Rand(-14, 14)), new V2(Rand(-90, 90), Rand(-90, 90)), Rand(7, 11), Rand(2.5f, 4), M.Clamp(c.X * j, 0, 1), M.Clamp(c.Y * j, 0, 1), M.Clamp(c.Z * j, 0, 1), 1, chip: 1.2f);
        }
    }

    // MARK: Bubble gun

    void UpdateBubbles(float dt)
    {
        bubbleAcc += dt * 9;
        while (bubbleAcc >= 1)
        {
            bubbleAcc -= 1;
            var v = (-Fwd).Rotated(Rand(-0.9f, 0.9f)) * Rand(60, 170);
            SpawnFun(PartKind.Bubble, BubbleGun + Side * Rand(-R * 0.3f, R * 0.3f), v, Rand(2.5f, 5.5f), Rand(9, 24), 1, 1, 1, 1);
        }
    }

    void PopBubble(Particle b)
    {
        for (int k = 0; k < 7; k++)
        {
            var a = Rand(0, 6.283f);
            SpawnFun(PartKind.Glow, b.Pos + new V2(MathF.Cos(a), MathF.Sin(a)) * b.Size * 0.8f, new V2(MathF.Cos(a), MathF.Sin(a)) * Rand(40, 110), Rand(0.15f, 0.3f), Rand(1.2f, 2.2f), 0.8f, 0.95f, 1, 0.9f);
        }
        if (popBudget >= 1) { popBudget -= 1; Audio?.Pop(); }
    }

    // MARK: Confetti

    void FunIngest(Item it)
    {
        if (HasConfetti) ConfettiBurst(Intake, 22, 320, Fwd, 1.2f);
    }

    void FunDone()
    {
        if (Fun == FunMode.Race) RaceDone();
        if (Fun == FunMode.Duel) DuelDone();
        FunDoneConfetti();
    }

    void FunDoneConfetti()
    {
        if (!HasConfetti) return;
        // The dock's cannon: a big one, straight up the screen.
        ConfettiBurst(DockPos + DockOut * (R * 0.4f), 160, 900, DockOut, 0.7f);
        Audio?.Popper();
    }

    void ConfettiBurst(V2 at, int n, float speed, V2 dir, float spread)
    {
        for (int k = 0; k < n; k++)
        {
            var (r, g, b) = Hue(Rand());
            var v = dir.Rotated(Rand(-spread, spread)) * Rand(speed * 0.35f, speed);
            SpawnFun(PartKind.Confetti, at, v, Rand(2.2f, 4f), Rand(5, 9), r, g, b, 1, chip: 1);
        }
    }

    // MARK: Disco

    void UpdateDisco(float dt)
    {
        var (r, g, b) = Hue(T * 0.35f);
        Led = new V3(r, g, b);
        // A ring of coloured light every beat, out from the robot across the floor.
        discoAcc += dt;
        if (discoAcc < 0.5f) return;
        discoAcc = 0;
        var (cr, cg, cb) = Hue(T * 0.35f + 0.5f);
        for (int k = 0; k < 28; k++)
        {
            var a = k / 28f * 6.283f;
            var d = new V2(MathF.Cos(a), MathF.Sin(a));
            SpawnFun(PartKind.Glow, Pos + d * R * 1.05f, d * 260, 0.9f, 3.2f, cr, cg, cb, 0.8f);
        }
    }

    static (float r, float g, float b) Hue(float h)
    {
        h -= MathF.Floor(h);
        float R6(float o) => M.Clamp(MathF.Abs((h * 6 + o) % 6 - 3) - 1, 0, 1);
        return (R6(0), R6(4), R6(2));
    }

    // MARK: Particles

    void SpawnFun(PartKind kind, V2 p, V2 vel, float life, float size, float r, float g, float b, float a, float chip = 0)
    {
        if (Particles.Count > 2300) Particles.RemoveRange(0, 100);
        Particles.Add(new Particle { Pos = p, Vel = vel, Life = life, MaxLife = life, Size = size, R = r, G = g, B = b, A = a, Chip = chip, Ang = Rand(0, 6.28f), Kind = kind, Seed = Rand(0, 100) });
    }

    void MoveFunParticle(ref Particle q, float dt)
    {
        switch (q.Kind)
        {
            case PartKind.Fire:
                q.Vel *= MathF.Exp(-dt * 2.6f);
                break;
            case PartKind.Smoke:
                q.Vel *= MathF.Exp(-dt * 1.2f);
                break;
            case PartKind.Bubble:
            {
                // Drifts and wobbles; pops on an icon, nudging it, or on the robot.
                q.Vel *= MathF.Exp(-dt * 0.55f);
                q.Vel += new V2(MathF.Sin(T * 2.1f + q.Seed), MathF.Cos(T * 1.7f + q.Seed * 1.3f)) * (26 * dt);
                if ((q.Pos - Pos).Len < R + q.Size * 0.5f && q.Life < q.MaxLife - 0.4f) { q.Life = 0; break; }
                foreach (var it in Items)
                {
                    if (!it.OnFloor) continue;
                    if ((it.Icon.Pos - q.Pos).Len < Metrics.IconHalf * 0.7f)
                    {
                        it.Icon.Vel += q.Vel * 0.25f;
                        it.Icon.Agit = MathF.Max(it.Icon.Agit, 0.3f);
                        q.Life = 0;
                        break;
                    }
                }
                break;
            }
            case PartKind.Confetti:
                q.Vel *= MathF.Exp(-dt * 2.8f);
                q.Ang += dt * (6 + q.Seed % 6);
                break;
            case PartKind.Swirl:
            {
                // Orbits the robot, drifting inwards.
                var d = q.Pos - Pos;
                q.Vel = d.Unit.Perp * 430 - d.Unit * 50;
                break;
            }
            case PartKind.Snow:
            {
                // Lies where it fell. The plow blade pushes it along, and the robot leaves a track through it.
                var rel = q.Pos - Pos;
                float f = rel.Dot(Fwd), s = rel.Dot(Side);
                if (Plowing && f > R * 0.6f && f < R * 1.4f && MathF.Abs(s) < R * 1.05f) q.Pos += Fwd * (MathF.Max(V, 0) * dt) + Side * (MathF.Sign(s) * MathF.Abs(V) * 0.15f * dt);
                else if (rel.Len < R) q.Pos = Pos + rel.Unit * R;
                if (SuctionOn && !Picked)
                {
                    var d = Intake - q.Pos; var l = d.Len;
                    if (l < R * 2.2f) q.Vel += d.Unit * (2400 * (1 - l / (R * 2.2f))) * dt;
                    if (l < R * 0.45f) q.Life = 0;
                }
                q.Vel *= MathF.Exp(-dt * 4);
                break;
            }
            case PartKind.Ash:
            {
                // Lies where it fell until the suction gets near, then goes in like any other dust.
                if (SuctionOn && !Picked)
                {
                    var d = Intake - q.Pos; var l = d.Len;
                    if (l < R * 2.4f) q.Vel += d.Unit * (2600 * (1 - l / (R * 2.4f))) * dt;
                    if (l < R * 0.45f) { q.Life = 0; ashTick += 0.12f; if (ashTick > 1) { ashTick = 0; Audio?.Tick(); } }
                }
                q.Vel *= MathF.Exp(-dt * 4.5f);
                q.Ang += dt * q.Vel.Len * 0.02f;
                break;
            }
            case PartKind.Glow:
                q.Vel *= MathF.Exp(-dt * 1.5f);
                break;
        }
        q.Pos += q.Vel * dt;
    }
}
