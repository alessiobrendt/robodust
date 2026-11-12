using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RoboDust;

/// The cat's own sounds. Purring, hissing and growling are synthesised; the meows are a real cat.
public interface ICatAudio
{
    void SetPurr(float level);
    void Hiss(float seconds);
    void Growl(float seconds);
    void Meow(int which, float rate, float amp);
    void Swat();
}

/// The cat in a clean. It's only for show: it watches the robot and gets in its way, and the most it
/// can do is make the robot steer around it. Nothing here touches which files get collected.
public sealed class CatActor
{
    public enum Mood { Supervisor = 0, Loaf = 1, Rider = 2, Swatter = 3, Saboteur = 4, Coward = 5 }
    enum Next { Sit, Loaf, Crouch, Mount, Scheme, Hide, Peek, Vanish }
    enum Rest { Sit, Loaf, Crouch }
    enum St { Waiting, Walking, Sitting, Loafing, Crouching, Climbing, Riding, Hopping, Swatting, Scheming, Startled, Cowering, Peeking, Gone }
    enum AfterHop { Watch, Home, Loaf, Fight }

    public static bool Trace;
    const float FloorSize = 0.85f, RiderSize = 0.8f;
    const int MaxPushes = 3;

    public readonly Mood Personality;
    public readonly CatCoat Coat;
    public ICatAudio Audio;

    /// Where the user's windows are, as rectangles in world points. Only their outlines are read,
    /// never what's in them. The Coward hides under them.
    public Func<List<RectF>> Windows = () => new List<RectF>();

    St state = St.Waiting;
    float stateT;
    // What the current state is about: where it's walking, where it climbed from, where it's hopping.
    V2 walkTo; float walkSpeed; Next walkThen;
    V2 climbFrom; float climbFromHeading;
    V2 hopFrom, hopTo;

    public V2 Pos { get; private set; } = new(-1000, -1000);
    float heading;
    Rest rest = Rest.Sit;
    float walkK, cycle;
    float look, lookTarget;
    float size = FloorSize, hop;
    float reach, reach2, puff, fightSide = 1, wiggle, earsBack, earFlick;
    float nextFlick = 4;
    float eyeOpenUntil;
    int bumps; float noteUntil;
    bool spoke, growled, disturbed, hasFought; int hissedStare;
    /// The Swatter has had its fight and run off.
    public bool HasFought => hasFought;

    // The Saboteur.
    Item target;
    V2 pushDir; int pushes; float pushT = -1; int planRow;
    float yawLag;

    // Being carried.
    public bool Picked { get; private set; }
    float pickK;
    (V2 spot, Next then)? home;
    AfterHop afterHop = AfterHop.Watch;
    V2 seatOffset;

    static readonly Random rnd = new();
    static float Rf(float a, float b) { lock (rnd) return a + (b - a) * (float)rnd.NextDouble(); }
    static bool Coin() { lock (rnd) return rnd.Next(2) == 0; }

    /// Every cat sounds a little different, even though it's always the same recording.
    readonly float voice = Rf(0.94f, 1.08f);

    void Say(int which, float rate, float amp) => Audio?.Meow(which, rate * voice, amp);

    public CatActor(Mood mood, CatCoat coat) { Personality = mood; Coat = coat; }

    /// One cat per clean, with a random mood and coat. Dark coats disappear on the robot's black top,
    /// so a Rider is always a light one.
    public static CatActor Random()
    {
        var moods = Enum.GetValues<Mood>();
        int i; lock (rnd) i = rnd.Next(moods.Length);
        return PickMood(moods[i]);
    }
    public static CatActor PickMood(Mood m)
    {
        var coats = m == Mood.Rider ? new[] { CatCoat.Ginger, CatCoat.Silver } : new[] { CatCoat.Ginger, CatCoat.Tuxedo, CatCoat.Charcoal, CatCoat.Silver };
        int i; lock (rnd) i = rnd.Next(coats.Length);
        return new CatActor(m, coats[i]);
    }

    /// On board it purrs. A Loaf on the floor purrs quietly, until it's been walked into or picked up.
    public float PurrLevel
    {
        get
        {
            if (Picked) return 0;
            return state switch
            {
                St.Riding => 1,
                St.Loafing => bumps == 0 && !disturbed ? 0.3f : 0,
                _ => 0,
            };
        }
    }

    /// A cat on board is real weight: the motor labours under it, and stops the moment it's off.
    public float LoadOnRobot => !Picked && state == St.Riding ? 0.32f : 0;

    void Set(St s, Sim sim)
    {
        state = s; stateT = 0;
        if (Trace) Debug.WriteLine($"{sim.T,6:0.0}s  cat: {s}");
    }
    void SetWalk(V2 to, float speed, Next then, Sim sim)
    {
        walkTo = to; walkSpeed = speed; walkThen = then;
        Set(St.Walking, sim);
    }
    void SetHop(V2 from, V2 to, Sim sim) { hopFrom = from; hopTo = to; Set(St.Hopping, sim); }

    public bool IsGone => state is St.Gone or St.Waiting;
    /// Riding, climbing and hopping are drawn over the robot. So is a cat being carried.
    public bool Above => Picked || pickK > 0.05f || state is St.Climbing or St.Riding or St.Hopping;

    static bool IsOver(Phase p) => p is Phase.Done or Phase.Tidying or Phase.Tidied or Phase.PuttingBack or Phase.Leaving or Phase.Finished;

    // MARK: - Update

    /// Once per sim step, before the robot moves. Also sets the purr.
    public void Update(Sim s, float dt)
    {
        Step(s, dt);
        Audio?.SetPurr(PurrLevel);
    }

    void Step(Sim s, float dt)
    {
        stateT += dt;
        pickK += ((Picked ? 1 : 0) - pickK) * MathF.Min(1, dt * 11);
        earFlick *= MathF.Exp(-dt * 7);
        if (s.T > nextFlick)
        {
            earFlick = 0.6f;
            nextFlick = s.T + Rf(3, 8);
        }
        if (Picked) { EaseWalk(0, dt); return; }

        if (IsOver(s.Phase))
        {
            switch (state)
            {
                case St.Waiting: Set(St.Gone, s); break;
                // Out of sight, it just stays there.
                case St.Cowering: Set(St.Gone, s); break;
                case St.Gone: case St.Hopping: break;
                case St.Walking when walkThen == Next.Vanish: break;
                case St.Riding: case St.Climbing: HopOff(s); break;
                default: Leave(s, 190); break;
            }
        }

        switch (state)
        {
            case St.Waiting: WaitToEnter(s); break;
            case St.Gone: break;
            case St.Walking: Walk(s, dt); break;
            case St.Sitting: Sit(s, dt); break;
            case St.Loafing: Loaf(s, dt); break;
            case St.Crouching: Crouch(s, dt); break;
            case St.Climbing: Climb(s, dt); break;
            case St.Riding: Ride(s, dt); break;
            case St.Hopping: HopTo(s, dt); break;
            case St.Swatting: Swat(s, dt); break;
            case St.Scheming: Scheme(s, dt); break;
            case St.Startled: Startle(s, dt); break;
            case St.Cowering: Cower(s, dt); break;
            case St.Peeking: Peek(s, dt); break;
        }
    }

