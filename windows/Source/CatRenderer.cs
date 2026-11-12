using System;
using System.Collections.Generic;

namespace RoboDust;

/// Coats are designed colourways, not attempts at real fur. Pattern 1 is saddle bands, 2 a tuxedo.
public sealed class CatCoat
{
    public V3 Base, Stripe, Belly;
    public float WhitePaws, Pattern;
    public static readonly CatCoat Ginger = new() { Base = new(0.70f, 0.49f, 0.30f), Stripe = new(0.50f, 0.30f, 0.16f), Belly = new(0.92f, 0.80f, 0.62f), WhitePaws = 1, Pattern = 1 };
    public static readonly CatCoat Tuxedo = new() { Base = new(0.20f, 0.20f, 0.22f), Stripe = new(0.20f, 0.20f, 0.22f), Belly = new(0.86f, 0.85f, 0.83f), WhitePaws = 1, Pattern = 2 };
    public static readonly CatCoat Charcoal = new() { Base = new(0.17f, 0.175f, 0.19f), Stripe = new(0.17f, 0.175f, 0.19f), Belly = new(0.17f, 0.175f, 0.19f), WhitePaws = 0, Pattern = 0 };
    public static readonly CatCoat Silver = new() { Base = new(0.63f, 0.63f, 0.62f), Stripe = new(0.40f, 0.40f, 0.40f), Belly = new(0.91f, 0.90f, 0.88f), WhitePaws = 1, Pattern = 1 };
}

/// One joint of a pose: along the body, to the side, radius.
public struct CatJoint
{
    public float A, S, R;
    public CatJoint(float a, float s, float r) { A = a; S = s; R = r; }
}

/// What the shader needs to draw one cat. Same fields as `CatU` in the Mac's Shaders.metal.
public sealed class CatU
{
    public V2 Center, Half;
    public readonly float[] Spine = new float[16];   // 4 x (x, y, radius, 0)
    public readonly float[] Head = new float[4];
    public V2 HeadDir = new(1, 0), ShadowOffset = new(5, 9);
    public readonly float[] Tail = new float[36];    // 9 joints
    public readonly float[] Paws = new float[32];    // 4 paws, then 4 shoulders and haunches (w = 1)
    public V3 Base, Stripe, Belly;
    public float WhitePaws, Pattern, Alpha = 1, EyesOpen = 1, EarL, EarR;
}

/// A pose, seen from directly above: hips, spine, head, paws and tail, relative to the cat's own heading.
public sealed class CatPose
{
    public V2 Pos; public float Heading;
    public CatJoint[] Spine;                 // hips first
    public CatJoint Head;
    public float Look;                       // head turn, radians
    public CatJoint[] Paws;
    public float TailStartA, TailStartS;
    public float TailAngle, TailCurl, TailLength = 120, TailWave;
    public float Lift;
    public bool EyesOpen = true;
    public float EarL, EarR;
    public float Size = 1;
    public CatJoint[] Blobs = Array.Empty<CatJoint>();   // shoulders and haunches

    static CatJoint J(float a, float s, float r) => new(a, s, r);

