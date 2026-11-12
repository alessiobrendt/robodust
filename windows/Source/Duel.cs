using System;
using System.Linq;

namespace RoboDust;

/// One robot in the duel: RoboDust itself, or the challenger it's fighting. Health, the weapon's
/// state, and the knocks it takes. Only a picture: no file is touched by any of it.
public sealed class Fighter
{
    public string Name;
    public V2 Pos, Knock;
    public float Heading = -MathF.PI / 2, V, W, R;
    public Look Look;
    public V3 Led;
    public float Hp = 1, Stun, Air, Spin, Swing, Strike = -1, Cool, BumpCool, Alpha = 1, KoT, Recoil, Flash, Grace;
    public bool Down, Leaving, Exploded;
    public int Wins;
    public float BrushAngle, BrushSpeed, LidarAngle, LedPulse;
    public const float AirTime = 0.8f;
    public int Side = 1;
    public V2 Fwd => new(MathF.Cos(Heading), MathF.Sin(Heading));
    public V2 SideV => Fwd.Perp;
    public (V2 l, V2 r) BrushCenters { get { var a = Fwd * (R * 0.62f); return (Pos + a + SideV * (R * 0.72f), Pos + a - SideV * (R * 0.72f)); } }
    /// 0 on the floor, 1 at the top of a flip.
    public float LiftK => Air > 0 ? MathF.Sin(M.Clamp(1 - Air / AirTime, 0, 1) * MathF.PI) : 0;
    public float Toughness => Look.Armor ? 0.55f : 1;
}

public sealed partial class Sim
{
    public Fighter Me, Foe;
    public int DuelWins, DuelLosses;
    /// The shop's fitting room: weapons swing on their own so they can be seen.
    public bool Showcase;
    int foeIndex;
    float foeDelay = 2.5f, engageT, engageCool = 2, grindBudget;

    static readonly (string name, int weapon, RobotSkin skin, V3 led, bool armor, bool spikes)[] Challengers =
    {
        ("Krawall", 1, RobotSkin.Carbon, new V3(1, 0.25f, 0.2f), false, false),
        ("Brecher", 2, RobotSkin.Camo, new V3(1, 0.6f, 0.15f), true, false),
        ("Kipper", 3, RobotSkin.Police, new V3(1, 0.9f, 0.2f), false, true),
        ("Wirbel", 4, RobotSkin.Lava, new V3(1, 0.3f, 0.8f), false, false),
        ("Igel", 5, RobotSkin.Galaxy, new V3(0.6f, 0.4f, 1), true, true),
    };

    /// "RoboDust 2 : 1 · gegen Brecher", for the status line.
    public string DuelLine => Me == null ? null : $"K.o.: RoboDust {DuelWins} : {DuelLosses} Gegner" + (Foe is { Down: false } f ? $" · gegen {f.Name}" : "");

    bool DuelOn => Phase is Phase.Cleaning or Phase.Hunting or Phase.Returning && !Picked;

    void UpdateDuel(float dt)
    {
        Me ??= new Fighter { Name = "RoboDust", R = R };
        var me = Me;
        me.Look = Look; me.Pos = Pos; me.Heading = Heading; me.V = V;
        if (me.Look.Weapon == 0) me.Look.Weapon = 5;   // never unarmed: the free wedge
        var active = DuelOn;
        var meWasUp = me.Air > 0;
        Tick(me, dt, SuctionOn);
        if (meWasUp && me.Air <= 0) { Dust(Pos, R, 20); Audio?.Thud(0.4f); }

        // Challengers, one after another, as long as RoboDust is out cleaning.
        if (Foe == null) { if (active) { foeDelay -= dt; if (foeDelay <= 0) SpawnFoe(); } }
        else UpdateFoe(Foe, dt, active);

        // RoboDust knocked out: sputters, blows up, and a fresh one drops in where it stood.
        if (me.Down)
        {
            me.KoT += dt;
            if (!me.Exploded) { Critical(me, dt); if (me.KoT > FuseTime) { Explode(me); RobotAlpha = 0; } }
            else if (me.KoT > FuseTime + 2.2f)
            {
                me.Down = false; me.Exploded = false; me.Alpha = 1; RobotAlpha = 1;
                me.Hp = 1; me.KoT = 0; me.Grace = 4; me.Air = Fighter.AirTime;
                Audio?.Chirp((523, 0.07f), (659, 0.07f), (784, 0.12f));
            }
        }
        else if (me.Hp < 1) me.Hp = MathF.Min(1, me.Hp + dt * 0.012f);

        // Knocks shove RoboDust about, but never through a wall or into the dock.
        if (me.Knock.Len > 2)
        {
            Pos = ClampToWalk(Pos + me.Knock * dt);
            PushOutOfDock();
            me.Knock *= MathF.Exp(-dt * 5);
        }
        else me.Knock = V2.Zero;
        me.Pos = Pos;

        if (Foe is { Down: false, Leaving: false } foe && active && !me.Down && foe.Alpha > 0.6f)
        {
            Attack(me, foe, dt);
            if (me.Grace <= 0) Attack(foe, me, dt);   // fresh from the repair, it gets a moment's peace
            Separate(foe);
            // Every so often it stops cleaning for a moment and goes for the challenger.
            engageCool -= dt; engageT -= dt;
            if (engageT <= 0 && engageCool <= 0 && (foe.Pos - Pos).Len < R * 3.6f)
            {
                engageT = Rand(1.6f, 2.8f);
                engageCool = engageT + Rand(2.5f, 4.5f);
            }
        }
        else engageT = 0;
        me.Pos = Pos;
    }

