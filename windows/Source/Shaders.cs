namespace RoboDust;

/// The Mac's Metal shaders, translated to GLSL 3.30 line by line. Same maths, same look.
public static class Shaders
{
    const string Head = @"#version 330
#define saturate(x) clamp((x), 0.0, 1.0)
const vec2 kQuad[6] = vec2[6](vec2(-1.0,-1.0), vec2(1.0,-1.0), vec2(-1.0,1.0), vec2(-1.0,1.0), vec2(1.0,-1.0), vec2(1.0,1.0));
uniform vec2 viewport;
uniform vec2 origin;   // this display's top-left in the shared world
vec4 toClip(vec2 w) { vec2 p = w - origin; return vec4(p.x / viewport.x * 2.0 - 1.0, 1.0 - p.y / viewport.y * 2.0, 0.0, 1.0); }
";

    // ---------- wallpaper + freshly vacuumed lanes ----------
    public const string BgVS = Head + @"
out vec2 uv;
void main() { vec2 q = kQuad[gl_VertexID]; gl_Position = vec4(q, 0.0, 1.0); uv = vec2(q.x * .5 + .5, .5 - q.y * .5); }";
    public const string BgFS = Head + @"
in vec2 uv; out vec4 o;
uniform sampler2D wall; uniform sampler2D track;
void main() {
    vec4 c = texture(wall, uv);
    // Wide blur: the nap should read as a soft sheen with no visible edge.
    vec2 texel = 1.0 / vec2(textureSize(track, 0));
    vec2 tuv = vec2(uv.x, 1.0 - uv.y);
    vec4 t = vec4(0.0); float wsum = 0.0;
    for (int y = -2; y <= 2; y++) for (int x = -2; x <= 2; x++) {
        float w = exp(-float(x*x + y*y) * 0.32);
        t += texture(track, tuv + vec2(x, y) * texel * 9.0) * w; wsum += w;
    }
    t /= wsum;
    float presence = t.r;
    if (presence > 0.004) {
        // Carpet-style nap: lanes driven in opposite directions catch the light differently.
        float nap = (t.g - .5) * 2.0;
        c.rgb += presence * nap * 0.017 * (0.5 + c.rgb);
    }
    o = vec4(c.rgb, 1.0);
}";

    // Track buffer stamps: r presence, g direction, b wheels.
    public const string StampVS = Head + @"
uniform vec2 center; uniform vec2 half_; uniform float angle;
out vec2 q;
void main() {
    q = kQuad[gl_VertexID]; float c = cos(angle), s = sin(angle);
    vec2 l = q * half_; vec2 w = center + vec2(c*l.x - s*l.y, s*l.x + c*l.y);
    gl_Position = toClip(w);
}";
    public const string StampFS = Head + @"
in vec2 q; out vec4 o; uniform float dir;
void main() {
    float side = abs(q.y);
    if (side > .98 || abs(q.x) > .98) discard;
    float wheels = smoothstep(.09, .03, abs(side - .62));
    o = vec4(1.0, dir, wheels, 1.0);
}";
    public const string FadeVS = Head + @"void main() { gl_Position = vec4(kQuad[gl_VertexID], 0.0, 1.0); }";
    public const string FadeFS = Head + @"out vec4 o; uniform float amount; void main() { o = vec4(amount, 0.0, 0.0, 0.0); }";

