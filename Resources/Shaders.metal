#include <metal_stdlib>
using namespace metal;

// ---------- shared ----------
struct Globals { float2 viewport; float time; float pad; };

static inline float4 toClip(float2 p, float2 vp) {
    return float4(p.x / vp.x * 2 - 1, 1 - p.y / vp.y * 2, 0, 1);
}
constant float2 kQuad[6] = { {-1,-1},{1,-1},{-1,1},{-1,1},{1,-1},{1,1} };

// ---------- wallpaper + freshly vacuumed lanes ----------
struct BgOut { float4 pos [[position]]; float2 uv; };
vertex BgOut bgVS(uint vid [[vertex_id]]) {
    float2 q = kQuad[vid]; BgOut o;
    o.pos = float4(q, 0, 1); o.uv = float2(q.x * .5 + .5, .5 - q.y * .5);
    return o;
}
fragment float4 bgFS(BgOut in [[stage_in]], texture2d<float> wall [[texture(0)]], texture2d<float> track [[texture(1)]]) {
    constexpr sampler s(filter::linear, address::clamp_to_edge);
    float4 c = wall.sample(s, in.uv);
    // Wide blur: the nap should read as a soft sheen with no visible edge.
    float2 texel = 1.0 / float2(track.get_width(), track.get_height());
    float4 t = 0; float wsum = 0;
    for (int y = -2; y <= 2; y++) for (int x = -2; x <= 2; x++) {
        float w = exp(-float(x*x + y*y) * 0.32);
        t += track.sample(s, in.uv + float2(x, y) * texel * 9.0) * w; wsum += w;
    }
    t /= wsum;
    float presence = t.r;
    if (presence > 0.004) {
        // Carpet-style nap: lanes driven in opposite directions catch the light differently.
        float nap = (t.g - .5) * 2;
        c.rgb += presence * nap * 0.017 * (0.5 + c.rgb);
    }
    return float4(c.rgb, 1);
}

// Track buffer stamps: r presence, g direction, b wheels.
struct StampU { float2 center; float2 half_; float angle; float dir; float pad0; float pad1; };
struct StampOut { float4 pos [[position]]; float2 q; float dir; };
vertex StampOut stampVS(uint vid [[vertex_id]], constant StampU& u [[buffer(0)]], constant Globals& g [[buffer(1)]]) {
    float2 q = kQuad[vid]; float c = cos(u.angle), s = sin(u.angle);
    float2 l = q * u.half_; float2 w = u.center + float2(c*l.x - s*l.y, s*l.x + c*l.y);
    StampOut o; o.pos = toClip(w, g.viewport); o.q = q; o.dir = u.dir; return o;
}
fragment float4 stampFS(StampOut in [[stage_in]]) {
    float side = abs(in.q.y);
    if (side > .98 || abs(in.q.x) > .98) discard_fragment();
    float wheels = smoothstep(.09, .03, abs(side - .62));
    return float4(1, in.dir, wheels, 1);
}
vertex float4 fadeVS(uint vid [[vertex_id]]) { return float4(kQuad[vid], 0, 1); }
fragment float4 fadeFS(float4 p [[position]], constant float& amount [[buffer(0)]]) { return float4(amount, 0, 0, 0); }

// ---------- icons and labels ----------
struct Sprite {
    float2 center; float2 half_;
    float2 intake; float angle; float scale;
    float alpha; uint slice; float pull; float agit;
    float4 tint;
    float2 shadowOffset; float shadowAlpha; float pad;
};
struct Grid { float2 q; };
struct SpriteOut { float4 pos [[position]]; float2 uv; float alpha; uint slice [[flat]]; float4 tint; float shadowAlpha; };

static float2 warp(constant Sprite& I, float2 q) {
    float c = cos(I.angle), s = sin(I.angle);
    float2 l = q * I.half_ * 2.0 * I.scale;
    float2 w = I.center + float2(c*l.x - s*l.y, s*l.x + c*l.y);

    // Before it goes: the corners nearest the robot are already reaching for it.
    float2 toI = I.intake - w; float dist = length(toI);
    float reach = saturate(1.0 - dist / 300.0);
    if (dist > 1.0) w += toI / dist * I.agit * reach * reach * 34.0;

    if (I.pull > 0.0) {
        float2 axisV = I.intake - I.center; float axisL = max(length(axisV), 1.0);
        float2 A = axisV / axisL;
        float extent = max(length(I.half_) * I.scale, 1.0);
        float rel = dot(w - I.center, A) / extent;                  // +1 near edge, -1 far edge
        float tv = saturate(I.pull * 1.75 - 0.75 * (0.5 - 0.5 * rel));
        float e = tv * tv * (3.0 - 2.0 * tv);
        float2 d = w - I.intake;
        float along = dot(d, A);
        float2 perp = d - A * along;
        w = I.intake + A * along * (1.0 - e) + perp * pow(1.0 - e, 2.2);
    }
    return w;
}

