// Copyright (c) 2026 Adel Naim. Licensed under the MIT License (see LICENSE).
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace TSP100Bridge;

/// <summary>A 1-bit image: Rows[y] holds WidthBytes bytes, MSB = leftmost dot, 1 = black.</summary>
public sealed class MonoImage
{
    public int WidthDots { get; }
    public int WidthBytes => (WidthDots + 7) / 8;
    public List<byte[]> Rows { get; } = new();
    public MonoImage(int widthDots) => WidthDots = widthDots;

    static bool IsBlank(byte[] r) { foreach (var b in r) if (b != 0) return false; return true; }

    public void TrimVertical()
    {
        while (Rows.Count > 0 && IsBlank(Rows[^1])) Rows.RemoveAt(Rows.Count - 1);
        int lead = 0;
        while (lead < Rows.Count && IsBlank(Rows[lead])) lead++;
        Rows.RemoveRange(0, lead);
    }

    public void Append(MonoImage other, int gapRows = 0)
    {
        for (int i = 0; i < gapRows; i++) Rows.Add(new byte[WidthBytes]);
        foreach (var r in other.Rows)
        {
            var row = new byte[WidthBytes];
            Array.Copy(r, row, Math.Min(r.Length, row.Length));
            Rows.Add(row);
        }
    }
}

/// <summary>Converts bitmaps to 1-bit printer images.</summary>
public static class ImageConverter
{
    /// <summary>
    /// Convert a bitmap to printer dots. actualSize=true means 1 source pixel = 1 printer dot (the PDF was
    /// rendered at 203 dpi): the content is cropped to its own width and only shrunk if wider than the paper.
    /// Otherwise the whole bitmap is scaled to the paper width.
    /// </summary>
    public static MonoImage ToMono(Bitmap src, Settings s, bool actualSize = false)
    {
        int w = s.PrintWidthDots;
        Rectangle srcRect, destRect;
        int h;
        bool oneToOne = false;
        if (actualSize)
        {
            var bb = ContentBounds(src);
            if (bb.IsEmpty) return new MonoImage(w);
            int left = Math.Max(0, bb.Left - 4), right = Math.Min(src.Width, bb.Right + 4);
            int cw = right - left;
            if (cw <= w)
            {
                oneToOne = true;
                h = src.Height;
                srcRect = new Rectangle(left, 0, cw, src.Height);
                destRect = new Rectangle(0, 0, cw, h);
            }
            else
            {
                h = Math.Max(1, (int)Math.Round(src.Height * (double)w / cw));
                srcRect = new Rectangle(left, 0, cw, src.Height);
                destRect = new Rectangle(0, 0, w, h);
            }
        }
        else
        {
            h = Math.Max(1, (int)Math.Round(src.Height * (double)w / src.Width));
            srcRect = new Rectangle(0, 0, src.Width, src.Height);
            destRect = new Rectangle(0, 0, w, h);
        }

        using var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.Clear(Color.White);
            g.InterpolationMode = oneToOne
                ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, destRect, srcRect, GraphicsUnit.Pixel);
        }

        // Grayscale buffer (0 = black, 255 = white)
        var gray = new float[w * h];
        var data = scaled.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* p = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    byte b = p[x * 4], gg = p[x * 4 + 1], r = p[x * 4 + 2], a = p[x * 4 + 3];
                    float lum = 0.299f * r + 0.587f * gg + 0.114f * b;
                    gray[y * w + x] = 255f - (255f - lum) * a / 255f; // composite on white
                }
            }
        }
        scaled.UnlockBits(data);

        var img = new MonoImage(w);
        int thr = Math.Clamp(s.Threshold, 1, 254);
        for (int y = 0; y < h; y++)
        {
            var row = new byte[img.WidthBytes];
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float old = gray[i];
                bool black = old < thr;
                if (black) row[x >> 3] |= (byte)(0x80 >> (x & 7));

                if (s.Dither == DitherMode.FloydSteinberg)
                {
                    float err = old - (black ? 0f : 255f);
                    if (x + 1 < w) gray[i + 1] += err * 7 / 16;
                    if (y + 1 < h)
                    {
                        if (x > 0) gray[i + w - 1] += err * 3 / 16;
                        gray[i + w] += err * 5 / 16;
                        if (x + 1 < w) gray[i + w + 1] += err * 1 / 16;
                    }
                }
            }
            img.Rows.Add(row);
        }
        return img;
    }

    /// <summary>Bounding box of non-white pixels (alpha-composited on white).</summary>
    static Rectangle ContentBounds(Bitmap src)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        var d = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            for (int y = 0; y < src.Height; y++)
            {
                byte* p = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < src.Width; x++)
                {
                    int a = p[x * 4 + 3];
                    if (a < 16) continue;
                    int lum = (p[x * 4] * 114 + p[x * 4 + 1] * 587 + p[x * 4 + 2] * 299) / 1000;
                    if (255 - (255 - lum) * a / 255 >= 200) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        src.UnlockBits(d);
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}

/// <summary>Star Graphic Mode (raster) command builder for the TSP100 family.</summary>
public static class StarRaster
{
    const byte ESC = 0x1B, NUL = 0x00, BEL = 0x07;

    static void Ascii(List<byte> buf, string s) => buf.AddRange(Encoding.ASCII.GetBytes(s));

    public static byte[] Build(MonoImage img, Settings s)
    {
        var buf = new List<byte>(img.Rows.Count * (img.WidthBytes + 3) + 64);

        if (s.OpenDrawerOnJob) buf.Add(BEL);                 // drive external device 1 (cash drawer)

        buf.Add(ESC); Ascii(buf, "*rR");                      // initialise raster mode
        buf.Add(ESC); Ascii(buf, "*rA");                      // enter raster mode
        buf.Add(ESC); Ascii(buf, "*rP0"); buf.Add(NUL);       // continuous page length
        string eot = s.Cut switch { CutMode.Full => "9", CutMode.FeedOnly => "2", _ => "13" };
        buf.Add(ESC); Ascii(buf, "*rE" + eot); buf.Add(NUL);  // EOT action: feed + cut

        int blank = 0;
        foreach (var row in img.Rows)
        {
            bool isBlank = true;
            foreach (var b in row) if (b != 0) { isBlank = false; break; }
            if (isBlank) { blank++; continue; }
            FlushBlank(buf, ref blank);
            buf.Add((byte)'b');                               // b n1 n2 d1..dk : transfer raster line
            buf.Add((byte)(row.Length & 0xFF));
            buf.Add((byte)(row.Length >> 8));
            buf.AddRange(row);
        }
        FlushBlank(buf, ref blank);

        buf.Add(ESC); Ascii(buf, "*rB");                      // quit raster mode -> executes EOT (cut)
        return buf.ToArray();
    }

    static void FlushBlank(List<byte> buf, ref int blank)
    {
        if (blank <= 0) return;
        buf.Add(ESC); Ascii(buf, "*rY" + blank); buf.Add(NUL); // vertical move instead of blank rows
        blank = 0;
    }

    public static byte[] DrawerKick() => new[] { BEL };
}