    // ---------- icons and labels ----------
    public const string SpriteVS = Head + @"
layout(location = 0) in vec2 gq;
layout(location = 1) in vec4 i0;   // center, half
layout(location = 2) in vec4 i1;   // intake, angle, scale
layout(location = 3) in vec4 i2;   // alpha, slice, pull, agit
layout(location = 4) in vec4 i3;   // tint
layout(location = 5) in vec4 i4;   // shadow offset, shadow alpha
uniform int shadowPass;
out vec2 uv; out float alpha; flat out float slice; out vec4 tint; out float shadowAlpha;

vec2 warp(vec2 q) {
    vec2 center = i0.xy, half_ = i0.zw, intake = i1.xy;
    float angle = i1.z, scale = i1.w, pull = i2.z, agit = i2.w;
    float c = cos(angle), s = sin(angle);
    vec2 l = q * half_ * 2.0 * scale;
    vec2 w = center + vec2(c*l.x - s*l.y, s*l.x + c*l.y);

    // Before it goes: the corners nearest the robot are already reaching for it.
    vec2 toI = intake - w; float dist = length(toI);
    float reach = saturate(1.0 - dist / 300.0);
    if (dist > 1.0) w += toI / dist * agit * reach * reach * 34.0;

    if (pull > 0.0) {
        vec2 axisV = intake - center; float axisL = max(length(axisV), 1.0);
        vec2 A = axisV / axisL;
        float extent = max(length(half_) * scale, 1.0);
        float rel = dot(w - center, A) / extent;                  // +1 near edge, -1 far edge
        float tv = saturate(pull * 1.75 - 0.75 * (0.5 - 0.5 * rel));
        float e = tv * tv * (3.0 - 2.0 * tv);
        vec2 d = w - intake;
        float along = dot(d, A);
        vec2 perp = d - A * along;
        w = intake + A * along * (1.0 - e) + perp * pow(1.0 - e, 2.2);
    }
    return w;
}
void main() {
    vec2 p = warp(gq);
    if (shadowPass == 1) p += i4.xy;
    gl_Position = toClip(p);
    uv = gq + .5; alpha = i2.x; slice = i2.y;
    tint = shadowPass == 1 ? vec4(0.0) : i3;
    shadowAlpha = shadowPass == 1 ? i4.z : 0.0;
}";
    public const string SpriteFS = Head + @"
in vec2 uv; in float alpha; flat in float slice; in vec4 tint; in float shadowAlpha;
uniform sampler2DArray tex; uniform int shadowPass;
out vec4 o;
void main() {
    if (shadowPass == 1) {
        float a = texture(tex, vec3(uv, slice), 2.2).a;
        o = vec4(0.0, 0.0, 0.0, a * shadowAlpha * alpha);
    } else {
        vec4 c = texture(tex, vec3(uv, slice));
        c.rgb = mix(c.rgb, tint.rgb * c.a, tint.a);
        o = c * alpha;
    }
}";

    // ---------- particles ----------
    public const string PartVS = Head + @"
layout(location = 0) in vec4 p0;   // pos, size, ang
layout(location = 1) in vec4 p1;   // colour
layout(location = 2) in vec4 p2;   // chip (0 dot, 1 paper chip, 2 soap bubble), glow
out vec2 q; out vec4 color; out float chip; out float glow;
void main() {
    q = kQuad[gl_VertexID]; float c = cos(p0.w), s = sin(p0.w);
    vec2 l = q * p0.z; if (p2.x > .5 && p2.x < 1.5) l.y *= .55;
    gl_Position = toClip(p0.xy + vec2(c*l.x - s*l.y, s*l.x + c*l.y));
    color = p1; chip = p2.x; glow = p2.y;
}";
    public const string PartFS = Head + @"
in vec2 q; in vec4 color; in float chip; in float glow; out vec4 o;
void main() {
    float r = length(q);
    if (chip > 1.5) {
        // A soap bubble from above: a thin rainbow rim, nearly clear inside, one bright window highlight.
        if (r > 1.0) discard;
        float rim = smoothstep(0.70, 0.94, r) * (1.0 - smoothstep(0.95, 1.0, r));
        vec2 h = q - vec2(-0.34, -0.40);
        float spec = exp(-dot(h, h) * 28.0);
        vec3 film = 0.5 + 0.5 * cos(6.2831 * (r * 1.4 + atan(q.y, q.x) * 0.16 + vec3(0.0, 0.33, 0.67)));
        vec3 col = mix(film, vec3(1.0), 0.35);
        float a = (rim * 0.6 + 0.05 + spec * 0.85) * color.a;
        o = vec4(col * a, a * 0.8);
        return;
    }
    float a = chip > .5 ? 1.0 : smoothstep(1.0, .35, r);
    float al = color.a * a;
    // Glowing things add light instead of covering what's beneath them.
    o = vec4(color.rgb * al, al * (1.0 - glow));
}";

