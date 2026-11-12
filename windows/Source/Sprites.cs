using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RoboDust;

/// Two premultiplied BGRA bitmaps per item (icon + label), drawn to match Explorer's desktop.
public sealed class Sprite
{
    public byte[] Icon, Label;
    public V3 Average;
}

public static class Sprites
{
    public static float TexScale = 2;
    public static int IconTex => (int)MathF.Round(Metrics.IconHalf * 2 * TexScale);
    public static int LabelW => (int)MathF.Round(Metrics.LabelHalf.X * 2 * TexScale);
    public static int LabelH => (int)MathF.Round(Metrics.LabelHalf.Y * 2 * TexScale);

    public static byte[] Pixels(Bitmap bmp)
    {
        var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var bytes = new byte[bmp.Width * bmp.Height * 4];
        for (int y = 0; y < bmp.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, bytes, y * bmp.Width * 4, bmp.Width * 4);
        bmp.UnlockBits(d);
        return bytes;
    }

    public static Bitmap FromPixels(byte[] px, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
        bmp.UnlockBits(d);
        return bmp;
    }

    public static Bitmap SampleThumbnail(int seed, bool tall)
    {
        var rng = new SeededRng((ulong)(seed + 91));
        int w = 256, h = tall ? 150 : 200;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color C(int a = 255) => Color.FromArgb(a, (int)(rng.Range(0.15f, 0.95f) * 255), (int)(rng.Range(0.2f, 0.9f) * 255), (int)(rng.Range(0.2f, 0.95f) * 255));
        using (var lg = new LinearGradientBrush(new Point(0, 0), new Point(w, h), C(), C()))
        {
            lg.InterpolationColors = new ColorBlend { Colors = new[] { C(), C(), C() }, Positions = new[] { 0f, 0.55f, 1f } };
            g.FillRectangle(lg, 0, 0, w, h);
        }
        if (tall)
        {
            // A window in a screenshot: title bar, then lines of text.
            g.FillRectangle(new SolidBrush(Color.FromArgb(250, 250, 250)), 18, 18, w - 36, h - 36);
            g.FillRectangle(new SolidBrush(Color.FromArgb(214, 214, 214)), 18, 18, w - 36, 20);
            for (int i = 0; i < 4; i++) g.FillRectangle(new SolidBrush(Color.FromArgb(190, 190, 190)), 34, 50 + i * 18, rng.Int(80, 170), 8);
        }
        else
        {
            for (int i = 0; i < 6; i++)
            {
                var d = rng.Range(40, 120);
                using var b = new SolidBrush(C(128));
                g.FillEllipse(b, rng.Range(-20, 220), rng.Range(-20, 170), d, d);
            }
        }
        return bmp;
    }

    static Rectangle Fit(Size s, Rectangle box)
    {
        var k = MathF.Min((float)box.Width / s.Width, (float)box.Height / s.Height);
        int w = (int)MathF.Round(s.Width * k), h = (int)MathF.Round(s.Height * k);
        return new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }

