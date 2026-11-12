using System;
using System.Collections.Generic;
using System.Linq;

namespace RoboDust;

public enum SqState { Enter, ToNut, Carry, Dig, Forget, Flee, Leave, Gone }

/// One member of the Eichhörnchen-Bande.
public sealed class Squirrel
{
    public V2 Pos, Spot, Exit;
    public float Heading = -MathF.PI / 2, Speed, AnimT, StateT;
    public SqState State;
    public Item Target, Held;
    public int Buried;
    public int Frame;    // SquirrelArt frame
    public bool Moving;
}

/// Fun mode: the Eichhörnchen-Bande. They grab files like nuts, bury them somewhere, and forget
/// where. RoboDust digs them up again. A picture only: no file is touched.
public sealed partial class Sim
{
    public readonly List<Squirrel> Squirrels = new();
    bool HasSquirrels => Fun == FunMode.Squirrels;
    bool gangArrived;
    string squirrelNote; float squirrelNoteT;
    float chatterBudget;

    public string SquirrelNote => T < squirrelNoteT ? squirrelNote : null;
    void Say(string s, float seconds = 2.5f) { squirrelNote = s; squirrelNoteT = T + seconds; }

    void UpdateSquirrels(float dt)
    {
        chatterBudget = MathF.Min(2, chatterBudget + dt * 1.5f);
        if (!gangArrived && Phase == Phase.Cleaning)
        {
            gangArrived = true;
            for (int i = 0; i < 5; i++) Squirrels.Add(new Squirrel { Pos = EdgePoint(out var inside), Spot = inside, State = SqState.Enter });
            Say("Die Eichhörnchen-Bande ist da!", 3);
            Chatter();
        }
        // RoboDust's suction digs up anything buried nearby.
        if (SuctionOn)
            foreach (var it in Items)
                if (it.Buried && it.OnFloor && (it.Icon.Pos - Intake).Len < R * 2.2f) Unbury(it);
        var done = Phase >= Phase.Done;
        foreach (var s in Squirrels)
        {
            if (s.State == SqState.Gone) continue;
            s.StateT += dt;
            if (done && s.State != SqState.Leave) StartLeave(s);
            // Too close to the robot: drop everything and run.
            if (s.State is SqState.ToNut or SqState.Carry or SqState.Forget && (s.Pos - Pos).Len < R * 1.7f && !Picked)
            {
                Drop(s);
                s.State = SqState.Flee; s.StateT = 0;
                Chatter();
            }
            switch (s.State)
            {
                case SqState.Enter:
                    if (Run(s, s.Spot, 300, dt)) Next(s);
                    break;
                case SqState.ToNut:
                    if (s.Target == null || !Free(s.Target, s)) s.Target = FindNut(s);
                    if (s.Target == null) { StartLeave(s); break; }
                    if (Run(s, s.Target.Icon.Pos, 310, dt, 16)) Grab(s);
                    break;
                case SqState.Carry:
                    if (s.Held == null || !s.Held.OnFloor || s.Held.Icon.Ingesting) { s.Held = null; Next(s); break; }
                    if (Run(s, s.Spot, 260, dt)) { s.State = SqState.Dig; s.StateT = 0; }
                    break;
                case SqState.Dig:
                    s.Moving = false;
                    s.Frame = SquirrelArt.Dig0 + ((int)(s.StateT * 10) % SquirrelArt.DigFrames);
                    if (Rand() < dt * 30)
                    {
                        var back = -new V2(MathF.Cos(s.Heading), MathF.Sin(s.Heading));
                        var g = Rand(0.3f, 0.45f);
                        SpawnFun(PartKind.Ash, s.Pos + back * 10, back.Rotated(Rand(-0.8f, 0.8f)) * Rand(60, 160), Rand(5, 8), Rand(2.5f, 4.5f), g, g * 0.65f, g * 0.38f, 1, chip: 1.2f);
                    }
                    if (s.Held != null) s.Held.Icon.Pos = s.Spot;
                    if (s.StateT > 1.1f) Bury(s);
                    break;
                case SqState.Forget:
                    // Sits up, looks around. Where was it again?
                    s.Moving = false; s.Frame = SquirrelArt.Sit;
                    s.Heading += MathF.Sin(s.StateT * 9) * dt * 2;
                    if (s.StateT > 1.3f) Next(s);
                    break;
                case SqState.Flee:
                {
                    var away = (s.Pos - Pos).Unit;
                    if (away.Len < 0.5f) away = new V2(1, 0);
                    Run(s, s.Pos + away * 200, 440, dt);
                    if (s.StateT > 0.9f) Next(s);
                    break;
                }
                case SqState.Leave:
                    if (Run(s, s.Exit, 340, dt, 8, clamp: false)) s.State = SqState.Gone;
                    break;
            }
            s.AnimT += dt * (s.Moving ? s.Speed / 14 : 0);
            if (s.State is not (SqState.Dig or SqState.Forget)) s.Frame = s.Moving ? SquirrelArt.Run0 + ((int)s.AnimT % SquirrelArt.RunFrames) : SquirrelArt.Sit;
            if (s.Held != null && s.State == SqState.Carry)
            {
                // Carried in its mouth, in front.
                var fwd = new V2(MathF.Cos(s.Heading), MathF.Sin(s.Heading));
                s.Held.Icon.Pos = s.Pos + fwd * 30;
                s.Held.Icon.Ang = s.Heading + M.Pi / 2;
            }
        }
    }

    bool Free(Item it, Squirrel me) =>
        it.OnFloor && !it.Buried && !it.HeldBySquirrel && !it.Icon.Ingesting && !it.Burning && !it.Carried && !it.Grabbed
        && !Squirrels.Any(o => o != me && (o.Target == it || o.Held == it));