    void WaitToEnter(Sim s)
    {
        switch (Personality)
        {
            case Mood.Supervisor or Mood.Coward:
                if (s.Phase != Phase.Scanning) return;
                Enter(s, OpenSpot(s), 210, Next.Sit);
                break;
            case Mood.Rider:
                if (s.Phase != Phase.Undocking) return;
                Enter(s, s.Pos, 230, Next.Mount);
                break;
            case Mood.Loaf or Mood.Swatter:
            {
                if (s.Phase != Phase.Cleaning) return;
                var then = Personality == Mood.Loaf ? Next.Loaf : Next.Crouch;
                Enter(s, LaneSpot(s) ?? OpenSpot(s), 250, then);
                break;
            }
            case Mood.Saboteur:
                if (s.Phase != Phase.Cleaning) return;
                if (SabotagePlan(s) is { } plan)
                {
                    target = plan.item; pushDir = plan.dir; planRow = plan.row;
                    Enter(s, plan.spot, 320, Next.Scheme);
                }
                else Enter(s, OpenSpot(s), 210, Next.Sit);
                break;
        }
    }

    void Enter(Sim s, V2 to, float speed, Next then)
    {
        var start = OffEdge(s, to, 150 * size + 60);
        // Not straight out of the dock's tower.
        if (InsideKeepOut(s, new V2(start.X, MathF.Min(start.Y, s.WallMax.Y))))
            start.X = s.DockPos.X + s.R * 5 * (s.DockPos.X < (s.WallMin.X + s.WallMax.X) / 2 ? 1 : -1);
        Pos = start;
        heading = MathF.Atan2(to.Y - Pos.Y, to.X - Pos.X);
        walkK = 1;
        SetWalk(to, speed, then, s);
    }

    /// Just past the nearest edge of p's display that leads off the screen, not onto another display.
    static V2 OffEdge(Sim s, V2 p, float off)
    {
        var room = s.Plan.Rooms[s.Plan.RoomOf(p)];
        var edges = new List<(float d, V2 at, V2 peek)>
        {
            (p.X - room.X, new V2(room.X - off, p.Y), new V2(room.X - 30, p.Y)),
            (room.Right - p.X, new V2(room.Right + off, p.Y), new V2(room.Right + 30, p.Y)),
            (p.Y - room.Y, new V2(p.X, room.Y - off), new V2(p.X, room.Y - 30)),
            (room.Bottom - p.Y, new V2(p.X, room.Bottom + off), new V2(p.X, room.Bottom + 30)),
        };
        var outer = edges.Where(e => !s.Plan.Rooms.Any(r => r.Contains(e.peek))).ToList();
        if (outer.Count == 0) outer = edges;
        return outer.OrderBy(e => e.d).First().at;
    }

    void Walk(Sim s, float dt)
    {
        var to = walkTo; var speed = walkSpeed; var then = walkThen;
        switch (then)
        {
            case Next.Mount:
            {
                // The robot has moved on before the cat got there, so it just watches instead.
                if (s.Phase is not (Phase.Undocking or Phase.Scanning)) { rest = Rest.Sit; Set(St.Sitting, s); return; }
                var away = (Pos - s.Pos).Unit;
                to = s.Pos + away * (s.R + 76);
                break;
            }
            case Next.Scheme:
                // The file went before the cat got there, or the robot is about to: on to the next one.
                if (target is { } t && (!t.OnFloor || t.Icon.Ingesting || t.Grabbed || t.Burning || (t.Icon.Pos - s.Pos).Len < s.R + 160)) { PlotNext(s); return; }
                break;
            case Next.Loaf: case Next.Crouch:
                // The robot got there first, so pick a spot further along its route.
                if ((to - s.Pos).Len < s.R + 150 && s.Phase == Phase.Cleaning && LaneSpot(s, s.PathI + 1) is { } next)
                {
                    SetWalk(next, speed, then, s); return;
                }
                break;
        }
        var d = to - Pos;
        // Waits for the robot to stop before it tries to get on.
        if (then == Next.Mount && d.Len < 8 && s.Phase != Phase.Scanning) { EaseWalk(0, dt); return; }
        if (d.Len < (then == Next.Vanish ? 14 : 8))
        {
            Arrive(then, s);
            return;
        }
        StepToward(to, speed, then == Next.Vanish || speed > 400 ? 8 : 3.6f, dt);
        lookTarget = 0;
        look += (lookTarget - look) * (1 - MathF.Exp(-dt * 6));
        KeepClear(s);
    }

    void Arrive(Next then, Sim s)
    {
        switch (then)
        {
            case Next.Sit: rest = Rest.Sit; Set(St.Sitting, s); break;
            case Next.Loaf: rest = Rest.Loaf; Set(St.Loafing, s); break;
            case Next.Crouch: rest = Rest.Crouch; Set(St.Crouching, s); break;
            case Next.Mount: climbFrom = Pos; climbFromHeading = heading; Set(St.Climbing, s); break;
            case Next.Scheme: rest = Rest.Crouch; Set(St.Scheming, s); break;
            case Next.Hide: rest = Rest.Sit; Set(St.Cowering, s); break;
            case Next.Peek: rest = Rest.Crouch; Set(St.Peeking, s); break;
            case Next.Vanish: Set(St.Gone, s); break;
        }
    }

    void StepToward(V2 to, float speed, float turn, float dt)
    {
        var d = to - Pos;
        var err = M.Wrap(MathF.Atan2(d.Y, d.X) - heading);
        heading = M.Wrap(heading + M.Clamp(err * 5, -turn, turn) * dt);
        var align = M.Clamp(1 - MathF.Abs(err) / 1.7f, 0.1f, 1);
        EaseWalk(1, dt);
        var sp = speed * align * walkK;
        Pos += new V2(MathF.Cos(heading), MathF.Sin(heading)) * (sp * dt);
        // Feet stay planted: the stride rate comes from the real speed. Turning on the spot still steps.
        cycle += MathF.Max(CatPose.StridesPerSecond(sp / size), MathF.Abs(err) > 0.3f ? 0.9f : 0) * dt;
    }

    void EaseWalk(float to, float dt) => walkK += (to - walkK) * (1 - MathF.Exp(-dt * 8));

