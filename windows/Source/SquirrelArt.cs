using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RoboDust;

/// The Eichhörnchen-Bande, drawn from above as cartoons: a few running frames, sitting, digging,
/// and the little heap of earth where a "nut" got buried. Drawn facing up the screen.
public static class SquirrelArt
{
    public const int Run0 = 0, RunFrames = 4, Sit = 4, Dig0 = 5, DigFrames = 2, Mound = 7, Count = 8;

    static readonly Color Fur = Color.FromArgb(196, 88, 31), FurDark = Color.FromArgb(132, 54, 16), FurLight = Color.FromArgb(232, 140, 78);
    static readonly Color TailCol = Color.FromArgb(186, 82, 30), TailTip = Color.FromArgb(242, 168, 98);

    /// All frames, as premultiplied BGRA at the icon texture size, in the order of the constants above.
    public static List<byte[]> Frames()
    {
        var list = new List<byte[]>();
        for (int i = 0; i < RunFrames; i++) list.Add(Render(g => Squirrel(g, run: i, sit: false, dig: -1)));
        list.Add(Render(g => Squirrel(g, run: 0, sit: true, dig: -1)));
        for (int i = 0; i < DigFrames; i++) list.Add(Render(g => Squirrel(g, run: 0, sit: false, dig: i)));
        list.Add(Render(MoundArt));
        return list;
    }

