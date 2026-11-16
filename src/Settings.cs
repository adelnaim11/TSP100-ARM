// Copyright (c) 2026 Adel Naim. Licensed under the MIT License (see LICENSE).
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSP100Bridge;

public enum ConnectionType { Auto, UsbPrinterClass, UsbWinUsb, Network, Serial }
public enum CutMode { Partial, Full, FeedOnly }
public enum DitherMode { FloydSteinberg, Threshold }

/// <summary>Settings shared by the tray app and the elevated installer (stored in ProgramData).</summary>
public sealed class Settings
{
    public ConnectionType Connection { get; set; } = ConnectionType.Auto;
    /// <summary>Optional explicit USB device path; empty = first Star device found.</summary>
    public string UsbDevicePath { get; set; } = "";
    public string NetworkHost { get; set; } = "";
    public int NetworkPort { get; set; } = 9100;
    public string SerialPort { get; set; } = "COM3";
    public int SerialBaud { get; set; } = 9600;

    /// <summary>Printable width in dots. 576 = 72 mm (80 mm paper). 406 ≈ 50.8 mm (58 mm paper).</summary>
    public int PrintWidthDots { get; set; } = 576;
    public CutMode Cut { get; set; } = CutMode.Partial;
    public bool OpenDrawerOnJob { get; set; } = false;
    public DitherMode Dither { get; set; } = DitherMode.FloydSteinberg;
    public int Threshold { get; set; } = 150;
    /// <summary>Remove blank space at the top/bottom of each page so receipts don't waste paper.</summary>
    public bool TrimWhitespace { get; set; } = true;
    /// <summary>Print PDF pages at true physical size, cropped to the content (so A4/Letter pages work
    /// without a custom paper size). Content wider than the paper is shrunk to fit.</summary>
    public bool ActualSize { get; set; } = true;

    public bool RawForwarderEnabled { get; set; } = false;
    public int RawForwarderPort { get; set; } = 9100;
    public bool RawForwarderLocalOnly { get; set; } = true;

    public bool WatchSpool { get; set; } = true;

    // ---- persistence -------------------------------------------------------
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TSP100Bridge");
    public static string SpoolDir => Path.Combine(DataDir, "spool");
    public static string SpoolFile => Path.Combine(SpoolDir, "job.pdf");
    public static string FilePath => Path.Combine(DataDir, "settings.json");
    public static string LogPath => Path.Combine(DataDir, "bridge.log");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("Could not read settings: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}

public static class Log
{
    static readonly object Gate = new();
    public static event Action<string>? Line;

    public static void Write(string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                var fi = new FileInfo(Settings.LogPath);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(Settings.LogPath, line + Environment.NewLine);
            }
            catch { /* logging must never break printing */ }
        }
        Line?.Invoke(line);
    }
}