    /// A cat on the floor never ends up under the robot.
    void KeepClear(Sim s)
    {
        var d = Pos - s.Pos; var l = d.Len; var clear = s.R + 62;
        if (l < clear) Pos = s.Pos + d.Unit * clear;
        // Around the dock, not over it.
        if (s.DockAlpha > 0.5f && InsideKeepOut(s, Pos) && Pos.Y < s.WallMax.Y)
        {
            var o = Pos - s.DockPos;
            float u = o.Dot(s.DockOut), side = o.Dot(s.DockOut.Perp);
            float toFront = s.R * 2 - u, toSide = s.R * 2.3f - MathF.Abs(side);
            if (toFront < toSide) Pos += s.DockOut * toFront; else Pos += s.DockOut.Perp * (side < 0 ? -toSide : toSide);
        }
    }

    void Leave(Sim s, float speed) => SetWalk(OffEdge(s, Pos, 150 * size + 80), speed, Next.Vanish, s);

    /// Bolts away from the robot, then sits at a safe distance to keep an eye on it.
    void Flee(Sim s)
    {
        hasFought = true;
        var spot = ClampSpot(Pos + (Pos - s.Pos).Unit * 520, s);
        if (InsideKeepOut(s, spot) || (spot - s.Pos).Len < s.R + 260) spot = OpenSpot(s);
        SetWalk(spot, 600, Next.Sit, s);
    }

    /// Holds still, then turns to catch up, like a real watcher.
    void Watch(Sim s, float dt, float limit = 1.15f)
    {
        var wanted = M.Wrap(MathF.Atan2(s.Pos.Y - Pos.Y, s.Pos.X - Pos.X) - heading);
        var held = M.Clamp(wanted, -limit, limit);
        if (MathF.Abs(held - lookTarget) > 0.3f) lookTarget = held;
        look += (lookTarget - look) * (1 - MathF.Exp(-dt * 5.5f));
        // Too far round to follow with its head: shuffles the whole body.
        if (MathF.Abs(wanted) > limit + 0.35f)
        {
            heading = M.Wrap(heading + (wanted > 0 ? 1 : -1) * 1.1f * dt);
            cycle += 0.9f * dt;
            EaseWalk(0.3f, dt);
        }
        else EaseWalk(0, dt);
    }

    void Sit(Sim s, float dt)
    {
        Watch(s, dt);
        earsBack *= MathF.Exp(-dt * 0.5f);
        if (!spoke && stateT > 1.6f)
        {
            spoke = true;
            Say(0, 1, 0.5f);
        }
        KeepClear(s);
        // The Coward doesn't wait to see what the robot does, and the dock emptying is far too loud.
        if (Personality == Mood.Coward && ((Pos - s.Pos).Len < s.R + 520 || (s.Phase == Phase.Emptying && s.PhaseT > 0.7f))) { StartleNow(s); return; }
        // The robot's coming this way: gets up and moves, unhurried, but not slowly.
        var toCat = Pos - s.Pos;
        if (toCat.Len < s.R + 215 && s.V > 40 && toCat.Unit.Dot(s.Fwd) > 0.25f)
        {
            var sideways = s.Fwd.Perp * (toCat.Dot(s.Fwd.Perp) >= 0 ? 340 : -340);
            var spot = ClampSpot(Pos + sideways, s);
            if ((spot - s.Pos).Len < s.R + 200 || InsideKeepOut(s, spot)) spot = ClampSpot(Pos - sideways, s);
            // Nowhere better to go, up against a wall: it stays put, and the robot steers around it.
            if ((spot - Pos).Len > 80) SetWalk(spot, 300, Next.Sit, s);
        }
    }

    void Loaf(Sim s, float dt)
    {
        EaseWalk(0, dt);
        KeepClear(s);
        look += (0 - look) * (1 - MathF.Exp(-dt * 4));
        // Won't move for a robot. But once it's only chasing the last few files, the cat has had enough.
        if (s.Phase == Phase.Hunting) Leave(s, 170);
    }

    void Crouch(Sim s, float dt)
    {
        var toRobot = s.Pos - Pos; var l = toRobot.Len;
        if (s.Phase is not (Phase.Cleaning or Phase.Hunting or Phase.Returning)) { Leave(s, 220); return; }
        // Always facing its target, ears going flat as it gets close.
        var err = M.Wrap(MathF.Atan2(toRobot.Y, toRobot.X) - heading);
        heading = M.Wrap(heading + M.Clamp(err * 6, -4.5f, 4.5f) * dt);
        look += (M.Clamp(err, -0.5f, 0.5f) - look) * (1 - MathF.Exp(-dt * 8));
        var close = 1 - M.Smooth(s.R + 170, s.R + 400, l);
        earsBack += (close - earsBack) * (1 - MathF.Exp(-dt * 7));
        wiggle += ((close > 0.3f ? 1 : 0) - wiggle) * (1 - MathF.Exp(-dt * 6));
        if (!growled && close > 0.25f) { growled = true; Audio?.Growl(1.3f); }
        // Waited long enough: creeps closer, low to the ground.
        if (stateT > 3 && l > s.R + 100 && MathF.Abs(err) < 0.8f)
        {
            const float sp = 130;
            Pos += new V2(MathF.Cos(heading), MathF.Sin(heading)) * (sp * dt);
            cycle += CatPose.StridesPerSecond(sp / size) * dt;
            EaseWalk(0.3f, dt);
        }
        else { EaseWalk(0, dt); if (MathF.Abs(err) > 0.5f) cycle += 0.9f * dt; }
        // Gave up waiting altogether.
        if (stateT > 26) Leave(s, 300);
    }