    /// While stunned, flipped or knocked out it can't drive; while it goes for the challenger it
    /// drives at it instead of along its lane. Called before the robot follows its path.
    bool DuelHold(float dt)
    {
        if (Fun != FunMode.Duel || Me == null) return false;
        if (Me.Down) { DriveRaw(0, Me.Exploded ? 0 : 2.4f, dt); Status = Me.Exploded ? "Ein neuer RoboDust kommt …" : "Kritischer Schaden!"; return true; }
        if (Me.Stun > 0 || Me.Air > 0) { DriveRaw(0, 0, dt); Status = "Benommen!"; return true; }
        if (engageT > 0 && Foe is { Down: false } f && !ManualActive)
        {
            var err = M.Wrap(MathF.Atan2(f.Pos.Y - Pos.Y, f.Pos.X - Pos.X) - Heading);
            var align = M.Clamp(1 - MathF.Abs(err) / 1.2f, 0, 1);
            DriveRaw(380 * SpeedScale * align, M.Clamp(err * 5, -3.4f, 3.4f), dt);
            Status = "Kämpft gegen " + f.Name;
            return true;
        }
        return false;
    }

    void SpawnFoe()
    {
        var spec = Challengers[foeIndex % Challengers.Length];
        var room = Plan.RoomOf(Pos);
        if (room < 0) room = Plan.Main;
        V2 at = Pos;
        for (int i = 0; i < 40; i++)
        {
            var r = Plan.Rooms[i < 30 ? room : (int)(Rand() * Plan.Rooms.Count) % Plan.Rooms.Count].Inset(R * 1.6f);
            if (!r.Valid) continue;
            var p = new V2(r.X + Rand() * r.W, r.Y + Rand() * r.H);
            if ((p - Pos).Len < R * 4.5f || (p - DockPos).Len < R * 3.5f) continue;
            at = p; break;
        }
        if ((at - Pos).Len < R * 3) return;   // nowhere to land; tries again next frame
        Foe = new Fighter
        {
            Name = spec.name, R = R * 0.9f, Pos = at, Led = spec.led, Air = Fighter.AirTime, Alpha = 0,
            Look = new Look { Skin = spec.skin, Weapon = spec.weapon, Armor = spec.armor, Spikes = spec.spikes },
            Heading = MathF.Atan2(Pos.Y - at.Y, Pos.X - at.X), Side = Rand() < 0.5f ? -1 : 1,
        };
        Status = "Herausforderer: " + spec.name + "!";
        Audio?.Chirp((196, 0.12f), (196, 0.12f), (262, 0.3f));
    }

