using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RoboDust;

/// Pets from the shop, and the coins lying about, drawn from above as cartoons facing up the screen.
/// Each pet has four running frames and one sitting; the coin has four frames of a spin.
public static class PetArt
{
    public const int FramesPerPet = 5, Sit = 4, CoinFrames = 4;
    public static readonly PetKind[] Order = { PetKind.Dachshund, PetKind.Golden, PetKind.Cat };

    public static List<byte[]> Frames()
    {
        var list = new List<byte[]>();
        foreach (var p in Order)
            for (int f = 0; f < FramesPerPet; f++) { var ff = f; list.Add(Render(g => Pet(g, p, ff))); }
        for (int f = 0; f < CoinFrames; f++) { var ff = f; list.Add(Render(g => Coin(g, ff))); }
        return list;
    }

    /// Where a pet's frames start, relative to the first pet frame.
    public static int Offset(PetKind p) => Array.IndexOf(Order, p) * FramesPerPet;
    public static int CoinOffset => Order.Length * FramesPerPet;

    static byte[] Render(Action<Graphics> draw)
    {
        const int S = 256;
        using var big = new Bitmap(S, S, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(big)) { g.SmoothingMode = SmoothingMode.AntiAlias; draw(g); }
        var px = Sprites.IconTex;
        using var small = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(big, 0, 0, px, px);
        }
        return Sprites.Pixels(small);
    }

    static void Shaded(Graphics g, Color c, Color light, float cx, float cy, float w, float h, float angle = 0)
    {
        var st = g.Save();
        g.TranslateTransform(cx, cy); g.RotateTransform(angle);
        using var path = new GraphicsPath();
        path.AddEllipse(-w / 2, -h / 2, w, h);
        using var b = new PathGradientBrush(path) { CenterColor = light, SurroundColors = new[] { c }, CenterPoint = new PointF(-w * 0.15f, -h * 0.18f) };
        g.FillPath(b, path);
        g.Restore(st);
    }

    static void Dot(Graphics g, Color c, float cx, float cy, float w, float h, float angle = 0)
    {
        var st = g.Save();
        g.TranslateTransform(cx, cy); g.RotateTransform(angle);
        using var b = new SolidBrush(c);
        g.FillEllipse(b, -w / 2, -h / 2, w, h);
        g.Restore(st);
    }

    static void Pet(Graphics g, PetKind kind, int frame)
    {
        bool sit = frame == Sit;
        float phase = frame / 4f * MathF.PI * 2, swing = sit ? 0 : MathF.Sin(phase);
        // Per breed: body length and width, colours, ears, tail.
        var (len, wid, fur, light, dark) = kind switch
        {
            PetKind.Dachshund => (120f, 46f, Color.FromArgb(150, 72, 30), Color.FromArgb(196, 112, 60), Color.FromArgb(92, 42, 18)),
            PetKind.Golden => (104f, 64f, Color.FromArgb(214, 160, 82), Color.FromArgb(240, 204, 140), Color.FromArgb(170, 116, 52)),
            _ => (90f, 50f, Color.FromArgb(226, 132, 56), Color.FromArgb(246, 180, 110), Color.FromArgb(170, 84, 28)),
        };
        if (sit) len *= 0.82f;
        float bodyY = 140, headY = bodyY - len / 2 - 14;

        // Tail behind, wagging.
        var wag = swing * 22 + (sit ? 0 : MathF.Sin(phase * 2) * 8);
        if (kind == PetKind.Golden)
            for (int i = 4; i >= 0; i--) Shaded(g, fur, light, 128 + wag * i / 4f, bodyY + len / 2 + 6 + i * 13, 26 - i * 2, 30, wag * 0.5f);
        else if (kind == PetKind.Cat)
        {
            using var pen = new Pen(fur, 13) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(pen, 128, bodyY + len / 2 - 6, 128 + wag, bodyY + len / 2 + 30, 128 - wag * 1.5f, bodyY + len / 2 + 60, 128 + wag * 0.8f + 18, bodyY + len / 2 + 86);
            using var tip = new Pen(dark, 13) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(tip, 128 + wag * 0.8f + 12, bodyY + len / 2 + 78, 128 + wag * 0.8f + 18, bodyY + len / 2 + 86);
        }
        else
        {
            using var pen = new Pen(dark, 9) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(pen, 128, bodyY + len / 2 - 4, 128 + wag * 0.5f, bodyY + len / 2 + 16, 128 + wag, bodyY + len / 2 + 30, 128 + wag * 1.3f, bodyY + len / 2 + 44);
        }
        // Legs: front and back pairs, in step.
        var legLen = kind == PetKind.Dachshund ? 12f : 20f;
        foreach (var (y, sgn) in new[] { (bodyY - len * 0.32f, 1f), (bodyY + len * 0.32f, -1f) })
            foreach (var side in new[] { -1f, 1f })
                Dot(g, dark, 128 + side * (wid / 2 + 2), y + swing * legLen * sgn * side, 16, 22);
        // Body, and for the cat, tabby stripes across it.
        Shaded(g, fur, light, 128, bodyY, wid, len);
        if (kind == PetKind.Cat)
            using (var pen = new Pen(Color.FromArgb(150, dark), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = -2; i <= 2; i++) g.DrawArc(pen, 128 - wid / 2 + 4, bodyY + i * 16 - 10, wid - 8, 20, 200, 140);
        // Head, ears and face.
        var headW = kind == PetKind.Dachshund ? 40f : kind == PetKind.Golden ? 50f : 46f;
        if (kind == PetKind.Cat)
            foreach (var s in new[] { -1, 1 })
            {
                var ear = new[] { new PointF(128 + s * 8, headY - 10), new PointF(128 + s * 22, headY - 34), new PointF(128 + s * 26, headY - 4) };
                using (var b = new SolidBrush(fur)) g.FillPolygon(b, ear);
                using (var b = new SolidBrush(Color.FromArgb(230, 160, 150))) g.FillPolygon(b, new[] { new PointF(128 + s * 12, headY - 10), new PointF(128 + s * 21, headY - 26), new PointF(128 + s * 23, headY - 8) });
            }
        else
        {
            // Floppy ears hanging at the sides, flapping a little as it runs.
            foreach (var s in new[] { -1, 1 }) Shaded(g, dark, fur, 128 + s * (headW / 2 + 4), headY + 6, 18, kind == PetKind.Dachshund ? 40 : 34, s * (12 + swing * 10));
        }
        Shaded(g, fur, light, 128, headY, headW, headW * 1.1f);
        var snout = kind == PetKind.Dachshund ? 34f : kind == PetKind.Golden ? 26f : 14f;
        Shaded(g, kind == PetKind.Cat ? light : fur, light, 128, headY - headW * 0.45f - snout * 0.3f, headW * 0.5f, snout + 10);
        Dot(g, Color.FromArgb(30, 18, 12), 128, headY - headW * 0.45f - snout * 0.75f - 2, 12, 9);                // nose
        Dot(g, Color.FromArgb(30, 18, 12), 128 - headW * 0.22f, headY - headW * 0.12f, 8, 9);                   // eyes
        Dot(g, Color.FromArgb(30, 18, 12), 128 + headW * 0.22f, headY - headW * 0.12f, 8, 9);
        Dot(g, Color.White, 126 - headW * 0.22f, headY - headW * 0.15f, 3, 3);
        Dot(g, Color.White, 126 + headW * 0.22f, headY - headW * 0.15f, 3, 3);
        if (kind == PetKind.Golden && !sit)
            Dot(g, Color.FromArgb(230, 110, 120), 128, headY - headW * 0.45f - snout - 2, 12, 16);              // tongue out, happy
    }

    static void Coin(Graphics g, int frame)
    {
        // A gold coin turning: it narrows and widens again, with a star stamped on it.
        var w = 150 * MathF.Abs(MathF.Cos(frame / (float)CoinFrames * MathF.PI));
        w = MathF.Max(w, 26);
        Dot(g, Color.FromArgb(150, 100, 20), 132, 132, w, 150);
        Shaded(g, Color.FromArgb(222, 162, 30), Color.FromArgb(255, 236, 140), 128, 128, w, 150);
        Shaded(g, Color.FromArgb(240, 190, 50), Color.FromArgb(255, 245, 190), 128, 128, w * 0.72f, 108);
        if (w > 70)
        {
            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                var a = -MathF.PI / 2 + i * MathF.PI / 5; var r = i % 2 == 0 ? 34 : 14;
                pts[i] = new PointF(128 + MathF.Cos(a) * r * w / 150, 128 + MathF.Sin(a) * r);
            }
            using var b = new SolidBrush(Color.FromArgb(200, 140, 20));
            g.FillPolygon(b, pts);
        }
    }
}