vertex SpriteOut spriteVS(uint vid [[vertex_id]], uint iid [[instance_id]],
                          constant Grid* grid [[buffer(0)]], constant Sprite* inst [[buffer(1)]],
                          constant Globals& g [[buffer(2)]]) {
    constant Sprite& I = inst[iid];
    float2 q = grid[vid].q;
    SpriteOut o;
    o.pos = toClip(warp(I, q), g.viewport);
    o.uv = q + .5; o.alpha = I.alpha; o.slice = I.slice; o.tint = I.tint; o.shadowAlpha = 0;
    return o;
}
vertex SpriteOut spriteShadowVS(uint vid [[vertex_id]], uint iid [[instance_id]],
                                constant Grid* grid [[buffer(0)]], constant Sprite* inst [[buffer(1)]],
                                constant Globals& g [[buffer(2)]]) {
    constant Sprite& I = inst[iid];
    float2 q = grid[vid].q;
    SpriteOut o;
    o.pos = toClip(warp(I, q) + I.shadowOffset, g.viewport);
    o.uv = q + .5; o.alpha = I.alpha; o.slice = I.slice; o.tint = float4(0); o.shadowAlpha = I.shadowAlpha;
    return o;
}
fragment float4 spriteFS(SpriteOut in [[stage_in]], texture2d_array<float> tex [[texture(0)]]) {
    constexpr sampler s(filter::linear, mip_filter::linear, address::clamp_to_edge, max_anisotropy(8));
    float4 c = tex.sample(s, in.uv, in.slice);
    c.rgb = mix(c.rgb, in.tint.rgb * c.a, in.tint.a);
    return c * in.alpha;
}
fragment float4 spriteShadowFS(SpriteOut in [[stage_in]], texture2d_array<float> tex [[texture(0)]]) {
    constexpr sampler s(filter::linear, mip_filter::linear, address::clamp_to_edge);
    float a = tex.sample(s, in.uv, in.slice, bias(2.2)).a;
    return float4(0, 0, 0, a * in.shadowAlpha * in.alpha);
}

// ---------- particles ----------
struct Part { float2 pos; float size; float ang; float4 color; float chip; float pad0; float pad1; float pad2; };
struct PartOut { float4 pos [[position]]; float2 q; float4 color; float chip; };
vertex PartOut partVS(uint vid [[vertex_id]], uint iid [[instance_id]], constant Part* p [[buffer(0)]], constant Globals& g [[buffer(1)]]) {
    constant Part& I = p[iid];
    float2 q = kQuad[vid]; float c = cos(I.ang), s = sin(I.ang);
    float2 l = q * I.size; if (I.chip > .5) l.y *= .55;
    PartOut o; o.pos = toClip(I.pos + float2(c*l.x - s*l.y, s*l.x + c*l.y), g.viewport);
    o.q = q; o.color = I.color; o.chip = I.chip; return o;
}
fragment float4 partFS(PartOut in [[stage_in]]) {
    float a = in.chip > .5 ? 1.0 : smoothstep(1.0, .35, length(in.q));
    return float4(in.color.rgb * in.color.a * a, in.color.a * a);
}

// ---------- generic quad for the procedural objects ----------
struct QuadU { float2 center; float2 half_; float angle; float pad0; float pad1; float pad2; };
struct QuadOut { float4 pos [[position]]; float2 p; };  // p in world points, relative to centre
vertex QuadOut quadVS(uint vid [[vertex_id]], constant QuadU& u [[buffer(0)]], constant Globals& g [[buffer(1)]]) {
    float2 q = kQuad[vid]; float c = cos(u.angle), s = sin(u.angle);
    float2 l = q * u.half_; float2 r = float2(c*l.x - s*l.y, s*l.x + c*l.y);
    QuadOut o; o.pos = toClip(u.center + r, g.viewport); o.p = r; return o;
}

// Lighting shared by robot and dock. Light comes from upper-left of the screen, always.
static float3 studio(float3 r) {
    // A large softbox up and to the left, a dim strip opposite, dark room otherwise.
    float box = smoothstep(.06, .34, -r.x * .55 - r.y * .78) * smoothstep(1.0, .55, abs(r.x * .78 - r.y * .55));
    float strip = smoothstep(.72, .92, r.x * .5 + r.y * .85) * .22;
    return float3(.16, .17, .19) + float3(1.0, .98, .95) * box * .95 + float3(.75, .85, 1) * strip;
}
struct Mat { float3 albedo; float gloss; float metal; float emis; float3 emisColor; };
static float3 shade(float3 N, Mat m, float ao) {
    float3 L = normalize(float3(-.46, -.62, .64));
    float diff = saturate(dot(N, L) * .78 + .22);
    float sky = .52 + .48 * N.z;
    float3 col = m.albedo * (diff * float3(1.02, 1, .97) * .84 + sky * float3(.40, .42, .46));
    float3 V = float3(0, 0, 1);
    float3 R = reflect(-V, N);
    float fres = pow(1 - saturate(N.z), 3.0);
    float3 H = normalize(L + V);
    float sp = pow(saturate(dot(N, H)), mix(14.0, 180.0, m.gloss)) * mix(.12, 1.5, m.gloss);
    col += sp;
    float refl = mix(.03, .42, m.gloss) + fres * .5;
    col = mix(col, studio(R) * mix(float3(1), m.albedo * 1.3, m.metal), refl * mix(.5, 1.0, m.gloss));
    col *= ao;
    col += m.emisColor * m.emis;
    return col;
}
static inline float sdBox(float2 p, float2 b, float r) { float2 d = abs(p) - b + r; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r; }
static inline float groove(float d, float w) { return smoothstep(w, 0.0, abs(d)); }

// ---------- robot ----------
struct RobotU {
    float2 center; float radius; float heading;
    float lidar; float bumper; float binFill; float ledPulse;
    float4 led;
    float alpha; float shakeX; float shakeY; float lift;
};

struct RobotSurf { float h; int mat; float ao; };