    // Lighting shared by robot and dock. Light comes from upper-left of the screen, always.
    const string Lighting = @"
vec3 studio(vec3 r) {
    // A large softbox up and to the left, a dim strip opposite, dark room otherwise.
    float box = smoothstep(.06, .34, -r.x * .55 - r.y * .78) * smoothstep(1.0, .55, abs(r.x * .78 - r.y * .55));
    float strip = smoothstep(.72, .92, r.x * .5 + r.y * .85) * .22;
    return vec3(.16, .17, .19) + vec3(1.0, .98, .95) * box * .95 + vec3(.75, .85, 1.0) * strip;
}
struct Mat { vec3 albedo; float gloss; float metal; float emis; vec3 emisColor; };
vec3 shade(vec3 N, Mat m, float ao) {
    vec3 L = normalize(vec3(-.46, -.62, .64));
    float diff = saturate(dot(N, L) * .78 + .22);
    float sky = .52 + .48 * N.z;
    vec3 col = m.albedo * (diff * vec3(1.02, 1.0, .97) * .84 + sky * vec3(.40, .42, .46));
    vec3 V = vec3(0.0, 0.0, 1.0);
    vec3 R = reflect(-V, N);
    float fres = pow(1.0 - saturate(N.z), 3.0);
    vec3 H = normalize(L + V);
    float sp = pow(saturate(dot(N, H)), mix(14.0, 180.0, m.gloss)) * mix(.12, 1.5, m.gloss);
    col += sp;
    float refl = mix(.03, .42, m.gloss) + fres * .5;
    col = mix(col, studio(R) * mix(vec3(1.0), m.albedo * 1.3, m.metal), refl * mix(.5, 1.0, m.gloss));
    col *= ao;
    col += m.emisColor * m.emis;
    return col;
}
float sdBox(vec2 p, vec2 b, float r) { vec2 d = abs(p) - b + r; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r; }
float groove(float d, float w) { return smoothstep(w, 0.0, abs(d)); }
";

