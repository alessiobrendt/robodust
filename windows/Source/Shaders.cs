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
out vec2 uv; out float alpha; flat out float slice; out vec4 tint; out float shadowAlpha; out float burn;

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
    burn = i4.w;
}";
    public const string SpriteFS = Head + @"
in vec2 uv; in float alpha; flat in float slice; in vec4 tint; in float shadowAlpha; in float burn;
uniform sampler2DArray tex; uniform int shadowPass;
out vec4 o;
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}
void main() {
    // Fun mode: burning to ash. Holes open where the noise is lowest and spread, with a glowing rim.
    float edge = 0.0;
    float amount = abs(burn);
    if (burn > 0.001) {
        vec2 q = uv * vec2(7.0, 7.0) + vec2(slice * 3.17, slice * 1.31);
        float n = vnoise(q) * 0.65 + vnoise(q * 2.7) * 0.35;
        float t = burn * 1.15 - 0.08;
        if (n < t) discard;
        edge = 1.0 - smoothstep(0.0, 0.09, n - t);
    } else if (burn < -0.001) {
        // The laser: square pixels drop out one by one, each lit cyan just before it goes.
        vec2 cell = floor(uv * 12.0);
        float n = hash(cell + vec2(slice * 1.7, slice * 0.9));
        float t = amount * 1.1 - 0.05;
        if (n < t) discard;
        edge = 1.0 - smoothstep(0.0, 0.12, n - t);
    }
    if (shadowPass == 1) {
        float a = texture(tex, vec3(uv, slice), 2.2).a;
        o = vec4(0.0, 0.0, 0.0, a * shadowAlpha * alpha * (1.0 - amount));
    } else {
        vec4 c = texture(tex, vec3(uv, slice));
        c.rgb = mix(c.rgb, tint.rgb * c.a, tint.a);
        if (burn > 0.001) {
            c.rgb = mix(c.rgb, vec3(0.05, 0.04, 0.035) * c.a, smoothstep(0.0, 0.45, burn) * 0.9);
            c.rgb += vec3(1.0, 0.45, 0.08) * edge * c.a * 1.7;
        } else if (burn < -0.001) {
            c.rgb += vec3(0.3, 0.9, 1.0) * edge * c.a * 1.2;
        }
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
    vec2 l = q * p0.z; if (p2.x > .5 && p2.x < 1.1) l.y *= .55;   // 1.1...1.5 stays square: pixels
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
uniform vec2 center; uniform float radius; uniform vec2 shake; uniform float quadScale;
out vec2 p;
void main() {
    vec2 q = kQuad[gl_VertexID] * radius * quadScale;   // bigger than the shell for a spoiler or underglow
    gl_Position = toClip(center + q + shake); p = q;
}";
    public const string RobotFS = Head + Lighting + @"
in vec2 p; out vec4 o;
uniform float radius; uniform float heading; uniform float lidar; uniform float bumper; uniform float ledPulse;
uniform vec4 led; uniform float alpha;
uniform vec3 bodyCol; uniform vec3 plateCol; uniform float bodyGloss; uniform float bodyMetal; uniform float hat; uniform float ears;   // skins
// Shop: hat kind, shell pattern, spoiler, decal, underglow colour (a > 0 when on), and time for anything that spins.
uniform int hatKind; uniform int pattern; uniform float spoiler; uniform int decal; uniform vec4 glow; uniform float time; uniform float lightbar;
float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vn(vec2 p) { vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f); return mix(mix(h21(i), h21(i + vec2(1.0, 0.0)), f.x), mix(h21(i + vec2(0.0, 1.0)), h21(i + vec2(1.0, 1.0)), f.x), f.y); }
Mat plain(vec3 c, float gloss, float metal) { Mat m; m.albedo = c; m.gloss = gloss; m.metal = metal; m.emis = 0.0; m.emisColor = vec3(0.0); return m; }
vec2 toWorldDir(vec2 d) { float c = cos(heading), s = sin(heading); return vec2(c*d.x - s*d.y, s*d.x + c*d.y); }
/// A dome (or cone) normal for something round seen from above: k is how steep its sides are.
vec3 domeN(vec2 q, float r, float k) { float h = length(q); vec2 d = h > 1e-4 ? q / h : vec2(0.0); return normalize(vec3(toWorldDir(d) * (h / r) * k, 1.0)); }

/// Hats, seen from straight above, sitting on the top plate. Returns true where the hat is.
bool hats(vec2 l, inout vec3 col) {
    vec2 hp = l - vec2(-0.08, 0.0);
    float hr = length(hp), a = atan(hp.y, hp.x);
    if (hatKind == 1) {          // party hat: a striped cone with a pompom
        col *= 1.0 - 0.35 * smoothstep(0.38, 0.27, hr) * step(0.27, hr);
        if (hr >= 0.27) return false;
        float stripe = step(0.5, fract(a / 6.2831 * 8.0));
        col = shade(domeN(hp, 0.27, 2.0), plain(mix(vec3(0.95, 0.25, 0.45), vec3(0.25, 0.72, 0.95), stripe), 0.3, 0.0), 1.0);
        col = mix(col, shade(domeN(hp, 0.085, 0.9), plain(vec3(1.0, 0.92, 0.55), 0.1, 0.0), 1.0), smoothstep(0.085, 0.065, hr));
        return true;
    }
    if (hatKind == 2) {          // crown: a gold band with points and jewels, velvet inside
        float outer = 0.25 + 0.06 * pow(abs(cos(a * 3.0)), 6.0);
        col *= 1.0 - 0.3 * smoothstep(outer + 0.1, outer, hr) * step(outer, hr);
        if (hr >= outer) return false;
        if (hr < 0.17) { col = shade(domeN(hp, 0.17, 0.6), plain(vec3(0.55, 0.05, 0.12), 0.25, 0.0), 1.0); return true; }
        col = shade(normalize(vec3(toWorldDir(hp / hr) * 0.6, 1.0)), plain(vec3(1.0, 0.78, 0.25), 0.85, 1.0), 1.0);
        for (int i = 0; i < 6; i++) {
            float ga = float(i) * 1.0472 + 0.5236;
            vec2 gp = hp - vec2(cos(ga), sin(ga)) * 0.21;
            float g = smoothstep(0.035, 0.025, length(gp));
            col = mix(col, shade(domeN(gp, 0.035, 1.0), plain(i % 2 == 0 ? vec3(0.85, 0.08, 0.15) : vec3(0.1, 0.35, 0.9), 1.0, 0.0), 1.0), g);
        }
        return true;
    }
    if (hatKind == 3) {          // top hat: a wide black brim, a tall crown, a red band
        col *= 1.0 - 0.4 * smoothstep(0.42, 0.31, hr) * step(0.31, hr);
        if (hr >= 0.31) return false;
        if (hr > 0.2) col = shade(domeN(hp, 0.31, 0.3), plain(vec3(0.03), 0.6, 0.0), 1.0);
        else if (hr > 0.18) col = shade(vec3(0.0, 0.0, 1.0), plain(vec3(0.7, 0.05, 0.08), 0.4, 0.0), 1.0);
        else col = shade(vec3(toWorldDir(hp) * 0.4, 1.0), plain(vec3(0.05), 0.7, 0.0), 1.0);
        return true;
    }
    if (hatKind == 4) {          // cowboy hat: an oval leather brim, curled at the sides, a dented crown
        vec2 e = hp / vec2(0.30, 0.38);
        float er = length(e);
        col *= 1.0 - 0.35 * smoothstep(1.3, 1.0, er) * step(1.0, er);
        if (er >= 1.0) return false;
        vec3 leather = vec3(0.55, 0.33, 0.16) * (0.9 + 0.1 * vn(hp * 40.0));
        col = shade(normalize(vec3(toWorldDir(e) * (er > 0.7 ? 1.2 : 0.2), 1.0)), plain(leather, 0.2, 0.0), 1.0);
        vec2 c2 = hp / vec2(0.17, 0.21);
        if (length(c2) < 1.0) {
            col = shade(domeN(c2, 1.0, 0.7), plain(leather * 0.9, 0.25, 0.0), 1.0);
            col *= 1.0 - 0.4 * smoothstep(0.08, 0.0, abs(c2.y)) * step(-0.6, c2.x) * step(c2.x, 0.6);   // the dent
        }
        if (abs(length(c2) - 1.0) < 0.08) col = vec3(0.12, 0.07, 0.04);
        return true;
    }
    if (hatKind == 5) {          // Santa hat: red, a white fur rim, a pompom flopped to one side
        col *= 1.0 - 0.35 * smoothstep(0.39, 0.28, hr) * step(0.28, hr);
        vec2 pp = hp - vec2(-0.12, 0.14);
        float pr = length(pp);
        if (pr < 0.08) { col = shade(domeN(pp, 0.08, 0.9), plain(vec3(0.97), 0.05, 0.0), 1.0); return true; }
        if (hr >= 0.28) return false;
        if (hr > 0.22) col = shade(domeN(hp, 0.28, 0.6), plain(vec3(0.96) * (0.9 + 0.1 * vn(hp * 60.0)), 0.05, 0.0), 1.0);
        else col = shade(domeN(hp - vec2(-0.06, 0.07), 0.22, 1.6), plain(vec3(0.82, 0.08, 0.1), 0.3, 0.0), 1.0);
        return true;
    }
    if (hatKind == 6) {          // propeller cap: four coloured panels, a button, and a spinning propeller
        col *= 1.0 - 0.3 * smoothstep(0.34, 0.25, hr) * step(0.25, hr);
        vec2 r = vec2(cos(time * 14.0) * hp.x + sin(time * 14.0) * hp.y, -sin(time * 14.0) * hp.x + cos(time * 14.0) * hp.y);
        float blade = smoothstep(0.03, 0.02, abs(r.y)) * step(abs(r.x), 0.36);
        if (hr < 0.25) {
            int q = int(floor(mod(a / 1.5708 + 4.0, 4.0)));
            vec3 pc = q == 0 ? vec3(0.9, 0.15, 0.15) : q == 1 ? vec3(0.98, 0.8, 0.1) : q == 2 ? vec3(0.15, 0.4, 0.9) : vec3(0.15, 0.7, 0.3);
            col = shade(domeN(hp, 0.25, 0.9), plain(pc, 0.4, 0.0), 1.0);
        }
        col = mix(col, vec3(0.75, 0.78, 0.8), blade * 0.75);
        if (hr < 0.04) col = shade(domeN(hp, 0.04, 1.0), plain(vec3(0.95, 0.85, 0.2), 0.8, 0.6), 1.0);
        return hr < 0.25 || blade > 0.0;
    }
    if (hatKind == 7) {          // Viking helmet: a steel dome, a rim of rivets, two horns curving out to the sides
        bool on = false;
        for (int k = -1; k <= 1; k += 2) {
            vec2 hc = hp - vec2(0.02, float(k) * 0.34);
            float d = length(hc / vec2(0.07, 0.17));
            if (d < 1.0) { col = shade(domeN(hc, 0.12, 1.2), plain(mix(vec3(0.95, 0.9, 0.78), vec3(0.7, 0.62, 0.48), abs(hc.y) / 0.17), 0.5, 0.0), 1.0); on = true; }
        }
        if (hr < 0.24) {
            col = shade(domeN(hp, 0.24, 1.0), plain(vec3(0.6, 0.62, 0.66), 0.8, 0.9), 1.0);
            float rivet = step(0.2, hr) * smoothstep(0.02, 0.01, length(vec2(hr - 0.215, fract(a / 6.2831 * 12.0) * 0.11 - 0.055)));
            col = mix(col, vec3(0.9), rivet);
            on = true;
        }
        return on;
    }
    if (hatKind == 8) {          // chef's hat: a puffy white top with soft folds
        col *= 1.0 - 0.3 * smoothstep(0.36, 0.26, hr) * step(0.26, hr);
        if (hr >= 0.26) return false;
        float puff = 0.5 + 0.5 * cos(a * 6.0);
        col = shade(normalize(vec3(toWorldDir(hp / max(hr, 1e-3)) * (hr / 0.26) * (0.6 + 0.3 * puff), 1.0)), plain(vec3(0.97), 0.1, 0.0), 1.0);
        return true;
    }
    if (hatKind == 9) {          // wizard hat: a deep blue cone with stars and a moon
        col *= 1.0 - 0.35 * smoothstep(0.4, 0.29, hr) * step(0.29, hr);
        if (hr >= 0.29) return false;
        col = shade(domeN(hp, 0.29, 2.2), plain(vec3(0.12, 0.1, 0.42), 0.4, 0.0), 1.0);
        vec2 sc = floor(hp * 24.0);
        float star = step(0.9, h21(sc)) * smoothstep(0.35, 0.0, length(fract(hp * 24.0) - 0.5));
        col += vec3(1.0, 0.9, 0.4) * star * (0.7 + 0.3 * sin(time * 4.0 + h21(sc) * 20.0));
        float moon = smoothstep(0.05, 0.04, length(hp - vec2(0.1, 0.08))) * (1.0 - smoothstep(0.045, 0.035, length(hp - vec2(0.12, 0.1))));
        col = mix(col, vec3(1.0, 0.92, 0.5), moon);
        return true;
    }
    if (hatKind == 10) {         // hard hat: a yellow shell with a ridge down the middle and a front peak
        vec2 e = (hp - vec2(0.03, 0.0)) / vec2(0.3, 0.25);
        float er = length(e);
        col *= 1.0 - 0.35 * smoothstep(1.3, 1.0, er) * step(1.0, er);
        if (er >= 1.0) return false;
        col = shade(normalize(vec3(toWorldDir(e) * er * 1.1, 1.0)), plain(vec3(0.98, 0.78, 0.08), 0.6, 0.0), 1.0);
        col *= 1.0 - 0.25 * smoothstep(0.03, 0.0, abs(hp.y)) * step(-0.2, hp.x);   // the ridge
        return true;
    }
    if (hatKind == 11) {         // halo: a golden ring floating just above, glowing
        float ring = smoothstep(0.03, 0.0, abs(hr - 0.24));
        col *= 1.0 - 0.2 * smoothstep(0.06, 0.0, abs(length(hp - vec2(-0.03, -0.04)) - 0.24));   // its shadow, a little offset
        col = mix(col, vec3(1.0, 0.9, 0.45) * (1.3 + 0.2 * sin(time * 3.0)), ring);
        col += vec3(1.0, 0.85, 0.4) * smoothstep(0.1, 0.0, abs(hr - 0.24)) * 0.35;
        return ring > 0.0;
    }
    if (hatKind == 12) {         // flower crown: a ring of little flowers in different colours
        bool on = false;
        for (int i = 0; i < 9; i++) {
            float fa = float(i) * 0.6981;
            vec2 fc = hp - vec2(cos(fa), sin(fa)) * 0.22;
            float fr = length(fc), pa = atan(fc.y, fc.x);
            float petal = 0.05 + 0.02 * cos(pa * 5.0 + float(i));
            if (fr < petal) {
                vec3 pc = i % 3 == 0 ? vec3(1.0, 0.45, 0.6) : i % 3 == 1 ? vec3(1.0, 0.92, 0.95) : vec3(0.7, 0.5, 1.0);
                col = shade(domeN(fc, petal, 0.7), plain(fr < 0.018 ? vec3(1.0, 0.82, 0.2) : pc, 0.2, 0.0), 1.0);
                on = true;
            }
        }
        if (!on && abs(hr - 0.22) < 0.012) { col = vec3(0.2, 0.5, 0.15); on = true; }   // the stems between
        return on;
    }
    return false;
}
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
    float c = cos(-heading), s = sin(-heading);
    vec2 l = vec2(c*wp.x - s*wp.y, s*wp.x + c*wp.y);
    if (edge <= 0.0) {
        // Outside the shell: only a spoiler, or the glow of a neon underglow.
        if (spoiler > 0.5) {
            float wing = sdBox(l - vec2(-1.13, 0.0), vec2(0.075, 0.8), 0.05);
            float strut = min(sdBox(vec2(l.x + 1.03, abs(l.y) - 0.45), vec2(0.06, 0.035), 0.01), 1.0);
            float d = min(wing, strut);
            if (d < 0.0) {
                float wa = smoothstep(0.0, -px * 1.5, d);
                vec3 n = normalize(vec3(toWorldDir(vec2(-(l.x + 1.13) * 6.0, 0.0)), 1.0));
                vec3 wc = shade(n, plain(mix(vec3(0.75, 0.06, 0.08), vec3(0.08), step(strut, wing)), 0.7, 0.0), 1.0);
                wc = mix(wc, vec3(0.95), smoothstep(0.012, 0.0, abs(l.x + 1.075)) * step(wing, 0.0) * 0.6);   // a highlight along the lip
                o = vec4(wc * wa * alpha, wa * alpha);
                return;
            }
        }
        if (glow.a > 0.5) {
            float g = exp(-pow(max(rr - 0.97, 0.0) / 0.13, 2.0)) * (0.75 + 0.25 * sin(time * 3.0));
            o = vec4(glow.rgb * g * alpha * 0.9, 0.0);   // adds light, covers nothing
            return;
        }
        discard;
    }

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

    Mat m; m.albedo = bodyCol; m.gloss = bodyGloss; m.metal = bodyMetal; m.emis = 0.0; m.emisColor = vec3(0.0);
    if (mat == 1) { m.albedo = bodyCol * .93; m.gloss = bodyGloss * .75; }          // bumper
    else if (mat == 2) { m.albedo = vec3(.018, .019, .022); m.gloss = 1.0; }          // sensor window
    else if (mat == 3) { m.albedo = plateCol; m.gloss = .72; }                       // top plate
    else if (mat == 4) { m.albedo = vec3(.035, .035, .04); m.gloss = .4; }            // pill
    else if (mat == 5) { m.albedo = vec3(.72, .73, .75); m.gloss = .5; m.metal = .6; } // buttons
    else if (mat == 6) { m.albedo = vec3(.62, .62, .62); m.gloss = .16; }             // turret skirt
    else if (mat == 7) { m.albedo = vec3(.015, .016, .02); m.gloss = 1.0; }           // turret dome
    float rl = length(l);
    if (mat == 0 || mat == 1) {
        // Shop patterns on the shell.
        if (pattern == 1) {        // carbon fibre weave
            float w = step(0.5, fract((l.x + l.y) * 16.0)) == step(0.5, fract((l.x - l.y) * 16.0)) ? 0.07 : 0.12;
            m.albedo = vec3(w); m.gloss = 0.8;
        } else if (pattern == 2) { // camouflage
            float n = vn(l * 4.0 + 3.1) + 0.5 * vn(l * 9.0);
            m.albedo = n < 0.6 ? vec3(0.27, 0.32, 0.18) : n < 0.9 ? vec3(0.42, 0.37, 0.22) : vec3(0.15, 0.17, 0.11); m.gloss = 0.1;
        } else if (pattern == 3) { // galaxy: deep space, a nebula and stars
            float n = vn(l * 3.0 + 7.0) * 0.6 + vn(l * 7.0) * 0.4;
            m.albedo = mix(vec3(0.04, 0.03, 0.12), vec3(0.45, 0.12, 0.55), n * n);
            vec2 sc = floor(l * 40.0);
            float star = step(0.985, h21(sc)) * smoothstep(0.3, 0.0, length(fract(l * 40.0) - 0.5));
            m.emis = star * 1.5; m.emisColor = vec3(1.0); m.gloss = 0.7;
        } else if (pattern == 4) { // neon: black with a glowing rim
            m.albedo = vec3(0.03); m.gloss = 0.6;
            m.emis = smoothstep(0.88, 0.97, rl) * (0.8 + 0.2 * sin(time * 4.0)); m.emisColor = vec3(0.1, 1.0, 0.85);
        } else if (pattern == 5) { // zebra: wavy black stripes across white
            float z = sin(l.x * 26.0 + sin(l.y * 7.0) * 2.2 + vn(l * 3.0) * 3.0);
            m.albedo = z > 0.15 ? vec3(0.92) : vec3(0.05); m.gloss = 0.25;
        } else if (pattern == 6) { // leopard: dark rosettes on gold
            vec2 cell = floor(l * 6.0); vec2 f = fract(l * 6.0) - 0.5 - (vec2(h21(cell), h21(cell + 7.0)) - 0.5) * 0.4;
            float d = length(f);
            m.albedo = d < 0.17 ? vec3(0.55, 0.32, 0.12) : d < 0.27 ? vec3(0.12, 0.07, 0.03) : vec3(0.86, 0.62, 0.28); m.gloss = 0.2;
        } else if (pattern == 7) { // wood: warm grain
            float g = sin((l.x + vn(l * vec2(2.0, 12.0)) * 0.25) * 60.0) * 0.5 + 0.5;
            m.albedo = mix(vec3(0.45, 0.26, 0.12), vec3(0.66, 0.42, 0.22), g); m.gloss = 0.35;
        } else if (pattern == 8) { // marble: white with grey veins
            float v = abs(sin(l.x * 6.0 + l.y * 3.0 + vn(l * 5.0) * 6.0));
            m.albedo = mix(vec3(0.55, 0.56, 0.6), vec3(0.95, 0.95, 0.94), smoothstep(0.0, 0.25, v)); m.gloss = 0.85;
        } else if (pattern == 9) { // rainbow: the colour wheel round the shell, turning slowly
            float a = atan(l.y, l.x) / 6.2831 + time * 0.05;
            m.albedo = 0.5 + 0.45 * cos(6.2831 * (a + vec3(0.0, 0.33, 0.67))); m.gloss = 0.5;
        } else if (pattern == 10) { // lava: dark crust, glowing cracks
            float n = vn(l * 6.0 + time * 0.15) + 0.5 * vn(l * 13.0);
            float crack = smoothstep(0.06, 0.0, abs(n - 0.75));
            m.albedo = vec3(0.08, 0.05, 0.04); m.gloss = 0.2;
            m.emis = crack * (1.2 + 0.4 * sin(time * 3.0 + n * 10.0)); m.emisColor = vec3(1.0, 0.35, 0.05);
        } else if (pattern == 11) { // ice: pale blue with frost and glints
            float n = vn(l * 9.0);
            m.albedo = mix(vec3(0.62, 0.8, 0.95), vec3(0.95, 0.98, 1.0), n); m.gloss = 1.0;
            m.emis = step(0.993, h21(floor(l * 30.0))) * (0.5 + 0.5 * sin(time * 5.0 + h21(floor(l * 30.0)) * 30.0)); m.emisColor = vec3(1.0);
        } else if (pattern == 12) { // police: navy and white halves
            m.albedo = l.y > 0.0 ? vec3(0.05, 0.1, 0.3) : vec3(0.92); m.gloss = 0.5;
        }
        // Decals over the paint.
        if (decal == 1 && abs(abs(l.y) - 0.11) < 0.04) m.albedo = vec3(0.95, 0.95, 0.97);               // racing stripes
        if (decal == 2) {                                                                              // flames, licking back from the bumper
            float tongue = l.x + 0.12 * sin(abs(l.y) * 20.0) + 0.05 * sin(abs(l.y) * 47.0);
            float f = smoothstep(0.02, 0.06, tongue);
            m.albedo = mix(m.albedo, mix(vec3(0.95, 0.2, 0.05), vec3(1.0, 0.85, 0.2), smoothstep(0.1, 0.8, l.x)), f);
        }
    }

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
    hats(l, col);
    // The squirrel look: two pointed ears with dark tufts, at the front of the shell.
    if (ears > 0.5) {
        for (int k = -1; k <= 1; k += 2) {
            vec2 ep = l - vec2(0.50, float(k) * 0.40);
            float ea = float(k) * 0.55;
            vec2 er = vec2(cos(ea) * ep.x + sin(ea) * ep.y, -sin(ea) * ep.x + cos(ea) * ep.y);
            float d = length(vec2(er.x / 0.15, er.y / 0.085));
            if (d < 1.0) {
                vec2 dl = er / max(length(er), 1e-3);
                float cw3 = cos(heading), sw3 = sin(heading);
                vec2 dw = vec2(cw3*dl.x - sw3*dl.y, sw3*dl.x + cw3*dl.y);
                Mat em; em.albedo = mix(vec3(0.82, 0.42, 0.16), vec3(0.92, 0.62, 0.52), smoothstep(0.75, 0.3, d) * 0.7);
                em.gloss = 0.15; em.metal = 0.0; em.emis = 0.0; em.emisColor = vec3(0.0);
                col = shade(normalize(vec3(dw * d * 0.8, 1.0)), em, 1.0);
                col = mix(col, vec3(0.14, 0.07, 0.03), smoothstep(0.08, 0.13, er.x) * 0.9);   // the tuft
            } else {
                col *= 1.0 - 0.3 * smoothstep(1.35, 1.0, d);   // a soft shadow round the ear
            }
        }
    }
    // Blue lights: red and blue at the front corners, flashing in turn.
    if (lightbar > 0.5) {
        for (int k = -1; k <= 1; k += 2) {
            vec2 lp = l - vec2(0.6, float(k) * 0.5);
            float d = length(lp / vec2(0.07, 0.13));
            bool lit = (k < 0) == (fract(time * 2.5) < 0.5);
            vec3 lc = k < 0 ? vec3(1.0, 0.12, 0.1) : vec3(0.15, 0.35, 1.0);
            if (d < 1.0) col = mix(col, lc * (lit ? 1.7 : 0.3), 0.92);
            col += lc * exp(-d * d * 0.6) * (lit ? 0.5 : 0.0);
        }
    }
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

    // ---------- snowplow blade ----------
    public const string BladeVS = Head + @"
uniform vec2 center; uniform vec2 half_; uniform float angle;
out vec2 q;
void main() {
    q = kQuad[gl_VertexID]; float c = cos(angle), s = sin(angle);
    vec2 l = q * (half_ + 3.0); gl_Position = toClip(center + vec2(c*l.x - s*l.y, s*l.x + c*l.y));
    q = l;
}";
    public const string BladeFS = Head + @"
in vec2 q; out vec4 o;
uniform vec2 half_; uniform vec3 color; uniform float alpha;
float sdBox(vec2 p, vec2 b, float r) { vec2 d = abs(p) - b + r; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r; }
void main() {
    float d = sdBox(q, half_, min(half_.x, half_.y) * 0.6);
    float a = 1.0 - smoothstep(-1.0, 1.0, d);
    if (a <= 0.0) discard;
    // A curved steel-and-paint blade: lit along its top, darker at the base, a bright lip.
    float t = clamp(q.x / half_.x * 0.5 + 0.5, 0.0, 1.0);
    vec3 col = color * mix(0.55, 1.15, t) + vec3(0.25) * smoothstep(0.8, 1.0, t);
    col *= 1.0 - 0.35 * smoothstep(-6.0, 0.0, d);
    o = vec4(col * a * alpha, a * alpha);
}";

    // ---------- duel: weapons, armour and spikes, in the robot's own frame (x forward, units of its radius) ----------
    public const string WeaponVS = Head + @"
uniform vec2 center; uniform float radius; uniform float angle;
out vec2 q;
void main() {
    vec2 k = kQuad[gl_VertexID] * radius * 1.8; float c = cos(angle), s = sin(angle);
    gl_Position = toClip(center + vec2(c*k.x - s*k.y, s*k.x + c*k.y));
    q = k / radius;
}";
    public const string WeaponFS = Head + @"
in vec2 q; out vec4 o;
uniform int kind; uniform int layer; uniform int armor; uniform int spikes;
uniform float spin; uniform float swing; uniform float alpha; uniform float radius;
float sdBox(vec2 p, vec2 b) { vec2 d = abs(p) - b; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0); }
float sdSeg(vec2 p, vec2 a, vec2 b) { vec2 pa = p - a, ba = b - a; float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0); return length(pa - ba * h); }
vec4 over(vec4 c, vec3 col, float a) { a = clamp(a, 0.0, 1.0); return vec4(col * a + c.rgb * (1.0 - a), a + c.a * (1.0 - a)); }
float fill(float d) { float aa = 1.2 / radius; return 1.0 - smoothstep(-aa, aa, d); }
vec3 steel(float t) { return mix(vec3(.30, .32, .36), vec3(.90, .92, .96), clamp(t, 0.0, 1.0)); }
void main() {
    vec4 c = vec4(0.0);
    vec2 p = q;
    float r = length(p);
    float ang = atan(p.y, p.x);
    if (layer == 0) {
        if (spikes == 1) {
            // Fourteen steel spikes round the rim, lit from the top-left.
            float f = abs(fract(ang / 6.28318 * 18.0) - 0.5) * 2.0;
            float len = 1.0 - (r - 0.97) / 0.19;
            float d = max(f - len * 0.75, max(0.97 - r, r - 1.16)) * 0.2;
            c = over(c, steel(0.35 + 0.6 * len * (0.6 + 0.4 * (-p.x - p.y) / r)), fill(d));
        }
        if (armor == 1) {
            // A ring of riveted plates sticking out past the shell.
            float d = abs(r - 1.02) - 0.08;
            float plate = fract(ang / 6.28318 * 12.0);
            float seam = smoothstep(0.035, 0.0, min(plate, 1.0 - plate));
            vec3 col = vec3(.34, .36, .40) * (0.8 + 0.35 * (-p.x - p.y) / max(r, .01)) * (1.0 - 0.5 * seam);
            vec2 rv = vec2(r - 1.02, (plate - 0.5) * 6.28318 / 12.0 * r);
            col = mix(col, vec3(.85), fill(length(rv) - 0.025) * 0.8);
            c = over(c, col, fill(d));
        }
        if (kind == 5) {
            // Wedge: a steel ramp in front, brightest at its edge.
            float hw = 0.95 - (p.x - 0.5) * 0.15;
            float d = max(abs(p.y) - hw, max(0.5 - p.x, p.x - 1.36));
            float t = (p.x - 0.5) / 0.86;
            vec3 col = steel(0.3 + 0.6 * t) * (1.0 - 0.6 * smoothstep(1.30, 1.36, p.x) * 0.0);
            col = mix(col, vec3(.98), smoothstep(1.31, 1.35, p.x) * 0.7);
            c = over(c, col, fill(d));
        }
        if (kind == 3) {
            // Flipper: a hazard-striped plate that lifts towards the camera when it fires.
            float ext = 1.34 + 0.22 * swing;
            float hw = 0.64 + 0.08 * swing;
            float d = max(abs(p.y) - hw, max(0.48 - p.x, p.x - ext));
            float st = step(0.5, fract((p.x + p.y) * 3.2));
            vec3 col = mix(vec3(.98, .78, .10), vec3(.10), st * 0.9) * (0.72 + 0.4 * swing + 0.25 * (p.x - 0.5));
            col *= 1.0 - 0.55 * smoothstep(0.02, 0.0, abs(p.x - 0.9));
            c = over(c, col, fill(d));
        }
        if (kind == 1) {
            // Circular saw on an arm, poking out of the front.
            c = over(c, vec3(.16, .16, .18), fill(sdBox(p - vec2(0.82, 0.0), vec2(0.28, 0.08))));
            vec2 v = p - vec2(1.1, 0.0);
            float rv = length(v);
            float a = atan(v.y, v.x) - spin;
            float edge = 0.44 + 0.07 * fract(a / 6.28318 * 18.0);
            vec3 col = steel(0.55 + 0.3 * cos(atan(v.y, v.x) * 2.0 + 0.8) - 0.15 * smoothstep(0.3, 0.44, rv));
            col *= 0.9 + 0.1 * sin(rv * 60.0);
            c = over(c, col, fill(rv - edge));
            c = over(c, vec3(.08), fill(rv - 0.11));
            c = over(c, vec3(.7), fill(length(v - vec2(-0.03, -0.03)) - 0.035));
        }
    } else {
        if (kind == 2) {
            // Hammer on a pivot behind the lid: folded back at rest, slammed down in front when it strikes.
            float phi = mix(2.25, 0.0, swing);   // at rest it lies back over the lid
            float h = sin(phi);
            float s = 1.0 + 0.45 * h;
            vec2 piv = vec2(-0.15, 0.0);
            vec2 head = vec2(piv.x + 1.2 * cos(phi), 0.0);
            c = over(c, vec3(.12), fill(sdBox(p - piv, vec2(0.13, 0.16))));
            float dh = sdSeg(p, piv, head) - 0.07 * s;
            c = over(c, vec3(.55, .36, .20) * (0.85 + 0.3 * h), fill(dh));
            float db = sdBox(p - head, vec2(0.17, 0.36) * s);
            vec3 col = steel(0.4 + 0.35 * h + 0.25 * (-(p.y - head.y) / (0.36 * s)));
            c = over(c, col, fill(db));
        }
        if (kind == 4) {
            // Spinner bar across the lid; at speed it blurs into a disc.
            float cs = cos(spin), sn = sin(spin);
            vec2 v = vec2(cs * p.x + sn * p.y, -sn * p.x + cs * p.y);
            c = over(c, vec3(.75, .77, .82), 0.22 * fill(r - 1.3));
            float taper = 0.13 - 0.04 * abs(v.x) / 1.3;
            float d = sdBox(v, vec2(1.3, taper));
            c = over(c, steel(0.5 + 0.4 * v.x / 1.3), fill(d));
            c = over(c, vec3(.1), fill(r - 0.2));
            c = over(c, vec3(.75), fill(length(p - vec2(-0.04, -0.04)) - 0.06));
        }
    }
    if (c.a <= 0.002) discard;
    o = c * alpha;
}";

    // ---------- duel: health bars ----------
    public const string BarVS = Head + @"