static float robotHeight(float2 l, constant RobotU& u, thread int& mat, thread float& ao) {
    float r = length(l);
    float ang = atan2(l.y, l.x);
    // Body: a fat rounded shell with a very slight crown.
    float e = saturate((1.0 - r) / .14);
    float h = .30 * sqrt(1.0 - (1.0 - e) * (1.0 - e)) + .035 * (1.0 - r * r);
    mat = 0; ao = 1;

    // Bumper arc wraps the front; it physically moves in when struck.
    float inner = .845 - u.bumper * .018;
    bool front = abs(ang) < 1.85;
    float seam = groove(r - inner, .012) * (front ? 1.0 : 0.0);
    float endA = abs(abs(ang) - 1.85) * r;
    float seamEnd = groove(endA, .012) * step(inner, r) * step(r, .99);
    h -= .05 * max(seam, seamEnd);
    ao *= 1 - .72 * max(seam, seamEnd);
    if (front && r > inner) {
        mat = 1;
        // Dark sensor window along the leading edge.
        if (abs(ang) < .72 && r > .905 && r < .977) mat = 2;
    }

    // Graphite top plate, glossy, inset slightly.
    float plate = r - .70;
    float plateRim = groove(plate, .010);
    if (plate < 0) { h -= .010; h += .085 * (1.0 - (r*r)/(.70*.70)); mat = 3; }
    ao *= 1 - .45 * plateRim;

    // Hairline cut for the bin lid at the rear.
    float2 lidP = l - float2(-.46, 0);
    float lid = sdBox(lidP, float2(.135, .38), .07);
    if (plate < 0) { h -= .010 * groove(lid, .007); ao *= 1 - .4 * groove(lid, .006); }

    // Button pill.
    float2 pp = l - float2(-.17, 0);
    float pill = sdBox(pp, float2(.04, .17), .04);
    if (pill < 0) { mat = 4; h += .005; }
    ao *= 1 - .4 * groove(pill, .008);
    float b1 = length(l - float2(-.17, .085)) - .03, b2 = length(l - float2(-.17, -.085)) - .03;
    if (b1 < 0 || b2 < 0) { mat = 5; h -= .004; }

    // Lidar turret: the one thing that stands proud of the body.
    float2 tp = l - float2(.27, 0);
    float td = length(tp);
    float tR = .265;
    float te = saturate((tR - td) / .05);
    h += .11 * sqrt(1.0 - (1.0 - te) * (1.0 - te));
    if (td < tR) {
        mat = 6;
        if (td < .185) { mat = 7; h += .045 * (1.0 - td*td/(.185*.185)); }
    }
    ao *= 1 - .55 * smoothstep(tR + .05, tR, td) * step(tR, td);
    return h;
}

vertex QuadOut robotVS(uint vid [[vertex_id]], constant RobotU& u [[buffer(0)]], constant Globals& g [[buffer(1)]]) {
    float2 q = kQuad[vid] * u.radius * 1.03;
    QuadOut o; o.pos = toClip(u.center + q + float2(u.shakeX, u.shakeY), g.viewport); o.p = q; return o;
}

fragment float4 robotFS(QuadOut in [[stage_in]], constant RobotU& u [[buffer(0)]], texture2d<float> logo [[texture(0)]]) {
    float2 wp = in.p / u.radius;
    float rr = length(wp);
    float px = fwidth(in.p.x) / u.radius;
    float edge = smoothstep(1.0, 1.0 - px * 1.5, rr);
    if (edge <= 0) discard_fragment();
    float c = cos(-u.heading), s = sin(-u.heading);
    float2 l = float2(c*wp.x - s*wp.y, s*wp.x + c*wp.y);

    int mat; float ao; int mm; float aa;
    const float E = .0035;
    float h0 = robotHeight(l, u, mat, ao);
    float hx = robotHeight(l + float2(E, 0), u, mm, aa) - robotHeight(l - float2(E, 0), u, mm, aa);
    float hy = robotHeight(l + float2(0, E), u, mm, aa) - robotHeight(l - float2(0, E), u, mm, aa);
    (void)h0;
    float2 g = float2(hx, hy) / (2 * E);
    // Back to world space so the light never rotates with the robot.
    float cw = cos(u.heading), sw = sin(u.heading);
    float2 gw = float2(cw*g.x - sw*g.y, sw*g.x + cw*g.y);
    float3 N = normalize(float3(-gw * 1.05, 1));

    Mat m; m.albedo = float3(.90, .90, .88); m.gloss = .28; m.metal = 0; m.emis = 0; m.emisColor = float3(0);
    switch (mat) {
        case 1: m.albedo = float3(.84, .84, .83); m.gloss = .20; break;       // bumper
        case 2: m.albedo = float3(.018, .019, .022); m.gloss = 1; break;      // sensor window
        case 3: m.albedo = float3(.055, .058, .065); m.gloss = .72; break;    // top plate
        case 4: m.albedo = float3(.035, .035, .04); m.gloss = .4; break;      // pill
        case 5: m.albedo = float3(.72, .73, .75); m.gloss = .5; m.metal = .6; break; // buttons
        case 6: m.albedo = float3(.62, .62, .62); m.gloss = .16; break;       // turret skirt
        case 7: m.albedo = float3(.015, .016, .02); m.gloss = 1; break;       // turret dome
    }

    // Spinning laser head, seen through the smoked dome.
    float2 tp = l - float2(.27, 0);
    float td = length(tp);
    float3 extra = float3(0);
    if (mat == 7) {
        float2 gl = float2(cos(u.lidar), sin(u.lidar)) * .105;
        extra += float3(.45, .85, 1.0) * exp(-dot(tp - gl, tp - gl) * 1500.0) * .9;
        extra += float3(.45, .85, 1.0) * exp(-dot(tp + gl * .9, tp + gl * .9) * 2600.0) * .25;
    }
    // Status ring around the turret.
    float ring = groove(td - .225, .009);
    float pulse = .62 + .38 * sin(u.ledPulse * 3.0);
    m.emis = ring * pulse * 1.15;
    m.emisColor = u.led.rgb;
    // Button glyph glow.
    float glow1 = exp(-pow(length(l - float2(-.17, .085)) / .022, 2));
    extra += u.led.rgb * glow1 * .5;

    // Logotype on the graphite plate.
    float2 luv = float2((l.y + .31) / .62, (-l.x - .42) / .218);
    if (all(luv > 0.0) && all(luv < 1.0) && length(l) < .70) {
        constexpr sampler ss(filter::linear, address::clamp_to_edge);
        float la = logo.sample(ss, luv).a;
        m.albedo = mix(m.albedo, float3(.62, .64, .68), la * .85);
    }

    float3 col = shade(N, m, ao) + extra;
    // Grounding: the rim rolls off a touch into its own shadow.
    col *= mix(.7, 1.0, smoothstep(1.0, .90, rr));
    return float4(col * edge * u.alpha, edge * u.alpha);
}

