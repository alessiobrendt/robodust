using System;
using System.Collections.Generic;
using System.Linq;

namespace RoboDust;

public struct RectF
{
    public float X, Y, W, H;
    public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    public float Right => X + W;
    public float Bottom => Y + H;
    public V2 Center => new(X + W / 2, Y + H / 2);
    public bool Contains(V2 p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public RectF Inset(float d) => new(X + d, Y + d, MathF.Max(0, W - 2 * d), MathF.Max(0, H - 2 * d));
    public V2 Clamp(V2 p) => new(M.Clamp(p.X, X, MathF.Max(X, Right)), M.Clamp(p.Y, Y, MathF.Max(Y, Bottom)));
    public bool Valid => W > 0 && H > 0;
}

/// All displays as one floor: each work area is a room, laid out the way the monitors sit, in points.
/// Where two rooms share an edge there's a door, so the robot can drive from one display to the next.
public sealed class Floorplan
{
    public readonly List<RectF> Rooms;
    public readonly int Main;   // the primary display, where the dock stands
    public readonly RectF Bounds;
    readonly List<Door> doors = new();

    sealed class Door
    {
        public int A, B;
        public bool Vertical;    // a vertical edge: rooms side by side
        public float Edge0, Edge1;   // where the edge sits: x for a vertical door (both sides of any gap), y otherwise
        public float From, To;   // the shared span along the edge
    }

    public Floorplan(List<RectF> rooms, int main = 0)
    {
        Rooms = rooms; Main = main;
        float x0 = rooms.Min(r => r.X), y0 = rooms.Min(r => r.Y), x1 = rooms.Max(r => r.Right), y1 = rooms.Max(r => r.Bottom);
        Bounds = new RectF(x0, y0, x1 - x0, y1 - y0);
        const float gap = 120;   // a taskbar between two displays still counts as touching
        for (int a = 0; a < rooms.Count; a++)
            for (int b = 0; b < rooms.Count; b++)
            {
                if (a == b) continue;
                RectF A = rooms[a], B = rooms[b];
                // B to the right of A.
                if (B.X >= A.Right - 2 && B.X - A.Right < gap)
                {
                    float f = MathF.Max(A.Y, B.Y), t = MathF.Min(A.Bottom, B.Bottom);
                    if (t - f > 40) doors.Add(new Door { A = a, B = b, Vertical = true, Edge0 = A.Right, Edge1 = B.X, From = f, To = t });
                }
                // B below A.
                if (B.Y >= A.Bottom - 2 && B.Y - A.Bottom < gap)
                {
                    float f = MathF.Max(A.X, B.X), t = MathF.Min(A.Right, B.Right);
                    if (t - f > 40) doors.Add(new Door { A = a, B = b, Vertical = false, Edge0 = A.Bottom, Edge1 = B.Y, From = f, To = t });
                }
            }
    }

    public static Floorplan Single(V2 size) => new(new List<RectF> { new(0, 0, size.X, size.Y) });

    /// Where something of this radius may be: every room shrunk by it, plus a passage through each door.
    IEnumerable<RectF> Regions(float inset)
    {
        foreach (var r in Rooms) { var i = r.Inset(inset); if (i.Valid) yield return i; }
        foreach (var d in doors)
        {
            if (d.To - d.From <= 2 * inset) continue;
            if (d.Vertical) yield return new RectF(d.Edge0 - inset - 1, d.From + inset, d.Edge1 - d.Edge0 + 2 * inset + 2, d.To - d.From - 2 * inset);
            else yield return new RectF(d.From + inset, d.Edge0 - inset - 1, d.To - d.From - 2 * inset, d.Edge1 - d.Edge0 + 2 * inset + 2);
        }
    }

    /// The nearest point that something of this radius can occupy.
    public V2 Clamp(V2 p, float inset)
    {
        V2 best = p; float bd = float.PositiveInfinity;
        foreach (var r in Regions(inset))
        {
            if (r.Contains(p)) return p;
            var q = r.Clamp(p); var d = (q - p).Len;
            if (d < bd) { bd = d; best = q; }
        }
        return best;
    }