    void UpdateFoe(Fighter f, float dt, bool active)
    {
        f.LedPulse += dt; f.LidarAngle += 14 * dt;
        f.BrushSpeed += ((f.Down ? 0 : 26) - f.BrushSpeed) * MathF.Min(1, dt * 3);
        f.BrushAngle += f.BrushSpeed * dt;
        var wasUp = f.Air > 0;
        Tick(f, dt, !f.Down);
        if (wasUp && f.Air <= 0) { Dust(f.Pos, f.R, 22); Audio?.Thud(0.35f); }
        f.Alpha = MathF.Min(f.Down || f.Leaving || !active ? f.Alpha : 1, f.Alpha + dt * 3);

        if (f.Down)
        {
            // Knocked out: spins, sputters, and blows up. Then the next challenger can come.
            f.KoT += dt;
            f.Heading += dt * MathF.Max(0, 6 - f.KoT * 3);
            f.V *= MathF.Exp(-dt * 3);
            if (!f.Exploded) { Critical(f, dt); if (f.KoT > FuseTime) Explode(f); }
            else if (f.KoT > FuseTime + 2.8f) { Foe = null; foeIndex++; foeDelay = Rand(3, 6); return; }
        }
        else if (!active || f.Leaving)
        {
            // The run is wrapping up, or it has won twice: the challenger calls it a day.
            f.V *= MathF.Exp(-dt * 4);
            f.Alpha -= dt * 1.5f;
            if (f.Alpha <= 0) { Foe = null; if (f.Leaving) { foeIndex++; foeDelay = Rand(6, 9); } return; }
        }
        else if (f.Stun > 0 || f.Air > 0) f.V *= MathF.Exp(-dt * 4);
        else
        {
            var me = Me;
            var d = me.Pos - f.Pos;
            V2 aim;
            if (me.Down || me.Grace > 0) aim = me.Pos + new V2(MathF.Cos(T * 1.3f), MathF.Sin(T * 1.3f)) * (R * 3.2f);   // victory lap
            else
            {
                // Comes in from the side, and the closer it gets the straighter it goes.
                var off = MathF.Min(d.Len, R * 4) * 0.4f * f.Side;
                aim = me.Pos + d.Perp.Unit * off;
                if (Rand() < dt * 0.25f) f.Side = -f.Side;
            }
            var route = Plan.Route(f.Pos, aim, f.R + 2);
            var to = route[0] - f.Pos;
            var err = M.Wrap(MathF.Atan2(to.Y, to.X) - f.Heading);
            if (f.Recoil > 0)
            {
                // Backs off after landing a hit, still facing RoboDust.
                err = M.Wrap(MathF.Atan2(d.Y, d.X) - f.Heading);
                f.W += M.Clamp(M.Clamp(err * 4, -3, 3) - f.W, -12 * dt, 12 * dt);
                f.V += M.Clamp(-220 - f.V, -900 * dt, 900 * dt);
            }
            else
            {
                var align = M.Clamp(1 - MathF.Abs(err) / 1.2f, 0, 1);
                var vCmd = 330 * SpeedScale * align * align;
                f.V += M.Clamp(vCmd - f.V, -800 * dt, 800 * dt);
                f.W += M.Clamp(M.Clamp(err * 4.4f, -3.4f, 3.4f) - f.W, -13 * dt, 13 * dt);
            }
            f.Heading = M.Wrap(f.Heading + f.W * dt);
        }
        f.Pos += f.Fwd * (f.V * dt) + f.Knock * dt;
        f.Knock *= MathF.Exp(-dt * 5);
        // Off the dock, inside the rooms.
        var k = f.Pos - DockPos;
        float u = k.Dot(DockOut), s = k.Dot(DockOut.Perp);
        if (u < R * 1.6f && u > -R * 2.4f && MathF.Abs(s) < R * 1.5f + f.R * 0.5f) f.Pos += DockOut * (R * 1.6f - u);
        f.Pos = Plan.Clamp(f.Pos, f.R + 2);
        // Icons in the way get shoved.
        foreach (var it in Items)
        {
            if (!it.OnFloor || it.Icon.Ingesting || it.Burning || it.Carried) continue;
            var e = it.Icon.Pos - f.Pos;
            if (e.Len < f.R + 14 && e.Len > 0.01f) { it.Icon.Pos = f.Pos + e.Unit * (f.R + 14); it.Icon.Agit = MathF.Max(it.Icon.Agit, 0.4f); }
        }
    }

    /// Timers and the weapon's own motion: the saw and the spinner turn, the hammer and the flipper settle back.
    void Tick(Fighter f, float dt, bool on)
    {
        f.Stun = MathF.Max(0, f.Stun - dt);
        f.Air = MathF.Max(0, f.Air - dt);
        f.Cool -= dt; f.BumpCool -= dt; f.Recoil -= dt;
        f.Flash *= MathF.Exp(-dt * 6);
        f.Grace = MathF.Max(0, f.Grace - dt);
        var spinRate = f.Look.Weapon switch { 4 => 30f, 1 => 24f, _ => 0f };
        if (on && !f.Down) f.Spin += spinRate * dt;
        if (f.Strike < 0) f.Swing = MathF.Max(0, f.Swing - dt * (f.Look.Weapon == 3 ? 3 : 1.6f));
    }