// Rotating side brush; blurs into a disc as it speeds up.
struct BrushU { float2 center; float radius; float angle; float speed; float alpha; float sign_; float pad; };
vertex QuadOut brushVS(uint vid [[vertex_id]], constant BrushU& u [[buffer(0)]], constant Globals& g [[buffer(1)]]) {
    float2 q = kQuad[vid] * u.radius;
    QuadOut o; o.pos = toClip(u.center + q, g.viewport); o.p = q; return o;
}
fragment float4 brushFS(QuadOut in [[stage_in]], constant BrushU& u [[buffer(0)]]) {
    float2 p = in.p / u.radius;
    float r = length(p);
    if (r > 1) discard_fragment();
    float a = atan2(p.y, p.x) * u.sign_ - u.angle;
    float blur = saturate(u.speed / 26.0);
    float total = 0;
    for (int i = 0; i < 3; i++) {
        // Bristle arms curve backwards against the spin.
        float armA = a + i * 2.0944 - r * .55;
        armA = atan2(sin(armA), cos(armA));
        float w = mix(.035, .46, blur) + .07 * (1 - r);
        float m = exp(-armA * armA / (w * w));
        float fray = .72 + .28 * sin(armA * 60.0 + r * 3.0);
        total += m * fray;
    }
    float fade = smoothstep(1.0, .78, r);
    float alpha = saturate(total) * fade * mix(.96, .52, blur);
    float hub = smoothstep(.17, .13, r);
    float3 bristle = mix(float3(.10, .10, .11), float3(.62, .62, .66), r * r);
    float3 col = mix(bristle, float3(.04), hub);
    float aOut = max(alpha, hub) * u.alpha;
    return float4(col * aOut, aOut);
}

// Soft drop shadows.
struct ShadowU { float2 center; float radius; float strength; float2 offset; float blur; float kind; float2 halfBox; float angle; float pad; };
vertex QuadOut shadowVS(uint vid [[vertex_id]], constant ShadowU& u [[buffer(0)]], constant Globals& g [[buffer(1)]]) {
    float2 ext = u.kind > .5 ? (u.halfBox + u.blur * 2 + 6) : float2(u.radius + u.blur * 2 + 6);
    float c = cos(u.angle), s = sin(u.angle);
    float2 l = kQuad[vid] * ext; float2 q = float2(c*l.x - s*l.y, s*l.x + c*l.y);
    QuadOut o; o.pos = toClip(u.center + u.offset + q, g.viewport); o.p = q; return o;
}
fragment float4 shadowFS(QuadOut in [[stage_in]], constant ShadowU& u [[buffer(0)]]) {
    float d;
    if (u.kind > .5) {
        float c = cos(-u.angle), s = sin(-u.angle);
        float2 l = float2(c*in.p.x - s*in.p.y, s*in.p.x + c*in.p.y);
        d = sdBox(l, u.halfBox, u.radius);
    } else { d = length(in.p) - u.radius; }
    float a = 1 - smoothstep(-u.blur, u.blur, d);
    a = a * a * (3 - 2 * a);
    return float4(0, 0, 0, a * u.strength);
}