    public int RoomOf(V2 p)
    {
        int best = Main; float bd = float.PositiveInfinity;
        for (int i = 0; i < Rooms.Count; i++)
        {
            if (Rooms[i].Contains(p)) return i;
            var d = (Rooms[i].Clamp(p) - p).Len;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    /// Waypoints from one place to another: through the doors in between, then the destination.
    public List<V2> Route(V2 from, V2 to, float inset)
    {
        var a = RoomOf(from); var b = RoomOf(to);
        var route = new List<V2>();
        if (a != b)
        {
            // Breadth-first over the doors: few displays, so this is instant.
            var prev = new Dictionary<int, (int room, Door door)>();
            var queue = new Queue<int>(); queue.Enqueue(a); prev[a] = (-1, null);
            while (queue.Count > 0)
            {
                var r = queue.Dequeue();
                if (r == b) break;
                foreach (var d in doors.Where(d => d.A == r || d.B == r))
                {
                    var n = d.A == r ? d.B : d.A;
                    if (prev.ContainsKey(n)) continue;
                    prev[n] = (r, d); queue.Enqueue(n);
                }
            }
            if (prev.ContainsKey(b))
            {
                var chain = new List<Door>();
                for (var r = b; prev[r].door != null; r = prev[r].room) chain.Add(prev[r].door);
                chain.Reverse();
                var at = from;
                foreach (var d in chain)
                {
                    // Cross where the path would like to, kept clear of the door's frame.
                    if (d.Vertical)
                    {
                        var y = M.Clamp((at.Y + to.Y) / 2, d.From + inset + 4, d.To - inset - 4);
                        var x = (d.Edge0 + d.Edge1) / 2;
                        var dir = d.A == RoomOf(at) ? 1 : -1;
                        route.Add(new V2(x - dir * inset * 0.6f, y));
                        route.Add(new V2(x + dir * inset * 0.6f, y));
                        at = new V2(x, y);
                    }
                    else
                    {
                        var x = M.Clamp((at.X + to.X) / 2, d.From + inset + 4, d.To - inset - 4);
                        var y = (d.Edge0 + d.Edge1) / 2;
                        var dir = d.A == RoomOf(at) ? 1 : -1;
                        route.Add(new V2(x, y - dir * inset * 0.6f));
                        route.Add(new V2(x, y + dir * inset * 0.6f));
                        at = new V2(x, y);
                    }
                }
            }
        }
        route.Add(Clamp(to, inset));
        return route.Select(p => Clamp(p, inset)).ToList();
    }

    /// Rooms in the order a robot standing here would visit them: nearest first, through the doors.
    public List<int> VisitOrder(V2 from)
    {
        var start = RoomOf(from);
        var order = new List<int> { start };
        var seen = new HashSet<int> { start };
        // Depth-first, so it sweeps one side before walking all the way to the other.
        void Visit(int r)
        {
            foreach (var n in doors.Where(d => d.A == r || d.B == r).Select(d => d.A == r ? d.B : d.A)
                         .OrderBy(n => (Rooms[n].Center - Rooms[r].Center).Len))
            {
                if (!seen.Add(n)) continue;
                order.Add(n); Visit(n);
            }
        }
        Visit(start);
        foreach (var i in Enumerable.Range(0, Rooms.Count)) if (seen.Add(i)) order.Add(i);
        return order;
    }

    /// Places each display in the shared world. The primary display's work area starts at (0, 0); the
    /// others sit against it the way they do physically, each measured in its own points.
    public static void Layout(List<Screen> screens)
    {
        var primary = screens.FindIndex(s => s.IsPrimary);
        if (primary < 0) primary = 0;
        var origin = new Dictionary<int, V2> { [primary] = V2.Zero };   // world position of each monitor's top-left
        var queue = new Queue<int>(); queue.Enqueue(primary);
        while (queue.Count > 0)
        {
            var a = queue.Dequeue(); var A = screens[a].Bounds; var sa = screens[a].Scale; var oa = origin[a];
            for (int b = 0; b < screens.Count; b++)
            {
                if (origin.ContainsKey(b)) continue;
                var B = screens[b].Bounds; var sb = screens[b].Scale;
                V2? o = null;
                if (B.Right == A.Left) o = new V2(oa.X - B.Width / sb, oa.Y + (B.Top - A.Top) / sa);
                else if (B.Left == A.Right) o = new V2(oa.X + A.Width / sa, oa.Y + (B.Top - A.Top) / sa);
                else if (B.Bottom == A.Top) o = new V2(oa.X + (B.Left - A.Left) / sa, oa.Y - B.Height / sb);
                else if (B.Top == A.Bottom) o = new V2(oa.X + (B.Left - A.Left) / sa, oa.Y + A.Height / sa);
                if (o == null) continue;
                origin[b] = o.Value; queue.Enqueue(b);
            }
        }
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            var o = origin.TryGetValue(i, out var v) ? v : new V2(s.Bounds.Left / s.Scale, s.Bounds.Top / s.Scale);
            s.WorldOrigin = o + new V2((s.WorkArea.Left - s.Bounds.Left) / s.Scale, (s.WorkArea.Top - s.Bounds.Top) / s.Scale);
        }
        // Keep the primary work area's top-left at exactly (0, 0).
        var shift = screens[primary].WorldOrigin;
        foreach (var s in screens) s.WorldOrigin -= shift;
    }

    /// dock: index of the display the dock stands on; the primary display if not given.
    public static Floorplan Of(List<Screen> screens, int dock = -1) =>
        new(screens.Select(s => new RectF(s.WorldOrigin.X, s.WorldOrigin.Y, s.SizePts.X, s.SizePts.Y)).ToList(),
            dock >= 0 && dock < screens.Count ? dock : Math.Max(0, screens.FindIndex(s => s.IsPrimary)));
}