    public CatU Uniforms(CatCoat coat, float time)
    {
        var f = new V2(MathF.Cos(Heading), MathF.Sin(Heading)); var sd = f.Perp;
        V2 P(float a, float b) => Pos + (f * a + sd * b) * Size;
        (float x, float y, float z, float w) Jt(CatJoint j) { var p = P(j.A, j.S); return (p.X, p.Y, j.R * Size, 0); }
        // Slow breathing: barely visible on its own, but it's what stops it reading as a statue.
        var breath = 1 + 0.018f * MathF.Sin(time * 1.7f + Pos.X * 0.01f);
        (float x, float y, float z, float w) B(CatJoint j) { var v = Jt(j); v.z *= breath; return v; }
        var u = new CatU();
        var lo = new V2(float.PositiveInfinity, float.PositiveInfinity); var hi = new V2(float.NegativeInfinity, float.NegativeInfinity);
        void Grow(float x, float y, float r)
        {
            if (r <= 0) return;
            lo = new V2(MathF.Min(lo.X, x - r), MathF.Min(lo.Y, y - r)); hi = new V2(MathF.Max(hi.X, x + r), MathF.Max(hi.Y, y + r));
        }
        void Put(float[] arr, int i, (float x, float y, float z, float w) v) { arr[i * 4] = v.x; arr[i * 4 + 1] = v.y; arr[i * 4 + 2] = v.z; arr[i * 4 + 3] = v.w; Grow(v.x, v.y, v.z); }

        for (int i = 0; i < 4; i++) Put(u.Spine, i, B(Spine[i]));
        var h = Jt(Head);
        u.Head[0] = h.x; u.Head[1] = h.y; u.Head[2] = h.z; u.Head[3] = 0;
        Grow(h.x, h.y, h.z * 1.7f);
        u.HeadDir = f.Rotated(Look);
        u.ShadowOffset = new V2(5, 9) + new V2(10, 18) * Lift;
        // The tail is a chain that bends a bit more with each joint.
        var tp = P(TailStartA, TailStartS); var a = Heading + TailAngle;
        var step = TailLength * Size / 8;
        for (int i = 0; i < 9; i++)
        {
            var k = i / 8f;
            Put(u.Tail, i, (tp.X, tp.Y, (9.5f - 2.6f * k) * Size, 0));
            a += TailCurl / 8 + MathF.Sin(time * 2.6f + i * 0.7f) * TailWave * (0.3f + k);
            tp += new V2(MathF.Cos(a), MathF.Sin(a)) * step;
        }
        int n = 0;
        foreach (var p in Paws) { if (n >= 8) break; Put(u.Paws, n++, Jt(p)); }
        foreach (var b in Blobs) { if (n >= 8) break; var v = B(b); v.w = 1; Put(u.Paws, n++, v); }
        u.Base = coat.Base; u.Stripe = coat.Stripe; u.Belly = coat.Belly;
        u.WhitePaws = coat.WhitePaws; u.Pattern = coat.Pattern;
        u.EarL = EarL; u.EarR = EarR;
        u.EyesOpen = EyesOpen ? 1 : 0;
        // Only as big a quad as this pose needs, shadow included: this shader is expensive per pixel.
        var pad = 34 + 1.7f * u.ShadowOffset.Len;
        u.Center = (lo + hi) * 0.5f; u.Half = (hi - lo) * 0.5f + new V2(pad, pad);
        return u;
    }

    /// Sitting tall: wide haunches, narrow chest, paws together, tail round to one side.
    public static CatPose Sitting(V2 pos, float heading, float look = 0, float time = 0) => new()
    {
        Pos = pos, Heading = heading,
        Spine = new[] { J(-46, 0, 38), J(-20, 0, 40), J(10, 0, 31), J(30, 0, 21) },
        Head = J(66, 0, 28), Look = look,
        Paws = Tucked(),
        TailStartA = -60, TailStartS = 24, TailAngle = 2.15f, TailCurl = -2.1f, TailLength = 120, TailWave = 0.012f + 0.03f * MathF.Pow(MathF.Max(0, MathF.Sin(time * 0.9f)), 6),
        Blobs = new[] { J(-40, -19, 31), J(-40, 19, 31), J(14, -14, 24), J(14, 14, 24) },
    };

    /// The loaf: a neat oval with everything tucked in.
    public static CatPose Loaf(V2 pos, float heading, float look = 0, float time = 0, bool eyes = false) => new()
    {
        Pos = pos, Heading = heading,
        Spine = new[] { J(-48, 0, 36), J(-20, 0, 40), J(8, 0, 38), J(28, 0, 28) },
        Head = J(66, 0, 28), Look = look, Paws = Tucked(),
        TailStartA = -68, TailStartS = 22, TailAngle = 2.3f, TailCurl = -2.5f, TailLength = 118, TailWave = 0.004f,
        Blobs = new[] { J(-38, -17, 30), J(-38, 17, 30), J(10, -18, 27), J(10, 18, 27) },
        EyesOpen = eyes,
    };

    /// One foot's position along the body through a stride. In stance it moves backward at exactly
    /// the speed the body moves forward, so the paw stays planted on the floor. Then it swings ahead.
    public static float Foot(float phase, float reach)
    {
        const float stance = 0.62f;
        var p = phase - MathF.Floor(phase);
        if (p < stance) return reach * (1 - 2 * p / stance);
        return -reach + 2 * reach * M.Smooth(0, 1, (p - stance) / (1 - stance));
    }
    public const float StrideReach = 25;
    /// Strides per second for a given speed, so that planted feet really don't slide.
    public static float StridesPerSecond(float speed) => speed * 0.62f / (2 * StrideReach);