    /// The fight. Side-on, it hits the robot in flurries, backs off with its tail up to stare it
    /// down, and goes back in. The robot just stops and takes it, until it twitches and the cat runs.
    void Swat(Sim s, float dt)
    {
        var t = stateT;
        var toCat = (Pos - s.Pos).Unit;
        float near = s.R + 84, far = s.R + 142;
        const float each = 0.14f;
        (float from, float hits) first = (0, 4), second = (1.5f, 5);
        float backAt1 = first.hits * each, staredAt1 = backAt1 + 0.85f;
        float backAt2 = second.from + second.hits * each, end = backAt2 + 0.85f;
        float dist = near, stare = 0;
        reach = 0; reach2 = 0;
        void Flurry(float from, float hits)
        {
            var x = (t - from) / each;
            if (x < 0 || x >= hits) return;
            var i = MathF.Floor(x); var k = MathF.Pow(MathF.Sin((x - i) * M.Pi), 2);
            if ((int)i % 2 == 0) reach = k; else reach2 = k;
            if (x - i >= 0.5f && x - i - dt / each < 0.5f) LandHit(s, toCat);
        }
        Flurry(first.from, first.hits);
        Flurry(second.from, second.hits);
        if (t >= backAt1 && t < second.from)
        {
            dist = near + (far - near) * (M.Smooth(0, 0.22f, t - backAt1) - M.Smooth(0, 0.14f, t - staredAt1));
            stare = M.Smooth(0, 0.2f, t - backAt1) * (1 - M.Smooth(0, 0.14f, t - staredAt1));
        }
        else if (t >= backAt2)
        {
            dist = near + (far - near) * M.Smooth(0, 0.22f, t - backAt2);
            stare = M.Smooth(0, 0.2f, t - backAt2);
        }
        // Both times it backs off, it hisses.
        if (hissedStare == 0 && t >= backAt1) { hissedStare = 1; Audio?.Hiss(0.8f); }
        if (hissedStare == 1 && t >= backAt2) { hissedStare = 2; Audio?.Hiss(0.75f); }
        hop = 0.22f * (Bump(t - backAt1) + Bump(t - backAt2) + Bump(t - staredAt1));
        // The robot twitches, and the cat has had enough.
        if (t >= end - 0.2f && t - dt < end - 0.2f) { s.Audio?.Chirp((392, 0.06f), (494, 0.08f)); s.Shake = MathF.Max(s.Shake, 0.5f); }
        Pos = s.Pos + toCat * dist;
        var side = 0.2f + 0.4f * stare;
        var toRobot = MathF.Atan2(-toCat.Y, -toCat.X);
        var want = M.Wrap(toRobot + (fightSide > 0 ? side : -side));
        heading = M.Wrap(heading + M.Clamp(M.Wrap(want - heading) * 9, -7, 7) * dt);
        // Body turned away, but its head never leaves the robot.
        look = M.Wrap(toRobot - heading);
        puff += (stare - puff) * (1 - MathF.Exp(-dt * 12));
        earsBack = 1;
        wiggle = 0;
        EaseWalk(0, dt);
        if (t > end)
        {
            hop = 0; puff = 0; reach = 0; reach2 = 0;
            s.CatManeuver(0.5f, 0.5f, fightSide > 0 ? 1 : -1);
            Say(3, 1.15f, 0.7f);
            Flee(s);
        }
    }

    static float Bump(float x) => x > 0 && x < 0.22f ? MathF.Sin(x / 0.22f * M.Pi) : 0;

    void LandHit(Sim s, V2 toCat)
    {
        s.Bumper = 1;
        s.Shake = MathF.Max(s.Shake, 0.55f);
        Audio?.Swat();
        s.Audio?.Chirp((740 + Rf(0, 180), 0.04f));
        // Knocks the robot about a bit, and loses some fur doing it.
        s.Pos -= toCat * 2.6f;
        s.Heading = M.Wrap(s.Heading + Rf(-0.035f, 0.035f));
        var at = s.Pos + toCat * (s.R + 6);
        var c = Coat.Base;
        for (int n = 0; n < 5; n++)
        {
            var v = toCat.Rotated(Rf(-1.1f, 1.1f)) * Rf(60, 210);
            Spawn(s, at, v, Rf(0.35f, 0.8f), Rf(2, 4.2f), c.X, c.Y, c.Z, 0.9f);
        }
    }

    /// Same as the sim's own particles: a puff of fur is just dust in the coat's colour.
    static void Spawn(Sim s, V2 p, V2 vel, float life, float sz, float r, float g, float b, float a)
    {
        if (s.Particles.Count > 2300) s.Particles.RemoveRange(0, 100);
        s.Particles.Add(new Particle { Pos = p, Vel = vel, Life = life, MaxLife = life, Size = sz, R = r, G = g, B = b, A = a, Chip = 0, Ang = Rf(0, 6.28f) });
    }

    void Climb(Sim s, float dt)
    {
        const float T = 1.5f;
        // Looks down first, tests it with a paw, then carefully gets on.
        var k = M.Clamp(stateT / T, 0, 1);
        var move = M.Smooth(0.28f, 1, k);
        var seat = s.Pos - s.Fwd * 8;
        Pos = climbFrom + (seat - climbFrom) * move;
        var toSeat = M.Wrap(MathF.Atan2(seat.Y - climbFrom.Y, seat.X - climbFrom.X) - climbFromHeading);
        heading = M.Wrap(climbFromHeading + toSeat * M.Smooth(0, 0.3f, k));
        look += ((k < 0.3f ? 0.25f : 0) - look) * (1 - MathF.Exp(-dt * 6));
        rest = Rest.Sit;
        walkK = 1 - M.Smooth(0.62f, 1, k);
        if (k > 0.28f && k < 0.95f) cycle += 1.1f * dt;
        size = FloorSize + (RiderSize - FloorSize) * move;
        hop = 0.3f * move;
        if (k >= 1)
        {
            yawLag = heading;
            lookTarget = 0.8f;
            s.Audio?.Thud(0.08f);
            Say(0, 1, 0.45f);
            Set(St.Riding, s);
        }
    }

    void Ride(Sim s, float dt)
    {
        // Heading home: it's getting off before the dock.
        if (s.Phase is Phase.Returning or Phase.Docking) { HopOff(s); return; }
        EaseWalk(0, dt);
        seatOffset = seatOffset * MathF.Exp(-dt * 7);
        size += (RiderSize - size) * (1 - MathF.Exp(-dt * 7));
        hop += (0.3f - hop) * (1 - MathF.Exp(-dt * 7));
        Pos = s.Pos - s.Fwd * 8 + seatOffset;
        // Leans late into each turn, looking away from wherever it's going.
        var err = M.Wrap(s.Heading - yawLag);
        yawLag = M.Wrap(yawLag + err * (1 - MathF.Exp(-dt * 2.2f)));
        heading = yawLag;
        if (rest == Rest.Loaf) lookTarget = 0;
        else if (stateT % 5 < dt) lookTarget = Rf(0.5f, 1) * (Coin() ? 1 : -1);
        look += (lookTarget - look) * (1 - MathF.Exp(-dt * 2));
    }

    void HopOff(Sim s)
    {
        var side = s.Fwd.Perp;
        var to = s.Pos + side * (s.R + 95);
        var other = s.Pos - side * (s.R + 95);
        if (InsideKeepOut(s, to) || !Same(to, ClampSpot(to, s))) to = other;
        SetHop(Pos, ClampSpot(to, s), s);
    }

