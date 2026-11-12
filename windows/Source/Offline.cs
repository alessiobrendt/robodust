using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RoboDust;

/// Renders a run without showing anything: same sim and shaders, fixed timestep, sample items only,
/// so nothing from the real desktop can end up in a frame. For checking motion and looks.
public static unsafe class Offline
{
    /// allScreens: the sample mess spread over every display, drawn as one picture of the whole floor.
    public static void Dump(float[] times, string outDir, CleanMode mode, bool plainFloor, float pixelScale, int follow, bool allScreens = false)
    {
        Directory.CreateDirectory(outDir);
        var screens = allScreens ? Screen.All() : new List<Screen> { Screen.Primary() };
        Floorplan.Layout(screens);
        var plan = Floorplan.Of(screens);
        var size = new V2(plan.Bounds.W, plan.Bounds.H);
        Sprites.TexScale = MathF.Max(2, pixelScale);
        using var host = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new System.Drawing.Size(64, 64) };
        host.CreateControl();
        var (dc, ctx) = Gl.CreateContext(host.Handle);
        Console.WriteLine("GL " + Gl.Version);
        Console.WriteLine("rooms " + string.Join(" ", plan.Rooms.Select(x => $"[{x.X:0},{x.Y:0} {x.W:0}x{x.H:0}]")));
        var r = new Renderer(size, new V2(plan.Bounds.X, plan.Bounds.Y));
        r.LidarRadius = MathF.Max(size.X, size.Y) * 0.85f;
        using (var wall = plainFloor || allScreens ? Wallpaper.Fallback((int)size.X, (int)size.Y) : Wallpaper.Capture(screens[0])) r.SetWallpaper(wall);
        var entries = DesktopReader.SampleAcross(screens);
        var sprites = Sprites.BuildNow(entries);
        r.SetSprites(sprites);
        var s = new Sim(plan, entries) { Mode = mode, SkipFinalClick = true };
        for (int i = 0; i < sprites.Count; i++) s.Items[i].Color = sprites[i].Average;

        int W = (int)(size.X * pixelScale), H = (int)(size.Y * pixelScale);
        var tex = Gl.Gen(Gl.GenTextures);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, W, H, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
        var fbo = Gl.Gen(Gl.GenFramebuffers);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE) throw new Exception("Framebuffer incomplete");

        const float dt = 1f / 60;
        var pending = new Queue<float>(times.OrderBy(t => t));
        var max = times.Length > 0 ? times.Max() : 0;
        for (float t = 0; pending.Count > 0; t += dt)
        {
            s.Step(dt / 2); s.Step(dt / 2);
            r.Encode(s, dt, fbo, W, H);
            if (pending.Count > 0 && t + 1e-4f >= pending.Peek())
            {
                var at = pending.Dequeue();
                var px = new byte[W * H * 4];
                Gl.PixelStorei(Gl.PACK_ALIGNMENT, 4);
                Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
                fixed (byte* p = px) Gl.ReadPixels(0, 0, W, H, Gl.BGRA, Gl.UNSIGNED_BYTE, p);
                using var bmp = new Bitmap(W, H, PixelFormat.Format32bppRgb);
                var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                for (int y = 0; y < H; y++) Marshal.Copy(px, (H - 1 - y) * W * 4, d.Scan0 + y * d.Stride, W * 4);
                bmp.UnlockBits(d);
                Bitmap save = bmp;
                if (follow > 0)
                {
                    // A close crop that follows the robot.
                    int fw = Math.Min(follow, W), fh = Math.Min(follow * H / W, H);
                    int cx = (int)M.Clamp(s.Pos.X * pixelScale - fw / 2, 0, W - fw), cy = (int)M.Clamp(s.Pos.Y * pixelScale - fh / 2, 0, H - fh);
                    save = bmp.Clone(new Rectangle(cx, cy, fw, fh), PixelFormat.Format32bppRgb);
                }
                var file = Path.Combine(outDir, $"frame-{at:0.0}.png");
                save.Save(file, ImageFormat.Png);
                if (save != bmp) save.Dispose();
                Console.WriteLine($"t={at:0.0} {s.Phase} collected={s.Collected}/{s.Items.Count} -> {file}");
            }
        }
        Gl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        Gl.wglDeleteContext(ctx);
    }

    /// Dev only, read-only: lays the copy of the real desktop over the real one at half strength and
    /// saves a screenshot, to check that every icon and label sits exactly on its original.
    public static void AlignTest(string outFile, int screenIndex = 0)
    {
        var screens = Screen.All();
        var screen = screens[screenIndex];
        var entries = DesktopReader.Read(screens, out var view)[screenIndex];
        Metrics.IconHalf = view.IconPt / 2;
        Metrics.LabelHalf = new V2(MathF.Max(30, view.LabelWidthPts / 2 - 1), 19);
        Sprites.TexScale = MathF.Max(2, screen.Scale);
        var sprites = Sprites.BuildNow(entries);
        using var o = new Overlay(screen) { KeepBelowWindows = false };
        o.Prepare();
        using (var wall = Wallpaper.Capture(screen)) o.Renderer.SetWallpaper(wall);
        o.Renderer.SetSprites(sprites);
        // A sim that never steps draws only the items: the dock and robot haven't dropped in yet.
        o.Sim = new Sim(screen.SizePts, V2.Zero, screen.SizePts, entries);
        var minimized = DesktopWindows.MinimizeAll();
        var wa = screen.WorkArea;
        Bitmap Shot() { var b = new Bitmap(wa.Width, wa.Height, PixelFormat.Format32bppArgb); using var g = Graphics.FromImage(b); g.CopyFromScreen(wa.Left, wa.Top, 0, 0, wa.Size); return b; }
        System.Threading.Thread.Sleep(900);
        using var real = Shot();
        o.Show();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1200) { Application.DoEvents(); RenderStill(o); System.Threading.Thread.Sleep(15); }
        using var copy = Shot();
        o.Hide();
        DesktopWindows.Restore(minimized);
        // Half and half: anything out of place shows up doubled.
        using var mix = new Bitmap(wa.Width, wa.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(mix))
        {
            g.DrawImage(real, 0, 0);
            var cm = new ColorMatrix { Matrix33 = 0.5f };
            var ia = new ImageAttributes(); ia.SetColorMatrix(cm);
            g.DrawImage(copy, new Rectangle(0, 0, wa.Width, wa.Height), 0, 0, wa.Width, wa.Height, GraphicsUnit.Pixel, ia);
        }
        mix.Save(outFile, ImageFormat.Png);
        copy.Save(Path.ChangeExtension(outFile, ".copy.png"), ImageFormat.Png);
        real.Save(Path.ChangeExtension(outFile, ".real.png"), ImageFormat.Png);
        Console.WriteLine($"items {entries.Count} icon {view.IconPt}pt spacing {view.Spacing.X}x{view.Spacing.Y} -> {outFile}");
    }

    static void RenderStill(Overlay o)
    {
        o.Renderer.Encode(o.Sim, 0, 0, o.ClientSize.Width, o.ClientSize.Height);
        o.Present();
    }
}
