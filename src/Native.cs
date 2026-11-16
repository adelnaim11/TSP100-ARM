// Copyright (c) 2026 Adel Naim. Licensed under the MIT License (see LICENSE).
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TSP100Bridge;

internal static unsafe class Native
{
    // ---- kernel32 -----------------------------------------------------------
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte* buf, int n, out int written, IntPtr ovl);

    public static bool WriteFile(SafeFileHandle h, byte[] data, int offset, int count, out int written)
    {
        fixed (byte* p = data) return WriteFile(h, p + offset, count, out written, IntPtr.Zero);
    }

    // ---- setupapi: enumerate device interfaces -------------------------------
    const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail,
        int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    public static List<string> GetInterfacePaths(Guid g)
    {
        var result = new List<string>();
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                if (need <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(need);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on 64-bit (ARM64/x64), 6 on 32-bit
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out _, IntPtr.Zero))
                        result.Add(Marshal.PtrToStringUni(buf + 4) ?? "");
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    // ---- winusb ---------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints,
            bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINUSB_PIPE_INFORMATION
    {
        public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval;
    }

    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_Initialize(SafeFileHandle h, out IntPtr usb);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_QueryInterfaceSettings(IntPtr usb, byte alt, out USB_INTERFACE_DESCRIPTOR d);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_QueryPipe(IntPtr usb, byte alt, byte index, out WINUSB_PIPE_INFORMATION p);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint len, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, byte[] buf, uint len, out uint written, IntPtr ovl);

    // ---- winspool: forms and default DEVMODE ---------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_DEFAULTS { public IntPtr pDatatype; public IntPtr pDevMode; public int DesiredAccess; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FORM_INFO_1
    {
        public int Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string pName;
        public int cx, cy;                       // size in 1/1000 mm
        public int left, top, right, bottom;    // imageable area
    }

    public const int PRINTER_ALL_ACCESS = 0x000F000C, SERVER_ACCESS_ADMINISTER = 0x1;
    public const int ERROR_FILE_EXISTS = 80;

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool OpenPrinter(string? name, out IntPtr h, ref PRINTER_DEFAULTS d);
    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr h);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AddForm(IntPtr h, int level, ref FORM_INFO_1 f);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetForm(IntPtr h, string name, int level, ref FORM_INFO_1 f);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool DeleteForm(IntPtr h, string name);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DocumentProperties(IntPtr hwnd, IntPtr h, string name, IntPtr outDm, IntPtr inDm, int mode);
    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool SetPrinter(IntPtr h, int level, IntPtr info, int command);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DeviceCapabilities(string device, string? port, short capability, IntPtr output, IntPtr devMode);

    public const int DM_OUT_BUFFER = 2, DM_IN_BUFFER = 8;
    public const short DC_PAPERS = 2, DC_PAPERNAMES = 16;
    public const int DM_PAPERSIZE = 0x2, DM_PAPERLENGTH = 0x4, DM_PAPERWIDTH = 0x8, DM_FORMNAME = 0x10000;
    // DEVMODEW field offsets
    public const int OFF_dmFields = 72, OFF_dmPaperSize = 78, OFF_dmFormName = 102;
}