    /// Walking: a four-beat gait seen from above. The shoulders and haunches roll with the legs,
    /// the spine sways a little, and the head stays steadier than the body.
    public static CatPose Walking(V2 pos, float heading, float cycle, float look = 0, float time = 0)
    {
        const float A = StrideReach;
        float lf = Foot(cycle, A), rh = Foot(cycle + 0.25f, A), rf = Foot(cycle + 0.5f, A), lh = Foot(cycle + 0.75f, A);
        var sway = MathF.Sin(cycle * 2 * M.Pi);
        return new CatPose
        {
            Pos = pos, Heading = heading,
            Spine = new[] { J(-62, sway * -3.5f, 26), J(-24, sway * 2, 26), J(14, sway * -1.5f, 25), J(46, sway * 1.2f, 19) },
            Head = J(72, sway * 0.8f, 25), Look = look - sway * 0.035f,
            Paws = new[] { J(42 + lf, -29, 8.5f), J(42 + rf, 29, 8.5f), J(-56 + lh, -32, 9), J(-56 + rh, 32, 9) },
            TailStartA = -84, TailStartS = sway * -2, TailAngle = M.Pi + sway * 0.1f, TailCurl = 0.75f + sway * 0.18f, TailLength = 112, TailWave = 0.02f,
            Blobs = new[] { J(-54 + (lh - rh) * 0.14f, -8, 21), J(-54 + (rh - lh) * 0.14f, 8, 21), J(22 + (lf - rf) * 0.16f, -7, 19), J(22 + (rf - lf) * 0.16f, 7, 19) },
        };
    }

    /// Low and long, tail flicking, with one paw or the other thrown forward as it swats.
    /// Puffed up, it's bigger and rounder, with its tail straight up, so from above it looks short.
    public static CatPose Crouching(V2 pos, float heading, float look, float reach, float reach2, float puff, float wiggle, float time = 0)
    {
        var big = 1 + 0.13f * puff;
        return new CatPose
        {
            Pos = pos, Heading = heading,
            Spine = new[] { J(-60, wiggle * 5, 27 * big), J(-24, wiggle * 2, 27 * big), J(14, 0, 26 * big), J(42, 0, 21 * big) },
            Head = J(68 + MathF.Max(reach, reach2) * 9, 0, 25), Look = look,
            Paws = new[] { J(34 + reach * 84, 22 - reach * 14, 8.5f), J(34 + reach2 * 84, -22 + reach2 * 14, 8.5f), J(-46, -31, 9), J(-46, 31, 9) },
            TailStartA = -82, TailStartS = wiggle * 7, TailAngle = M.Pi + wiggle * 0.25f, TailCurl = -0.7f * (1 - puff), TailLength = 112 - 58 * puff, TailWave = 0.09f,
            Blobs = new[] { J(-50, -10, 23 * big), J(-50, 10, 23 * big), J(20, -9, 20 * big), J(20, 9, 20 * big) },
        };
    }

    /// Carried: gone long and limp, with its legs and tail hanging down.
    public static CatPose Limp(V2 pos, float heading, float time = 0)
    {
        var sw = MathF.Sin(time * 3);
        return new CatPose
        {
            Pos = pos, Heading = heading,
            Spine = new[] { J(-70, 0, 24), J(-30, 0, 25), J(14, 0, 25), J(50, 0, 19) },
            Head = J(78, 0, 25), Look = sw * 0.12f,
            Paws = new[] { J(46, -23, 7.5f), J(46, 23, 7.5f), J(-66, -23, 8), J(-66, 23, 8) },
            TailStartA = -88, TailStartS = 0, TailAngle = M.Pi, TailCurl = sw * 0.25f, TailLength = 62, TailWave = 0.01f,
            Blobs = new[] { J(-60, -8, 20), J(-60, 8, 20), J(24, -8, 19), J(24, 8, 19) },
        };
    }

    /// Hidden under the body, so sitting cats can stand up without their feet popping into existence.
    static CatJoint[] Tucked() => new[] { J(16, -11, 8), J(16, 11, 8), J(-30, -24, 8), J(-30, 24, 8) };

    /// Between two poses of the same cat. Every pose has the same parts, which is what makes this work.
    public static CatPose Mix(CatPose a, CatPose b, float k)
    {
        float Mf(float x, float y) => x + (y - x) * k;
        CatJoint T3(CatJoint x, CatJoint y) => new(Mf(x.A, y.A), Mf(x.S, y.S), Mf(x.R, y.R));
        CatJoint[] Zip(CatJoint[] x, CatJoint[] y)
        {
            var n = Math.Min(x.Length, y.Length);
            var o = new CatJoint[n];
            for (int i = 0; i < n; i++) o[i] = T3(x[i], y[i]);
            return o;
        }
        var p = (CatPose)a.MemberwiseClone();
        p.Spine = Zip(a.Spine, b.Spine);
        p.Head = T3(a.Head, b.Head);
        p.Look = Mf(a.Look, b.Look);
        p.Paws = Zip(a.Paws, b.Paws);
        p.Blobs = Zip(a.Blobs, b.Blobs);
        p.TailStartA = Mf(a.TailStartA, b.TailStartA); p.TailStartS = Mf(a.TailStartS, b.TailStartS);
        p.TailAngle = Mf(a.TailAngle, b.TailAngle); p.TailCurl = Mf(a.TailCurl, b.TailCurl);
        p.TailLength = Mf(a.TailLength, b.TailLength); p.TailWave = Mf(a.TailWave, b.TailWave);
        return p;
    }
}