// ---------- dock ----------
struct DockU { float2 center; float radius; float alpha; float bag; float glow; float ledPulse; float pad0; float4 led; };
vertex QuadOut dockVS(uint vid [[vertex_id]], constant QuadU& u [[buffer(0)]], constant Globals& g [[buffer(1)]]) {
    float2 q = kQuad[vid] * u.half_;
    QuadOut o; o.pos = toClip(u.center + q, g.viewport); o.p = q; return o;
}
static float dockHeight(float2 d, thread int& mat, thread float& ao) {
    // d: x sideways, y outward from the back wall, in robot radii, origin at docked robot centre.
    ao = 1; mat = 0;
    float rampD = sdBox(d - float2(0, -.20), float2(1.14, .98), .24);
    float h = 0;
    if (rampD < 0) {
        float e = saturate(-rampD / .05);
        h = .05 * sqrt(1 - (1-e)*(1-e));
        mat = 1;
        // Non-slip ribs fade towards the lip.
        float rib = sin(d.y * 90.0) * smoothstep(.72, .1, d.y) * .0022 * smoothstep(-.06, -.2, rampD);
        h += rib;
    }
    // Charge contacts.
    float2 cp = float2(abs(d.x) - .42, d.y + .78);
    float contact = sdBox(cp, float2(.13, .04), .03);
    if (contact < 0 && rampD < 0) { mat = 2; h += .012; }

    // Tower behind the robot.
    float2 tc = d - float2(0, -1.50);
    float tower = sdBox(tc, float2(1.26, .58), .30);
    if (tower < 0) {
        float e = saturate(-tower / .13);
        h = .40 * sqrt(1 - (1-e)*(1-e));
        mat = 3;
        // Bag window.
        float win = sdBox(d - float2(0, -1.50), float2(.76, .29), .11);
        if (win < 0) { mat = 4; h = .34 + .0 * win; }
        ao *= 1 - .6 * groove(win, .014);
        // Vents at each end.
        float2 vp = float2(abs(d.x) - 1.05, d.y + 1.50);
        if (abs(vp.x) < .09 && abs(vp.y) < .26) {
            float slit = smoothstep(.2, .7, sin(vp.y * 62.0));
            h -= .014 * slit; ao *= 1 - .55 * slit;
        }
    }
    ao *= 1 - .5 * groove(tower, .02);
    return h;
}
fragment float4 dockFS(QuadOut in [[stage_in]], constant DockU& u [[buffer(0)]], texture2d<float> logo [[texture(0)]]) {
    // Dock faces up the screen: outward = -y.
    float2 d = float2(in.p.x, -in.p.y) / u.radius;
    int mat, mm; float ao, aa;
    const float E = .004;
    float h0 = dockHeight(d, mat, ao);
    float hx = dockHeight(d + float2(E,0), mm, aa) - dockHeight(d - float2(E,0), mm, aa);
    float hy = dockHeight(d + float2(0,E), mm, aa) - dockHeight(d - float2(0,E), mm, aa);
    if (h0 <= 0.0001) discard_fragment();
    float2 g = float2(hx, -hy) / (2 * E);
    float3 N = normalize(float3(-g, 1));
    Mat m; m.albedo = float3(.9, .9, .88); m.gloss = .3; m.metal = 0; m.emis = 0; m.emisColor = float3(0);
    float3 extra = float3(0);
    if (mat == 1) { m.albedo = float3(.10, .105, .115); m.gloss = .18; }
    if (mat == 2) { m.albedo = float3(.95, .78, .42); m.gloss = .85; m.metal = 1; }
    if (mat == 4) {
        // Dust bag behind smoked glass: a pillow that grows and glows as it fills.
        float2 w = d - float2(0, -1.50);
        float fill = u.bag;
        float bx = .72 * (.3 + .7 * sqrt(fill));
        float bag = sdBox(w, float2(bx, .25), .10);
        float inside = smoothstep(.01, -.01, bag);
        float puff = sqrt(saturate(-bag / .13));
        float3 base = float3(.04, .045, .05);
        float wr = sin(w.x * 21.0 + w.y * 11.0) * sin(w.y * 37.0 - w.x * 9.0);
        float3 bagc = mix(float3(.34, .36, .39), float3(.72, .73, .74), puff) * (1 + .04 * wr);
        m.albedo = mix(base, bagc, inside * .85);
        m.gloss = .9;
        extra += float3(.25, .6, .9) * u.glow * .24 * (.3 + inside);
    }
    // LED strip along the tower's front edge.
    float2 lp = d - float2(0, -.98);
    if (abs(lp.x) < .34 && abs(lp.y) < .012 + .004) {
        m.emis = (.65 + .35 * sin(u.ledPulse * 2.4)) * 1.1;
        m.emisColor = u.led.rgb;
    }
    float3 col = shade(N, m, ao) + extra;
    float2 luv = float2((d.x + .34) / .68, (d.y + 2.03 - .04) / .19);
    if (all(luv > 0.0) && all(luv < 1.0)) {
        constexpr sampler ss(filter::linear, address::clamp_to_edge);
        float la = logo.sample(ss, float2(luv.x, 1 - luv.y)).a;
        col = mix(col, float3(.32, .33, .35), la * .85);
    }
    float a = u.alpha * smoothstep(0.0, .012, h0);
    return float4(col * a, a);
}

// ---------- lidar sweep ----------
struct LidarU { float2 center; float radius; float angle; float alpha; float pad0; float pad1; float pad2; };
vertex QuadOut lidarVS(uint vid [[vertex_id]], constant LidarU& u [[buffer(0)]], constant Globals& g [[buffer(1)]]) {
    float2 q = kQuad[vid] * u.radius;
    QuadOut o; o.pos = toClip(u.center + q, g.viewport); o.p = q; return o;
}
fragment float4 lidarFS(QuadOut in [[stage_in]], constant LidarU& u [[buffer(0)]]) {
    float r = length(in.p) / u.radius;
    if (r > 1) discard_fragment();
    float a = atan2(in.p.y, in.p.x) - u.angle;
    a = fmod(-a + 62.83185 * 4, 6.283185);           // radians behind the beam
    float tail = exp(-a * 2.4) * .26;
    float line = exp(-a * a * 1800.0) * .55;
    float rings = (smoothstep(.012, 0.0, abs(fract(r * 9.0) - .5) / 9.0)) * .05 * exp(-a * 1.2);
    float fall = pow(1 - r, .35);
    float i = (tail + line + rings) * fall * u.alpha;
    float3 c = float3(.32, .86, 1.0);
    return float4(c * i, i * .55);
}


// ---------- the cat: designed like the robot, a soft matte object rather than an imitation of fur ----------
struct CatU {
    float2 center; float2 half_;
    float4 spine[4];      // x, y, radius
    float4 head;          // x, y, radius
    float2 headDir; float2 shadowOffset;
    float4 tail[9];
    float4 paws[8];
    float4 base; float4 stripe; float4 belly;
    float whitePaws; float pattern; float alpha; float eyesOpen;
    float earL; float earR; float pad0; float pad1;
};

static inline float catSmin(float a, float b, float k) { float h = saturate(0.5 + 0.5 * (b - a) / k); return mix(b, a, h) - k * h * (1 - h); }
static inline float catCone(float2 p, float2 a, float2 b, float ra, float rb) {
    float2 ba = b - a; float h = saturate(dot(p - a, ba) / max(dot(ba, ba), 1e-3));
    return length(p - a - ba * h) - mix(ra, rb, h);
}
static inline float catHash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
static float catNoise(float2 p) {
    float2 i = floor(p), f = fract(p); f = f * f * (3 - 2 * f);
    return mix(mix(catHash(i), catHash(i + float2(1, 0)), f.x), mix(catHash(i + float2(0, 1)), catHash(i + float2(1, 1)), f.x), f.y);
}
static inline float catTri(float2 p, float2 a, float2 b, float2 c) {
    float2 e0 = b - a, e1 = c - b, e2 = a - c, v0 = p - a, v1 = p - b, v2 = p - c;
    float2 pq0 = v0 - e0 * saturate(dot(v0, e0) / dot(e0, e0));
    float2 pq1 = v1 - e1 * saturate(dot(v1, e1) / dot(e1, e1));
    float2 pq2 = v2 - e2 * saturate(dot(v2, e2) / dot(e2, e2));
    float s = sign(e0.x * e2.y - e0.y * e2.x);
    float2 d = min(min(float2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)), float2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))), float2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
    return -sqrt(d.x) * sign(d.y);
}

