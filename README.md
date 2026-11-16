# TSP100 Bridge

**Make your Star TSP100 receipt printer work on a Windows on ARM laptop (Surface, Snapdragon, etc.).**

I switched to a Surface with a Snapdragon chip and found out the hard way that my Star TSP100 receipt printer just… stopped working. Star's installer runs, Windows shows an "unknown device", and nothing prints. The reason is simple: Star only makes drivers for regular Intel/AMD (x64) Windows. ARM laptops can run x64 *apps* through emulation, but not x64 *drivers*.

So I worked around the driver entirely. TSP100 Bridge is a small tray app that talks to the printer directly, and it only uses parts that already come with Windows on ARM. There's no driver to install and nothing to sign.

> **Heads-up:** this is an independent hobby project. It's not made, endorsed or supported by Star Micronics. "Star", "TSP100" and "futurePRNT" are their trademarks, used here only to say what the app works with. Use it at your own risk.

## What it does

- Adds a printer called **Star TSP100 (ARM)** that you can print to from any app: your browser, Word, a POS web app, a PDF.
- Prints receipts at their real size, trims the blank paper and cuts at the end.
- Works over **USB**, **network (LAN/Wi-Fi)** or **Bluetooth**.
- Has buttons for a test page and for opening a cash drawer.
- Sits quietly in the system tray and can start with Windows.

It should work with the TSP143 / TSP100 family (U, II, IIIU, IIILAN, IIIW, IIIBI, IV) on 80 mm or 58 mm paper. I built it for my own setup, so if your model behaves differently, please [open an issue](../../issues) and tell me what you see.

## How it works

You don't need to know any of this to use it, but here's the idea:

```
 your app ──print──► "Star TSP100 (ARM)"   (Windows' own Print-to-PDF driver)
                              │  saves the job as a PDF
                              ▼
                       TSP100 Bridge
         turns the PDF into black-and-white dots and Star's printer commands
                              │
             USB  ·  network (port 9100)  ·  Bluetooth
                              ▼
                        your TSP100 🧾
```

## Getting started

### 1. Build the app (one time, a few minutes)

You need Microsoft's free **.NET 8 SDK for Arm64**. Install it with:

```
winget install -e --id Microsoft.DotNet.SDK.8 --source winget
```

Or download the *Windows Arm64* SDK from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0).

Then double-click **`build.cmd`**. It compiles everything and opens the app when it's done. You'll find the finished program at `bin\TSP100Bridge.exe`. It's a single file, so you can copy it wherever you like.

If Windows SmartScreen says the app is unrecognised, click **More info → Run anyway**. It's only warning you because the app isn't code-signed.

> Don't install Star's own `tsp100_V770_setup.exe` on an ARM laptop. It can't work there and can leave a half-installed device behind.

### 2. Connect the printer

Plug the printer in, switch it on, and click **Detect printer**.

| What you see | What it means |
|---|---|
| ✔ green | You're good. Go to step 4. |
| ⚠ orange | The printer needs Windows' built-in USB driver. See step 3. |
| ✖ red | The app can't find it. Check the cable and power, or set up network/Bluetooth below. |

**Network printer:** pick *Network* under *Connect via* and type in the printer's IP address. If you don't know the IP, hold the **FEED** button while you switch the printer on. It prints a page that shows it.

**Bluetooth printer:** pair it in Windows Settings first (the PIN is usually `1234`). Then go to *More Bluetooth settings → COM Ports* to find its "outgoing" port, pick *Serial* in the app and enter that port.

### 3. Only if you got the orange warning

Some TSP100s identify themselves in a way Windows doesn't recognise. Windows already has a generic driver that fixes this; you just have to choose it by hand:

1. Open **Device Manager** and find the printer. It's usually under *Other devices*.
2. Right-click it, then choose **Update driver → Browse my computer → Let me pick from a list**.
3. Choose **Universal Serial Bus devices**, then **WinUsb Device**, then **WinUsb Device** again. Click **Next** and accept the warning.
4. Back in the app, click **Detect printer** again.

(If you've used Zadig for this kind of thing before, it doesn't work on ARM Windows 11. Its drivers aren't signed the way ARM Windows requires. The method above is the way.)

### 4. Print a test page

Click **Print test page**. You should get a little receipt with a box around it.

- **All four edges of the box show:** your width setting is right.
- **The right side is cut off:** switch to **58 mm** paper in the app.
- **Grey bars or text look speckled:** set *Images* to `Threshold` and play with the darkness.

### 5. Print from anything

1. Click **Add Windows printer** and say yes to the admin prompt.
2. In any app, print to **Star TSP100 (ARM)**.
   - Leave the paper size on **A4** or **Letter**. Windows' PDF driver doesn't offer an 80 mm option, but that's fine: the app finds the receipt on the page and prints it at its real size.
   - In a browser, set margins to **None** and turn off headers and footers.
3. Tick **Start with Windows** and click **Save settings**.

The app needs to be running for printing to work. Closing the window just hides it in the tray; to quit for real, right-click the tray icon and choose **Exit**.

## Extras

- **Cash drawer:** use the *Open drawer* button, or tick *Open cash drawer on every job* if your drawer is plugged into the printer.
- **Print a file:** print a PDF or image directly from the app.
- **POS software over the network:** if your POS app supports a networked "Star TSP100 LAN" printer, turn on *Accept raw Star jobs on TCP port* and point the POS app at `127.0.0.1`. Your USB printer then acts like a network one. TSP100s don't understand ESC/POS, so ESC/POS-only apps should use the Windows printer instead.
- **Command line:** `TSP100Bridge.exe --test`, `--drawer`, or `--print receipt.pdf`.

## When something goes wrong

| Problem | Try this |
|---|---|
| Nothing prints | Make sure the tray icon is there and *Print jobs from the Windows printer* is ticked. The log in the app window usually says what happened. |
| "Print failed" pop-up | Your job is saved as `failed-….pdf` (use **Open data folder** to find it). Fix the problem, then use **Print a file…** to print it. |
| The receipt prints tiny | Tick *Print at actual size* and save. |
| Random characters come out | Some other program is sending plain text to the printer. The TSP100 only understands images. |
| "USB write failed" | The printer is off, out of paper, or the cover is open. Look for a blinking red light. |

Settings and logs live in `C:\ProgramData\TSP100Bridge\`.

To remove everything: click **Remove Windows printer**, untick *Start with Windows*, then delete the app and that folder.

## For developers

Curious how it works? The interesting parts are:

- **`src/StarRaster.cs`**: builds Star Graphic Mode raster commands (`ESC*rA`, a `b n1 n2 data` line per dot row, `ESC*rY` to skip blank rows, `ESC*rB` to finish and cut, `BEL` for the drawer).
- **`src/PrintService.cs`**: renders PDF pages with Windows' built-in PDF engine at the printer's 203 dpi, then dithers them to 1-bit.
- **`src/Transports.cs`**: USB (printer class and WinUSB), TCP 9100 and serial/Bluetooth.
- **`src/Installer.cs`**: sets up the Windows printer using the inbox Microsoft Print to PDF driver and a file port.
- **`src/SpoolWatcher.cs`**: picks up each finished print job and sends it to the printer.

Pull requests and bug reports are very welcome. 🙂

## License

MIT © 2026 Adel Naim. See [LICENSE](LICENSE). Free to use, change and share; it comes with no warranty.
