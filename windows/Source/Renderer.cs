using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace RoboDust;

/// Draws a frame of the sim. Same passes, order and parameters as the Mac renderer.
public sealed unsafe class Renderer
{
    sealed class Prog
    {
        public uint Id;
        readonly Dictionary<string, int> locs = new();
        public Prog(string vs, string fs) { Id = Gl.Program(vs, fs); }
        public int this[string n] { get { if (!locs.TryGetValue(n, out var l)) { l = Gl.Loc(Id, n); locs[n] = l; } return l; } }
        public void Use(V2 viewport, V2 origin) { Gl.UseProgram(Id); Gl.Uniform2f(this["viewport"], viewport.X, viewport.Y); Gl.Uniform2f(this["origin"], origin.X, origin.Y); }
        public void F(string n, float v) => Gl.Uniform1f(this[n], v);
        public void F2(string n, V2 v) => Gl.Uniform2f(this[n], v.X, v.Y);
        public void F2(string n, float x, float y) => Gl.Uniform2f(this[n], x, y);
        public void F4(string n, float x, float y, float z, float w) => Gl.Uniform4f(this[n], x, y, z, w);
        public void I(string n, int v) => Gl.Uniform1i(this[n], v);
    }

    public readonly V2 SizePts;
    readonly Prog pBg, pSprite, pPart, pRobot, pBrush, pShadow, pDock, pLidar, pStamp, pFade, pBlade, pWeapon, pBar, pArena, pTag;
    public RobotSkin Skin;
    public Look Look;          // hats, tuning and pet from the shop; Skin and Hat are kept in step
    float botTime;
    readonly CatRenderer catR;
    int squirrelBase, petBase;
    public bool Hat;
    readonly uint emptyVao, spriteVao, partVao, gridBuf, instBuf, partBuf;
    readonly int gridCount;
    uint wall, track, trackFbo, iconTex, labelTex, logoTex;
    readonly int trackW, trackH;
    float fadeAcc;
    V2? lastStamp;
    const int InstFloats = 20, PartFloats = 12;
    float[] inst = new float[InstFloats * 4096];
    readonly float[] parts = new float[PartFloats * 2600];

    /// This display's top-left in the shared world, and how far the lidar sweep reaches across all of them.
    public V2 Origin;
    public float LidarRadius;