    void HopTo(Sim s, float dt)
    {
        const float T = 0.42f; var k = M.Clamp(stateT / T, 0, 1);
        Pos = hopFrom + (hopTo - hopFrom) * M.Smooth(0, 1, k);
        hop = 0.3f * (1 - k) + MathF.Sin(k * M.Pi) * 0.55f;
        size = RiderSize + (FloorSize - RiderSize) * k;
        var want = MathF.Atan2(hopTo.Y - hopFrom.Y, hopTo.X - hopFrom.X);
        heading = M.Wrap(heading + M.Clamp(M.Wrap(want - heading) * 8, -6, 6) * dt);
        EaseWalk(0.6f, dt);
        if (k < 1) return;
        hop = 0;
        s.Audio?.Thud(0.14f);
        rest = Rest.Sit;
        KeepClear(s);
        if (IsOver(s.Phase)) { Leave(s, 190); return; }
        switch (afterHop)
        {
            case AfterHop.Watch: SetWalk(OpenSpot(s), 190, Next.Sit, s); break;
            case AfterHop.Loaf: SetWalk(OpenSpot(s), 190, Next.Loaf, s); break;
            case AfterHop.Home:
                if (home is { } h) SetWalk(h.spot, 200, h.then, s);
                else Arrive(Personality == Mood.Loaf ? Next.Loaf : Next.Sit, s);
                break;
            case AfterHop.Fight:
                rest = Rest.Crouch;
                fightSide = Coin() ? 1 : -1;
                noteUntil = s.T + 4.5f;
                hissedStare = 0;
                Set(St.Swatting, s);
                break;
        }
    }

    // MARK: - The Saboteur

    /// A file on the robot's route, far enough ahead for the cat to get there first. The cat crouches
    /// on the route side of it, so a swipe sends it sliding out of the robot's way.
    (Item item, V2 spot, V2 dir, int row)? SabotagePlan(Sim s)
    {
        var path = s.CatPath;
        if (pushes >= MaxPushes || path.Count <= 1) return null;
        var start = Math.Max(s.PathI, 0);
        if (start + 1 >= path.Count) return null;
        // How far the robot drives before it starts each stretch of its route.
        var routeTo = (path[start] - s.Pos).Len;
        for (int i = start; i < path.Count - 1; i++)
        {
            V2 a = path[i], b = path[i + 1], seg = b - a; var l = seg.Len;
            try
            {
                if (l <= s.R * 2) continue;
                var along = seg / l; var across = along.Perp;
                // Rows only: the robot turns at the ends, so a file there might never be passed.
                if (MathF.Abs(along.Y) >= 0.2f) continue;
                var candidates = s.Items.Where(it =>
                {
                    if (!it.OnFloor || it.Icon.Ingesting || it.Grabbed || it.Burning || it == target) return false;
                    // Not one the cat would give up on straight away: the next row can be close by.
                    if ((it.Icon.Pos - s.Pos).Len < s.R + 160) return false;
                    var d = it.Icon.Pos - a; var t = d.Dot(along);
                    if (!(t > l * 0.15f && t < l * 0.85f && MathF.Abs(d.Dot(across)) < s.R * 0.9f)) return false;
                    // The cat has to get there, and settle, well before the robot does.
                    float robotTime = (routeTo + t) / 330, catTime = DistanceToCat(it.Icon.Pos, s) / 320;
                    return robotTime > catTime + 1;
                }).OrderBy(it => (it.Icon.Pos - a).Dot(along)).ToList();
                foreach (var it in candidates)
                {
                    var sideOf = (it.Icon.Pos - a).Dot(across);
                    foreach (var sign in new[] { sideOf >= 0 ? 1f : -1f, sideOf >= 0 ? -1f : 1f })
                    {
                        var dir = across * sign;
                        V2 spot = it.Icon.Pos - dir * 80 * size, landing = it.Icon.Pos + dir * 240;
                        // Somewhere the file can actually slide to, with room for the cat beside it.
                        if (!Same(spot, ClampToScreen(spot, s)) || InsideKeepOut(s, spot)) continue;
                        if (!Same(landing, ClampToScreen(landing, s)) || InsideKeepOut(s, landing)) continue;
                        return (it, spot, dir, i);
                    }
                }
            }
            finally { routeTo += l; }
        }
        return null;
    }

    /// Before it's arrived, the cat is as far away as the nearest edge of the screen.
    float DistanceToCat(V2 p, Sim s)
    {
        if (state == St.Waiting)
        {
            var room = s.Plan.Rooms[s.Plan.RoomOf(p)];
            return MathF.Min(MathF.Min(p.X - room.X, room.Right - p.X), MathF.Min(p.Y - room.Y, room.Bottom - p.Y)) + 150 * size;
        }
        return (p - Pos).Len;
    }

    void PlotNext(Sim s)
    {
        pushT = -1; reach = 0;
        if (s.Phase == Phase.Cleaning && SabotagePlan(s) is { } plan)
        {
            target = plan.item; pushDir = plan.dir; planRow = plan.row;
            SetWalk(plan.spot, 320, Next.Scheme, s);
        }
        else
        {
            // Done, or nothing left to knock over: it sits back to admire its work.
            target = null;
            SetWalk(OpenSpot(s), 220, Next.Sit, s);
        }
    }

    /// Crouched by a file, eyes on the robot. When it gets close, one swipe sends the file sliding.
    void Scheme(Sim s, float dt)
    {
        var t = target;
        if (t == null || !t.OnFloor || t.Icon.Ingesting || t.Grabbed || t.Burning || s.Phase != Phase.Cleaning) { PlotNext(s); return; }
        var toFile = t.Icon.Pos - Pos;
        var want = MathF.Atan2(toFile.Y, toFile.X);
        heading = M.Wrap(heading + M.Clamp(M.Wrap(want - heading) * 6, -4, 4) * dt);
        var toRobot = M.Wrap(MathF.Atan2(s.Pos.Y - Pos.Y, s.Pos.X - Pos.X) - heading);
        EaseWalk(0, dt);
        if (pushT < 0)
        {
            // Watching the robot come, ready to swipe.
            look += (M.Clamp(toRobot, -0.9f, 0.9f) - look) * (1 - MathF.Exp(-dt * 6));
            earsBack += (0.5f - earsBack) * (1 - MathF.Exp(-dt * 4));
            wiggle += (0.35f - wiggle) * (1 - MathF.Exp(-dt * 4));
            // Swipes once the robot is driving down this row towards the file.
            var r = t.Icon.Pos - s.Pos;
            var onRow = s.PathI == planRow + 1;
            if (stateT > 0.3f && onRow && r.Len < s.R + 290 && r.Unit.Dot(s.Fwd) > 0.2f) pushT = 0;
            // The robot has gone past this row, or it's taking far too long: try another file.
            if (s.PathI > planRow + 1 || stateT > 40) PlotNext(s);
            return;
        }
        // The swipe, then a look straight at the robot, as if nothing happened.
        var before = pushT;
        pushT += dt;
        var k = M.Clamp(pushT / 0.3f, 0, 1);
        reach = MathF.Pow(MathF.Sin(k * M.Pi), 2);
        wiggle = 0;
        look += (0 - look) * (1 - MathF.Exp(-dt * 10));
        if (before < 0.15f && pushT >= 0.15f)
        {
            t.Icon.Vel += pushDir * 1350 + pushDir.Perp * Rf(-140, 140);
            t.Icon.AngV += Rf(-6, 6);
            t.Icon.Agit = MathF.Max(t.Icon.Agit, 0.35f);
            pushes++;
            noteUntil = s.T + 3;
            if (Trace) Debug.WriteLine($"{s.T,6:0.0}s  cat: pushed a file away ({pushes})");
            s.Audio?.Tick(); s.Audio?.Thud(0.06f);
        }
        if (pushT > 0.3f) { look += (M.Clamp(toRobot, -1.1f, 1.1f) - look) * (1 - MathF.Exp(-dt * 8)); earsBack *= MathF.Exp(-dt * 3); }
        if (pushT > 0.75f)
        {
            if (pushes == 1) Say(0, 1.1f, 0.4f);
            PlotNext(s);
        }
    }