struct CatParts { float body, head, skull, ear, earInner, tail, paw, sock, leg, torso, all; };

static CatParts catMap(float2 p, constant CatU& u) {
    CatParts c;
    float b = 1e5;
    for (int i = 0; i < 3; i++) b = catSmin(b, catCone(p, u.spine[i].xy, u.spine[i + 1].xy, u.spine[i].z, u.spine[i + 1].z), 12);
    float t = 1e5;
    for (int i = 0; i < 8; i++) t = catSmin(t, catCone(p, u.tail[i].xy, u.tail[i + 1].xy, u.tail[i].z, u.tail[i + 1].z), 7);
    float pw = 1e5, leg = 1e5, sock = 1e5;
    for (int i = 0; i < 8; i++) if (u.paws[i].z > 0) {
        if (u.paws[i].w > 0.5) {
            // Not a foot: a shoulder or haunch, shaping the body's outline.
            b = catSmin(b, length(p - u.paws[i].xy) - u.paws[i].z, 14);
            continue;
        }
        // A slim leg that comes out from under the body, ending in a slightly long foot.
        float2 joint = i < 2 ? u.spine[2].xy : u.spine[0].xy;
        float2 toe = normalize(u.paws[i].xy - joint) * u.paws[i].z * 0.45;
        pw = min(pw, catCone(p, u.paws[i].xy - toe, u.paws[i].xy + toe, u.paws[i].z, u.paws[i].z * 0.9));
        sock = min(sock, length(p - u.paws[i].xy - toe * 0.9) - u.paws[i].z);
        leg = min(leg, catCone(p, joint, u.paws[i].xy, u.paws[i].z * 1.2, u.paws[i].z * 0.85));
    }
    float2 hc = u.head.xy, d = u.headDir, s = float2(-d.y, d.x);
    float r = u.head.z;
    float2 q = p - hc;
    float2 lq = float2(dot(q, d), dot(q, s)) / r;
    // A cat's skull from above: wide at the cheeks, tapering to a short muzzle.
    float taper = 1 + 0.42 * saturate(lq.x + 0.05) * saturate(lq.x + 0.05);
    float skull = (length(float2(lq.x * 0.90, lq.y * (0.97 * taper))) - 1) * r;
    float ear = 1e5, inner = 1e5;
    for (int k = -1; k <= 1; k += 2) {
        float fk = float(k);
        float tw = k < 0 ? u.earL : u.earR;
        // Upright, slightly forward, set wide: small enough to feel like a cat, not a mascot.
        float2 tip = hc + (d * (-0.30 + tw * 0.26) + s * fk * (1.30 - tw * 0.1)) * r;
        float2 a = hc + (d * 0.16 + s * fk * 0.70) * r, b2 = hc + (-d * 0.68 + s * fk * 0.42) * r;
        ear = min(ear, catTri(p, a, b2, tip) - 2.8);
        float2 ic = (a + b2 + tip) / 3;
        inner = min(inner, catTri(p, mix(a, ic, 0.34), mix(b2, ic, 0.34), mix(tip, ic, 0.3)));
    }
    c.body = b; c.leg = leg; c.skull = skull; c.head = catSmin(skull, ear, 5); c.ear = ear; c.earInner = inner; c.tail = t; c.paw = pw; c.sock = sock;
    float all = catSmin(b, c.head, 11);       // a neck, rather than a ball resting on a body
    all = catSmin(all, t, 6);
    c.torso = all;
    c.all = catSmin(all, min(leg, pw), 3);
    return c;
}

static inline float catDome(float d, float r) { float e = saturate(-d / r); return r * sqrt(1 - (1 - e) * (1 - e)); }

static inline float catSmax(float a, float b, float k) { return -catSmin(-a, -b, k); }

static float catHeight(float2 p, constant CatU& u) {
    CatParts c = catMap(p, u);
    // One continuous form with soft swellings added on top, so no part has a seam where it joins.
    float h = catDome(c.torso, 30) * 0.9;
    h += 4 * saturate(-c.body / 18);
    // Legs are their own low forms, which the body sits over.
    h = max(h, catDome(min(c.leg, c.paw), 8) * 0.85 + 1.2 * saturate(-c.paw / 6));
    float2 q = p - u.head.xy, d = u.headDir, s = float2(-d.y, d.x);
    float2 lq = float2(dot(q, d), dot(q, s)) / u.head.z;
    float skullBump = saturate(-c.skull / (u.head.z * 0.5));
    h += skullBump * skullBump * (3 - 2 * skullBump) * 9;
    h += skullBump * 2.6 * exp(-(lq.y * lq.y * 8 + (lq.x - 0.55) * (lq.x - 0.55) * 5));
    h += skullBump * 1.5 * exp(-((abs(lq.y) - 0.4) * (abs(lq.y) - 0.4) * 16 + (lq.x - 0.12) * (lq.x - 0.12) * 14));
    // Ears: raised at the rim, cupped in the middle.
    float earBump = saturate(-c.ear / 4);
    // Where an ear crosses the shoulder it stays low, so it can't read as a fold in the neck.
    float overBody = 1 - 0.65 * saturate(-c.body / 10);
    h += earBump * earBump * (3 - 2 * earBump) * 4 * (1 - skullBump) * overBody;
    h -= 2.2 * saturate(-c.earInner / 3.5) * (1 - skullBump) * overBody;
    return h;
}

