// Copyright (c) 2026 Adel Naim. Licensed under the MIT License (see LICENSE).
namespace TSP100Bridge;

/// <summary>
/// The virtual printer ("Star TSP100 (ARM)") is the inbox Microsoft Print to PDF driver writing to a
/// local file port. This watcher picks each finished PDF up, prints it, and deletes it.
/// </summary>
public sealed class SpoolWatcher : IDisposable
{
    readonly System.Threading.Timer _timer;
    readonly FileSystemWatcher? _fsw;
    int _busy;

    public SpoolWatcher()
    {
        Directory.CreateDirectory(Settings.SpoolDir);
        try
        {
            _fsw = new FileSystemWatcher(Settings.SpoolDir, "*.pdf")
            { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _fsw.Changed += (_, _) => Kick();
            _fsw.Created += (_, _) => Kick();
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Write("File watcher unavailable, polling only: " + ex.Message); }
        _timer = new System.Threading.Timer(_ => Kick(), null, 1000, 1500);   // polling as a safety net
        Log.Write("Watching " + Settings.SpoolDir);
    }

    void Kick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await ProcessPendingAsync(); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    static async Task ProcessPendingAsync()
    {
        // 1. Claim the spooler's output file once Windows has finished writing it.
        var spool = new FileInfo(Settings.SpoolFile);
        if (spool.Exists && spool.Length > 0 && DateTime.UtcNow - spool.LastWriteTimeUtc > TimeSpan.FromMilliseconds(700))
        {
            string claimed = Path.Combine(Settings.SpoolDir, $"pending-{DateTime.Now:yyyyMMdd-HHmmss-fff}.pdf");
            try
            {
                using (new FileStream(spool.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Move(spool.FullName, claimed);
            }
            catch (IOException) { /* still being written */ }
            catch (UnauthorizedAccessException ex) { Log.Write("Cannot claim spool file: " + ex.Message); }
        }

        // 2. Print every claimed job, oldest first.
        foreach (var f in Directory.GetFiles(Settings.SpoolDir, "pending-*.pdf").OrderBy(x => x))
        {
            try
            {
                Log.Write("Printing job " + Path.GetFileName(f));
                await PrintService.PrintFileAsync(f);
                File.Delete(f);
            }
            catch (Exception ex)
            {
                Log.Write($"Job {Path.GetFileName(f)} failed: {ex.Message}");
                string failed = Path.ChangeExtension(f, ".failed.pdf").Replace("pending-", "failed-");
                try { File.Move(f, failed, true); } catch { }
                Notify?.Invoke("Print failed", ex.Message);
            }
        }
    }

    public static event Action<string, string>? Notify;

    public void Dispose()
    {
        _timer.Dispose();
        _fsw?.Dispose();
    }
}

/// <summary>
/// Optional: accepts raw Star-raster jobs on a TCP port (like a LAN printer would) and forwards them
/// to the USB/BT printer. Lets POS apps that support "Star TSP100 LAN" print to a USB unit.
/// </summary>
public sealed class RawForwarder : IDisposable
{
    readonly System.Net.Sockets.TcpListener _l;
    readonly CancellationTokenSource _cts = new();

    public RawForwarder(int port, bool localOnly)
    {
        _l = new System.Net.Sockets.TcpListener(localOnly ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Any, port);
        _l.Start();
        Log.Write($"Raw forwarder listening on {(localOnly ? "127.0.0.1" : "all interfaces")}:{port}");
        _ = AcceptLoop();
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { break; }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try
                    {
                        using var ms = new MemoryStream();
                        var ns = c.GetStream();
                        ns.ReadTimeout = 10000;
                        await ns.CopyToAsync(ms, _cts.Token);
                        if (ms.Length > 0) await PrintService.SendRawAsync(ms.ToArray(), "raw TCP job");
                    }
                    catch (Exception ex) { Log.Write("Raw job failed: " + ex.Message); }
                }
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _l.Stop(); } catch { }
    }
}