    Item FindNut(Squirrel s)
    {
        Item best = null; float bd = float.PositiveInfinity;
        foreach (var it in Items)
        {
            if (!Free(it, s) || (it.Icon.Pos - Pos).Len < R * 2.4f) continue;
            var d = (it.Icon.Pos - s.Pos).Len;
            if (d < bd) { bd = d; best = it; }
        }
        return best;
    }

    void Next(Squirrel s)
    {
        s.StateT = 0;
        if (s.Buried >= 3 || Phase >= Phase.Done) { StartLeave(s); return; }
        s.State = SqState.ToNut; s.Target = FindNut(s);
    }

    void Grab(Squirrel s)
    {
        var it = s.Target;
        if (it == null || !Free(it, s)) { s.Target = null; return; }
        it.HeldBySquirrel = true;
        it.Icon.Vel = V2.Zero;
        s.Held = it; s.Target = null;
        // Somewhere to bury it, well away from the robot and the dock.
        V2 spot = it.Icon.Pos;
        for (int tries = 0; tries < 20; tries++)
        {
            var room = Plan.Rooms[(int)(Rand() * Plan.Rooms.Count) % Plan.Rooms.Count].Inset(70);
            if (!room.Valid) continue;
            spot = new V2(room.X + Rand() * room.W, room.Y + Rand() * room.H);
            if ((spot - Pos).Len > R * 3.5f && (spot - DockPos).Len > R * 3 && (spot - it.Icon.Pos).Len < 900) break;
        }
        s.Spot = spot;
        s.State = SqState.Carry; s.StateT = 0;
        Audio?.Tick();
    }

    void Bury(Squirrel s)
    {
        var it = s.Held;
        s.Held = null;
        if (it != null)
        {
            it.HeldBySquirrel = false;
            it.Buried = true;
            it.Icon.Pos = s.Spot; it.Icon.Vel = V2.Zero; it.Icon.Ang = 0; it.Icon.Agit = 0;
            s.Buried++;
            Say(s.Buried == 1 ? "Ein Eichhörnchen hat eine Datei vergraben" : "Und gleich wieder vergessen, wo");
            Audio?.Thud(0.12f);
        }
        s.State = SqState.Forget; s.StateT = 0;
    }

    void Drop(Squirrel s)
    {
        if (s.Held == null) return;
        s.Held.HeldBySquirrel = false;
        s.Held.Icon.Vel = new V2(Rand(-60, 60), Rand(-60, 60));
        s.Held.Icon.Agit = 0.4f;
        s.Held = null;
    }

    void Unbury(Item it)
    {
        it.Buried = false;
        it.Icon.Agit = 0.6f;
        for (int k = 0; k < 16; k++)
        {
            var a = Rand(0, 6.283f); var g = Rand(0.3f, 0.45f);
            SpawnFun(PartKind.Ash, it.Icon.Pos, new V2(MathF.Cos(a), MathF.Sin(a)) * Rand(60, 200), Rand(5, 8), Rand(2.5f, 4.5f), g, g * 0.65f, g * 0.38f, 1, chip: 1.2f);
        }
        Say("RoboDust hat eine vergrabene Datei gefunden");
        Audio?.Tick();
    }

    void StartLeave(Squirrel s)
    {
        Drop(s);
        s.Target = null;
        s.State = SqState.Leave; s.StateT = 0;
        s.Exit = NearestExit(s.Pos);
    }

    /// Moves towards a point, scurrying in little hops. True once it has arrived.
    bool Run(Squirrel s, V2 to, float speed, float dt, float arrive = 10, bool clamp = true)
    {
        var d = to - s.Pos;
        if (d.Len < arrive) { s.Moving = false; s.Speed = 0; return true; }
        var want = MathF.Atan2(d.Y, d.X);
        s.Heading += M.Wrap(want - s.Heading) * MathF.Min(1, dt * 12);
        var hop = 0.75f + 0.25f * MathF.Sin(s.AnimT * MathF.PI * 2 / SquirrelArt.RunFrames);
        s.Speed = speed * hop;
        s.Pos += new V2(MathF.Cos(s.Heading), MathF.Sin(s.Heading)) * MathF.Min(s.Speed * dt, d.Len);
        if (clamp) s.Pos = Plan.Clamp(s.Pos, 20);
        s.Moving = true;
        return false;
    }

    /// A point just off the edge of a display, and one a little way in from it.
    V2 EdgePoint(out V2 inside)
    {
        var room = Plan.Rooms[(int)(Rand() * Plan.Rooms.Count) % Plan.Rooms.Count];
        var side = (int)(Rand() * 4) % 4;
        float x = room.X + Rand(0.15f, 0.85f) * room.W, y = room.Y + Rand(0.15f, 0.85f) * room.H;
        V2 edge = side switch
        {
            0 => new V2(room.X - 40, y), 1 => new V2(room.Right + 40, y), 2 => new V2(x, room.Y - 40), _ => new V2(x, room.Bottom + 40),
        };
        inside = Plan.Clamp(edge + (room.Center - edge).Unit * 220, 60);
        return edge;
    }

    V2 NearestExit(V2 p)
    {
        var room = Plan.Rooms[Plan.RoomOf(p)];
        var options = new[] { new V2(room.X - 60, p.Y), new V2(room.Right + 60, p.Y), new V2(p.X, room.Y - 60), new V2(p.X, room.Bottom + 60) };
        return options.OrderBy(o => (o - p).Len).First();
    }

    void Chatter()
    {
        if (chatterBudget < 1) return;
        chatterBudget -= 1;
        // Quick, scolding chirps.
        Audio?.Chirp((2600, 0.035f), (2300, 0.035f), (2700, 0.035f), (2200, 0.035f), (2800, 0.05f));
    }
}