    // ---------- robot ----------
    public const string RobotVS = Head + @"
uniform vec2 center; uniform float radius; uniform vec2 shake;
out vec2 p;
void main() {
    vec2 q = kQuad[gl_VertexID] * radius * 1.03;
    gl_Position = toClip(center + q + shake); p = q;
}";
    public const string RobotFS = Head + Lighting + @"
in vec2 p; out vec4 o;
uniform float radius; uniform float heading; uniform float lidar; uniform float bumper; uniform float ledPulse;
uniform vec4 led; uniform float alpha;
uniform sampler2D logo;

float robotHeight(vec2 l, out int mat, out float ao) {
    float r = length(l);
    float ang = atan(l.y, l.x);
    // Body: a fat rounded shell with a very slight crown.
    float e = saturate((1.0 - r) / .14);
    float h = .30 * sqrt(1.0 - (1.0 - e) * (1.0 - e)) + .035 * (1.0 - r * r);
    mat = 0; ao = 1.0;

    // Bumper arc wraps the front; it physically moves in when struck.
    float inner = .845 - bumper * .018;
    bool front = abs(ang) < 1.85;
    float seam = groove(r - inner, .012) * (front ? 1.0 : 0.0);
    float endA = abs(abs(ang) - 1.85) * r;
    float seamEnd = groove(endA, .012) * step(inner, r) * step(r, .99);
    h -= .05 * max(seam, seamEnd);
    ao *= 1.0 - .72 * max(seam, seamEnd);
    if (front && r > inner) {
        mat = 1;
        // Dark sensor window along the leading edge.
        if (abs(ang) < .72 && r > .905 && r < .977) mat = 2;
    }

    // Graphite top plate, glossy, inset slightly.
    float plate = r - .70;
    float plateRim = groove(plate, .010);
    if (plate < 0.0) { h -= .010; h += .085 * (1.0 - (r*r)/(.70*.70)); mat = 3; }
    ao *= 1.0 - .45 * plateRim;

    // Hairline cut for the bin lid at the rear.
    vec2 lidP = l - vec2(-.46, 0.0);
    float lid = sdBox(lidP, vec2(.135, .38), .07);
    if (plate < 0.0) { h -= .010 * groove(lid, .007); ao *= 1.0 - .4 * groove(lid, .006); }

    // Button pill.
    vec2 pp = l - vec2(-.17, 0.0);
    float pill = sdBox(pp, vec2(.04, .17), .04);
    if (pill < 0.0) { mat = 4; h += .005; }
    ao *= 1.0 - .4 * groove(pill, .008);
    float b1 = length(l - vec2(-.17, .085)) - .03, b2 = length(l - vec2(-.17, -.085)) - .03;
    if (b1 < 0.0 || b2 < 0.0) { mat = 5; h -= .004; }

    // Lidar turret: the one thing that stands proud of the body.
    vec2 tp = l - vec2(.27, 0.0);
    float td = length(tp);
    float tR = .265;
    float te = saturate((tR - td) / .05);
    h += .11 * sqrt(1.0 - (1.0 - te) * (1.0 - te));
    if (td < tR) {
        mat = 6;
        if (td < .185) { mat = 7; h += .045 * (1.0 - td*td/(.185*.185)); }
    }
    ao *= 1.0 - .55 * smoothstep(tR + .05, tR, td) * step(tR, td);
    return h;
}

void main() {
    vec2 wp = p / radius;
    float rr = length(wp);
    float px = fwidth(p.x) / radius;
    float edge = smoothstep(1.0, 1.0 - px * 1.5, rr);
    if (edge <= 0.0) discard;
    float c = cos(-heading), s = sin(-heading);
    vec2 l = vec2(c*wp.x - s*wp.y, s*wp.x + c*wp.y);

    int mat; float ao; int mm; float aa;
    const float E = .0035;
    float h0 = robotHeight(l, mat, ao);
    float hx = robotHeight(l + vec2(E, 0.0), mm, aa) - robotHeight(l - vec2(E, 0.0), mm, aa);
    float hy = robotHeight(l + vec2(0.0, E), mm, aa) - robotHeight(l - vec2(0.0, E), mm, aa);
    vec2 g = vec2(hx, hy) / (2.0 * E);
    // Back to world space so the light never rotates with the robot.
    float cw = cos(heading), sw = sin(heading);
    vec2 gw = vec2(cw*g.x - sw*g.y, sw*g.x + cw*g.y);
    vec3 N = normalize(vec3(-gw * 1.05, 1.0));

    Mat m; m.albedo = vec3(.90, .90, .88); m.gloss = .28; m.metal = 0.0; m.emis = 0.0; m.emisColor = vec3(0.0);
    if (mat == 1) { m.albedo = vec3(.84, .84, .83); m.gloss = .20; }                  // bumper
    else if (mat == 2) { m.albedo = vec3(.018, .019, .022); m.gloss = 1.0; }          // sensor window
    else if (mat == 3) { m.albedo = vec3(.055, .058, .065); m.gloss = .72; }          // top plate
    else if (mat == 4) { m.albedo = vec3(.035, .035, .04); m.gloss = .4; }            // pill
    else if (mat == 5) { m.albedo = vec3(.72, .73, .75); m.gloss = .5; m.metal = .6; } // buttons
    else if (mat == 6) { m.albedo = vec3(.62, .62, .62); m.gloss = .16; }             // turret skirt
    else if (mat == 7) { m.albedo = vec3(.015, .016, .02); m.gloss = 1.0; }           // turret dome

    // Spinning laser head, seen through the smoked dome.
    vec2 tp = l - vec2(.27, 0.0);
    float td = length(tp);
    vec3 extra = vec3(0.0);
    if (mat == 7) {
        vec2 gl = vec2(cos(lidar), sin(lidar)) * .105;
        extra += vec3(.45, .85, 1.0) * exp(-dot(tp - gl, tp - gl) * 1500.0) * .9;
        extra += vec3(.45, .85, 1.0) * exp(-dot(tp + gl * .9, tp + gl * .9) * 2600.0) * .25;
    }
    // Status ring around the turret.
    float ring = groove(td - .225, .009);
    float pulse = .62 + .38 * sin(ledPulse * 3.0);
    m.emis = ring * pulse * 1.15;
    m.emisColor = led.rgb;
    // Button glyph glow.
    float glow1 = exp(-pow(length(l - vec2(-.17, .085)) / .022, 2.0));
    extra += led.rgb * glow1 * .5;

    // Logotype on the graphite plate.
    vec2 luv = vec2((l.y + .31) / .62, (-l.x - .42) / .218);
    if (all(greaterThan(luv, vec2(0.0))) && all(lessThan(luv, vec2(1.0))) && length(l) < .70) {
        float la = texture(logo, luv).a;
        m.albedo = mix(m.albedo, vec3(.62, .64, .68), la * .85);
    }

    vec3 col = shade(N, m, ao) + extra;
    // Grounding: the rim rolls off a touch into its own shadow.
    col *= mix(.7, 1.0, smoothstep(1.0, .90, rr));
    o = vec4(col * edge * alpha, edge * alpha);
}";