/// The clean's own cat, drawn in the same pass as everything else. Owns its GL program, so it needs
/// the GL context current when it's made and when it draws.
public sealed unsafe class CatRenderer
{
    readonly uint prog, vao;
    readonly Dictionary<string, int> locs = new();

    public CatRenderer()
    {
        prog = Gl.Program(VS, FS);
        vao = Gl.Gen(Gl.GenVertexArrays);
    }

    int L(string n) { if (!locs.TryGetValue(n, out var l)) { l = Gl.Loc(prog, n); locs[n] = l; } return l; }
    void F(string n, float v) => Gl.Uniform1f(L(n), v);
    void F2(string n, V2 v) => Gl.Uniform2f(L(n), v.X, v.Y);
    void F4(string n, float x, float y, float z, float w) => Gl.Uniform4f(L(n), x, y, z, w);
    void F4v(string n, float[] v) { fixed (float* p = v) Gl.Uniform4fv(L(n), v.Length / 4, p); }

    /// above: the pass after the robot, for a cat that's riding, climbing on, hopping off or being carried.
    /// The caller has blending on, ONE / ONE_MINUS_SRC_ALPHA.
    public void Draw(Sim s, CatActor cat, V2 viewport, V2 origin, bool above)
    {
        if (cat == null || cat.IsGone || cat.Above != above) return;
        var u = cat.Pose(s.T, s).Uniforms(cat.Coat, s.T);
        u.Alpha = s.RobotAlpha;
        // Not on this display: nothing to draw, and this shader is expensive.
        if (u.Center.X + u.Half.X < origin.X || u.Center.X - u.Half.X > origin.X + viewport.X ||
            u.Center.Y + u.Half.Y < origin.Y || u.Center.Y - u.Half.Y > origin.Y + viewport.Y) return;
        Gl.UseProgram(prog);
        F2("viewport", viewport); F2("origin", origin);
        F2("uCenter", u.Center); F2("uHalf", u.Half);
        F4v("uSpine[0]", u.Spine);
        F4("uHead", u.Head[0], u.Head[1], u.Head[2], u.Head[3]);
        F2("uHeadDir", u.HeadDir); F2("uShadowOffset", u.ShadowOffset);
        F4v("uTail[0]", u.Tail);
        F4v("uPaws[0]", u.Paws);
        F4("uBase", u.Base.X, u.Base.Y, u.Base.Z, 1);
        F4("uStripe", u.Stripe.X, u.Stripe.Y, u.Stripe.Z, 1);
        F4("uBelly", u.Belly.X, u.Belly.Y, u.Belly.Z, 1);
        F("uWhitePaws", u.WhitePaws); F("uPattern", u.Pattern); F("uAlpha", u.Alpha); F("uEyesOpen", u.EyesOpen);
        F("uEarL", u.EarL); F("uEarR", u.EarR);
        Gl.BindVertexArray(vao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
    }

    const string Head = @"#version 330
#define saturate(x) clamp((x), 0.0, 1.0)
const vec2 kQuad[6] = vec2[6](vec2(-1.0,-1.0), vec2(1.0,-1.0), vec2(-1.0,1.0), vec2(-1.0,1.0), vec2(1.0,-1.0), vec2(1.0,1.0));
uniform vec2 viewport;
uniform vec2 origin;   // this display's top-left in the shared world
vec4 toClip(vec2 w) { vec2 p = w - origin; return vec4(p.x / viewport.x * 2.0 - 1.0, 1.0 - p.y / viewport.y * 2.0, 0.0, 1.0); }
";

    // ---------- the cat: designed like the robot, a soft matte object rather than an imitation of fur ----------
    const string VS = Head + @"
uniform vec2 uCenter; uniform vec2 uHalf;
out vec2 wp;   // world points
void main() {
    vec2 q = kQuad[gl_VertexID] * uHalf;
    gl_Position = toClip(uCenter + q); wp = uCenter + q;
}";

    const string FS = Head + @"
in vec2 wp; out vec4 o;
uniform vec2 uCenter; uniform vec2 uHalf;
uniform vec4 uSpine[4];      // x, y, radius
uniform vec4 uHead;          // x, y, radius
uniform vec2 uHeadDir; uniform vec2 uShadowOffset;
uniform vec4 uTail[9];
uniform vec4 uPaws[8];
uniform vec4 uBase; uniform vec4 uStripe; uniform vec4 uBelly;
uniform float uWhitePaws; uniform float uPattern; uniform float uAlpha; uniform float uEyesOpen;
uniform float uEarL; uniform float uEarR;

float catSmin(float a, float b, float k) { float h = saturate(0.5 + 0.5 * (b - a) / k); return mix(b, a, h) - k * h * (1.0 - h); }
float catCone(vec2 p, vec2 a, vec2 b, float ra, float rb) {
    vec2 ba = b - a; float h = saturate(dot(p - a, ba) / max(dot(ba, ba), 1e-3));
    return length(p - a - ba * h) - mix(ra, rb, h);
}
float catHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float catNoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(catHash(i), catHash(i + vec2(1.0, 0.0)), f.x), mix(catHash(i + vec2(0.0, 1.0)), catHash(i + vec2(1.0, 1.0)), f.x), f.y);
}
float catTri(vec2 p, vec2 a, vec2 b, vec2 c) {
    vec2 e0 = b - a, e1 = c - b, e2 = a - c, v0 = p - a, v1 = p - b, v2 = p - c;
    vec2 pq0 = v0 - e0 * saturate(dot(v0, e0) / dot(e0, e0));
    vec2 pq1 = v1 - e1 * saturate(dot(v1, e1) / dot(e1, e1));
    vec2 pq2 = v2 - e2 * saturate(dot(v2, e2) / dot(e2, e2));
    float s = sign(e0.x * e2.y - e0.y * e2.x);
    vec2 d = min(min(vec2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)), vec2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))), vec2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
    return -sqrt(d.x) * sign(d.y);
}

