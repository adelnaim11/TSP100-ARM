// Copyright (c) 2026 Adel Naim. Licensed under the MIT License (see LICENSE).
using System.Drawing;
using System.Drawing.Text;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TSP100Bridge;

/// <summary>Turns documents into Star raster jobs and sends them, one job at a time.</summary>
public static class PrintService
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task SendRawAsync(byte[] data, string label)
    {
        await Gate.WaitAsync();
        try
        {
            var s = Settings.Load();
            await Task.Run(() =>
            {
                using var t = DeviceFinder.Open(s);
                t.Write(data);
                Log.Write($"Sent {label} ({data.Length:N0} bytes) via {t.Description}");
            });
        }
        finally { Gate.Release(); }
    }

    public static async Task PrintBitmapsAsync(IReadOnlyList<Bitmap> pages, string label, bool actualSize = false)
    {
        var s = Settings.Load();
        var job = new MonoImage(s.PrintWidthDots);
        for (int i = 0; i < pages.Count; i++)
        {
            var mono = ImageConverter.ToMono(pages[i], s, actualSize);
            if (s.TrimWhitespace) mono.TrimVertical();
            if (mono.Rows.Count == 0) continue;
            job.Append(mono, job.Rows.Count > 0 ? 24 : 0);
        }
        if (job.Rows.Count == 0) { Log.Write($"{label}: nothing to print (all pages blank)"); return; }
        await SendRawAsync(StarRaster.Build(job, s), $"{label}, {pages.Count} page(s), {job.Rows.Count} dot rows");
    }

    public static async Task PrintFileAsync(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool pdf = ext == ".pdf";
        var pages = pdf ? await RenderPdfAsync(path) : new List<Bitmap> { LoadImage(path) };
        try { await PrintBitmapsAsync(pages, Path.GetFileName(path), pdf && Settings.Load().ActualSize); }
        finally { foreach (var b in pages) b.Dispose(); }
    }

    static Bitmap LoadImage(string path)
    {
        using var fs = File.OpenRead(path);
        using var tmp = new Bitmap(fs);
        return new Bitmap(tmp);
    }

    /// <summary>Renders each PDF page with the built-in Windows PDF engine (native on ARM64).</summary>
    public static async Task<List<Bitmap>> RenderPdfAsync(string path)
    {
        var s = Settings.Load();
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var doc = await PdfDocument.LoadFromFileAsync(file);
        var result = new List<Bitmap>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            // Actual size: render at the printer's 203 dpi (page.Size is in 1/96 inch).
            // Otherwise render a little above printer resolution for cleaner downscaling.
            // Background defaults to white; transparency is also flattened onto white later.
            uint width = s.ActualSize
                ? (uint)Math.Clamp(Math.Round(page.Size.Width * 203.0 / 96.0), 64, 12000)
                : (uint)(s.PrintWidthDots * 2);
            var opts = new PdfPageRenderOptions { DestinationWidth = width };
            using var ras = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ras, opts);
            ras.Seek(0);
            using var stream = ras.AsStreamForRead();
            using var tmp = new Bitmap(stream);
            result.Add(new Bitmap(tmp));
        }
        return result;
    }

    public static Bitmap BuildTestPage()
    {
        var s = Settings.Load();
        int w = s.PrintWidthDots;
        var bmp = new Bitmap(w, 520);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        using var big = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
        using var norm = new Font("Segoe UI", 20, GraphicsUnit.Pixel);
        using var mono = new Font("Consolas", 18, GraphicsUnit.Pixel);
        var center = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString("TSP100 ARM Bridge", big, Brushes.Black, new RectangleF(0, 10, w, 40), center);
        g.DrawString("Test page — it works!", norm, Brushes.Black, new RectangleF(0, 55, w, 30), center);
        g.DrawLine(new Pen(Color.Black, 2), 0, 95, w, 95);
        int y = 110;
        foreach (var line in new[]
        {
            $"Date      {DateTime.Now:yyyy-MM-dd HH:mm}",
            $"Machine   {Environment.MachineName}",
            $"CPU       {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
            $"Width     {w} dots",
            $"Cut       {s.Cut}",
        })
        { g.DrawString(line, mono, Brushes.Black, 8, y); y += 26; }
        y += 10;
        for (int i = 0; i < 8; i++)                     // grey ramp to check dithering
        {
            int shade = i * 255 / 7;
            using var br = new SolidBrush(Color.FromArgb(shade, shade, shade));
            g.FillRectangle(br, i * w / 8, y, w / 8, 50);
        }
        g.DrawRectangle(Pens.Black, 0, y, w - 1, 50);
        y += 70;
        g.DrawRectangle(new Pen(Color.Black, 2), 1, 1, w - 3, y);   // border shows printable edges
        g.DrawString("If all four edges of this box are visible,\nthe width setting is right.", norm,
            Brushes.Black, new RectangleF(0, y + 10, w, 60), center);
        return bmp;
    }
}
