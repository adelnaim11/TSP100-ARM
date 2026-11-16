// Copyright (c) 2026 Adel Naim. Licensed under the MIT License (see LICENSE).
namespace TSP100Bridge;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Elevated helper verbs: TSP100Bridge.exe --install|--uninstall <resultFile>
        if (args.Length >= 1 && (args[0] == "--install" || args[0] == "--uninstall"))
        {
            string result = args[0] == "--install" ? Installer.Install() : Installer.Uninstall();
            if (args.Length >= 2) try { File.WriteAllText(args[1], result); } catch { }
            return result.Contains("ERROR") ? 1 : 0;
        }

        // Command-line printing: TSP100Bridge.exe --print file.pdf|.png   /  --test  /  --drawer
        if (args.Length >= 1 && args[0] is "--print" or "--test" or "--drawer")
        {
            try
            {
                if (args[0] == "--test") { using var b = PrintService.BuildTestPage(); PrintService.PrintBitmapsAsync(new[] { b }, "test page").GetAwaiter().GetResult(); }
                else if (args[0] == "--drawer") PrintService.SendRawAsync(StarRaster.DrawerKick(), "drawer kick").GetAwaiter().GetResult();
                else PrintService.PrintFileAsync(args[1]).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex) { Log.Write("ERROR: " + ex.Message); return 2; }
        }

        using var mutex = new Mutex(true, @"Local\TSP100Bridge.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("TSP100 Bridge is already running — look for its icon in the system tray.", "TSP100 Bridge");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startHidden: args.Contains("--tray")));
        return 0;
    }
}