    // MARK: - The Coward

    /// Somewhere to hide: under a window, or just off the edge of the screen. The cat sits at `Inside`,
    /// out of sight, and peeks from `Peek`, with only its head showing.
    sealed class Hideout { public RectF Cover; public bool UnderWindow; public V2 Inside, Peek, Outward; }
    Hideout hideout;
    float checkT;

    List<RectF> WindowList() { try { return Windows?.Invoke() ?? new List<RectF>(); } catch { return new List<RectF>(); } }

    static RectF Intersect(RectF a, RectF b)
    {
        float x0 = MathF.Max(a.X, b.X), y0 = MathF.Max(a.Y, b.Y), x1 = MathF.Min(a.Right, b.Right), y1 = MathF.Min(a.Bottom, b.Bottom);
        return new RectF(x0, y0, MathF.Max(0, x1 - x0), MathF.Max(0, y1 - y0));
    }

    Hideout FindHideout(Sim s)
    {
        var options = new List<(Hideout h, float score)>();
        float Score(Hideout h) => (h.Peek - Pos).Len - 0.4f * (h.Peek - s.Pos).Len;
        // Under a window, from the nearest edge of it that's on screen.
        foreach (var w in WindowList())
            foreach (var room in s.Plan.Rooms)
            {
                var r = Intersect(w, room);
                if (r.W < 220 || r.H < 160) continue;
                var c = new V2(M.Clamp(Pos.X, r.X, r.Right), M.Clamp(Pos.Y, r.Y, r.Bottom));
                var edges = new (float d, V2 o)[]
                {
                    (MathF.Abs(c.X - r.X), new V2(-1, 0)), (MathF.Abs(r.Right - c.X), new V2(1, 0)),
                    (MathF.Abs(c.Y - r.Y), new V2(0, -1)), (MathF.Abs(r.Bottom - c.Y), new V2(0, 1)),
                };
                var outw = edges.OrderBy(e => e.d).First().o;
                var edge = c;
                if (outw.X < 0) edge.X = r.X; else if (outw.X > 0) edge.X = r.Right;
                if (outw.Y < 0) edge.Y = r.Y; else if (outw.Y > 0) edge.Y = r.Bottom;
                if (outw.X != 0) edge.Y = M.Clamp(edge.Y, r.Y + 90, r.Bottom - 90); else edge.X = M.Clamp(edge.X, r.X + 90, r.Right - 90);
                var h = new Hideout { Cover = r, UnderWindow = true, Inside = edge - outw * 150 * size, Peek = edge - outw * 42 * size, Outward = outw };
                // A peek spot out in the open, not off the screen or in the dock.
                var lookAt = edge + outw * 40;
                if (!Same(lookAt, ClampToScreen(lookAt, s)) || InsideKeepOut(s, lookAt)) continue;
                options.Add((h, Score(h)));
            }
        // Off the edge of the screen. The taskbar counts as an edge too; an edge onto another display doesn't.
        AddEdges(s, options, Score, true);
        if (options.Count == 0) AddEdges(s, options, Score, false);
        if (options.Count == 0)
        {
            // Nowhere at all: just behind the robot's back, as far as it can get.
            var spot = OpenSpot(s);
            return new Hideout { Cover = new RectF(spot.X, spot.Y, 0, 0), Inside = spot, Peek = spot, Outward = (spot - s.Pos).Unit };
        }
        return options.OrderBy(o => o.score).First().h;
    }

    void AddEdges(Sim s, List<(Hideout h, float score)> options, Func<Hideout, float> score, bool outerOnly)
    {
        var room = s.Plan.Rooms[s.Plan.RoomOf(Pos)];
        var bands = new (RectF band, V2 inward)[]
        {
            (new RectF(room.X - 400, room.Y - 400, 400, room.H + 800), new V2(1, 0)),
            (new RectF(room.Right, room.Y - 400, 400, room.H + 800), new V2(-1, 0)),
            (new RectF(room.X - 400, room.Y - 400, room.W + 800, 400), new V2(0, 1)),
            (new RectF(room.X - 400, room.Bottom, room.W + 800, 400), new V2(0, -1)),
        };
        foreach (var (band, inward) in bands)
        {
            var edge = Pos;
            void Snap()
            {
                if (inward.X > 0) edge.X = room.X; else if (inward.X < 0) edge.X = room.Right;
                if (inward.Y > 0) edge.Y = room.Y; else if (inward.Y < 0) edge.Y = room.Bottom;
            }
            Snap();
            edge = new V2(M.Clamp(edge.X, room.X + 120, room.Right - 120), M.Clamp(edge.Y, room.Y + 120, room.Bottom - 120));
            Snap();
            if (InsideKeepOut(s, edge + inward * 60)) continue;
            if (outerOnly && s.Plan.Rooms.Any(r => r.Contains(edge - inward * 30))) continue;
            var outw = inward;
            var h = new Hideout { Cover = band, Inside = edge - outw * 170 * size, Peek = edge - outw * 42 * size, Outward = outw };
            options.Add((h, score(h) + 120));
        }
    }

    /// Puffed up, hissing, then gone.
    void StartleNow(Sim s)
    {
        rest = Rest.Crouch;
        noteUntil = s.T + 3;
        Audio?.Hiss(0.45f);
        Set(St.Startled, s);
    }

    void Startle(Sim s, float dt)
    {
        var away = Pos - s.Pos;
        var want = MathF.Atan2(away.Y, away.X);
        heading = M.Wrap(heading + M.Clamp(M.Wrap(want + M.Pi - heading) * 8, -7, 7) * dt);
        look = 0;
        puff += (1 - puff) * (1 - MathF.Exp(-dt * 14));
        earsBack = 1;
        hop = 0.18f * (stateT < 0.2f ? MathF.Sin(stateT / 0.2f * M.Pi) : 0);
        EaseWalk(0, dt);
        KeepClear(s);
        if (stateT > 0.6f)
        {
            hop = 0;
            var h = FindHideout(s);
            hideout = h;
            SetWalk(h.Inside, 560, Next.Hide, s);
        }
    }

