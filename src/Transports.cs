// Copyright (c) 2026 Adel Naim. Licensed under the MIT License (see LICENSE).
using System.ComponentModel;
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TSP100Bridge;

public interface IPrinterTransport : IDisposable
{
    string Description { get; }
    void Write(byte[] data);
}

public sealed record FoundDevice(string Kind, string Path, string Note);

/// <summary>Finds Star printers attached by USB (inbox usbprint.sys or WinUSB).</summary>
public static class DeviceFinder
{
    public static readonly Guid UsbPrintInterface = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");
    public static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    const string StarVid = "vid_0519";

    public static List<FoundDevice> FindAll()
    {
        var list = new List<FoundDevice>();
        foreach (var p in Native.GetInterfacePaths(UsbPrintInterface))
        {
            bool star = p.Contains(StarVid, StringComparison.OrdinalIgnoreCase);
            list.Add(new FoundDevice("USB printer class", p, star ? "Star device" : "other USB printer"));
        }
        foreach (var p in Native.GetInterfacePaths(UsbDeviceInterface))
        {
            if (!p.Contains(StarVid, StringComparison.OrdinalIgnoreCase)) continue;
            bool winusb = WinUsbTransport.CanOpen(p);
            list.Add(new FoundDevice("USB device", p, winusb ? "WinUSB ready" : "no WinUSB driver bound"));
        }
        return list;
    }

    public static IPrinterTransport Open(Settings s) => s.Connection switch
    {
        ConnectionType.Network => new TcpTransport(s.NetworkHost, s.NetworkPort),
        ConnectionType.Serial => new SerialTransport(s.SerialPort, s.SerialBaud),
        ConnectionType.UsbPrinterClass => new UsbPrintTransport(PickUsbPrint(s.UsbDevicePath)
                                          ?? throw new InvalidOperationException("No USB printer-class Star device found.")),
        ConnectionType.UsbWinUsb => new WinUsbTransport(PickWinUsb(s.UsbDevicePath)
                                          ?? throw new InvalidOperationException("No Star device with WinUSB driver found.")),
        _ => OpenAuto(s)
    };

    static string? PickUsbPrint(string preferred)
    {
        var paths = Native.GetInterfacePaths(UsbPrintInterface);
        if (!string.IsNullOrWhiteSpace(preferred) && paths.Contains(preferred, StringComparer.OrdinalIgnoreCase)) return preferred;
        return paths.FirstOrDefault(p => p.Contains(StarVid, StringComparison.OrdinalIgnoreCase));
    }

    static string? PickWinUsb(string preferred)
    {
        var paths = Native.GetInterfacePaths(UsbDeviceInterface)
            .Where(p => p.Contains(StarVid, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(preferred) && paths.Contains(preferred, StringComparer.OrdinalIgnoreCase)) return preferred;
        return paths.FirstOrDefault(WinUsbTransport.CanOpen);
    }

    static IPrinterTransport OpenAuto(Settings s)
    {
        var up = PickUsbPrint(s.UsbDevicePath);
        if (up != null) return new UsbPrintTransport(up);
        var wu = PickWinUsb(s.UsbDevicePath);
        if (wu != null) return new WinUsbTransport(wu);
        if (!string.IsNullOrWhiteSpace(s.NetworkHost)) return new TcpTransport(s.NetworkHost, s.NetworkPort);

        bool starSeen = Native.GetInterfacePaths(UsbDeviceInterface)
            .Any(p => p.Contains(StarVid, StringComparison.OrdinalIgnoreCase));
        throw new InvalidOperationException(starSeen
            ? "A Star USB device is plugged in but Windows has no usable driver bound to it. " +
              "In Device Manager give it the built-in \"WinUsb Device\" driver (see README, step 3) and try again."
            : "No Star printer found. Check the USB cable and power, or set a network address / COM port.");
    }
}

/// <summary>USB printer-class device driven by the inbox usbprint.sys (exists on Windows ARM64).</summary>
public sealed class UsbPrintTransport : IPrinterTransport
{
    readonly SafeFileHandle _h;
    public string Description { get; }