uniform vec2 center; uniform vec2 half_;
out vec2 q;
void main() { q = kQuad[gl_VertexID] * (half_ + 2.0); gl_Position = toClip(center + q); }";
    public const string BarFS = Head + @"
in vec2 q; out vec4 o;
uniform vec2 half_; uniform float fill; uniform vec3 color; uniform float alpha;
float sdBox(vec2 p, vec2 b, float r) { vec2 d = abs(p) - b + r; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r; }
void main() {
    float d = sdBox(q, half_, half_.y);
    float a = 1.0 - smoothstep(-0.7, 0.7, d);
    if (a <= 0.0) discard;
    vec3 col = vec3(.06, .06, .07); float al = 0.75;
    float inner = 1.0 - smoothstep(-0.7, 0.7, sdBox(q, half_ - 1.5, half_.y - 1.5));
    float f = step(q.x, -half_.x + 2.0 * half_.x * fill) * inner;
    col = mix(col, color * (0.8 + 0.4 * smoothstep(half_.y, -half_.y, q.y)), f); al = mix(al, 1.0, f);
    float edge = smoothstep(-1.4, -0.3, d);
    col = mix(col, vec3(.96), edge * 0.55); al = max(al, edge * 0.7);
    o = vec4(col * al, al) * a * alpha;
}";
}