    // Rotating side brush; blurs into a disc as it speeds up.
    public const string BrushVS = Head + @"
uniform vec2 center; uniform float radius;
out vec2 p;
void main() { vec2 q = kQuad[gl_VertexID] * radius; gl_Position = toClip(center + q); p = q; }";
    public const string BrushFS = Head + @"
in vec2 p; out vec4 o;
uniform float radius; uniform float angle; uniform float speed; uniform float alpha; uniform float sign_;
void main() {
    vec2 q = p / radius;
    float r = length(q);
    if (r > 1.0) discard;
    float a = atan(q.y, q.x) * sign_ - angle;
    float blur = saturate(speed / 26.0);
    float total = 0.0;
    for (int i = 0; i < 3; i++) {
        // Bristle arms curve backwards against the spin.
        float armA = a + float(i) * 2.0944 - r * .55;
        armA = atan(sin(armA), cos(armA));
        float w = mix(.035, .46, blur) + .07 * (1.0 - r);
        float m = exp(-armA * armA / (w * w));
        float fray = .72 + .28 * sin(armA * 60.0 + r * 3.0);
        total += m * fray;
    }
    float fade = smoothstep(1.0, .78, r);
    float al = saturate(total) * fade * mix(.96, .52, blur);
    float hub = smoothstep(.17, .13, r);
    vec3 bristle = mix(vec3(.10, .10, .11), vec3(.62, .62, .66), r * r);
    vec3 col = mix(bristle, vec3(.04), hub);
    float aOut = max(al, hub) * alpha;
    o = vec4(col * aOut, aOut);
}";

    // Soft drop shadows.
    public const string ShadowVS = Head + @"
uniform vec2 center; uniform float radius; uniform vec2 offset; uniform float blur; uniform float kind; uniform vec2 halfBox; uniform float angle;
out vec2 p;
void main() {
    vec2 ext = kind > .5 ? (halfBox + blur * 2.0 + 6.0) : vec2(radius + blur * 2.0 + 6.0);
    float c = cos(angle), s = sin(angle);
    vec2 l = kQuad[gl_VertexID] * ext; vec2 q = vec2(c*l.x - s*l.y, s*l.x + c*l.y);
    gl_Position = toClip(center + offset + q); p = q;
}";
    public const string ShadowFS = Head + @"
float sdBox(vec2 p, vec2 b, float r) { vec2 d = abs(p) - b + r; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r; }
in vec2 p; out vec4 o;
uniform float radius; uniform float strength; uniform float blur; uniform float kind; uniform vec2 halfBox; uniform float angle;
void main() {
    float d;
    if (kind > .5) {
        float c = cos(-angle), s = sin(-angle);
        vec2 l = vec2(c*p.x - s*p.y, s*p.x + c*p.y);
        d = sdBox(l, halfBox, radius);
    } else { d = length(p) - radius; }
    float a = 1.0 - smoothstep(-blur, blur, d);
    a = a * a * (3.0 - 2.0 * a);
    o = vec4(0.0, 0.0, 0.0, a * strength);
}";

