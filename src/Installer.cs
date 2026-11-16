// Copyright (c) 2026 Adel Naim. Licensed under the MIT License (see LICENSE).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TSP100Bridge;

/// <summary>
/// Creates the "Star TSP100 (ARM)" Windows printer using only inbox, ARM64-native components:
/// the Microsoft Print To PDF driver + a Local Port that is a file path + an 80 mm paper form.
/// Must run elevated (the app relaunches itself with --install).
/// </summary>
public static class Installer
{
    public const string PrinterName = "Star TSP100 (ARM)";
    public const string Form80 = "Receipt 80mm";
    public const string Form58 = "Receipt 58mm";
    const string Driver = "Microsoft Print To PDF";

    public static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Relaunch elevated with the given verb; returns the log text it produced.</summary>
    public static string RunElevated(string verb)
    {
        string resultFile = Path.Combine(Path.GetTempPath(), $"tsp100bridge-{verb.Trim('-')}.txt");
        try { File.Delete(resultFile); } catch { }
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"{verb} \"{resultFile}\"")
        { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception) { return "Cancelled — administrator permission is needed to add a printer."; }
        return File.Exists(resultFile) ? File.ReadAllText(resultFile) : "No result was reported.";
    }

    public static string Install()
    {
        var log = new StringBuilder();
        void L(string s) { log.AppendLine(s); Log.Write("[install] " + s); }
        try
        {
            // 1. Spool folder that both the spooler and the signed-in user can write to.
            Directory.CreateDirectory(Settings.SpoolDir);
            var di = new DirectoryInfo(Settings.DataDir);
            var acl = di.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(acl);
            L("Spool folder ready: " + Settings.SpoolDir);

            // 2. Paper forms (sizes in 1/1000 mm). Long page; the app trims unused paper.
            AddOrUpdateForm(Form80, 80_000, 297_000); L("Paper size added: " + Form80);
            AddOrUpdateForm(Form58, 58_000, 297_000); L("Paper size added: " + Form58);

            // 3. Port + printer via the PrintManagement cmdlets (inbox on ARM64 Windows).
            string port = Settings.SpoolFile.Replace("'", "''");
            string name = PrinterName.Replace("'", "''");
            string ps = $@"
$ErrorActionPreference = 'Stop'
if (-not (Get-PrinterPort -Name '{port}' -ErrorAction SilentlyContinue)) {{ Add-PrinterPort -Name '{port}' }}
if (Get-Printer -Name '{name}' -ErrorAction SilentlyContinue) {{ Remove-Printer -Name '{name}' }}
Add-Printer -Name '{name}' -DriverName '{Driver}' -PortName '{port}'
'OK'";
            L(RunPowerShell(ps).Trim());
            L($"Printer '{PrinterName}' created.");

            // 4. Make the receipt form the default paper for this printer.
            var s = Settings.Load();
            string form = s.PrintWidthDots <= 450 ? Form58 : Form80;
            L(SetDefaultForm(PrinterName, form)
                ? $"Default paper set to '{form}'."
                : "The PDF driver only offers standard paper sizes — that's fine: use A4 or Letter, " +
                  "the app prints the receipt at its real size and trims the rest.");
            L("Done. Print to 'Star TSP100 (ARM)' from any app while TSP100 Bridge is running.");
        }
        catch (Exception ex) { L("ERROR: " + ex.Message); }
        return log.ToString();
    }

    public static string Uninstall()
    {
        var log = new StringBuilder();
        try
        {
            string port = Settings.SpoolFile.Replace("'", "''");
            string name = PrinterName.Replace("'", "''");
            log.AppendLine(RunPowerShell($@"
if (Get-Printer -Name '{name}' -ErrorAction SilentlyContinue) {{ Remove-Printer -Name '{name}' }}
Start-Sleep -Milliseconds 500
if (Get-PrinterPort -Name '{port}' -ErrorAction SilentlyContinue) {{ Remove-PrinterPort -Name '{port}' }}
'Printer removed.'").Trim());
            WithServer(h => { Native.DeleteForm(h, Form80); Native.DeleteForm(h, Form58); });
            log.AppendLine("Paper sizes removed.");
        }
        catch (Exception ex) { log.AppendLine("ERROR: " + ex.Message); }
        Log.Write("[uninstall] " + log.ToString().Trim());
        return log.ToString();
    }

    static string RunPowerShell(string script)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("PowerShell: " + (e.Length > 0 ? e : o));
        return o;
    }

    static void WithServer(Action<IntPtr> a)
    {
        var d = new Native.PRINTER_DEFAULTS { DesiredAccess = Native.SERVER_ACCESS_ADMINISTER };
        if (!Native.OpenPrinter(null, out var h, ref d))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "OpenPrinter(server)");
        try { a(h); } finally { Native.ClosePrinter(h); }
    }

    static void AddOrUpdateForm(string name, int cx, int cy)
    {
        WithServer(h =>
        {
            var f = new Native.FORM_INFO_1 { Flags = 0 /*FORM_USER*/, pName = name, cx = cx, cy = cy, right = cx, bottom = cy };
            if (!Native.AddForm(h, 1, ref f))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Native.ERROR_FILE_EXISTS || !Native.SetForm(h, name, 1, ref f))
                    throw new System.ComponentModel.Win32Exception(err == Native.ERROR_FILE_EXISTS ? Marshal.GetLastWin32Error() : err, "AddForm " + name);
            }
        });
    }

    /// <summary>Writes a default DEVMODE (global, level 8) selecting the receipt form.</summary>
    static bool SetDefaultForm(string printer, string form)
    {
        var d = new Native.PRINTER_DEFAULTS { DesiredAccess = Native.PRINTER_ALL_ACCESS };
        if (!Native.OpenPrinter(printer, out var h, ref d)) return false;
        IntPtr dm = IntPtr.Zero;
        try
        {
            int size = Native.DocumentProperties(IntPtr.Zero, h, printer, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0) return false;
            dm = Marshal.AllocHGlobal(size);
            if (Native.DocumentProperties(IntPtr.Zero, h, printer, dm, IntPtr.Zero, Native.DM_OUT_BUFFER) < 0) return false;

            short paperId = FindPaperId(printer, form);
            int fields = Marshal.ReadInt32(dm, Native.OFF_dmFields);
            fields &= ~(Native.DM_PAPERLENGTH | Native.DM_PAPERWIDTH);
            fields |= Native.DM_FORMNAME;
            if (paperId > 0) { fields |= Native.DM_PAPERSIZE; Marshal.WriteInt16(dm, Native.OFF_dmPaperSize, paperId); }
            Marshal.WriteInt32(dm, Native.OFF_dmFields, fields);
            var nameBytes = new char[32];
            form.AsSpan(0, Math.Min(31, form.Length)).CopyTo(nameBytes);
            Marshal.Copy(nameBytes, 0, dm + Native.OFF_dmFormName, 32);

            if (Native.DocumentProperties(IntPtr.Zero, h, printer, dm, dm, Native.DM_IN_BUFFER | Native.DM_OUT_BUFFER) < 0) return false;

            IntPtr info = Marshal.AllocHGlobal(IntPtr.Size);   // PRINTER_INFO_8 / _9 = { LPDEVMODE }
            try
            {
                Marshal.WriteIntPtr(info, dm);
                bool ok = Native.SetPrinter(h, 8, info, 0);
                Native.SetPrinter(h, 9, info, 0);              // per-user default too (best effort)
                return ok && paperId > 0;
            }
            finally { Marshal.FreeHGlobal(info); }
        }
        finally
        {
            if (dm != IntPtr.Zero) Marshal.FreeHGlobal(dm);
            Native.ClosePrinter(h);
        }
    }

    static short FindPaperId(string printer, string form)
    {
        int n = Native.DeviceCapabilities(printer, null, Native.DC_PAPERNAMES, IntPtr.Zero, IntPtr.Zero);
        if (n <= 0) return 0;
        IntPtr names = Marshal.AllocHGlobal(n * 64 * 2), ids = Marshal.AllocHGlobal(n * 2);
        try
        {
            Native.DeviceCapabilities(printer, null, Native.DC_PAPERNAMES, names, IntPtr.Zero);
            Native.DeviceCapabilities(printer, null, Native.DC_PAPERS, ids, IntPtr.Zero);
            for (int i = 0; i < n; i++)
            {
                string nm = Marshal.PtrToStringUni(names + i * 128, 64).TrimEnd('\0');
                int z = nm.IndexOf('\0'); if (z >= 0) nm = nm[..z];
                if (string.Equals(nm, form, StringComparison.OrdinalIgnoreCase))
                    return Marshal.ReadInt16(ids, i * 2);
            }
        }
        finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(ids); }
        return 0;
    }
}