    /// Out of sight. Every so often, once the robot is well away, it creeps out for a look.
    void Cower(Sim s, float dt)
    {
        if (hideout is not { } h) { StartleNow(s); return; }
        puff *= MathF.Exp(-dt * 1.5f);
        earsBack *= MathF.Exp(-dt * 0.4f);
        TurnTo(MathF.Atan2(h.Outward.Y, h.Outward.X), dt);
        EaseWalk(0, dt);
        if (!StillHidden(s, dt)) return;
        if (stateT > 4 && (h.Peek - s.Pos).Len > 700 && s.Phase is Phase.Cleaning or Phase.Hunting or Phase.Returning)
            SetWalk(h.Peek, 90, Next.Peek, s);
    }

    /// Just its head out, watching the robot. Back under the moment it comes this way.
    void Peek(Sim s, float dt)
    {
        if (hideout is not { } h) { StartleNow(s); return; }
        puff *= MathF.Exp(-dt * 2);
        TurnTo(MathF.Atan2(h.Outward.Y, h.Outward.X), dt);
        var toRobot = M.Wrap(MathF.Atan2(s.Pos.Y - Pos.Y, s.Pos.X - Pos.X) - heading);
        look += (M.Clamp(toRobot, -1, 1) - look) * (1 - MathF.Exp(-dt * 5));
        earsBack += (0.6f - earsBack) * (1 - MathF.Exp(-dt * 3));
        EaseWalk(0, dt);
        if (!StillHidden(s, dt)) return;
        var r = Pos - s.Pos;
        if (r.Len < s.R + 480 || stateT > 9)
            SetWalk(h.Inside, r.Len < s.R + 480 ? 480 : 140, Next.Hide, s);
    }

    void TurnTo(float want, float dt) => heading = M.Wrap(heading + M.Clamp(M.Wrap(want - heading) * 5, -4, 4) * dt);

    /// Windows move and close. If its hiding place has gone, it finds another, fast.
    bool StillHidden(Sim s, float dt)
    {
        checkT += dt;
        if (checkT <= 1.5f || hideout == null) return true;
        checkT = 0;
        if (!hideout.UnderWindow) return true;
        var p = hideout.Inside;
        if (WindowList().Any(w => w.Contains(p))) return true;
        StartleNow(s);
        return false;
    }

    // MARK: - Choosing spots

    static bool Same(V2 a, V2 b) => (a - b).Len < 0.01f;

    /// Same keep-out box the sim uses for its dock.
    static bool InsideKeepOut(Sim s, V2 p)
    {
        var d = p - s.DockPos;
        float u = d.Dot(s.DockOut), side = MathF.Abs(d.Dot(s.DockOut.Perp));
        return u < s.R * 2.0f && side < s.R * 2.3f;
    }

    static V2 ClampSpot(V2 p, Sim s) => s.Plan.Clamp(p, 170);

