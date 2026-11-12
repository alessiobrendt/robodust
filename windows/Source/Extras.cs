using System;
using System.Collections.Generic;
using System.Linq;

namespace RoboDust;

/// A pet from the shop: trots along behind the robot and sits by the dock while it empties.
public sealed class Pet
{
    public PetKind Kind;
    public V2 Pos;
    public float Heading = -MathF.PI / 2, AnimT, BarkT = 5, Speed;
    public bool Moving;
    public int Frame;
}

/// A coin lying on the desktop. The robot vacuums it up for five coins in the shop.
public sealed class Coin
{
    public V2 Pos;
    public float Spin, Pull;
    public bool Taken, Gone;
}

public sealed partial class Sim
{
    public Look Look;
    public Pet Pet;
    public readonly List<Coin> Coins = new();
    public int CoinsPicked;
    bool coinsDropped;
    float exhaustAcc, trailAcc;

    /// What it plays when it bumps into something: a beep, or a horn from the shop.
    void Honk()
    {
        switch (Look.Horn)
        {
            case 1: Audio?.Chirp((523, 0.08f), (659, 0.08f), (784, 0.08f), (1047, 0.25f)); break;
            case 2: Audio?.Chirp((392, 0.09f), (392, 0.09f), (392, 0.09f), (523, 0.2f), (659, 0.28f)); break;
            case 3: Audio?.Chirp((311, 0.28f), (370, 0.4f)); break;
            default: Audio?.Chirp((494, 0.07f), (392, 0.09f)); break;
        }
    }

    /// What this run earns in the shop: one coin per file RoboDust itself took, five per coin picked up.
    public int CoinsEarned => Math.Max(0, OwnScore) + CoinsPicked * 5;

    void UpdateExtras(float dt)
    {
        UpdatePet(dt);
        UpdateCoins(dt);
        // Trails from the shop, left behind as it drives.
        if (Look.Trail != 0 && !Picked && MathF.Abs(V) > 40 && Phase is not (Phase.Dropping or Phase.Leaving or Phase.Finished))
        {
            trailAcc += dt * (Look.Trail == 3 ? 40 : 30);
            while (trailAcc >= 1)
            {
                trailAcc -= 1;
                var at = Pos - Fwd * (R * 0.9f) + Side * Rand(-R * 0.6f, R * 0.6f);
                switch (Look.Trail)
                {
                    case 1:   // rainbow: bands of colour, slowly fading where it went
                    {
                        var s = Rand(-1, 1);
                        var (r, g, b) = Hue(0.5f + s * 0.5f * 0.85f);
                        SpawnFun(PartKind.Glow, Pos - Fwd * (R * 0.9f) + Side * (s * R * 0.6f), V2.Zero, 1.6f, 7, r, g, b, 0.35f);
                        break;
                    }
                    case 2:   // smoke
                        SpawnFun(PartKind.Smoke, at, -Fwd * Rand(20, 60) + new V2(Rand(-15, 15), Rand(-15, 15)), Rand(1.2f, 2), Rand(9, 14), 0.85f, 0.85f, 0.87f, 0.35f);
                        break;
                    default:  // glitter
                    {
                        var (r, g, b) = Rand() < 0.5f ? (1f, 0.9f, 0.5f) : (0.85f, 0.95f, 1f);
                        SpawnFun(PartKind.Glow, at, new V2(Rand(-10, 10), Rand(-10, 10)), Rand(0.5f, 1.1f), Rand(1.2f, 2.4f), r, g, b, 1);
                        break;
                    }
                }
            }
        }
        // Exhaust flames: a few licks of fire out of the back whenever it puts its foot down.
        if (Look.Exhaust && !Picked && MathF.Abs(V) > 60)
        {
            exhaustAcc += dt * 70 * MathF.Min(1, MathF.Abs(V) / 300);
            while (exhaustAcc >= 1)
            {
                exhaustAcc -= 1;
                var at = Pos - Fwd * (R * 1.02f) + Side * (R * 0.28f * (Rand() < 0.5f ? -1 : 1));
                SpawnFun(PartKind.Fire, at, -Fwd.Rotated(Rand(-0.25f, 0.25f)) * Rand(150, 320) + Fwd * V * 0.5f, Rand(0.18f, 0.3f), Rand(3.5f, 6), 1, 0.85f, 0.4f, 1);
            }
        }
    }

    // MARK: Pet