    public Renderer(V2 sizePts, V2 origin = default)
    {
        SizePts = sizePts; Origin = origin; LidarRadius = MathF.Max(sizePts.X, sizePts.Y) * 0.85f;
        pBg = new Prog(Shaders.BgVS, Shaders.BgFS);
        pSprite = new Prog(Shaders.SpriteVS, Shaders.SpriteFS);
        pPart = new Prog(Shaders.PartVS, Shaders.PartFS);
        pRobot = new Prog(Shaders.RobotVS, Shaders.RobotFS);
        pBrush = new Prog(Shaders.BrushVS, Shaders.BrushFS);
        pShadow = new Prog(Shaders.ShadowVS, Shaders.ShadowFS);
        pDock = new Prog(Shaders.DockVS, Shaders.DockFS);
        pLidar = new Prog(Shaders.LidarVS, Shaders.LidarFS);
        pStamp = new Prog(Shaders.StampVS, Shaders.StampFS);
        pFade = new Prog(Shaders.FadeVS, Shaders.FadeFS);
        pBlade = new Prog(Shaders.BladeVS, Shaders.BladeFS);
        pWeapon = new Prog(Shaders.WeaponVS, Shaders.WeaponFS);
        pBar = new Prog(Shaders.BarVS, Shaders.BarFS);
        pArena = new Prog(Shaders.ArenaVS, Shaders.ArenaFS);
        pTag = new Prog(Shaders.TagVS, Shaders.TagFS);

        emptyVao = Gl.Gen(Gl.GenVertexArrays);

        // 14x14 cell mesh per sprite: fine enough to bend smoothly into the intake.
        const int n = 14;
        var verts = new List<float>();
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            float ax = (float)i / n - 0.5f, ay = (float)j / n - 0.5f, s = 1f / n;
            verts.AddRange(new[] { ax, ay, ax + s, ay, ax, ay + s, ax, ay + s, ax + s, ay, ax + s, ay + s });
        }
        gridCount = verts.Count / 2;
        spriteVao = Gl.Gen(Gl.GenVertexArrays);
        Gl.BindVertexArray(spriteVao);
        gridBuf = Gl.Gen(Gl.GenBuffers);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, gridBuf);
        var va = verts.ToArray();
        fixed (float* p = va) Gl.BufferData(Gl.ARRAY_BUFFER, va.Length * 4, p, Gl.STATIC_DRAW);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, 0, 8, 0);
        Gl.EnableVertexAttribArray(0);
        instBuf = Gl.Gen(Gl.GenBuffers);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, instBuf);
        Gl.BufferData(Gl.ARRAY_BUFFER, inst.Length * 4, null, Gl.STREAM_DRAW);
        for (uint a = 0; a < 5; a++)
        {
            Gl.VertexAttribPointer(1 + a, 4, Gl.FLOAT, 0, InstFloats * 4, (nint)(a * 16));
            Gl.EnableVertexAttribArray(1 + a);
            Gl.VertexAttribDivisor(1 + a, 1);
        }
        partVao = Gl.Gen(Gl.GenVertexArrays);
        Gl.BindVertexArray(partVao);
        partBuf = Gl.Gen(Gl.GenBuffers);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, partBuf);
        Gl.BufferData(Gl.ARRAY_BUFFER, parts.Length * 4, null, Gl.STREAM_DRAW);
        for (uint a = 0; a < 3; a++)
        {
            Gl.VertexAttribPointer(a, 4, Gl.FLOAT, 0, PartFloats * 4, (nint)(a * 16));
            Gl.EnableVertexAttribArray(a);
            Gl.VertexAttribDivisor(a, 1);
        }
        Gl.BindVertexArray(0);

        trackW = Math.Max(1, (int)sizePts.X); trackH = Math.Max(1, (int)sizePts.Y);
        track = Texture2D(trackW, trackH, null, Gl.RGBA, false);
        trackFbo = Gl.Gen(Gl.GenFramebuffers);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, trackFbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, track, 0);
        Gl.Viewport(0, 0, trackW, trackH);
        Gl.ClearColor(0, 0.5f, 0, 0);
        Gl.Clear(Gl.COLOR_BUFFER_BIT);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        logoTex = MakeLogo();
        catR = new CatRenderer();
    }

    static uint Texture2D(int w, int h, byte[] bgra, uint format, bool mip)
    {
        var t = Gl.Gen(Gl.GenTextures);
        Gl.BindTexture(Gl.TEXTURE_2D, t);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        if (bgra != null) fixed (byte* p = bgra) Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, w, h, 0, Gl.BGRA, Gl.UNSIGNED_BYTE, p);
        else Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, w, h, 0, format, Gl.UNSIGNED_BYTE, null);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, (int)(mip ? Gl.LINEAR_MIPMAP_LINEAR : Gl.LINEAR));
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
        if (mip) Gl.GenerateMipmap(Gl.TEXTURE_2D);
        return t;
    }

    uint MakeLogo()
    {
        const int w = 512, h = 180;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var f = new Font("Segoe UI Semibold", 92, FontStyle.Regular, GraphicsUnit.Pixel);
            var sz = g.MeasureString("robodust", f);
            g.DrawString("robodust", f, Brushes.White, (w - sz.Width) / 2, (h - sz.Height) / 2);
        }
        return Texture2D(w, h, Sprites.Pixels(bmp), Gl.BGRA, false);
    }

    public void SetWallpaper(Bitmap img)
    {
        if (wall != 0) { var t = wall; Gl.DeleteTextures(1, &t); }
        wall = Texture2D(img.Width, img.Height, Sprites.Pixels(img), Gl.BGRA, false);
    }

    static uint ArrayTexture(List<byte[]> images, int w, int h)
    {
        var t = Gl.Gen(Gl.GenTextures);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, t);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        Gl.TexImage3D(Gl.TEXTURE_2D_ARRAY, 0, (int)Gl.RGBA8, w, h, Math.Max(images.Count, 1), 0, Gl.BGRA, Gl.UNSIGNED_BYTE, null);
        for (int i = 0; i < images.Count; i++)
            fixed (byte* p = images[i]) Gl.TexSubImage3D(Gl.TEXTURE_2D_ARRAY, 0, 0, 0, i, w, h, 1, Gl.BGRA, Gl.UNSIGNED_BYTE, p);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR_MIPMAP_LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
        Gl.TexParameterf(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAX_ANISOTROPY, 8);
        Gl.GetError(); // anisotropy is optional
        Gl.GenerateMipmap(Gl.TEXTURE_2D_ARRAY);
        return t;
    }

    public void SetSprites(List<Sprite> s)
    {
        // The squirrels' frames ride along after the items in the same texture.
        var icons = s.ConvertAll(x => x.Icon);
        squirrelBase = icons.Count;
        icons.AddRange(SquirrelArt.Frames());
        petBase = icons.Count;
        icons.AddRange(PetArt.Frames());
        iconTex = ArrayTexture(icons, Sprites.IconTex, Sprites.IconTex);
        labelTex = ArrayTexture(s.ConvertAll(x => x.Label), Sprites.LabelW, Sprites.LabelH);
    }

    struct Inst
    {
        public V2 Center, Half, Intake; public float Angle, Scale, Alpha, Slice, Pull, Agit;
        public float TR, TG, TB, TA; public V2 ShadowOffset; public float ShadowAlpha, Burn;
    }

    // MARK: - Frame

    /// Draws into the framebuffer currently bound, which is pxW by pxH pixels.
    public void Encode(Sim sim, float dt, uint targetFbo, int pxW, int pxH)
    {
        Gl.Enable(Gl.BLEND);
        Gl.BlendEquation(Gl.FUNC_ADD);
        Gl.BlendFunc(Gl.ONE, Gl.ONE_MINUS_SRC_ALPHA);
        StampTrack(sim, dt);

        // Assemble sprite instances: labels first, then icons, things being eaten on top.
        var ground = new List<Inst>(sim.Items.Count);
        var groundLabels = new List<Inst>(sim.Items.Count);
        var top = new List<Inst>(); var topLabels = new List<Inst>(); var late = new List<Inst>();
        var intake = sim.Intake;
        var iconHalf = new V2(Metrics.IconHalf, Metrics.IconHalf);
        var labelHalf = Metrics.LabelHalf;
        // Funnels in up close. From far away it shrinks and zips over, or it would smear across the screen.
        var funnelReach = sim.R * 2.2f;
        (float pull, float scale, bool near) Funnel(in Body b)
        {
            if (!b.Ingesting) return (0, 1, true);
            var near = (intake - b.Pos).Len < funnelReach;
            return near ? (b.Pull, 1, true) : (0, 1 - b.Pull * 0.55f, false);
        }
        foreach (var it in sim.Items)
        {
            intake = sim.IntakeFor(it);   // a rival may be the one pulling it in
            DrawItem(it);
        }
        void DrawItem(Item it)
        {
            var flying = it.Flight != Flight.None && it.FlightDelay <= 0;
            if (it.Flight != Flight.None && it.FlightDelay > 0 && it.Flight != Flight.ToBag && it.InDock) return;
            if (it.InBin || (it.InDock && !flying)) return;
            if (it.Buried && !flying)
            {
                // Under a little heap of earth. Its label is buried with it.
                ground.Add(new Inst { Center = it.Icon.Pos, Half = new V2(Metrics.IconHalf, Metrics.IconHalf) * 1.05f, Intake = intake + new V2(0, 100000), Scale = 1, Alpha = 1, Slice = squirrelBase + SquirrelArt.Mound });
                return;
            }
            if (it.HeldBySquirrel && !flying)
            {
                // In a squirrel's mouth: small, and without its label.
                top.Add(new Inst { Center = it.Icon.Pos, Half = new V2(Metrics.IconHalf, Metrics.IconHalf), Intake = intake + new V2(0, 100000), Angle = it.Icon.Ang, Scale = 0.5f, Alpha = 1, Slice = it.Index, ShadowOffset = new V2(3, 5), ShadowAlpha = 0.25f });
                return;
            }
            var far = intake + new V2(0, 100000);
            if (!it.Icon.Gone || flying)
            {
                var a = it.Icon.Agit;
                var wob = flying ? 0 : a * 0.13f * MathF.Sin(sim.T * (17 + it.Icon.Seed % 7) + it.Icon.Seed);
                var jitter = flying ? V2.Zero : new V2(MathF.Sin(sim.T * 33 + it.Icon.Seed), MathF.Cos(sim.T * 29 + it.Icon.Seed * 2)) * (a * a * 1.6f);
                var squash = 1 + it.Land * 0.12f * MathF.Sin(it.Land * 6);
                var f = Funnel(it.Icon);
                var scale = (flying ? it.FlightScale : 1 + a * 0.05f) * squash * f.scale;
                var lift = flying ? MathF.Sin(it.FlightT * M.Pi) : a;
                var inst = new Inst
                {
                    Center = it.Icon.Pos + jitter, Half = iconHalf, Intake = flying ? far : intake, Angle = it.Icon.Ang + wob, Scale = scale,
                    Alpha = 1, Slice = it.Index, Pull = f.pull, Agit = flying || !f.near ? 0 : a,
                    // Burnt things stay burnt on the way into the bag: charred scraps rather than whole icons.
                    // Laser bits fall apart into pixels (a negative burn), fire into ash.
                    Burn = it.Flight == Flight.ToBag ? (it.BurnStyle == 1 && it.Scorch < 0.5f && it.Burn > 0.99f ? -0.5f : it.Scorch > 0.99f ? 0.55f : 0) : (it.BurnStyle == 1 ? -it.Burn : it.Burn),
                    ShadowOffset = new V2(2, 3) + new V2(5, 9) * lift, ShadowAlpha = 0.34f * MathF.Max(lift, 0),
                };
                if (it.Ping > 0) { inst.TR = 0.45f; inst.TG = 0.95f; inst.TB = 1; inst.TA = it.Ping * 0.55f; }
                // Fun mode: singed by the flamethrower, glowing at the edge while it's still in the fire.
                else if (it.Scorch > 0.01f)
                {
                    var hot = sim.FlameLevel * (0.5f + 0.5f * MathF.Sin(sim.T * 23 + it.Icon.Seed));
                    inst.TR = 0.13f + 0.5f * hot; inst.TG = 0.07f + 0.12f * hot; inst.TB = 0.03f; inst.TA = M.Clamp(it.Scorch * 0.78f, 0, 0.78f);
                }
                if (flying && it.Flight == Flight.ToBag) late.Add(inst);
                else if (flying || it.Icon.Ingesting) top.Add(inst);
                else ground.Add(inst);
            }
            // Label.
            var labelVisible = !it.Label.Gone || flying;
            if (labelVisible && it.Flight != Flight.ToBag)
            {
                var attached = it.LabelAttached;
                var lpos = attached ? it.Icon.Pos + new V2(0, Metrics.LabelOffset * (flying ? it.FlightScale : 1)).Rotated(it.Icon.Ang) : it.Label.Pos;
                var lang = attached ? it.Icon.Ang : it.Label.Ang;
                var lf = attached ? (0f, 1f, true) : Funnel(it.Label);
                var scale = (attached && flying ? it.FlightScale : 1) * lf.Item2;
                var a = !lf.Item3 ? 0 : (attached ? it.Icon.Agit * 0.6f : it.Label.Agit);
                var inst = new Inst
                {
                    Center = lpos, Half = labelHalf, Intake = flying ? far : intake, Angle = lang, Scale = scale, Alpha = 1, Slice = it.Index,
                    Pull = lf.Item1, Agit = flying ? 0 : a, Burn = attached ? (it.BurnStyle == 1 ? -it.Burn : it.Burn) : it.LabelBurn,
                };
                if (it.Ping > 0) { inst.TR = 0.55f; inst.TG = 0.97f; inst.TB = 1; inst.TA = it.Ping * 0.7f; }
                if (flying || it.Label.Ingesting) topLabels.Add(inst); else groundLabels.Add(inst);
            }
        }

        // Coins lying on the floor, turning in the light.
        foreach (var c in sim.Coins)
        {
            if (c.Gone || sim.Phase >= Phase.Leaving) continue;
            ground.Add(new Inst
            {
                Center = c.Pos, Half = new V2(15, 15), Intake = c.Pos + new V2(0, 100000), Scale = 1 - c.Pull * 0.6f, Alpha = 1,
                Slice = petBase + PetArt.CoinOffset + ((int)c.Spin % PetArt.CoinFrames), ShadowOffset = new V2(2, 3), ShadowAlpha = 0.25f,
            });
        }
        // A pet from the shop, trotting along.
        if (sim.Pet is { } pet && sim.RobotAlpha > 0.05f && sim.DockDrop > 0.9f)
        {
            var slice = pet.Kind == PetKind.Squirrel ? squirrelBase + (pet.Frame == 4 ? SquirrelArt.Sit : pet.Frame) : petBase + PetArt.Offset(pet.Kind) + pet.Frame;
            top.Add(new Inst
            {
                Center = pet.Pos, Half = new V2(54, 54), Intake = pet.Pos + new V2(0, 100000), Angle = pet.Heading + M.Pi / 2, Scale = 1, Alpha = sim.RobotAlpha,
                Slice = slice, ShadowOffset = new V2(3, 5), ShadowAlpha = 0.3f,
            });
        }
        // The Eichhörnchen-Bande, on top of the files: they run over them.
        foreach (var sq in sim.Squirrels)
        {
            if (sq.State == SqState.Gone) continue;
            top.Add(new Inst
            {
                Center = sq.Pos, Half = new V2(42, 42), Intake = sq.Pos + new V2(0, 100000), Angle = sq.Heading + M.Pi / 2, Scale = 1, Alpha = 1,
                Slice = squirrelBase + sq.Frame, ShadowOffset = new V2(3, 5), ShadowAlpha = 0.3f,
            });
        }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, targetFbo);
        Gl.Viewport(0, 0, pxW, pxH);

        // Wallpaper.
        Gl.Disable(Gl.BLEND);
        pBg.Use(SizePts, Origin);
        Gl.BindVertexArray(emptyVao);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, wall);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, track);
        pBg.I("wall", 0); pBg.I("track", 1);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Enable(Gl.BLEND);
        if (sim.Arena is { } arena)
        {
            pArena.Use(SizePts, Origin);
            Gl.BindVertexArray(emptyVao);
            pArena.F4("rect", 0, 0, arena.W, arena.H); pArena.F("time", sim.T);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        }

        DrawSprites(groundLabels, labelTex, false);
        // Only lifted icons cast a shadow, so the resting ones match Explorer's look exactly.
        DrawSprites(ground.FindAll(x => x.ShadowAlpha <= 0.015f), iconTex, false);
        DrawSprites(ground.FindAll(x => x.ShadowAlpha > 0.015f), iconTex, true);
        DrawSprites(topLabels, labelTex, false);
        DrawSprites(top, iconTex, true);

        // Lidar sweep sits on the floor, under the robot.
        if (sim.ScanActive > 0.01f)
        {
            pLidar.Use(SizePts, Origin);
            Gl.BindVertexArray(emptyVao);
            pLidar.F2("center", sim.Pos); pLidar.F("radius", LidarRadius);
            pLidar.F("angle", -M.Pi / 2 + sim.ScanSweep); pLidar.F("alpha", sim.ScanActive);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        }

        DrawDock(sim);
        DrawParticles(sim);
        catR.Draw(sim, sim.Cat, SizePts, Origin, false);
        DrawRobot(sim);
        catR.Draw(sim, sim.Cat, SizePts, Origin, true);
        DrawSprites(late, iconTex, false);
        if (sim.Arena != null) DrawArenaHud(sim);
    }

    // MARK: - Arena

    readonly Dictionary<string, (uint tex, V2 size)> texts = new();

    /// Text as a texture: white with a dark outline, so it reads on any wallpaper. Cached.
    (uint tex, V2 size) TextTexture(string s, float heightPts, Color color)
    {
        var key = $"{s}|{heightPts}|{color.ToArgb()}";
        if (texts.TryGetValue(key, out var t)) return t;
        if (texts.Count > 64) { foreach (var v in texts.Values) { var id = v.tex; Gl.DeleteTextures(1, &id); } texts.Clear(); }
        const float k = 2.5f;   // pixels per point, sharp on every display
        var px = heightPts * k;
        using var font = new Font("Segoe UI", px * 0.72f, FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF sz;
        using (var probe = new Bitmap(1, 1)) using (var pg = Graphics.FromImage(probe)) sz = pg.MeasureString(s, font);
        int w = Math.Max(4, (int)MathF.Ceiling(sz.Width + px * 0.3f)), h = Math.Max(4, (int)MathF.Ceiling(px * 1.15f));
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddString(s, font.FontFamily, (int)font.Style, font.Size, new PointF((w - sz.Width) / 2 + px * 0.05f, (h - sz.Height) / 2), StringFormat.GenericDefault);
            using var pen = new Pen(Color.FromArgb(200, 10, 10, 14), px * 0.14f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawPath(pen, path);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
        t = (Texture2D(w, h, Sprites.Pixels(bmp), Gl.BGRA, false), new V2(w / k, h / k));
        texts[key] = t;
        return t;
    }

    void DrawText(string s, V2 center, float heightPts, float alpha, Color color, float scale = 1)
    {
        if (string.IsNullOrEmpty(s) || alpha <= 0.01f) return;
        var (tex, size) = TextTexture(s, heightPts, color);
        pTag.Use(SizePts, Origin);
        Gl.BindVertexArray(emptyVao);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, tex);
        pTag.I("tex", 0); pTag.F2("center", center); pTag.F2("half_", size * (0.5f * scale)); pTag.F("alpha", alpha);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
    }

    void DrawArenaHud(Sim sim)
    {
        var a = sim.Arena;
        // Names over the health bars: yours in cyan, theirs in red.
        if (sim.Me is { } me) DrawText(me.Name, sim.Pos + new V2(0, -(sim.R * 1.28f + 28)), 16, 1, Color.FromArgb(150, 230, 255));
        if (sim.Foe is { } foe) DrawText(foe.Name, foe.Pos + new V2(0, -(foe.R * 1.28f + 28)), 16, foe.Alpha, Color.FromArgb(255, 150, 140));
        // The score, just above the arena.
        DrawText($"{a.NameA}   {a.RoundsA} : {a.RoundsB}   {a.NameB}", new V2(a.W / 2, -24), 22, 1, Color.White);
        // The banner: the countdown pops and fades each second, the rest stays until it changes.
        if (a.Banner != "")
        {
            var digit = a.Banner.Length == 1;
            var pop = 1 + 0.35f * MathF.Max(0, 1 - a.BannerT * 5);
            var alpha = digit ? 1 - M.Smooth(0.7f, 1, a.BannerT) : a.Banner == "KAMPF!" ? 1 - M.Smooth(0.9f, 1.2f, a.BannerT) : 1;
            var col = a.Banner == "KAMPF!" ? Color.FromArgb(255, 210, 80) : Color.White;
            DrawText(a.Banner, new V2(a.W / 2, a.H * 0.2f), digit ? 110 : 64, alpha, col, pop);
        }
        DrawText("Pfeiltasten: fahren  ·  Leertaste: Turbo  ·  Esc: aufgeben", new V2(a.W / 2, a.H + 26), 15, 0.85f, Color.FromArgb(220, 225, 235));
    }

    void DrawSprites(List<Inst> list, uint tex, bool shadows)
    {
        if (list.Count == 0 || tex == 0) return;
        var count = Math.Min(list.Count, 4096);
        for (int i = 0; i < count; i++)
        {
            var s = list[i]; var o = i * InstFloats;
            inst[o] = s.Center.X; inst[o + 1] = s.Center.Y; inst[o + 2] = s.Half.X; inst[o + 3] = s.Half.Y;
            inst[o + 4] = s.Intake.X; inst[o + 5] = s.Intake.Y; inst[o + 6] = s.Angle; inst[o + 7] = s.Scale;
            inst[o + 8] = s.Alpha; inst[o + 9] = s.Slice; inst[o + 10] = s.Pull; inst[o + 11] = s.Agit;
            inst[o + 12] = s.TR; inst[o + 13] = s.TG; inst[o + 14] = s.TB; inst[o + 15] = s.TA;
            inst[o + 16] = s.ShadowOffset.X; inst[o + 17] = s.ShadowOffset.Y; inst[o + 18] = s.ShadowAlpha; inst[o + 19] = s.Burn;
        }
        Gl.BindVertexArray(spriteVao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, instBuf);
        Gl.BufferData(Gl.ARRAY_BUFFER, inst.Length * 4, null, Gl.STREAM_DRAW);
        fixed (float* p = inst) Gl.BufferSubData(Gl.ARRAY_BUFFER, 0, count * InstFloats * 4, p);
        pSprite.Use(SizePts, Origin);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, tex);
        pSprite.I("tex", 0);
        if (shadows)
        {
            pSprite.I("shadowPass", 1);
            Gl.DrawArraysInstanced(Gl.TRIANGLES, 0, gridCount, count);
        }
        pSprite.I("shadowPass", 0);
        Gl.DrawArraysInstanced(Gl.TRIANGLES, 0, gridCount, count);
    }

    void Shadow(V2 center, float radius, float strength, V2 offset, float blur, float kind, V2 halfBox)
    {
        pShadow.Use(SizePts, Origin);
        Gl.BindVertexArray(emptyVao);
        pShadow.F2("center", center); pShadow.F("radius", radius); pShadow.F("strength", strength); pShadow.F2("offset", offset);
        pShadow.F("blur", blur); pShadow.F("kind", kind); pShadow.F2("halfBox", halfBox); pShadow.F("angle", 0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
    }

    void DrawDock(Sim sim)
    {
        var drop = sim.DockDrop;
        var bounce = 1 - MathF.Pow(1 - drop, 3);
        var scale = 1 + (1 - bounce) * 0.55f;
        var alpha = sim.DockAlpha * M.Smooth(0, 0.35f, drop);
        var shakeK = sim.Phase == Phase.Dropping ? sim.Shake : 0;
        var shakeOff = new V2(MathF.Sin(sim.T * 70), MathF.Cos(sim.T * 61)) * shakeK * 3;
        var center = sim.DockPos + shakeOff;
        var R = sim.R * scale;
        var towerC = center + new V2(0, R * 1.04f);
        var height = 1 - bounce;
        Shadow(towerC, R * 0.3f, 0.44f * alpha * (1 - height * 0.6f), new V2(5, 8) + new V2(40, 70) * height, 20 + height * 40, 1, new V2(R * 1.3f, R * 1.02f));

        pDock.Use(SizePts, Origin);
        Gl.BindVertexArray(emptyVao);
        pDock.F2("center", center); pDock.F2("half_", R * 1.36f, R * 2.14f);
        pDock.F("radius", R); pDock.F("alpha", alpha); pDock.F("bag", sim.BagFill); pDock.F("glow", sim.BagGlow); pDock.F("ledPulse", sim.LedPulse);
        pDock.F4("led", sim.Led.X, sim.Led.Y, sim.Led.Z, 1);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, logoTex); pDock.I("logo", 0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
    }

    void DrawParticles(Sim sim)
    {
        if (sim.Particles.Count == 0) return;
        int n = 0;
        foreach (var q in sim.Particles)
        {
            if (n >= 2600) break;
            var k = M.Clamp(q.Life / q.MaxLife, 0, 1);
            var age = 1 - k;
            float r = q.R, g = q.G, b = q.B, size = q.Size, glow = 0, chip = q.Chip, a;
            switch (q.Kind)
            {
                case PartKind.Fire:
                {
                    // White-hot at the nozzle, through orange and red, into a wisp of smoke. Glows while it burns.
                    (r, g, b) = age < 0.25f ? Mix((1, 0.95f, 0.75f), (1, 0.62f, 0.16f), age / 0.25f)
                              : age < 0.6f ? Mix((1, 0.62f, 0.16f), (0.85f, 0.18f, 0.04f), (age - 0.25f) / 0.35f)
                              : Mix((0.85f, 0.18f, 0.04f), (0.22f, 0.2f, 0.19f), (age - 0.6f) / 0.4f);
                    size = q.Size * (1 + age * 2.6f);
                    glow = 1 - M.Smooth(0.55f, 0.8f, age);
                    a = M.Smooth(0, 0.06f, age) * (glow > 0.5f ? 0.85f : 0.4f) * (1 - M.Smooth(0.75f, 1, age));
                    break;
                }
                case PartKind.Smoke:
                    size = q.Size * (1 + age * 2.2f);
                    a = q.A * MathF.Sin(k * M.Pi) * 0.7f;
                    break;
                case PartKind.Bubble:
                    chip = 2;
                    size = q.Size * (0.55f + 0.45f * M.Smooth(0, 0.25f, age));
                    a = M.Smooth(0, 0.08f, k);
                    break;
                case PartKind.Confetti:
                    a = q.A * M.Smooth(0, 0.35f, k);
                    break;
                case PartKind.Snow:
                    a = q.A * M.Smooth(0, 0.03f, k) * M.Smooth(1, 0.985f, k);
                    break;
                case PartKind.Ribbon:
                    a = q.A * M.Smooth(0, 0.6f, k);   // stays, then fades
                    break;
                case PartKind.Swirl:
                    a = q.A * MathF.Sin(k * M.Pi);
                    break;
                case PartKind.Glow:
                    glow = 1;
                    a = q.A * k;
                    break;
                default:
                    a = q.A * (q.Chip > 0.5f ? M.Smooth(0, 0.3f, k) : MathF.Sin(k * M.Pi));
                    break;
            }
            var o = n * PartFloats;
            parts[o] = q.Pos.X; parts[o + 1] = q.Pos.Y; parts[o + 2] = size; parts[o + 3] = q.Ang;
            parts[o + 4] = r; parts[o + 5] = g; parts[o + 6] = b; parts[o + 7] = a;
            parts[o + 8] = chip; parts[o + 9] = glow; parts[o + 10] = 0; parts[o + 11] = 0;
            n++;
        }
        Gl.BindVertexArray(partVao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, partBuf);
        Gl.BufferData(Gl.ARRAY_BUFFER, parts.Length * 4, null, Gl.STREAM_DRAW);
        fixed (float* p = parts) Gl.BufferSubData(Gl.ARRAY_BUFFER, 0, n * PartFloats * 4, p);
        pPart.Use(SizePts, Origin);
        Gl.DrawArraysInstanced(Gl.TRIANGLES, 0, 6, n);
    }

    static (float, float, float) Mix((float r, float g, float b) a, (float r, float g, float b) b, float t)
    {
        t = M.Clamp(t, 0, 1);
        return (a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t);
    }

    /// Body colour, top plate, gloss and metal for each look.
    static (V3 body, V3 plate, float gloss, float metal) SkinOf(RobotSkin s) => s switch
    {
        RobotSkin.Black => (new V3(0.07f, 0.07f, 0.08f), new V3(0.02f, 0.02f, 0.025f), 0.55f, 0),
        RobotSkin.Gold => (new V3(0.95f, 0.74f, 0.32f), new V3(0.08f, 0.06f, 0.04f), 0.75f, 0.85f),
        RobotSkin.Retro => (new V3(0.86f, 0.80f, 0.66f), new V3(0.42f, 0.33f, 0.24f), 0.18f, 0),
        RobotSkin.Pink => (new V3(0.97f, 0.62f, 0.76f), new V3(0.12f, 0.06f, 0.09f), 0.35f, 0),
        RobotSkin.Squirrel => (new V3(0.77f, 0.36f, 0.13f), new V3(0.26f, 0.13f, 0.05f), 0.22f, 0),
        RobotSkin.Carbon => (new V3(0.09f, 0.09f, 0.1f), new V3(0.03f, 0.03f, 0.035f), 0.8f, 0),
        RobotSkin.Chrome => (new V3(0.86f, 0.87f, 0.9f), new V3(0.06f, 0.06f, 0.07f), 1f, 1f),
        RobotSkin.Camo => (new V3(0.3f, 0.34f, 0.2f), new V3(0.1f, 0.11f, 0.07f), 0.1f, 0),
        RobotSkin.Galaxy => (new V3(0.06f, 0.04f, 0.14f), new V3(0.02f, 0.015f, 0.05f), 0.7f, 0),
        RobotSkin.Neon => (new V3(0.03f, 0.03f, 0.03f), new V3(0.015f, 0.015f, 0.02f), 0.6f, 0),
        RobotSkin.Zebra => (new V3(0.9f, 0.9f, 0.9f), new V3(0.04f, 0.04f, 0.04f), 0.25f, 0),
        RobotSkin.Leopard => (new V3(0.86f, 0.62f, 0.28f), new V3(0.18f, 0.1f, 0.04f), 0.2f, 0),
        RobotSkin.Wood => (new V3(0.55f, 0.33f, 0.16f), new V3(0.2f, 0.11f, 0.05f), 0.35f, 0),
        RobotSkin.Marble => (new V3(0.9f, 0.9f, 0.89f), new V3(0.12f, 0.12f, 0.13f), 0.85f, 0),
        RobotSkin.Rainbow => (new V3(0.9f, 0.5f, 0.5f), new V3(0.05f, 0.05f, 0.07f), 0.5f, 0),
        RobotSkin.Lava => (new V3(0.08f, 0.05f, 0.04f), new V3(0.04f, 0.02f, 0.02f), 0.2f, 0),
        RobotSkin.Ice => (new V3(0.75f, 0.88f, 0.98f), new V3(0.1f, 0.18f, 0.28f), 1f, 0),
        RobotSkin.Police => (new V3(0.92f, 0.92f, 0.94f), new V3(0.04f, 0.06f, 0.16f), 0.5f, 0),
        _ => (new V3(0.90f, 0.90f, 0.88f), new V3(0.055f, 0.058f, 0.065f), 0.28f, 0),
    };

    struct BotPose
    {
        public V2 Pos, Shake, BrushL, BrushR;
        public float Heading, R, Scale, Alpha, Height, BrushAngle, BrushSpeed, Lidar, Bumper, LedPulse, Spin, Swing;
        public V3 Led;
        public RobotSkin Skin;
        public Look Look;
        public bool Hat;
    }

    void DrawRobot(Sim sim)
    {
        botTime = sim.T;
        DrawRobots(sim);
    }

    void DrawRobots(Sim sim)
    {
        var drop = sim.DockDrop;
        var bounce = 1 - MathF.Pow(1 - drop, 3);
        var dropScale = 1 + (1 - bounce) * 0.55f;
        var liftK = MathF.Max(sim.Lift, sim.Me?.LiftK ?? 0);
        var scale = dropScale * (1 + liftK * 0.13f);
        var shakeP = new V2(MathF.Sin(sim.T * 83), MathF.Cos(sim.T * 71)) * sim.Shake * 2.6f;
        if (sim.Picked) shakeP = new V2(MathF.Sin(sim.T * 19), MathF.Cos(sim.T * 15)) * 1.2f;
        var (l, r) = sim.BrushCenters;
        var alpha = sim.RobotAlpha * M.Smooth(0, 0.35f, drop);
        // The rivals in a race: same body, their own colours.
        foreach (var rv in sim.Rivals)
        {
            var (rl, rr) = rv.BrushCenters;
            DrawBot(new BotPose
            {
                Pos = rv.Pos, Heading = rv.Heading, R = rv.R * dropScale, Scale = dropScale, Alpha = alpha, Height = 1 - bounce,
                BrushL = rl, BrushR = rr, BrushAngle = rv.BrushAngle, BrushSpeed = rv.BrushSpeed, Lidar = rv.LidarAngle, LedPulse = rv.LedPulse,
                Led = rv.Led, Skin = rv.Skin, Look = new Look { Skin = rv.Skin },
            });
        }
        // The duel's challenger: drops in, gets flipped, and spins out when it's beaten.
        if (sim.Foe is { Alpha: > 0 } f)
        {
            var (fl, fr) = f.BrushCenters;
            var fs = dropScale * (1 + f.LiftK * 0.25f);
            DrawBot(new BotPose
            {
                Pos = f.Pos, Heading = f.Heading, R = f.R * fs, Scale = fs, Alpha = alpha * M.Clamp(f.Alpha, 0, 1), Height = f.LiftK * 0.9f,
                BrushL = fl, BrushR = fr, BrushAngle = f.BrushAngle, BrushSpeed = f.BrushSpeed, Lidar = f.LidarAngle, LedPulse = f.LedPulse, Bumper = f.Flash,
                Led = f.Down ? new V3(1, 0.12f, 0.1f) : f.Led, Skin = f.Look.Skin, Look = f.Look, Spin = f.Spin, Swing = f.Swing,
            });
        }
        // The snowplow's blade sits in front, under the bumper.
        if (sim.Plowing && sim.Phase is not (Phase.Dropping or Phase.Undocking or Phase.Docking or Phase.Emptying))
        {
            pBlade.Use(SizePts, Origin);
            Gl.BindVertexArray(emptyVao);
            pBlade.F2("center", sim.Pos + sim.Fwd * (sim.R * 1.02f) + shakeP); pBlade.F2("half_", sim.R * 0.14f, sim.R * 1.08f);
            pBlade.F("angle", sim.Heading); pBlade.F("alpha", alpha);
            var c = pBlade["color"];
            unsafe { var v = stackalloc float[3] { 0.98f, 0.6f, 0.1f }; Gl.Uniform3fv(c, 1, v); }
            Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        }
        DrawBot(new BotPose
        {
            Pos = sim.Pos, Heading = sim.Heading, R = sim.R * scale, Scale = scale, Alpha = alpha, Height = (1 - bounce) + liftK * 0.9f, Shake = shakeP,
            BrushL = l, BrushR = r, BrushAngle = sim.BrushAngle, BrushSpeed = sim.BrushSpeed, Lidar = sim.LidarAngle, Bumper = sim.Bumper, LedPulse = sim.LedPulse,
            Led = sim.Me is { Down: true } ? new V3(1, 0.12f, 0.1f) : sim.Led, Skin = Look.Skin, Hat = Look.Hat == 1, Look = sim.Me?.Look ?? Look,
            // Saw and spinner turn with the brushes; in the shop the hammer and flipper show what they do.
            Spin = sim.Me?.Spin ?? sim.BrushAngle * 1.1f,
            Swing = sim.Me?.Swing ?? (sim.Showcase ? MathF.Pow(MathF.Max(0, MathF.Sin(sim.T * 2.2f)), 3) : 0),
        });
        if (sim.Me != null)
        {
            HealthBar(sim.Pos, sim.R, sim.Me.Hp, alpha);
            if (sim.Foe is { Alpha: > 0 } f2) HealthBar(f2.Pos, f2.R, f2.Hp, alpha * M.Clamp(f2.Alpha, 0, 1));
        }
    }

    /// Above the robot, or below it when it's at the top of the screen.
    void HealthBar(V2 pos, float r, float hp, float alpha)
    {
        var c = pos + new V2(0, -(r * 1.28f + 10));
        if (c.Y - Origin.Y < 14) c = pos + new V2(0, r * 1.28f + 10);
        hp = M.Clamp(hp, 0, 1);
        var col = hp > 0.5f ? new V3(1 - (hp - 0.5f) * 2 * 0.7f, 0.9f, 0.25f) : new V3(1, 0.25f + hp * 1.3f, 0.2f);
        pBar.Use(SizePts, Origin);
        Gl.BindVertexArray(emptyVao);
        pBar.F2("center", c); pBar.F2("half_", r * 0.7f, 5); pBar.F("fill", hp); pBar.F("alpha", alpha);
        unsafe { var v = stackalloc float[3] { col.X, col.Y, col.Z }; Gl.Uniform3fv(pBar["color"], 1, v); }
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
    }

    /// Fighting parts: layer 0 sits under the shell (saw, wedge, flipper, armour, spikes), layer 1 on the lid (hammer, spinner).
    void Weapon(BotPose b, int layer)
    {
        var lk = b.Look;
        var under = lk.Armor || lk.Spikes || lk.Weapon is 1 or 3 or 5;
        var over = lk.Weapon is 2 or 4;
        if (layer == 0 ? !under : !over) return;
        pWeapon.Use(SizePts, Origin);
        Gl.BindVertexArray(emptyVao);
        pWeapon.F2("center", b.Pos + b.Shake); pWeapon.F("radius", b.R); pWeapon.F("angle", b.Heading);
        pWeapon.I("kind", lk.Weapon); pWeapon.I("layer", layer); pWeapon.I("armor", lk.Armor ? 1 : 0); pWeapon.I("spikes", lk.Spikes ? 1 : 0);
        pWeapon.F("spin", b.Spin); pWeapon.F("swing", b.Swing); pWeapon.F("alpha", b.Alpha);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
    }

    void DrawBot(BotPose b)
    {
        var pos = b.Pos; var R = b.R; var alpha = b.Alpha; var height = b.Height;
        // Two-part shadow: a tight contact core and a wide soft one.
        Shadow(pos, R * 0.985f, 0.40f * (1 - height * 0.5f) * alpha, new V2(2, 3) + new V2(26, 44) * height, 7 + 18 * height, 0, V2.Zero);
        Shadow(pos, R * 0.985f, 0.30f * (1 - height * 0.5f) * alpha, new V2(10, 16) + new V2(34, 58) * height, 26 + 34 * height, 0, V2.Zero);
        // Side brushes reach out from under the shell.
        pBrush.Use(SizePts, Origin);
        Gl.BindVertexArray(emptyVao);
        for (int i = 0; i < 2; i++)
        {
            var c = i == 0 ? b.BrushL : b.BrushR;
            pBrush.F2("center", pos + (c - pos) * b.Scale + b.Shake); pBrush.F("radius", R * 0.56f); pBrush.F("angle", b.BrushAngle);
            pBrush.F("speed", b.BrushSpeed); pBrush.F("alpha", alpha); pBrush.F("sign_", i == 0 ? 1 : -1);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        }
        Weapon(b, 0);
        var sk = SkinOf(b.Skin);
        pRobot.Use(SizePts, Origin);
        pRobot.F2("center", pos); pRobot.F("radius", R); pRobot.F2("shake", b.Shake);
        pRobot.F("heading", b.Heading); pRobot.F("lidar", b.Lidar); pRobot.F("bumper", b.Bumper); pRobot.F("ledPulse", b.LedPulse);
        pRobot.F4("led", b.Led.X, b.Led.Y, b.Led.Z, 1); pRobot.F("alpha", alpha);
        unsafe
        {
            var body = stackalloc float[3] { sk.body.X, sk.body.Y, sk.body.Z };
            var plate = stackalloc float[3] { sk.plate.X, sk.plate.Y, sk.plate.Z };
            Gl.Uniform3fv(pRobot["bodyCol"], 1, body); Gl.Uniform3fv(pRobot["plateCol"], 1, plate);
        }
        pRobot.F("bodyGloss", sk.gloss); pRobot.F("bodyMetal", sk.metal); pRobot.F("hat", 0); pRobot.F("ears", b.Skin == RobotSkin.Squirrel ? 1 : 0);
        var lk = b.Look;
        Gl.Uniform1i(pRobot["hatKind"], lk.Hat);
        Gl.Uniform1i(pRobot["pattern"], b.Skin switch
        {
            RobotSkin.Carbon => 1, RobotSkin.Camo => 2, RobotSkin.Galaxy => 3, RobotSkin.Neon => 4, RobotSkin.Zebra => 5, RobotSkin.Leopard => 6,
            RobotSkin.Wood => 7, RobotSkin.Marble => 8, RobotSkin.Rainbow => 9, RobotSkin.Lava => 10, RobotSkin.Ice => 11, RobotSkin.Police => 12, _ => 0,
        });
        pRobot.F("lightbar", lk.Lightbar ? 1 : 0);
        pRobot.F("spoiler", lk.Spoiler ? 1 : 0);
        Gl.Uniform1i(pRobot["decal"], lk.Decal);
        var gc = lk.GlowColor switch
        {
            1 => new V3(1f, 0.2f, 0.7f), 2 => new V3(0.2f, 1f, 0.35f),
            3 => new V3(0.5f + 0.5f * MathF.Cos(botTime * 2), 0.5f + 0.5f * MathF.Cos(botTime * 2 - 2.1f), 0.5f + 0.5f * MathF.Cos(botTime * 2 + 2.1f)),
            _ => new V3(0.15f, 0.55f, 1f),
        };
        pRobot.F4("glow", gc.X, gc.Y, gc.Z, lk.Glow ? 1 : 0);
        pRobot.F("time", botTime);
        pRobot.F("quadScale", lk.Spoiler ? 1.34f : lk.Glow ? 1.32f : 1.03f);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, logoTex); pRobot.I("logo", 0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        Weapon(b, 1);
    }

    void StampTrack(Sim sim, float dt)
    {
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, trackFbo);
        Gl.Viewport(0, 0, trackW, trackH);
        Gl.BindVertexArray(emptyVao);
        // Very slow fade of the nap, so a run stays visible and then relaxes.
        fadeAcc += dt;
        if (fadeAcc > 0.28f)
        {
            fadeAcc = 0;
            Gl.BlendEquation(Gl.FUNC_REVERSE_SUBTRACT);
            Gl.BlendFunc(Gl.ONE, Gl.ONE);
            Gl.ColorMask(1, 0, 0, 0);
            pFade.Use(SizePts, Origin); pFade.F("amount", 1 / 255f);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
            Gl.ColorMask(1, 1, 1, 1);
            Gl.BlendEquation(Gl.FUNC_ADD);
            Gl.BlendFunc(Gl.ONE, Gl.ONE_MINUS_SRC_ALPHA);
        }
        if (sim.SuctionOn && MathF.Abs(sim.V) > 8 && !sim.Picked)
        {
            // Pattern follows travel direction so opposite passes read as lighter and darker lanes.
            var dirv = sim.Fwd;
            var dir = 0.5f + 0.5f * (MathF.Abs(dirv.X) > MathF.Abs(dirv.Y) ? (dirv.X > 0 ? 1 : -1) : (dirv.Y > 0 ? 0.5f : -0.5f));
            var from = lastStamp ?? sim.Pos;
            var segLen = (sim.Pos - from).Len;
            Gl.Disable(Gl.BLEND);
            pStamp.Use(SizePts, Origin);
            pStamp.F2("center", (from + sim.Pos) * 0.5f - sim.Fwd * (sim.R * 0.2f)); pStamp.F2("half_", segLen * 0.5f + 8, sim.R * 0.86f);
            pStamp.F("angle", sim.Heading); pStamp.F("dir", dir);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
            Gl.Enable(Gl.BLEND);
        }
        lastStamp = sim.Pos;
    }
}