    void Attack(Fighter a, Fighter b, float dt)
    {
        if (a.Stun > 0 || a.Air > 0) return;
        var d = b.Pos - a.Pos; var dist = d.Len;
        if (dist < 0.01f) return;
        var u = d / dist;
        var front = u.Dot(a.Fwd);
        var gap = dist - a.R - b.R;
        // The hammer comes down a moment after it starts its swing.
        if (a.Strike >= 0)
        {
            a.Strike -= dt;
            a.Swing = M.Clamp(1 - a.Strike / 0.24f, 0, 1);
            if (a.Strike < 0)
            {
                a.Strike = -1; a.Swing = 1;
                var head = a.Pos + a.Fwd * (a.R * 1.2f);
                if ((b.Pos - head).Len < b.R * 1.15f && b.Air <= 0) { Hit(a, b, 0.22f, a.Fwd * 170, 0.8f); Sparks(head, 16, 380); Audio?.Thud(0.6f); Audio?.Chirp((1760, 0.03f), (1320, 0.06f)); }
                else { Dust(head, a.R * 0.4f, 10); Audio?.Thud(0.25f); }
            }
            return;
        }
        if (b.Air > 0) return;   // can't be hit in mid-air
        switch (a.Look.Weapon)
        {
            case 1:   // saw: grinds whatever is pressed against the front
                if (gap < a.R * 0.42f && front > 0.55f)
                {
                    Hit(a, b, 0.3f * dt, u * 70, 0, recoil: false);
                    if (Rand() < dt * 60) Sparks(a.Pos + a.Fwd * (a.R * 1.2f), 2, 320);
                    grindBudget += dt;
                    if (grindBudget > 0.18f) { grindBudget = 0; Audio?.Chirp((2400, 0.03f), (2100, 0.03f)); }
                }
                break;
            case 2:   // hammer: winds up when something is in front
                if (a.Cool <= 0 && gap < a.R * 0.8f && front > 0.6f) { a.Strike = 0.24f; a.Cool = 1.7f; }
                break;
            case 3:   // flipper: throws it into the air
                if (a.Cool <= 0 && gap < a.R * 0.45f && front > 0.7f)
                {
                    a.Cool = 2.4f; a.Swing = 1;
                    Hit(a, b, 0.12f, a.Fwd * 430, 0.3f);
                    b.Air = Fighter.AirTime;
                    Dust(a.Pos + a.Fwd * a.R, a.R * 0.5f, 12);
                    Audio?.Chirp((330, 0.05f), (660, 0.05f), (990, 0.1f));
                }
                break;
            case 4:   // spinner: anything that touches it gets flung
                if (a.Cool <= 0 && gap < a.R * 0.32f)
                {
                    a.Cool = 0.5f;
                    Hit(a, b, 0.13f, u * 560, 0.15f);
                    a.Knock -= u * 220;
                    Sparks(a.Pos + u * (a.R * 1.15f), 14, 460);
                    Audio?.Chirp((1980, 0.025f), (1480, 0.05f)); Audio?.Thud(0.3f);
                }
                break;
            case 5:   // wedge: shoves, and now and then gets under it
                if (a.Cool <= 0 && gap < 10 && front > 0.6f)
                {
                    a.Cool = 0.8f;
                    Hit(a, b, 0.09f, a.Fwd * 360, 0.25f);
                    if (Rand() < 0.3f) b.Air = Fighter.AirTime * 0.6f;
                    Audio?.Thud(0.3f);
                }
                break;
            default:  // nothing bolted on: a plain ram
                if (a.Cool <= 0 && gap < 6 && front > 0.7f && MathF.Abs(a.V) > 120)
                {
                    a.Cool = 0.9f;
                    Hit(a, b, 0.045f, u * 260, 0);
                    Audio?.Thud(0.25f);
                }
                break;
        }
        // Spikes: ramming into them hurts the one doing the ramming.
        if (b.Look.Spikes && gap < 6 && b.BumpCool <= 0 && front > 0.5f && MathF.Abs(a.V) > 80)
        {
            b.BumpCool = 0.8f;
            Hit(b, a, 0.05f, -u * 200, 0, recoil: false);
            Sparks(a.Pos + u * a.R, 6, 260);
        }
    }