struct CatParts { float body; float head; float skull; float ear; float earInner; float tail; float paw; float sock; float leg; float torso; float whole; };

CatParts catMap(vec2 p) {
    CatParts c;
    float b = 1e5;
    for (int i = 0; i < 3; i++) b = catSmin(b, catCone(p, uSpine[i].xy, uSpine[i + 1].xy, uSpine[i].z, uSpine[i + 1].z), 12.0);
    float t = 1e5;
    for (int i = 0; i < 8; i++) t = catSmin(t, catCone(p, uTail[i].xy, uTail[i + 1].xy, uTail[i].z, uTail[i + 1].z), 7.0);
    float pw = 1e5, leg = 1e5, sock = 1e5;
    for (int i = 0; i < 8; i++) if (uPaws[i].z > 0.0) {
        if (uPaws[i].w > 0.5) {
            // Not a foot: a shoulder or haunch, shaping the body's outline.
            b = catSmin(b, length(p - uPaws[i].xy) - uPaws[i].z, 14.0);
            continue;
        }
        // A slim leg that comes out from under the body, ending in a slightly long foot.
        vec2 joint = i < 2 ? uSpine[2].xy : uSpine[0].xy;
        vec2 toe = normalize(uPaws[i].xy - joint) * uPaws[i].z * 0.45;
        pw = min(pw, catCone(p, uPaws[i].xy - toe, uPaws[i].xy + toe, uPaws[i].z, uPaws[i].z * 0.9));
        sock = min(sock, length(p - uPaws[i].xy - toe * 0.9) - uPaws[i].z);
        leg = min(leg, catCone(p, joint, uPaws[i].xy, uPaws[i].z * 1.2, uPaws[i].z * 0.85));
    }
    vec2 hc = uHead.xy, d = uHeadDir, s = vec2(-d.y, d.x);
    float r = uHead.z;
    vec2 q = p - hc;
    vec2 lq = vec2(dot(q, d), dot(q, s)) / r;
    // A cat's skull from above: wide at the cheeks, tapering to a short muzzle.
    float taper = 1.0 + 0.42 * saturate(lq.x + 0.05) * saturate(lq.x + 0.05);
    float skull = (length(vec2(lq.x * 0.90, lq.y * (0.97 * taper))) - 1.0) * r;
    float ear = 1e5, inner = 1e5;
    for (int k = -1; k <= 1; k += 2) {
        float fk = float(k);
        float tw = k < 0 ? uEarL : uEarR;
        // Upright, slightly forward, set wide: small enough to feel like a cat, not a mascot.
        vec2 tip = hc + (d * (-0.30 + tw * 0.26) + s * fk * (1.30 - tw * 0.1)) * r;
        vec2 a = hc + (d * 0.16 + s * fk * 0.70) * r, b2 = hc + (-d * 0.68 + s * fk * 0.42) * r;
        ear = min(ear, catTri(p, a, b2, tip) - 2.8);
        vec2 ic = (a + b2 + tip) / 3.0;
        inner = min(inner, catTri(p, mix(a, ic, 0.34), mix(b2, ic, 0.34), mix(tip, ic, 0.3)));
    }
    c.body = b; c.leg = leg; c.skull = skull; c.head = catSmin(skull, ear, 5.0); c.ear = ear; c.earInner = inner; c.tail = t; c.paw = pw; c.sock = sock;
    float whole = catSmin(b, c.head, 11.0);       // a neck, rather than a ball resting on a body
    whole = catSmin(whole, t, 6.0);
    c.torso = whole;
    c.whole = catSmin(whole, min(leg, pw), 3.0);
    return c;
}

