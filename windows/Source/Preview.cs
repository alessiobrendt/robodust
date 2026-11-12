using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PixelFormats = System.Windows.Media.PixelFormats;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Color = System.Drawing.Color;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace RoboDust;

/// The shop's fitting room: the real robot, drawn by the real renderer into a small picture, slowly
/// turning so every part can be seen, with the pet sitting beside it.
public sealed unsafe class ShopPreview : IDisposable
{
    public const int W = 720, H = 440;        // pixels; drawn at half that in the panel
    static readonly V2 Size = new(500, 306);   // points: room round the robot for a spoiler and a pet
    Forms.Form host;
    IntPtr dc, ctx;
    Renderer r;
    Sim sim;
    uint fbo, tex;
    WriteableBitmap bmp;
    readonly byte[] px = new byte[W * H * 4];
    readonly Random rnd = new();

    public bool Ready => r != null;

    public ShopPreview()
    {
        try
        {
            host = new Forms.Form { ShowInTaskbar = false, FormBorderStyle = Forms.FormBorderStyle.None, Size = new System.Drawing.Size(32, 32) };
            host.CreateControl();
            (dc, ctx) = Gl.CreateContext(host.Handle);
            if (Sprites.TexScale < 2) Sprites.TexScale = 2;
            r = new Renderer(Size);
            using (var floor = Floor()) r.SetWallpaper(floor);
            r.SetSprites(new List<Sprite>());
            r.LidarRadius = 1;
            tex = Gl.Gen(Gl.GenTextures);
            Gl.BindTexture(Gl.TEXTURE_2D, tex);
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, W, H, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
            fbo = Gl.Gen(Gl.GenFramebuffers);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
            sim = new Sim(Size, V2.Zero, Size, new List<DesktopEntry>()) { DockDrop = 1, DockAlpha = 0 };
            bmp = new WriteableBitmap(W, H, 192, 192, PixelFormats.Bgra32, null);
        }
        catch { r = null; }
    }

    /// A soft studio floor, so the colours of the parts read clearly.
    static Bitmap Floor()
    {
        var b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(b);
        using var path = new GraphicsPath();
        path.AddEllipse(-W * 0.3f, -H * 0.4f, W * 1.6f, H * 1.8f);
        using var br = new PathGradientBrush(path) { CenterColor = Color.FromArgb(78, 84, 104), SurroundColors = new[] { Color.FromArgb(30, 32, 42) } };
        g.Clear(Color.FromArgb(30, 32, 42));
        g.FillPath(br, path);
        return b;
    }

    /// One frame of the robot wearing this look, t seconds into the turn.
    public BitmapSource Render(Look look, float t, float dt)
    {
        if (r == null) return null;
        Gl.wglMakeCurrent(dc, ctx);
        var s = sim;
        s.Look = look; r.Look = look;
        s.T = t;
        // It drives a slow circle, so trails show; without a trail it just turns on the spot.
        var drive = look.Trail != 0;
        var ang = t * 0.9f;
        var lastPos = s.Pos;
        s.Pos = drive ? new V2(210, 150) + new V2(MathF.Cos(ang), MathF.Sin(ang)) * 48 : new V2(210, 150);
        s.Heading = drive ? ang + MathF.PI / 2 : -MathF.PI / 2 + t * 0.55f;
        if (drive) s.EmitTrail((s.Pos - lastPos).Len < 50 ? (s.Pos - lastPos).Len : 0);
        s.BrushSpeed = 22; s.BrushAngle = t * 22; s.LidarAngle = t * 12; s.LedPulse = t;
        s.Led = look.Skin == RobotSkin.Neon ? new V3(0.1f, 1, 0.85f) : new V3(0.2f, 0.85f, 1);
        // The pet sits beside it and watches it turn.
        if (look.Pet != PetKind.None)
        {
            s.Pet ??= new Pet();
            s.Pet.Kind = look.Pet; s.Pet.Pos = new V2(400, 175); s.Pet.Frame = 4;
            var d = s.Pos - s.Pet.Pos; s.Pet.Heading = MathF.Atan2(d.Y, d.X);
        }
        else s.Pet = null;
        // Exhaust flames, so they can be seen too: the preview moves its own little particles.
        for (int i = s.Particles.Count - 1; i >= 0; i--)
        {
            var q = s.Particles[i];
            q.Life -= dt; q.Pos += q.Vel * dt; q.Vel *= MathF.Exp(-dt * 2.6f);
            if (q.Life <= 0) s.Particles.RemoveAt(i); else s.Particles[i] = q;
        }
        if (look.Exhaust)
            for (int k = 0; k < 3; k++)
            {
                var side = rnd.NextDouble() < 0.5 ? -1 : 1;
                var at = s.Pos - s.Fwd * (s.R * 1.02f) + s.Side * (s.R * 0.28f * side);
                var v = (-s.Fwd).Rotated((float)(rnd.NextDouble() - 0.5) * 0.5f) * (150 + (float)rnd.NextDouble() * 170);
                s.Particles.Add(new Particle { Pos = at, Vel = v, Life = 0.25f, MaxLife = 0.25f, Size = 5, R = 1, G = 0.85f, B = 0.4f, A = 1, Kind = PartKind.Fire });
            }
        r.Encode(s, dt, fbo, W, H);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        Gl.PixelStorei(Gl.PACK_ALIGNMENT, 4);
        fixed (byte* p = px) Gl.ReadPixels(0, 0, W, H, Gl.BGRA, Gl.UNSIGNED_BYTE, p);
        // OpenGL reads bottom-up; the picture wants top-down, and fully opaque.
        bmp.Lock();
        var dst = (byte*)bmp.BackBuffer;
        for (int y = 0; y < H; y++)
        {
            fixed (byte* src = &px[(H - 1 - y) * W * 4])
            {
                var row = dst + y * bmp.BackBufferStride;
                Buffer.MemoryCopy(src, row, W * 4, W * 4);
                for (int x = 0; x < W; x++) row[x * 4 + 3] = 255;
            }
        }
        bmp.AddDirtyRect(new System.Windows.Int32Rect(0, 0, W, H));
        bmp.Unlock();
        return bmp;
    }

    public void Dispose()
    {
        if (ctx != IntPtr.Zero) { Gl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); Gl.wglDeleteContext(ctx); ctx = IntPtr.Zero; }
        host?.Dispose(); host = null;
    }
}