    static byte[] Render(Action<Graphics> draw)
    {
        const int S = 256;
        using var big = new Bitmap(S, S, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            draw(g);
        }
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

    static void Ellipse(Graphics g, Color c, float cx, float cy, float w, float h, float angle = 0)
    {
        var st = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(angle);
        using var b = new SolidBrush(c);
        g.FillEllipse(b, -w / 2, -h / 2, w, h);
        g.Restore(st);
    }

    static void Shaded(Graphics g, Color c, Color light, float cx, float cy, float w, float h, float angle = 0)
    {
        var st = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(angle);
        using var path = new GraphicsPath();
        path.AddEllipse(-w / 2, -h / 2, w, h);
        using var b = new PathGradientBrush(path) { CenterColor = light, SurroundColors = new[] { c }, CenterPoint = new PointF(-w * 0.15f, -h * 0.18f) };
        g.FillPath(b, path);
        g.Restore(st);
    }

    /// run: which running frame; sit: upright with the tail curled over the back; dig: digging frame, or -1.
    static void Squirrel(Graphics g, int run, bool sit, int dig)
    {
        float phase = run / (float)RunFrames * MathF.PI * 2;
        // The tail: a big bushy S, swaying while it runs. Sitting, it curls forward over the back.
        var sway = sit ? 0 : MathF.Sin(phase) * 18;
        int n = 9;
        for (int i = n - 1; i >= 0; i--)
        {
            float t = i / (float)(n - 1);
            float x, y, r;
            if (sit)
            {
                var a = MathF.PI * 0.5f + t * MathF.PI * 1.25f;
                x = 128 + MathF.Cos(a) * 46 * (0.4f + t * 0.6f) + 18;
                y = 168 - t * 70 + MathF.Sin(a) * 18;
                r = 26 + 14 * MathF.Sin(t * MathF.PI);
            }
            else
            {
                x = 128 + MathF.Sin(t * 3.2f + phase) * (10 + sway * t);
                y = 168 + t * 74;
                r = 24 + 16 * MathF.Sin(t * MathF.PI * 0.9f + 0.2f);
            }
            Shaded(g, TailCol, i > n - 3 ? TailTip : FurLight, x, y, r * 1.9f, r * 1.7f);
        }
        // Fur strokes on the tail, so it reads as bushy.
        using (var pen = new Pen(Color.FromArgb(90, 250, 200, 150), 2.2f))
            for (int i = 0; i < 7; i++)
            {
                float t = (i + 0.5f) / 7;
                var cx = sit ? 140 : 128 + MathF.Sin(t * 3.2f + phase) * (10 + sway * t);
                var cy = sit ? 168 - t * 70 : 168 + t * 74;
                g.DrawArc(pen, cx - 22, cy - 14, 44, 28, 200, 140);
            }

        // Paws: front ones reach forward, back ones push off. Digging, the front paws scrabble.
        float f1 = MathF.Sin(phase) * 12, f2 = -f1;
        if (dig >= 0) { f1 = dig == 0 ? -14 : 6; f2 = -f1; }
        if (sit) { f1 = 6; f2 = 6; }
        Ellipse(g, FurDark, 104, 104 + f1, 16, 22, -15);
        Ellipse(g, FurDark, 152, 104 + f2, 16, 22, 15);
        Ellipse(g, FurDark, 98, 178 - f1 * 0.7f, 18, 26, 10);
        Ellipse(g, FurDark, 158, 178 - f2 * 0.7f, 18, 26, -10);

        // Haunches, body, a darker stripe down the back.
        Shaded(g, Fur, FurLight, 102, 160, 34, 46, 10);
        Shaded(g, Fur, FurLight, 154, 160, 34, 46, -10);
        Shaded(g, Fur, FurLight, 128, 140, sit ? 70 : 62, sit ? 76 : 88);
        using (var stripe = new SolidBrush(Color.FromArgb(110, FurDark))) g.FillEllipse(stripe, 116, 112, 24, 62);

        // Head with ear tufts, bright eyes and a dark nose: the front of the face is up.
        foreach (var s in new[] { -1, 1 })
        {
            var ear = new[] { new PointF(128 + s * 14, 70), new PointF(128 + s * 26, 34), new PointF(128 + s * 34, 66) };
            using (var b = new SolidBrush(Fur)) g.FillPolygon(b, ear);
            var inner = new[] { new PointF(128 + s * 18, 66), new PointF(128 + s * 26, 44), new PointF(128 + s * 30, 64) };
            using (var b = new SolidBrush(Color.FromArgb(214, 150, 120))) g.FillPolygon(b, inner);
            using var tuft = new Pen(FurDark, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(tuft, 128 + s * 26, 36, 128 + s * 24, 24);
            g.DrawLine(tuft, 128 + s * 27, 36, 128 + s * 31, 26);
        }
        Shaded(g, Fur, FurLight, 128, 84, 56, 60);
        Ellipse(g, Color.FromArgb(244, 214, 176), 128, 66, 30, 22);                 // pale muzzle
        Ellipse(g, Color.FromArgb(28, 14, 8), 110, 78, 11, 12);                      // eyes
        Ellipse(g, Color.FromArgb(28, 14, 8), 146, 78, 11, 12);
        Ellipse(g, Color.White, 108, 75, 4, 4);
        Ellipse(g, Color.White, 144, 75, 4, 4);
        Ellipse(g, Color.FromArgb(60, 28, 18), 128, 57, 12, 8);                      // nose
    }

    static void MoundArt(Graphics g)
    {
        // A small heap of fresh earth, a few crumbs around it, and a leaf on top.
        for (int i = 0; i < 14; i++)
        {
            var a = i * 2.4f; var d = 70 + (i * 37 % 20);
            Ellipse(g, Color.FromArgb(150, 92, 60, 36), 128 + MathF.Cos(a) * d, 132 + MathF.Sin(a) * d * 0.8f, 10 + i % 4 * 3, 8 + i % 3 * 3, i * 40);
        }
        Shaded(g, Color.FromArgb(96, 60, 34), Color.FromArgb(150, 104, 66), 128, 132, 150, 118);
        Shaded(g, Color.FromArgb(110, 70, 40), Color.FromArgb(168, 120, 78), 120, 124, 96, 72);
        using (var pen = new Pen(Color.FromArgb(70, 40, 22, 10), 3))
        {
            g.DrawArc(pen, 70, 96, 110, 70, 200, 60);
            g.DrawArc(pen, 90, 120, 90, 50, 10, 70);
        }
        // A leaf, and an acorn cap peeking out.
        var st = g.Save();
        g.TranslateTransform(160, 104); g.RotateTransform(-30);
        using (var leaf = new SolidBrush(Color.FromArgb(104, 150, 58))) g.FillEllipse(leaf, -22, -9, 44, 18);
        using (var vein = new Pen(Color.FromArgb(70, 110, 40), 2)) g.DrawLine(vein, -20, 0, 20, 0);
        g.Restore(st);
        Ellipse(g, Color.FromArgb(120, 82, 40), 104, 140, 26, 18);
        Ellipse(g, Color.FromArgb(160, 110, 56), 104, 137, 18, 10);
    }
}