    // ---------- dock ----------
    public const string DockVS = Head + @"
uniform vec2 center; uniform vec2 half_;
out vec2 p;
void main() { vec2 q = kQuad[gl_VertexID] * half_; gl_Position = toClip(center + q); p = q; }";
    public const string DockFS = Head + Lighting + @"
in vec2 p; out vec4 o;
uniform float radius; uniform float alpha; uniform float bag; uniform float glow; uniform float ledPulse; uniform vec4 led;
uniform sampler2D logo;

float dockHeight(vec2 d, out int mat, out float ao) {
    // d: x sideways, y outward from the back wall, in robot radii, origin at docked robot centre.
    ao = 1.0; mat = 0;
    float rampD = sdBox(d - vec2(0.0, -.20), vec2(1.14, .98), .24);
    float h = 0.0;
    if (rampD < 0.0) {
        float e = saturate(-rampD / .05);
        h = .05 * sqrt(1.0 - (1.0-e)*(1.0-e));
        mat = 1;
        // Non-slip ribs fade towards the lip.
        float rib = sin(d.y * 90.0) * smoothstep(.72, .1, d.y) * .0022 * smoothstep(-.06, -.2, rampD);
        h += rib;
    }
    // Charge contacts.
    vec2 cp = vec2(abs(d.x) - .42, d.y + .78);
    float contact = sdBox(cp, vec2(.13, .04), .03);
    if (contact < 0.0 && rampD < 0.0) { mat = 2; h += .012; }

    // Tower behind the robot.
    vec2 tc = d - vec2(0.0, -1.50);
    float tower = sdBox(tc, vec2(1.26, .58), .30);
    if (tower < 0.0) {
        float e = saturate(-tower / .13);
        h = .40 * sqrt(1.0 - (1.0-e)*(1.0-e));
        mat = 3;
        // Bag window.
        float win = sdBox(d - vec2(0.0, -1.50), vec2(.76, .29), .11);
        if (win < 0.0) { mat = 4; h = .34; }
        ao *= 1.0 - .6 * groove(win, .014);
        // Vents at each end.
        vec2 vp = vec2(abs(d.x) - 1.05, d.y + 1.50);
        if (abs(vp.x) < .09 && abs(vp.y) < .26) {
            float slit = smoothstep(.2, .7, sin(vp.y * 62.0));
            h -= .014 * slit; ao *= 1.0 - .55 * slit;
        }
    }
    ao *= 1.0 - .5 * groove(tower, .02);
    return h;
}
void main() {
    // Dock faces up the screen: outward = -y.
    vec2 d = vec2(p.x, -p.y) / radius;
    int mat, mm; float ao, aa;
    const float E = .004;
    float h0 = dockHeight(d, mat, ao);
    float hx = dockHeight(d + vec2(E, 0.0), mm, aa) - dockHeight(d - vec2(E, 0.0), mm, aa);
    float hy = dockHeight(d + vec2(0.0, E), mm, aa) - dockHeight(d - vec2(0.0, E), mm, aa);
    if (h0 <= 0.0001) discard;
    vec2 g = vec2(hx, -hy) / (2.0 * E);
    vec3 N = normalize(vec3(-g, 1.0));
    Mat m; m.albedo = vec3(.9, .9, .88); m.gloss = .3; m.metal = 0.0; m.emis = 0.0; m.emisColor = vec3(0.0);
    vec3 extra = vec3(0.0);
    if (mat == 1) { m.albedo = vec3(.10, .105, .115); m.gloss = .18; }
    if (mat == 2) { m.albedo = vec3(.95, .78, .42); m.gloss = .85; m.metal = 1.0; }
    if (mat == 4) {
        // Dust bag behind smoked glass: a pillow that grows and glows as it fills.
        vec2 w = d - vec2(0.0, -1.50);
        float fill = bag;
        float bx = .72 * (.3 + .7 * sqrt(fill));
        float bg = sdBox(w, vec2(bx, .25), .10);
        float inside = smoothstep(.01, -.01, bg);
        float puff = sqrt(saturate(-bg / .13));
        vec3 base = vec3(.04, .045, .05);
        float wr = sin(w.x * 21.0 + w.y * 11.0) * sin(w.y * 37.0 - w.x * 9.0);
        vec3 bagc = mix(vec3(.34, .36, .39), vec3(.72, .73, .74), puff) * (1.0 + .04 * wr);
        m.albedo = mix(base, bagc, inside * .85);
        m.gloss = .9;
        extra += vec3(.25, .6, .9) * glow * .24 * (.3 + inside);
    }
    // LED strip along the tower's front edge.
    vec2 lp = d - vec2(0.0, -.98);
    if (abs(lp.x) < .34 && abs(lp.y) < .012 + .004) {
        m.emis = (.65 + .35 * sin(ledPulse * 2.4)) * 1.1;
        m.emisColor = led.rgb;
    }
    vec3 col = shade(N, m, ao) + extra;
    vec2 luv = vec2((d.x + .34) / .68, (d.y + 2.03 - .04) / .19);
    if (all(greaterThan(luv, vec2(0.0))) && all(lessThan(luv, vec2(1.0)))) {
        float la = texture(logo, vec2(luv.x, 1.0 - luv.y)).a;
        col = mix(col, vec3(.32, .33, .35), la * .85);
    }
    float a = alpha * smoothstep(0.0, .012, h0);
    o = vec4(col * a, a);
}";

    // ---------- lidar sweep ----------
    public const string LidarVS = Head + @"
uniform vec2 center; uniform float radius;
out vec2 p;
void main() { vec2 q = kQuad[gl_VertexID] * radius; gl_Position = toClip(center + q); p = q; }";
    public const string LidarFS = Head + @"
in vec2 p; out vec4 o;
uniform float radius; uniform float angle; uniform float alpha;
void main() {
    float r = length(p) / radius;
    if (r > 1.0) discard;
    float a = atan(p.y, p.x) - angle;
    a = mod(-a + 62.83185 * 4.0, 6.283185);           // radians behind the beam
    float tail = exp(-a * 2.4) * .26;
    float line = exp(-a * a * 1800.0) * .55;
    float rings = (smoothstep(.012, 0.0, abs(fract(r * 9.0) - .5) / 9.0)) * .05 * exp(-a * 1.2);
    float fall = pow(1.0 - r, .35);
    float i = (tail + line + rings) * fall * alpha;
    vec3 c = vec3(.32, .86, 1.0);
    o = vec4(c * i, i * .55);
}";
}