float catDome(float d, float r) { float e = saturate(-d / r); return r * sqrt(1.0 - (1.0 - e) * (1.0 - e)); }

float catSmax(float a, float b, float k) { return -catSmin(-a, -b, k); }

float catHeight(vec2 p) {
    CatParts c = catMap(p);
    // One continuous form with soft swellings added on top, so no part has a seam where it joins.
    float h = catDome(c.torso, 30.0) * 0.9;
    h += 4.0 * saturate(-c.body / 18.0);
    // Legs are their own low forms, which the body sits over.
    h = max(h, catDome(min(c.leg, c.paw), 8.0) * 0.85 + 1.2 * saturate(-c.paw / 6.0));
    vec2 q = p - uHead.xy, d = uHeadDir, s = vec2(-d.y, d.x);
    vec2 lq = vec2(dot(q, d), dot(q, s)) / uHead.z;
    float skullBump = saturate(-c.skull / (uHead.z * 0.5));
    h += skullBump * skullBump * (3.0 - 2.0 * skullBump) * 9.0;
    h += skullBump * 2.6 * exp(-(lq.y * lq.y * 8.0 + (lq.x - 0.55) * (lq.x - 0.55) * 5.0));
    h += skullBump * 1.5 * exp(-((abs(lq.y) - 0.4) * (abs(lq.y) - 0.4) * 16.0 + (lq.x - 0.12) * (lq.x - 0.12) * 14.0));
    // Ears: raised at the rim, cupped in the middle.
    float earBump = saturate(-c.ear / 4.0);
    // Where an ear crosses the shoulder it stays low, so it can't read as a fold in the neck.
    float overBody = 1.0 - 0.65 * saturate(-c.body / 10.0);
    h += earBump * earBump * (3.0 - 2.0 * earBump) * 4.0 * (1.0 - skullBump) * overBody;
    h -= 2.2 * saturate(-c.earInner / 3.5) * (1.0 - skullBump) * overBody;
    return h;
}