    void UpdatePet(float dt)
    {
        if (Look.Pet == PetKind.None) { Pet = null; return; }
        if (Pet == null || Pet.Kind != Look.Pet)
            Pet = new Pet { Kind = Look.Pet, Pos = Plan.Clamp(DockPos + DockOut.Perp * (R * 2.6f) + DockOut * (R * 0.4f), 40) };
        var p = Pet;
        // At heel while the robot works; by the dock while it docks and empties.
        var home = Phase is Phase.Dropping or Phase.Docking or Phase.Emptying or Phase.Done or Phase.Tidying or Phase.Tidied or Phase.PuttingBack or Phase.Leaving or Phase.Finished;
        var target = home ? DockPos + DockOut.Perp * (R * 2.6f) + DockOut * (R * 0.4f) : Pos - Fwd * (R * 1.9f) + Side * (R * 0.85f);
        target = Plan.Clamp(target, 40);
        var d = target - p.Pos;
        if (d.Len > 26)
        {
            var want = MathF.Atan2(d.Y, d.X);
            p.Heading += M.Wrap(want - p.Heading) * MathF.Min(1, dt * 10);
            p.Speed = M.Clamp(d.Len * 2.6f, 90, 440);
            p.Pos += new V2(MathF.Cos(p.Heading), MathF.Sin(p.Heading)) * MathF.Min(p.Speed * dt, d.Len);
            p.Moving = true;
        }
        else
        {
            // Sits and watches the robot.
            var look = Pos - p.Pos;
            p.Heading += M.Wrap(MathF.Atan2(look.Y, look.X) - p.Heading) * MathF.Min(1, dt * 4);
            p.Moving = false; p.Speed = 0;
        }
        // Never under the robot or inside the dock.
        var e = p.Pos - Pos;
        if (e.Len < R + 34 && e.Len > 0.01f) p.Pos = Pos + e.Unit * (R + 34);
        p.Pos = Plan.Clamp(p.Pos, 30);
        p.AnimT += dt * (p.Moving ? p.Speed / 16 : 0);
        p.Frame = p.Moving ? (int)p.AnimT % 4 : 4;
        // Now and then it has something to say.
        p.BarkT -= dt;
        if (p.BarkT <= 0 && Working)
        {
            p.BarkT = Rand(7, 15);
            if (p.Kind == PetKind.Cat) Audio?.PetMeow();
            else if (p.Kind == PetKind.Squirrel) Audio?.Chirp((2600, 0.03f), (2300, 0.03f), (2700, 0.05f));
            else Audio?.Bark();
        }
    }

    // MARK: Coins

    void UpdateCoins(float dt)
    {
        if (!coinsDropped && Phase == Phase.Cleaning)
        {
            coinsDropped = true;
            var n = Math.Clamp(5 + Items.Count / 6, 5, 20);
            for (int i = 0, tries = 0; i < n && tries < 400; tries++)
            {
                var room = Plan.Rooms[(int)(Rand() * Plan.Rooms.Count) % Plan.Rooms.Count].Inset(80);
                if (!room.Valid) continue;
                var p = new V2(room.X + Rand() * room.W, room.Y + Rand() * room.H);
                if ((p - DockPos).Len < R * 3 || Items.Any(it => it.OnFloor && (it.Icon.Pos - p).Len < 60) || Coins.Any(c => (c.Pos - p).Len < 80)) continue;
                Coins.Add(new Coin { Pos = p, Spin = Rand(0, 4) });
                i++;
            }
            if (Plan.Rooms.Count > 0) PlanLanes();   // so the lanes pass over the coins too
        }
        foreach (var c in Coins)
        {
            if (c.Gone) continue;
            c.Spin += dt * 6;
            if (!c.Taken && SuctionOn && !Picked && (c.Pos - Intake).Len < R * 1.1f) c.Taken = true;
            if (!c.Taken) continue;
            c.Pull = MathF.Min(1, c.Pull + dt / 0.25f);
            c.Pos += (Intake - c.Pos) * MathF.Min(1, dt * 14);
            if (c.Pull < 1) continue;
            c.Gone = true;
            CoinsPicked++;
            Audio?.Chirp((1568, 0.05f), (2093, 0.14f));
            for (int k = 0; k < 10; k++)
            {
                var a = Rand(0, 6.283f);
                SpawnFun(PartKind.Glow, c.Pos, new V2(MathF.Cos(a), MathF.Sin(a)) * Rand(60, 180), Rand(0.2f, 0.45f), Rand(1.5f, 2.5f), 1, 0.85f, 0.3f, 1);
            }
        }
    }

    IEnumerable<V2> CoinSpots(int room) => Coins.Where(c => !c.Taken && Plan.RoomOf(c.Pos) == room).Select(c => c.Pos);
}