    public UsbPrintTransport(string path)
    {
        Description = "USB (printer class)";
        _h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (_h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open USB printer");
    }

    public void Write(byte[] data)
    {
        const int chunk = 16 * 1024;
        for (int off = 0; off < data.Length; off += chunk)
        {
            int n = Math.Min(chunk, data.Length - off);
            if (!Native.WriteFile(_h, data, off, n, out int written) || written != n)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "USB write failed (is the printer on and the cover closed?)");
        }
    }

    public void Dispose() => _h.Dispose();
}

/// <summary>Vendor-class Star device with WinUSB bound to it (e.g. via Zadig).</summary>
public sealed class WinUsbTransport : IPrinterTransport
{
    readonly SafeFileHandle _h;
    readonly IntPtr _usb;
    readonly byte _outPipe;
    public string Description => "USB (WinUSB)";

    public static bool CanOpen(string path)
    {
        try { using var t = new WinUsbTransport(path); return true; } catch { return false; }
    }

    public WinUsbTransport(string path)
    {
        _h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (_h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open USB device");
        if (!Native.WinUsb_Initialize(_h, out _usb))
        {
            int err = Marshal.GetLastWin32Error();
            _h.Dispose();
            throw new Win32Exception(err, "WinUSB not bound to this device");
        }
        if (!Native.WinUsb_QueryInterfaceSettings(_usb, 0, out var desc))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        byte found = 0;
        for (byte i = 0; i < desc.bNumEndpoints; i++)
        {
            if (Native.WinUsb_QueryPipe(_usb, 0, i, out var pipe) && pipe.PipeType == 2 /*bulk*/ && (pipe.PipeId & 0x80) == 0)
            { found = pipe.PipeId; break; }
        }
        if (found == 0) { Dispose(); throw new InvalidOperationException("No bulk OUT endpoint on device"); }
        _outPipe = found;
        uint timeout = 15000;
        Native.WinUsb_SetPipePolicy(_usb, _outPipe, 0x03 /*PIPE_TRANSFER_TIMEOUT*/, 4, ref timeout);
    }

    public void Write(byte[] data)
    {
        const int chunk = 16 * 1024;
        var tmp = new byte[chunk];
        for (int off = 0; off < data.Length; off += chunk)
        {
            int n = Math.Min(chunk, data.Length - off);
            Buffer.BlockCopy(data, off, tmp, 0, n);
            if (!Native.WinUsb_WritePipe(_usb, _outPipe, tmp, (uint)n, out uint written, IntPtr.Zero) || written != n)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "USB write failed");
        }
    }

    public void Dispose()
    {
        if (_usb != IntPtr.Zero) Native.WinUsb_Free(_usb);
        _h.Dispose();
    }
}

/// <summary>LAN / Wi-Fi models (TSP143IIILAN, TSP143IIIW, TSP143IV LAN): raw TCP port 9100.</summary>
public sealed class TcpTransport : IPrinterTransport
{
    readonly TcpClient _c = new();
    public string Description { get; }

    public TcpTransport(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("No printer IP address set.");
        Description = $"Network {host}:{port}";
        if (!_c.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException($"Printer at {host}:{port} did not answer.");
        _c.SendTimeout = 15000;
    }

    public void Write(byte[] data)
    {
        var s = _c.GetStream();
        s.Write(data, 0, data.Length);
        s.Flush();
    }

    public void Dispose()
    {
        try { _c.Client.Shutdown(SocketShutdown.Send); Thread.Sleep(200); } catch { }
        _c.Dispose();
    }
}

/// <summary>Bluetooth (TSP143IIIBI) via its SPP virtual COM port, or a serial adapter.</summary>
public sealed class SerialTransport : IPrinterTransport
{
    readonly SerialPort _p;
    public string Description { get; }

    public SerialTransport(string port, int baud)
    {
        Description = $"Serial {port}";
        _p = new SerialPort(port, baud) { WriteTimeout = 20000, Handshake = Handshake.None };
        _p.Open();
    }

    public void Write(byte[] data)
    {
        _p.Write(data, 0, data.Length);
        while (_p.BytesToWrite > 0) Thread.Sleep(20);
    }

    public void Dispose() => _p.Dispose();
}