void main() {
    vec2 p = wp;
    // Before any discard, so the derivative is valid for every pixel.
    float px = max(fwidth(p.x), 0.001);
    CatParts c = catMap(p);
    if (c.whole > 34.0 + 1.7 * length(uShadowOffset)) discard;

    // Two-part shadow, built the same way as the robot's.
    float tight = 1.0 - smoothstep(-3.0, 8.0, catMap(p - vec2(2.0, 3.5)).whole);
    float wide = 1.0 - smoothstep(-16.0, 24.0, catMap(p - uShadowOffset * 1.7).whole);
    float sh = 1.0 - (1.0 - tight * 0.40) * (1.0 - wide * 0.27);

    float edge = 1.0 - smoothstep(-px, px, c.whole);
    if (edge <= 0.0 && sh <= 0.003) discard;

    const float E = 1.2;
    float hx = catHeight(p + vec2(E, 0.0)) - catHeight(p - vec2(E, 0.0));
    float hy = catHeight(p + vec2(0.0, E)) - catHeight(p - vec2(0.0, E));
    // Only a hint of nap: soft-touch, not fur.
    float grainX = catNoise(p * 2.4) - 0.5, grainY = catNoise(p * 2.4 + 37.0) - 0.5;
    vec3 N = normalize(vec3(-(hx / (2.0 * E)) * 0.72 + grainX * 0.05, -(hy / (2.0 * E)) * 0.72 + grainY * 0.05, 1.0));

    // Where we are along and across the spine, for markings that follow the body.
    float along = 0.0, across = 1e5, soFar = 0.0, radiusHere = 40.0, flank = 1.0;
    for (int i = 0; i < 3; i++) {
        vec2 a = uSpine[i].xy, ba = uSpine[i + 1].xy - a; float l = max(length(ba), 1e-3);
        float h = saturate(dot(p - a, ba) / (l * l));
        float d = length(p - a - ba * h);
        if (d < across) {
            across = d; along = soFar + h * l; radiusHere = mix(uSpine[i].z, uSpine[i + 1].z, h);
            flank = ba.x * (p.y - a.y) - ba.y * (p.x - a.x) >= 0.0 ? 1.0 : -1.0;
        }
        soFar += l;
    }
    float side = saturate(across / max(radiusHere, 1.0));

    // Short coat: tone follows the body, and fine streaks run along it. Barely visible, but it's what
    // separates this from a smooth toy.
    vec2 hairUV = vec2(along * 0.55, across * 3.1);
    float hair = (catNoise(hairUV) - 0.5) + 0.5 * (catNoise(hairUV * 2.3 + 11.0) - 0.5);
    vec3 col = uBase.rgb;
    int pat = int(uPattern + 0.5);
    float tiny = px * 1.2;
    if (c.body < 0.0 || c.paw < 0.0 || c.leg < 0.0) {
        // Natural countershading: darker down the spine, paler on the flanks.
        col *= mix(0.86, 1.08, smoothstep(0.1, 0.95, side));
        if (pat == 1) {
            // Mackerel tabby: narrow, soft-edged bars that wander and fade out towards the belly.
            // Each flank has its own bars, unevenly spaced and broken up: matched pairs meeting at the
            // spine read as a ribcage.
            float warp = (catNoise(p * 0.055) - 0.5) * 11.0;
            float v = along + side * side * 5.0 + warp + flank * 9.0;
            float wave = 0.5 + 0.5 * sin(v * 0.40 + 1.8 * catNoise(vec2(along * 0.045, flank * 7.0)));
            float broken = smoothstep(0.28, 0.52, catNoise(vec2(along * 0.09 + flank * 13.0, side * 3.2)));
            // Markings stop before the spine ends, where its distance field would turn them into rings.
            float onSpine = smoothstep(6.0, 22.0, along) * (1.0 - smoothstep(soFar - 8.0, soFar + 2.0, along));
            float bars = smoothstep(0.62, 0.94, wave) * (1.0 - smoothstep(0.55, 0.98, side)) * smoothstep(0.1, 0.28, side) * onSpine * broken;
            col = mix(col, uStripe.rgb, bars * 0.58);
            float dorsal = (1.0 - smoothstep(0.02, 0.16, side)) * onSpine;
            col = mix(col, uStripe.rgb, dorsal * 0.5);
        }
        col *= 1.0 + hair * 0.04 + (catNoise(p * 0.11) - 0.5) * 0.045;
    }
    if (c.tail < 0.0 && c.body >= 0.0) {
        float fromTip = length(p - uTail[8].xy);
        if (pat == 1) {
            float wave = 0.5 + 0.5 * sin(fromTip * 0.27);
            col = mix(col, uStripe.rgb, smoothstep(0.55, 0.95, wave) * 0.75);
            col = mix(col, uStripe.rgb, (1.0 - smoothstep(8.0, 20.0, fromTip)) * 0.8);
        }
        if (pat == 2) col = mix(col, uBelly.rgb, 1.0 - smoothstep(7.0, 15.0, fromTip));
    }
    if (c.head >= 0.0 && c.body >= 0.0) col = mix(col, uBelly.rgb, uWhitePaws * 0.9 * (1.0 - smoothstep(-2.5, 1.5, c.sock)));

    // The face.
    vec2 q = p - uHead.xy, d = uHeadDir, s = vec2(-d.y, d.x);
    vec2 lq = vec2(dot(q, d), dot(q, s)) / uHead.z;
    if (c.head < 0.0) {
        col = uBase.rgb;
        col *= mix(0.9, 1.04, smoothstep(0.1, 1.0, abs(lq.y)));
        col *= 1.0 + (catNoise(vec2(lq.x * 9.0, lq.y * 22.0)) - 0.5) * 0.06;
        if (pat == 1) {
            // The tabby's forehead lines, soft and fine.
            float lineK = 0.0;
            for (int k = -1; k <= 1; k++) {
                float cx = float(k) * (0.14 + 0.09 * saturate(-lq.x));
                float along2 = smoothstep(-0.95, -0.72, lq.x) * (1.0 - smoothstep(-0.1, 0.2, lq.x));
                lineK = max(lineK, (1.0 - smoothstep(0.018, 0.075, abs(lq.y - cx))) * along2);
            }
            col = mix(col, uStripe.rgb, lineK * 0.78);
        }
        // Muzzle fades in rather than having an edge.
        float muz = length(vec2((lq.x - 0.92) * 1.5, lq.y * 1.75));
        float muzzle = 1.0 - smoothstep(0.2, 0.62, muz);
        if (pat == 2) {
            float blaze = (1.0 - smoothstep(0.0, 0.11, abs(lq.y) - (0.006 + 0.17 * pow(saturate((lq.x - 0.1) * 1.05), 1.8)))) * smoothstep(-0.12, 0.28, lq.x);
            muzzle = max(muzzle, blaze);
        }
        col = mix(col, uBelly.rgb, muzzle * (pat == 2 ? 0.92 : 0.72));
        if (c.earInner < 0.0 && c.skull >= 0.0) {
            float deep = saturate(-c.earInner / 3.5);
            col = mix(col, uBase.rgb * 0.62 + vec3(0.20, 0.12, 0.11), 0.85 * deep);
        }
    }

    float ao = 1.0;
    // The body shades the legs it stands over.
    if (c.torso >= 0.0 && min(c.leg, c.paw) < 0.0) ao *= mix(0.74, 1.0, smoothstep(0.0, 7.0, c.torso));
    // Head and tail sit on the body and shade it softly.
    if (c.head >= 0.0 && c.body < 0.0) ao *= 1.0 - 0.20 * (1.0 - smoothstep(-6.0, 22.0, catMap(p - vec2(2.0, 5.0)).skull));
    // Soft-touch, not plastic: the same light as the robot, but with a velvety sheen at the edges
    // where plastic would darken. That's what stops the outline reading as drawn.
    vec3 L = normalize(vec3(-0.46, -0.62, 0.64));
    float diff = saturate(dot(N, L) * 0.72 + 0.28);
    float sky = 0.52 + 0.48 * N.z;
    vec3 lit = col * (diff * vec3(1.02, 1.0, 0.97) * 0.86 + sky * vec3(0.36, 0.38, 0.42)) * ao;
    float rim = pow(1.0 - saturate(N.z), 2.2);
    float lum = dot(col, vec3(0.3, 0.59, 0.11));
    // Dark coats need more sheen, or their shape disappears.
    lit += mix(col, vec3(1.0), 0.25 + 0.25 * (1.0 - lum)) * rim * (0.17 + 0.42 * (1.0 - lum) * (1.0 - lum));
    vec3 H = normalize(L + vec3(0.0, 0.0, 1.0));
    lit += pow(saturate(dot(N, H)), 12.0) * (0.04 + 0.075 * (1.0 - lum));

    if (c.head < 0.0) {
        // Seen from above, you only catch the top of a cat's eyes: two narrow, slanted glints.
        for (int k = -1; k <= 1; k += 2) {
            float fk = float(k);
            vec2 c0 = vec2(0.36, fk * 0.42);
            vec2 r0 = lq - c0;
            float ang = fk * 0.5, ca = cos(ang), sa = sin(ang);
            vec2 e = vec2(ca * r0.x - sa * r0.y, sa * r0.x + ca * r0.y) / vec2(0.052, 0.135);
            if (uEyesOpen > 0.5) {
                float eye = 1.0 - smoothstep(0.82, 1.12, length(e));
                lit = mix(lit, vec3(0.05, 0.04, 0.035), eye * 0.92);
                float glint = 1.0 - smoothstep(0.08, 0.3, length(e - vec2(0.1, -0.3)));
                lit = mix(lit, vec3(0.75), glint * eye * 0.45);
            } else {
                float eye = 1.0 - smoothstep(0.35, 0.8, abs(e.x) + e.y * e.y * 0.4);
                lit = mix(lit, lit * 0.55, eye * (1.0 - smoothstep(0.85, 1.1, abs(e.y))));
            }
        }
        vec2 nq = vec2((lq.x - 1.02) / 0.035, lq.y / 0.06);
        lit = mix(lit, vec3(0.46, 0.31, 0.29), 1.0 - smoothstep(0.7, 1.15, length(nq)));
    }
    // Whiskers: fine, short and nearly invisible until you look.
    float wh = 0.0;
    for (int k = -1; k <= 1; k += 2) for (int w = 0; w < 2; w++) {
        float fw = float(w) - 0.5;
        vec2 wa0 = vec2(0.72 + fw * 0.08, float(k) * 0.34), wb0 = wa0 + vec2(fw * 0.34 - 0.08, float(k) * 0.74);
        vec2 pa = lq - wa0, ba = wb0 - wa0; float h = saturate(dot(pa, ba) / dot(ba, ba));
        float dw = length(pa - ba * h);
        wh = max(wh, (1.0 - smoothstep(0.004, 0.014, dw)) * (1.0 - h));
    }

    float a = edge * uAlpha;
    vec3 outCol = lit * a;
    float wa = wh * uAlpha * 0.32;
    outCol = outCol * (1.0 - wa) + vec3(0.93) * wa;
    float alphaOut = max(a, wa);
    float shadow = sh * uAlpha * (1.0 - alphaOut);
    o = vec4(outCol, alphaOut + shadow);
}";
}