vertex QuadOut catVS(uint vid [[vertex_id]], constant CatU& u [[buffer(0)]], constant Globals& g [[buffer(1)]]) {
    float2 q = kQuad[vid] * u.half_;
    QuadOut o; o.pos = toClip(u.center + q, g.viewport); o.p = u.center + q; return o;
}

fragment float4 catFS(QuadOut in [[stage_in]], constant CatU& u [[buffer(0)]]) {
    float2 p = in.p;
    CatParts c = catMap(p, u);
    if (c.all > 34 + 1.7 * length(u.shadowOffset)) discard_fragment();
    float px = max(fwidth(p.x), 0.001);

    // Two-part shadow, built the same way as the robot's.
    float tight = 1 - smoothstep(-3.0, 8.0, catMap(p - float2(2, 3.5), u).all);
    float wide = 1 - smoothstep(-16.0, 24.0, catMap(p - u.shadowOffset * 1.7, u).all);
    float sh = 1 - (1 - tight * 0.40) * (1 - wide * 0.27);

    float edge = 1 - smoothstep(-px, px, c.all);
    if (edge <= 0 && sh <= 0.003) discard_fragment();

    const float E = 1.2;
    float hx = catHeight(p + float2(E, 0), u) - catHeight(p - float2(E, 0), u);
    float hy = catHeight(p + float2(0, E), u) - catHeight(p - float2(0, E), u);
    // Only a hint of nap: soft-touch, not fur.
    float grainX = catNoise(p * 2.4) - 0.5, grainY = catNoise(p * 2.4 + 37.0) - 0.5;
    float3 N = normalize(float3(-(hx / (2 * E)) * 0.72 + grainX * 0.05, -(hy / (2 * E)) * 0.72 + grainY * 0.05, 1));

    // Where we are along and across the spine, for markings that follow the body.
    float along = 0, across = 1e5, soFar = 0, radiusHere = 40, flank = 1;
    for (int i = 0; i < 3; i++) {
        float2 a = u.spine[i].xy, ba = u.spine[i + 1].xy - a; float l = max(length(ba), 1e-3);
        float h = saturate(dot(p - a, ba) / (l * l));
        float d = length(p - a - ba * h);
        if (d < across) {
            across = d; along = soFar + h * l; radiusHere = mix(u.spine[i].z, u.spine[i + 1].z, h);
            flank = ba.x * (p.y - a.y) - ba.y * (p.x - a.x) >= 0 ? 1 : -1;
        }
        soFar += l;
    }
    float side = saturate(across / max(radiusHere, 1.0f));

    // Short coat: tone follows the body, and fine streaks run along it. Barely visible, but it's what
    // separates this from a smooth toy.
    float2 hairUV = float2(along * 0.55, across * 3.1);
    float hair = (catNoise(hairUV) - 0.5) + 0.5 * (catNoise(hairUV * 2.3 + 11.0) - 0.5);
    float3 col = u.base.rgb;
    int pat = int(u.pattern + 0.5);
    float tiny = px * 1.2;
    if (c.body < 0 || c.paw < 0 || c.leg < 0) {
        // Natural countershading: darker down the spine, paler on the flanks.
        col *= mix(0.86, 1.08, smoothstep(0.1, 0.95, side));
        if (pat == 1) {
            // Mackerel tabby: narrow, soft-edged bars that wander and fade out towards the belly.
            // Each flank has its own bars, unevenly spaced and broken up: matched pairs meeting at the
            // spine read as a ribcage.
            float warp = (catNoise(p * 0.055) - 0.5) * 11.0;
            float v = along + side * side * 5 + warp + flank * 9.0;
            float wave = 0.5 + 0.5 * sin(v * 0.40 + 1.8 * catNoise(float2(along * 0.045, flank * 7.0)));
            float broken = smoothstep(0.28, 0.52, catNoise(float2(along * 0.09 + flank * 13.0, side * 3.2)));
            // Markings stop before the spine ends, where its distance field would turn them into rings.
            float onSpine = smoothstep(6.0, 22.0, along) * (1 - smoothstep(soFar - 8, soFar + 2, along));
            float bars = smoothstep(0.62, 0.94, wave) * (1 - smoothstep(0.55, 0.98, side)) * smoothstep(0.1, 0.28, side) * onSpine * broken;
            col = mix(col, u.stripe.rgb, bars * 0.58);
            float dorsal = (1 - smoothstep(0.02, 0.16, side)) * onSpine;
            col = mix(col, u.stripe.rgb, dorsal * 0.5);
        }
        col *= 1 + hair * 0.04 + (catNoise(p * 0.11) - 0.5) * 0.045;
    }
    if (c.tail < 0 && c.body >= 0) {
        float fromTip = length(p - u.tail[8].xy);
        if (pat == 1) {
            float wave = 0.5 + 0.5 * sin(fromTip * 0.27);
            col = mix(col, u.stripe.rgb, smoothstep(0.55, 0.95, wave) * 0.75);
            col = mix(col, u.stripe.rgb, (1 - smoothstep(8.0, 20.0, fromTip)) * 0.8);
        }
        if (pat == 2) col = mix(col, u.belly.rgb, 1 - smoothstep(7.0, 15.0, fromTip));
    }
    if (c.head >= 0 && c.body >= 0) col = mix(col, u.belly.rgb, u.whitePaws * 0.9 * (1 - smoothstep(-2.5, 1.5, c.sock)));

    // The face.
    float2 q = p - u.head.xy, d = u.headDir, s = float2(-d.y, d.x);
    float2 lq = float2(dot(q, d), dot(q, s)) / u.head.z;
    if (c.head < 0) {
        col = u.base.rgb;
        col *= mix(0.9, 1.04, smoothstep(0.1, 1.0, abs(lq.y)));
        col *= 1 + (catNoise(float2(lq.x * 9, lq.y * 22)) - 0.5) * 0.06;
        if (pat == 1) {
            // The tabby's forehead lines, soft and fine.
            float lines = 0;
            for (int k = -1; k <= 1; k++) {
                float cx = float(k) * (0.14 + 0.09 * saturate(-lq.x));
                float along2 = smoothstep(-0.95, -0.72, lq.x) * (1 - smoothstep(-0.1, 0.2, lq.x));
                lines = max(lines, (1 - smoothstep(0.018, 0.075, abs(lq.y - cx))) * along2);
            }
            col = mix(col, u.stripe.rgb, lines * 0.78);
        }
        // Muzzle fades in rather than having an edge.
        float muz = length(float2((lq.x - 0.92) * 1.5, lq.y * 1.75));
        float muzzle = 1 - smoothstep(0.2, 0.62, muz);
        if (pat == 2) {
            float blaze = (1 - smoothstep(0.0, 0.11, abs(lq.y) - (0.006 + 0.17 * pow(saturate((lq.x - 0.1) * 1.05), 1.8)))) * smoothstep(-0.12, 0.28, lq.x);
            muzzle = max(muzzle, blaze);
        }
        col = mix(col, u.belly.rgb, muzzle * (pat == 2 ? 0.92 : 0.72));
        if (c.earInner < 0 && c.skull >= 0) {
            float deep = saturate(-c.earInner / 3.5);
            col = mix(col, u.base.rgb * 0.62 + float3(0.20, 0.12, 0.11), 0.85 * deep);
        }
    }

    float ao = 1;
    // The body shades the legs it stands over.
    if (c.torso >= 0 && min(c.leg, c.paw) < 0) ao *= mix(0.74, 1.0, smoothstep(0.0, 7.0, c.torso));
    // Head and tail sit on the body and shade it softly.
    if (c.head >= 0 && c.body < 0) ao *= 1 - 0.20 * (1 - smoothstep(-6.0, 22.0, catMap(p - float2(2, 5), u).skull));
    // Soft-touch, not plastic: the same light as the robot, but with a velvety sheen at the edges
    // where plastic would darken. That's what stops the outline reading as drawn.
    float3 L = normalize(float3(-.46, -.62, .64));
    float diff = saturate(dot(N, L) * .72 + .28);
    float sky = .52 + .48 * N.z;
    float3 lit = col * (diff * float3(1.02, 1, .97) * .86 + sky * float3(.36, .38, .42)) * ao;
    float rim = pow(1 - saturate(N.z), 2.2);
    float lum = dot(col, float3(0.3, 0.59, 0.11));
    // Dark coats need more sheen, or their shape disappears.
    lit += mix(col, float3(1), 0.25 + 0.25 * (1 - lum)) * rim * (0.17 + 0.42 * (1 - lum) * (1 - lum));
    float3 H = normalize(L + float3(0, 0, 1));
    lit += pow(saturate(dot(N, H)), 12.0) * (0.04 + 0.075 * (1 - lum));

    if (c.head < 0) {
        // Seen from above, you only catch the top of a cat's eyes: two narrow, slanted glints.
        for (int k = -1; k <= 1; k += 2) {
            float fk = float(k);
            float2 c0 = float2(0.36, fk * 0.42);
            float2 r0 = lq - c0;
            float ang = fk * 0.5, ca = cos(ang), sa = sin(ang);
            float2 e = float2(ca * r0.x - sa * r0.y, sa * r0.x + ca * r0.y) / float2(0.052, 0.135);
            if (u.eyesOpen > 0.5) {
                float eye = 1 - smoothstep(0.82, 1.12, length(e));
                lit = mix(lit, float3(0.05, 0.04, 0.035), eye * 0.92);
                float glint = 1 - smoothstep(0.08, 0.3, length(e - float2(0.1, -0.3)));
                lit = mix(lit, float3(0.75), glint * eye * 0.45);
            } else {
                float eye = 1 - smoothstep(0.35, 0.8, abs(e.x) + e.y * e.y * 0.4);
                lit = mix(lit, lit * 0.55, eye * (1 - smoothstep(0.85, 1.1, abs(e.y))));
            }
        }
        float2 nq = float2((lq.x - 1.02) / 0.035, lq.y / 0.06);
        lit = mix(lit, float3(0.46, 0.31, 0.29), 1 - smoothstep(0.7, 1.15, length(nq)));
    }
    // Whiskers: fine, short and nearly invisible until you look.
    float wh = 0;
    for (int k = -1; k <= 1; k += 2) for (int w = 0; w < 2; w++) {
        float fw = float(w) - 0.5;
        float2 a = float2(0.72 + fw * 0.08, float(k) * 0.34), b = a + float2(fw * 0.34 - 0.08, float(k) * 0.74);
        float2 pa = lq - a, ba = b - a; float h = saturate(dot(pa, ba) / dot(ba, ba));
        float dw = length(pa - ba * h);
        wh = max(wh, (1 - smoothstep(0.004, 0.014, dw)) * (1 - h));
    }

    float a = edge * u.alpha;
    float3 outCol = lit * a;
    float wa = wh * u.alpha * 0.32;
    outCol = outCol * (1 - wa) + float3(0.93) * wa;
    float alphaOut = max(a, wa);
    float shadow = sh * u.alpha * (1 - alphaOut);
    return float4(outCol, alphaOut + shadow);
}