    void Hit(Fighter by, Fighter to, float damage, V2 knock, float stun, bool recoil = true)
    {
        if (to.Down) return;
        to.Hp -= damage * to.Toughness;
        to.Knock += knock;
        to.Stun = MathF.Max(to.Stun, stun);
        to.Flash = 1;
        if (recoil && by != Me) by.Recoil = 0.5f;
        if (to == Me && knock.Len > 100) { Shake = MathF.Max(Shake, 0.8f); Bumper = 0.8f; }
        if (knock.Len > 100) Event(3, to.Pos.X, to.Pos.Y, M.Clamp(knock.Len / 900, 0.15f, 0.7f));
        if (to.Hp > 0) return;
        Event(6, to.Pos.X, to.Pos.Y, 0);
        // K.o.
        // K.o.: a shower of sparks now; a moment later, while it sputters, it blows up (see Explode).
        to.Hp = 0; to.Down = true; to.KoT = 0; to.Stun = 0; to.Exploded = false;
        Sparks(to.Pos, 18, 480);
        Audio?.Thud(0.7f);
        if (to == Me)
        {
            DuelLosses++;
            if (++by.Wins >= 2) by.Leaving = true;
            Audio?.Chirp((392, 0.15f), (330, 0.15f), (262, 0.4f));
        }
        else
        {
            DuelWins++;
            ConfettiBurst(Pos, 50, 420, Fwd, 3.1f);
            Audio?.Chirp((523, 0.08f), (659, 0.08f), (784, 0.08f), (1047, 0.3f));
        }
    }

    // MARK: Going up in flames

    /// How long a beaten robot sputters before it blows up.
    public const float FuseTime = 1.3f;

    /// Beaten, about to blow: sparks, little flames and smoke from the shell. Each side draws this
    /// for itself, so nothing here goes over the network.
    void Critical(Fighter f, float dt)
    {
        var k = M.Clamp(f.KoT / FuseTime, 0, 1);
        if (Rand() < dt * (14 + 30 * k)) SparksLocal(f.Pos + new V2(Rand(-f.R, f.R), Rand(-f.R, f.R)) * 0.7f, 3, 260);
        if (Rand() < dt * (20 + 50 * k))
        {
            var p = f.Pos + new V2(Rand(-f.R, f.R), Rand(-f.R, f.R)) * 0.55f;
            SpawnFun(PartKind.Fire, p, new V2(Rand(-30, 30), Rand(-90, -30)), Rand(0.35f, 0.6f), Rand(9, 15), 1, 0.7f, 0.3f, 1);
        }
        if (Rand() < dt * 16) SpawnFun(PartKind.Smoke, f.Pos + new V2(Rand(-f.R * 0.5f, f.R * 0.5f), Rand(-f.R * 0.5f, f.R * 0.5f)), new V2(Rand(-20, 20), Rand(-70, -25)), Rand(1.2f, 2), Rand(14, 22), 0.2f, 0.2f, 0.22f, 0.75f);
    }

    /// Boom. The robot is gone; what's left is fire, smoke, flying bits of shell and a scorch mark.
    void Explode(Fighter f)
    {
        if (f.Exploded) return;
        f.Exploded = true; f.Alpha = 0; f.V = 0; f.Knock = V2.Zero;
        Event(7, f.Pos.X, f.Pos.Y, (int)f.Look.Skin);
        ExplosionFx(f.Pos, f.R, f.Look.Skin);
    }