    public static Sprite Make(DesktopEntry e)
    {
        var px = IconTex;
        using var bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(0, 0, px, px);
            Bitmap src = null;
            bool owned = false;
            if (e.Image != null)
            {
                // Sample pictures: a thumbnail with a hairline frame and a soft shadow.
                var f = Fit(e.Image.Size, Rectangle.Inflate(box, -(int)(3 * TexScale), -(int)(3 * TexScale)));
                using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(sh, f.X + 1, f.Y + 2, f.Width, f.Height);
                g.DrawImage(e.Image, f);
                using var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 1);
                g.DrawRectangle(pen, f.X, f.Y, f.Width - 1, f.Height - 1);
            }
            else
            {
                if (e.Pidl != null) { src = ShellImage(e.Pidl, px); owned = true; }
                if (src == null) { src = TypeIcon(e); owned = true; }
                if (src != null) g.DrawImage(src, Fit(src.Size, box));
            }
            if (owned) src?.Dispose();
        }
        var icon = Pixels(bmp);
        return new Sprite { Icon = icon, Label = Label(e.Name), Average = AverageOf(icon) };
    }

    /// The same picture Explorer shows: a thumbnail for images and videos, the icon otherwise.
    static Bitmap ShellImage(byte[] pidlBytes, int px)
    {
        var pidl = Marshal.AllocCoTaskMem(pidlBytes.Length);
        try
        {
            Marshal.Copy(pidlBytes, 0, pidl, pidlBytes.Length);
            var iid = Native.IID_IShellItem;
            if (Native.SHCreateItemFromIDList(pidl, ref iid, out var obj) != 0 || obj is not IShellItemImageFactory f) return null;
            try
            {
                if (f.GetImage(new SIZE(px, px), Native.SIIGBF_RESIZETOFIT, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
                try { return FromHBitmap(hbm); } finally { Native.DeleteObject(hbm); }
            }
            finally { Marshal.ReleaseComObject(f); }
        }
        catch { return null; }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    static Bitmap FromHBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bmi = new Native.BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bits = new byte[w * h * 4];
        var dc = Native.GetDC(IntPtr.Zero);
        try { if (Native.GetDIBits(dc, hbm, 0, (uint)h, bits, ref bmi, 0) == 0) return null; }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }
        // Thumbnails come without alpha, icons with it, sometimes premultiplied and sometimes not.
        bool anyAlpha = false, straight = false;
        for (int i = 0; i < bits.Length; i += 4)
        {
            var a = bits[i + 3];
            if (a != 0) anyAlpha = true;
            if (bits[i] > a || bits[i + 1] > a || bits[i + 2] > a) straight = true;
        }
        for (int i = 0; i < bits.Length; i += 4)
        {
            if (!anyAlpha) { bits[i + 3] = 255; continue; }
            if (straight)
            {
                var a = bits[i + 3];
                bits[i] = (byte)(bits[i] * a / 255); bits[i + 1] = (byte)(bits[i + 1] * a / 255); bits[i + 2] = (byte)(bits[i + 2] * a / 255);
            }
        }
        return FromPixels(bits, w, h);
    }

    /// For the sample mess: the system's icon for that kind of file, without any file behind it.
    static Bitmap TypeIcon(DesktopEntry e)
    {
        try
        {
            var info = new Native.SHFILEINFO();
            var isDir = e.Kind == Kind.Folder;
            var name = isDir ? "folder" : "x" + Path.GetExtension(e.Name);
            Native.SHGetFileInfo(name, isDir ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_SYSICONINDEX | Native.SHGFI_USEFILEATTRIBUTES);
            var iid = Native.IID_IImageList;
            if (Native.SHGetImageList(Native.SHIL_JUMBO, ref iid, out var list) != 0) return null;
            var h = Native.ImageList_GetIcon(list, info.iIcon, 1);
            if (h == IntPtr.Zero) return null;
            try
            {
                using var ico = Icon.FromHandle(h);
                var bmp = ico.ToBitmap();
                return Trim(bmp);
            }
            finally { Native.DestroyIcon(h); }
        }
        catch { return null; }
    }

    /// Jumbo icons for some types are small art in the corner of a 256 canvas; crop to what's drawn.
    static Bitmap Trim(Bitmap src)
    {
        var px = Pixels(src);
        int w = src.Width, h = src.Height, minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            if (px[(y * w + x) * 4 + 3] > 8) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX < 0 || (maxX - minX > w * 0.6 && maxY - minY > h * 0.6)) return src;
        var side = Math.Max(maxX - minX, maxY - minY) + 1;
        var cx = (minX + maxX) / 2; var cy = (minY + maxY) / 2;
        var outBmp = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(outBmp)) g.DrawImage(src, new Rectangle(0, 0, side, side), new Rectangle(cx - side / 2, cy - side / 2, side, side), GraphicsUnit.Pixel);
        src.Dispose();
        return outBmp;
    }

    /// White Segoe UI with a soft shadow, two lines at most, ending in an ellipsis: how desktop labels look.
    public static byte[] Label(string name)
    {
        int w = LabelW, h = LabelH;
        var ts = TexScale;
        using var font = new Font("Segoe UI", 12 * ts, FontStyle.Regular, GraphicsUnit.Pixel);
        var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter };
        var lineH = 16 * ts;
        var width = w - 4 * ts;
        using var shadow = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        List<string> lines;
        using (var g = Graphics.FromImage(shadow))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            lines = Wrap(name, width, s => g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width);
            for (int i = 0; i < lines.Count; i++) g.DrawString(lines[i], font, Brushes.Black, new RectangleF(2 * ts, (2 + i * 16) * ts, width, lineH + 4 * ts), fmt);
        }
        var sh = Pixels(shadow);
        Blur(sh, w, h, Math.Max(1, (int)MathF.Round(1.5f * ts)));
        using var bmp = FromPixels(sh, w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            for (int i = 0; i < lines.Count; i++) g.DrawString(lines[i], font, Brushes.White, new RectangleF(2 * ts, (1 + i * 16) * ts, width, lineH + 4 * ts), fmt);
        }
        return Pixels(bmp);
    }

    /// Two lines at most. Long names break mid-word if they must, preferring a space, dash or
    /// underscore; the second line ends in an ellipsis when it doesn't fit. That's how Explorer does it.
    static List<string> Wrap(string text, float width, Func<string, float> measure)
    {
        if (measure(text) <= width) return new List<string> { text };
        int fit = 1;
        while (fit < text.Length && measure(text[..(fit + 1)]) <= width) fit++;
        var cut = fit;
        for (int i = fit; i > fit / 3; i--) if (" -_.".Contains(text[i - 1])) { cut = i; break; }
        var first = text[..cut].TrimEnd();
        var rest = text[cut..].TrimStart();
        return rest.Length == 0 ? new List<string> { first } : new List<string> { first, rest };
    }

    /// Box blur of a black shadow's alpha, twice, which is close enough to Gaussian.
    static void Blur(byte[] px, int w, int h, int r)
    {
        var a = new float[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = px[i * 4 + 3];
        var t = new float[w * h];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int k = -r; k <= r; k++) { var xx = x + k; if (xx < 0 || xx >= w) continue; s += a[y * w + xx]; n++; }
                t[y * w + x] = s / n;
            }
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int k = -r; k <= r; k++) { var yy = y + k; if (yy < 0 || yy >= h) continue; s += t[yy * w + x]; n++; }
                a[y * w + x] = s / n;
            }
        }
        for (int i = 0; i < a.Length; i++) { px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = 0; px[i * 4 + 3] = (byte)M.Clamp(a[i] * 1.15f, 0, 255); }
    }

    static V3 AverageOf(byte[] px)
    {
        double r = 0, g = 0, b = 0, a = 0;
        for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; a += px[i + 3]; }
        if (a < 1) return new V3(0.6f, 0.6f, 0.6f);
        return new V3((float)(r / a), (float)(g / a), (float)(b / a));
    }

    /// Builds sprites on a background thread (the shell wants a single-threaded apartment), reporting progress.
    public static void Build(List<DesktopEntry> entries, Action<int> progress, Action<List<Sprite>> done, System.Windows.Threading.Dispatcher ui)
    {
        var t = new Thread(() =>
        {
            var list = new List<Sprite>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                Sprite s;
                try { s = Make(entries[i]); }
                catch { s = new Sprite { Icon = new byte[IconTex * IconTex * 4], Label = new byte[LabelW * LabelH * 4], Average = new V3(0.6f, 0.6f, 0.6f) }; }
                list.Add(s);
                var n = i + 1;
                if (n % 4 == 0 || n == entries.Count) ui.BeginInvoke(() => progress(n));
            }
            ui.BeginInvoke(() => done(list));
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    /// Blocking version, for tests and headless renders.
    public static List<Sprite> BuildNow(List<DesktopEntry> entries)
    {
        var list = new List<Sprite>();
        foreach (var e in entries) list.Add(Make(e));
        return list;
    }
}

public static class Wallpaper
{
    /// The wallpaper as it looks on the primary display, cropped to the work area.
    public static Bitmap Capture(Screen screen)
    {
        var mon = screen.Bounds;
        string path = null; int position = 4; uint bg = 0;
        Rectangle virt = System.Windows.Forms.SystemInformation.VirtualScreen;
        try
        {
            var dw = (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(Native.CLSID_DesktopWallpaper));
            var n = dw.GetMonitorDevicePathCount();
            for (uint i = 0; i < n; i++)
            {
                var id = dw.GetMonitorDevicePathAt(i);
                var r = dw.GetMonitorRECT(id);
                if (r.Left == mon.Left && r.Top == mon.Top && r.Width == mon.Width && r.Height == mon.Height) { path = dw.GetWallpaper(id); break; }
            }
            position = dw.GetPosition();
            bg = dw.GetBackgroundColor();
            Marshal.ReleaseComObject(dw);
        }
        catch { }
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            var t = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            path = File.Exists(t) ? t : null;
        }
        var canvas = new Bitmap(mon.Width, mon.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.FromArgb((int)(bg & 0xFF), (int)((bg >> 8) & 0xFF), (int)((bg >> 16) & 0xFF)));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Image img = null;
            try { if (path != null) using (var fs = File.OpenRead(path)) img = Image.FromStream(new MemoryStream(ReadAll(fs))); } catch { img = null; }
            if (img != null)
            {
                using (img)
                {
                    float iw = img.Width, ih = img.Height;
                    switch (position)
                    {
                        case 0: // centre
                            g.DrawImage(img, (mon.Width - iw) / 2, (mon.Height - ih) / 2, iw, ih); break;
                        case 1: // tile
                            for (float y = 0; y < mon.Height; y += ih) for (float x = 0; x < mon.Width; x += iw) g.DrawImage(img, x, y, iw, ih);
                            break;
                        case 2: // stretch
                            g.DrawImage(img, 0, 0, mon.Width, mon.Height); break;
                        case 3: // fit
                        {
                            var k = MathF.Min(mon.Width / iw, mon.Height / ih);
                            g.DrawImage(img, (mon.Width - iw * k) / 2, (mon.Height - ih * k) / 2, iw * k, ih * k); break;
                        }
                        case 5: // span: fill the whole virtual screen, of which this monitor is a part
                        {
                            var k = MathF.Max(virt.Width / iw, virt.Height / ih);
                            float ox = virt.Left + (virt.Width - iw * k) / 2 - mon.Left, oy = virt.Top + (virt.Height - ih * k) / 2 - mon.Top;
                            g.DrawImage(img, ox, oy, iw * k, ih * k); break;
                        }
                        default: // fill
                        {
                            var k = MathF.Max(mon.Width / iw, mon.Height / ih);
                            g.DrawImage(img, (mon.Width - iw * k) / 2, (mon.Height - ih * k) / 2, iw * k, ih * k); break;
                        }
                    }
                }
            }
        }
        var wa = screen.WorkArea;
        var crop = new Bitmap(wa.Width, wa.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(crop)) g.DrawImage(canvas, new Rectangle(0, 0, wa.Width, wa.Height), new Rectangle(wa.Left - mon.Left, wa.Top - mon.Top, wa.Width, wa.Height), GraphicsUnit.Pixel);
        canvas.Dispose();
        return crop;
    }

    static byte[] ReadAll(Stream s) { using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }

    /// Procedural floor for when no wallpaper can be read, and for headless renders.
    public static Bitmap Fallback(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using var lg = new LinearGradientBrush(new Point(0, 0), new Point(w, h), Color.FromArgb(33, 56, 107), Color.FromArgb(107, 82, 128));
        g.FillRectangle(lg, 0, 0, w, h);
        return bmp;
    }
}