    /// Somewhere with a view, clear of the robot and its dock.
    V2 OpenSpot(Sim s)
    {
        var best = ClampSpot(s.Pos + new V2(0, -420), s); float bestScore = -1;
        var b = s.Plan.Bounds;
        for (int i = 0; i < 12; i++)
        {
            var p = ClampSpot(new V2(Rf(b.X, b.Right), Rf(b.Y, b.Bottom)), s);
            if (InsideKeepOut(s, p)) continue;
            var d = (p - s.Pos).Len;
            // Near enough to supervise, but not in the way.
            var score = d < s.R + 300 ? 0 : 1000 - MathF.Abs(d - 620);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    /// A point on the robot's route, ahead of it, that the cat has time to walk to.
    V2? LaneSpot(Sim s, int? start = null)
    {
        var path = s.CatPath;
        if (path.Count <= 1) return null;
        var from = start ?? Math.Max(s.PathI, (int)(path.Count * 0.3f));
        if (from < 0 || from + 1 >= path.Count) return null;
        for (int i = from; i < path.Count - 1; i++)
        {
            V2 a = path[i], b = path[i + 1];
            if ((b - a).Len <= s.R * 2.4f) continue;
            var p = a + (b - a) * Rf(0.35f, 0.65f);
            if (!Same(p, ClampSpot(p, s)) || InsideKeepOut(s, p) || (p - s.Pos).Len < s.R + 260) continue;
            return p;
        }
        return null;
    }

    // MARK: - Meeting the robot

    /// Asked each tick while the robot follows its path. True means the robot should bump and back off.
    public bool RobotContact(Sim s)
    {
        if (Picked) return false;
        var d = Pos - s.Pos;
        switch (state)
        {
            case St.Loafing:
                if (!(bumps < 2 && d.Len < s.R + 74 && d.Dot(s.Fwd) > 0)) return false;
                bumps++;
                if (Trace) Debug.WriteLine($"{s.T,6:0.0}s  cat: bumped {bumps}");
                noteUntil = s.T + 3;
                eyeOpenUntil = s.T + 1.6f;
                earFlick = 1;
                if (bumps == 1) Say(2, 0.9f, 0.6f);
                else Say(1, 1, 0.85f);
                return true;
            case St.Crouching:
                if (!(d.Len < s.R + 106 && d.Dot(s.Fwd) > 0)) return false;
                fightSide = Coin() ? 1 : -1;
                noteUntil = s.T + 4.5f;
                Audio?.Hiss(0.32f);
                Set(St.Swatting, s);
                return false;
            default: return false;
        }
    }

    /// Who the robot is steering around, once it knows better than to bump them.
    float? Avoid
    {
        get
        {
            if (Picked) return null;
            return state switch
            {
                St.Loafing => bumps >= 2 ? 92 + 80 : (float?)null,
                St.Sitting or St.Scheming => 92 + 72,
                _ => (float?)null,
            };
        }
    }

    /// Bends the robot's aim around the cat. It only changes the route, never what gets collected.
    public V2 Steer(V2 aim, Sim s)
    {
        if (Avoid is not { } r || s.Phase is not (Phase.Cleaning or Phase.Returning)) return aim;
        var d = s.Pos - Pos; var l = d.Len;
        if (l >= r + 120) return aim;
        V2 toAim = (aim - s.Pos).Unit, outw = d.Unit;
        if (toAim.Dot(outw) >= 0.2f) return aim;
        var tangent = outw.Perp;
        if (tangent.Dot(toAim) < 0) tangent = tangent * -1;
        var w = 1 - M.Clamp((l - r) / 120, 0, 1);
        var dir = toAim * (1 - w) + tangent * w + outw * (MathF.Max(0, r - l) / 30);
        return s.Pos + dir.Unit * 240;
    }

    /// A lane stop under a cat the robot has given up on is skipped. Whatever it missed gets picked up later.
    public bool Covers(V2 p, Sim s)
    {
        if (s.Phase != Phase.Cleaning || Avoid is not { } r) return false;
        return (p - Pos).Len < r;
    }

    /// While the Rider is getting on, the robot waits for it.
    public bool HoldsRobot(Sim s)
    {
        if (s.PhaseT >= 10) return false;
        return state == St.Climbing || (state == St.Walking && walkThen == Next.Mount);
    }

    /// The status line while the cat interferes, or null when there's nothing to say.
    public string Note(Sim s)
    {
        if (s.T >= noteUntil) return null;
        return Personality switch
        {
            Mood.Swatter => "Wird angegriffen!",
            Mood.Saboteur => "Die Katze hat eine Datei weggeschubst",
            Mood.Coward => "Die Katze hat sich versteckt",
            _ => "Da ist eine Katze im Weg",
        };
    }

    /// The robot stops dead while it's being hit.
    public bool Fighting => state == St.Swatting;

    // MARK: - Being picked up

    public bool Hit(V2 p, Sim s)
    {
        switch (state)
        {
            // A cat on board comes first: anywhere on the robot picks up the cat, not the robot.
            case St.Riding: return (p - s.Pos).Len < s.R;
            case St.Sitting: case St.Loafing: case St.Crouching: case St.Scheming: case St.Peeking: break;
            case St.Walking: if (walkThen is Next.Vanish or Next.Mount) return false; break;
            default: return false;
        }
        return (p - Pos).Len < 88 * size;
    }

    public void Pick(Sim s)
    {
        if (Picked) return;
        switch (state)
        {
            case St.Sitting: home = (Pos, Next.Sit); break;
            case St.Loafing: home = (Pos, Next.Loaf); break;
            case St.Crouching: home = (Pos, Next.Crouch); break;
            case St.Scheming: home = (Pos, Next.Scheme); pushT = -1; reach = 0; break;
            case St.Peeking: home = hideout != null ? (hideout.Inside, Next.Hide) : null; break;
            case St.Walking: home = (walkTo, walkThen); break;
            case St.Riding:
                // Lifted off the robot, it's a floor cat again, and it stays wherever it's put.
                home = null;
                hop = 0; size = FloorSize; seatOffset = V2.Zero;
                Set(rest == Rest.Loaf ? St.Loafing : St.Sitting, s);
                break;
            default: home = null; break;
        }
        Picked = true;
        disturbed = true;
        lookTarget = 0;
        Say(3, 1, 0.85f);
    }

    public void Drag(V2 p, Sim s)
    {
        var d = p - Pos;
        // Swings a little as it's carried, then settles.
        heading = M.Wrap(heading + M.Clamp(d.X * 0.0009f, -0.03f, 0.03f));
        Pos = s.Plan.Clamp(p, 60);
    }

    /// Put down, it walks straight back to where it was. Put down on the robot, it depends on the cat.
    public void Drop(Sim s)
    {
        if (!Picked) return;
        Picked = false;
        s.Audio?.Thud(0.1f);
        var d = Pos - s.Pos;
        if (d.Len < s.R)
        {
            var canRide = s.Phase is Phase.Undocking or Phase.Scanning or Phase.Cleaning or Phase.Hunting && s.RobotAlpha > 0.5f;
            if (canRide)
            {
                switch (Personality)
                {
                    case Mood.Loaf: Board(s, Rest.Loaf); return;
                    case Mood.Rider: Say(0, 1, 0.45f); Board(s, Rest.Sit); return;
                    case Mood.Supervisor when Coin(): Board(s, Rest.Sit); return;
                    case Mood.Swatter:
                        Audio?.Hiss(0.5f);
                        afterHop = AfterHop.Fight;
                        HopDown(s);
                        return;
                }
            }
            Say(2, 0.92f, 0.5f);
            afterHop = AfterHop.Home;
            HopDown(s);
            return;
        }
        Say(2, 0.92f, 0.5f);
        // Dropped half on the robot's edge: hops clear, rather than being shoved.
        if (d.Len < s.R + 62)
        {
            afterHop = AfterHop.Home;
            HopDown(s);
            return;
        }
        if (home is { } h && (h.spot - Pos).Len > 20) SetWalk(h.spot, 200, h.then, s);
    }

    /// Stays on for the ride, sitting or as a loaf. It still gets off before the dock.
    void Board(Sim s, Rest resting)
    {
        rest = resting;
        afterHop = resting == Rest.Loaf ? AfterHop.Loaf : AfterHop.Watch;
        seatOffset = Pos - (s.Pos - s.Fwd * 8);
        yawLag = heading;
        lookTarget = resting == Rest.Loaf ? 0 : 0.8f;
        Set(St.Riding, s);
    }

    void HopDown(Sim s)
    {
        var d = Pos - s.Pos;
        var dir = d.Len > 1 ? d.Unit : s.Fwd * -1;
        var to = s.Pos + dir * (s.R + 78);
        if (InsideKeepOut(s, to) || !Same(to, ClampToScreen(to, s))) to = s.Pos - dir * (s.R + 78);
        SetHop(Pos, ClampToScreen(to, s), s);
    }

    static V2 ClampToScreen(V2 p, Sim s) => s.Plan.Clamp(p, 60);

    // MARK: - Drawing

    public CatPose Pose(float t, Sim s)
    {
        var r = rest switch
        {
            Rest.Sit => CatPose.Sitting(Pos, heading, look, t),
            Rest.Loaf => CatPose.Loaf(Pos, heading, look, t),
            _ => CatPose.Crouching(Pos, heading, look, reach, reach2, puff, wiggle * MathF.Sin(t * 21), t),
        };
        var w = CatPose.Walking(Pos, heading, cycle, look, t);
        var p = CatPose.Mix(r, w, M.Smooth(0, 1, walkK));
        if (pickK > 0.001f) p = CatPose.Mix(p, CatPose.Limp(Pos, heading, t), M.Smooth(0, 1, pickK));
        var riding = state is St.Riding or St.Climbing or St.Hopping;
        var liftNow = riding ? s.Lift * 0.9f : 0;
        p.Lift = MathF.Max(hop, pickK) + liftNow;
        p.Size = size * (1 + 0.12f * pickK + (riding ? 0.13f * s.Lift : 0));
        if (state == St.Riding) p.Pos = s.Pos - s.Fwd * 8 + seatOffset;
        // Eyes: loaves sleep, unless a robot has just walked into them. Everyone else blinks.
        var blinking = (t + 0.7f) % 3.3f < 0.15f;
        p.EyesOpen = rest == Rest.Loaf && walkK < 0.5f && pickK < 0.5f ? t < eyeOpenUntil : !blinking;
        var even = (int)nextFlick % 2 == 0;
        p.EarL = earFlick * (even ? 1 : 0) - earsBack;
        p.EarR = earFlick * (even ? 0 : 1) - earsBack;
        return p;
    }
}