    void ExplosionFx(V2 at, float r, RobotSkin skin)
    {
        V2 Dir(out float a) { a = Rand(0, 6.283f); return new V2(MathF.Cos(a), MathF.Sin(a)); }
        // A scorch mark on the floor, under everything else, that fades slowly.
        for (int i = 0; i < 9; i++) SpawnFun(PartKind.Ribbon, at + Dir(out _) * Rand(0, r * 0.7f), V2.Zero, Rand(7, 10), Rand(26, 42), 0.06f, 0.05f, 0.05f, 0.5f);
        // The flash: a white-hot core, then a fireball rolling outwards.
        for (int i = 0; i < 10; i++) SpawnFun(PartKind.Fire, at + Dir(out _) * Rand(0, r * 0.3f), Dir(out _) * Rand(20, 80), Rand(0.25f, 0.4f), Rand(28, 40), 1, 0.95f, 0.8f, 1);
        for (int i = 0; i < 70; i++) SpawnFun(PartKind.Fire, at + Dir(out _) * Rand(0, r * 0.5f), Dir(out _) * Rand(120, 620), Rand(0.45f, 0.95f), Rand(12, 24), 1, 0.7f, 0.3f, 1);
        // The shockwave: a ring of light racing out.
        for (int i = 0; i < 48; i++) { var d = new V2(MathF.Cos(i * 0.1309f), MathF.Sin(i * 0.1309f)); SpawnFun(PartKind.Glow, at + d * r * 0.4f, d * 1100, 0.28f, 3.2f, 1, 0.85f, 0.55f, 1); }
        // Sparks everywhere.
        SparksLocal(at, 60, 900);
        // Bits of shell, top plate and metal: they land and lie there until RoboDust vacuums them up.
        var body = DebrisColor(skin);
        for (int i = 0; i < 46; i++)
        {
            var pick = Rand();
            var c = pick < 0.55f ? body : pick < 0.8f ? new V3(0.08f, 0.08f, 0.09f) : new V3(0.62f, 0.64f, 0.68f);
            SpawnFun(PartKind.Ash, at + Dir(out _) * Rand(0, r * 0.6f), Dir(out _) * Rand(220, 640), Rand(8, 12), Rand(4.5f, 9.5f), c.X, c.Y, c.Z, 1, chip: 1.2f);
        }
        // Thick smoke rising from where it stood.
        for (int i = 0; i < 26; i++) SpawnFun(PartKind.Smoke, at + Dir(out _) * Rand(0, r * 0.6f), Dir(out _) * Rand(40, 200) + new V2(0, -40), Rand(2, 3.6f), Rand(24, 40), 0.16f, 0.15f, 0.15f, 0.85f);
        Shake = MathF.Max(Shake, 1.2f);
        Audio?.Popper(); Audio?.Thud(1); Audio?.Flame(1.1f); Audio?.Chirp((70, 0.5f));
    }

    /// Roughly the colour of the shell, for the debris.
    static V3 DebrisColor(RobotSkin s) => s switch
    {
        RobotSkin.Black or RobotSkin.Carbon or RobotSkin.Neon or RobotSkin.Lava => new V3(0.12f, 0.12f, 0.13f),
        RobotSkin.Galaxy => new V3(0.12f, 0.08f, 0.24f),
        RobotSkin.Gold => new V3(0.95f, 0.74f, 0.32f),
        RobotSkin.Pink => new V3(0.97f, 0.62f, 0.76f),
        RobotSkin.Camo => new V3(0.3f, 0.34f, 0.2f),
        RobotSkin.Squirrel or RobotSkin.Wood or RobotSkin.Leopard => new V3(0.62f, 0.36f, 0.16f),
        RobotSkin.Police => new V3(0.18f, 0.24f, 0.55f),
        RobotSkin.Retro => new V3(0.86f, 0.8f, 0.66f),
        RobotSkin.Ice => new V3(0.75f, 0.88f, 0.98f),
        _ => new V3(0.9f, 0.9f, 0.88f),
    };

    /// Robots don't drive through each other: the challenger gives way more than RoboDust does.
    void Separate(Fighter f)
    {
        var e = f.Pos - Pos; var min = f.R + R + 2;
        if (e.Len >= min || e.Len < 0.01f) return;
        var push = e.Unit * (min - e.Len);
        f.Pos += push * 0.7f;
        Pos = ClampToWalk(Pos - push * 0.3f);
    }

    void Sparks(V2 at, int n, float speed)
    {
        Event(1, at.X, at.Y, n);
        for (int i = 0; i < n; i++)
        {
            var a = Rand(0, 6.283f);
            var hot = Rand();
            SpawnFun(PartKind.Glow, at, new V2(MathF.Cos(a), MathF.Sin(a)) * Rand(speed * 0.3f, speed), Rand(0.15f, 0.4f), Rand(1.4f, 2.6f), 1, 0.75f + hot * 0.25f, 0.3f + hot * 0.5f, 1);
        }
    }

    void Dust(V2 at, float r, int n)
    {
        Event(2, at.X, at.Y, n);
        for (int i = 0; i < n; i++)
        {
            var a = Rand(0, 6.283f);
            SpawnFun(PartKind.Smoke, at + new V2(MathF.Cos(a), MathF.Sin(a)) * r, new V2(MathF.Cos(a), MathF.Sin(a)) * Rand(40, 120), Rand(0.5f, 0.9f), Rand(8, 13), 0.7f, 0.68f, 0.64f, 0.45f);
        }
    }

    void DuelDone()
    {
        if (Me == null) return;
        if (DuelWins > 0 && DuelWins >= DuelLosses) { ConfettiBurst(Pos, 120, 700, DockOut, 3.1f); Audio?.Popper(); }
    }
}
