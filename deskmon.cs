// deskmon - Conky-style desktop resource panel for Windows.
// Transparent, click-through, sits just above the wallpaper and below every app window.
// No admin rights, no kernel driver, no HWiNFO: performance counters + NVML only.
//
//   deskmon.exe [--png out.png]
//
// Right-click the tray icon for monitor, corner, color and startup options.
// Settings live in %APPDATA%\deskmon\settings.ini.
//
// Built with the .NET Framework compiler (C# 5), see build.cmd.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Win32
{
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    public const uint GW_HWNDPREV = 3;
    public const int ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
}

static class Nvml
{
    [StructLayout(LayoutKind.Sequential)] public struct Util { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] public struct Mem { public ulong Total, Free, Used; }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] public static extern int Init();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] public static extern int Shutdown();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] public static extern int GetHandle(uint i, out IntPtr dev);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] public static extern int GetUtil(IntPtr dev, out Util u);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] public static extern int GetMem(IntPtr dev, out Mem m);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] public static extern int GetTemp(IntPtr dev, int sensor, out uint c);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")] public static extern int GetPower(IntPtr dev, out uint mw);
}

class DriveStat { public string Name; public double Used, Total; }

// Everything the panel shows. Sampled once per tick.
class Stats
{
    const int HistoryLen = 60;
    static readonly Regex VirtualNic = new Regex("tailscale|hyper-v|virtual|parsec|vpn|loopback", RegexOptions.IgnoreCase);

    public double Cpu, MemUsed, MemTotal, CommitUsed, CommitTotal;
    public bool GpuOk;
    public double GpuLoad, GpuTemp, GpuWatts, VramUsed, VramTotal;
    public double DiskRead, DiskWrite, NetDown, NetUp;
    public List<DriveStat> Drives = new List<DriveStat>();
    public TimeSpan Uptime;
    public readonly Queue<double> CpuHist = new Queue<double>(), GpuHist = new Queue<double>();

    PerformanceCounter cpuCtr, readCtr, writeCtr;
    IntPtr gpu = IntPtr.Zero;
    int gpuRetry;
    bool nvmlInitialized;
    long lastRx, lastTx, lastNet;
    string lastAdapters;

    public Stats()
    {
        cpuCtr = Counter("Processor Information", "% Processor Utility", "_Total");
        readCtr = Counter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        writeCtr = Counter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
    }

    static PerformanceCounter Counter(string cat, string name, string inst)
    {
        try { var c = new PerformanceCounter(cat, name, inst, true); c.NextValue(); return c; }
        catch { return null; }
    }

    static double Read(PerformanceCounter c)
    {
        try { return c == null ? 0 : c.NextValue(); } catch { return 0; }
    }

    static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLen) q.Dequeue();
    }

    public void Sample()
    {
        Cpu = Math.Max(0, Math.Min(100, Read(cpuCtr)));
        Push(CpuHist, Cpu);

        var m = new Win32.MEMORYSTATUSEX();
        m.dwLength = (uint)Marshal.SizeOf(m);
        if (Win32.GlobalMemoryStatusEx(ref m))
        {
            MemTotal = m.ullTotalPhys; MemUsed = m.ullTotalPhys - m.ullAvailPhys;
            CommitTotal = m.ullTotalPageFile; CommitUsed = m.ullTotalPageFile - m.ullAvailPageFile;
        }

        SampleGpu();
        Push(GpuHist, GpuOk ? GpuLoad : 0);

        DiskRead = Read(readCtr);
        DiskWrite = Read(writeCtr);
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                {
                    long total = d.TotalSize; // not cached: each read is a native call
                    Drives.Add(new DriveStat { Name = d.Name.Substring(0, 2), Total = total, Used = total - d.TotalFreeSpace });
                }
            }
            catch { }
        }

        SampleNet();
        Uptime = TimeSpan.FromMilliseconds(Win32.GetTickCount64());
    }

    void SampleGpu()
    {
        try
        {
            if (gpu == IntPtr.Zero)
            {
                if (gpuRetry-- > 0) { GpuOk = false; return; }
                gpuRetry = 30; // a driver update can drop NVML; retry about once a minute
                if (Nvml.Init() != 0) { GpuOk = false; return; }
                nvmlInitialized = true;
                if (Nvml.GetHandle(0, out gpu) != 0) { ResetGpu(); return; }
            }
            Nvml.Util u; Nvml.Mem mem; uint temp, mw;
            if (Nvml.GetUtil(gpu, out u) != 0 || Nvml.GetMem(gpu, out mem) != 0) { ResetGpu(); return; }
            GpuLoad = u.Gpu; VramUsed = mem.Used; VramTotal = mem.Total;
            GpuTemp = Nvml.GetTemp(gpu, 0, out temp) == 0 ? temp : 0;
            GpuWatts = Nvml.GetPower(gpu, out mw) == 0 ? mw / 1000.0 : 0;
            GpuOk = true;
        }
        catch (DllNotFoundException) { ResetGpu(); }
        catch (EntryPointNotFoundException) { ResetGpu(); }
    }

    // nvmlInit is reference counted, so every failed session needs its matching shutdown.
    void ResetGpu()
    {
        gpu = IntPtr.Zero;
        GpuOk = false;
        gpuRetry = 30;
        if (!nvmlInitialized) return;
        nvmlInitialized = false;
        try { Nvml.Shutdown(); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    void SampleNet()
    {
        long rx = 0, tx = 0, now = Stopwatch.GetTimestamp();
        string adapters = "";
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType != NetworkInterfaceType.Ethernet && n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                if (VirtualNic.IsMatch(n.Name) || VirtualNic.IsMatch(n.Description)) continue;
                var s = n.GetIPStatistics();
                rx += s.BytesReceived; tx += s.BytesSent;
                adapters += n.Id;
            }
        }
        catch (NetworkInformationException)
        {
            NetDown = NetUp = 0;
            lastAdapters = null;
            return;
        }

        // Totals are lifetime counters, so a rate only means something if the same adapters
        // were summed last time. Otherwise a rejoining adapter reads as a huge spike.
        if (adapters == lastAdapters)
        {
            double dt = (now - lastNet) / (double)Stopwatch.Frequency;
            NetDown = Math.Max(0, (rx - lastRx) / dt);
            NetUp = Math.Max(0, (tx - lastTx) / dt);
        }
        else
        {
            NetDown = NetUp = 0;
        }
        lastAdapters = adapters; lastRx = rx; lastTx = tx; lastNet = now;
    }
}

// User choices from the tray menu, kept in %APPDATA%\deskmon\settings.ini.
class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "deskmon", "settings.ini");

    public string Monitor = "";   // Screen.DeviceName; empty or unplugged means primary
    public string Corner = "TR";  // TL, TR, BL or BR
    public Color Accent = Color.FromArgb(94, 200, 216);

    public static Settings Load()
    {
        var st = new Settings();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string key = line.Substring(0, i).Trim(), val = line.Substring(i + 1).Trim();
                if (key == "monitor") st.Monitor = val;
                else if (key == "corner" && Regex.IsMatch(val, "^[TB][LR]$")) st.Corner = val;
                else if (key == "color") st.Accent = ColorTranslator.FromHtml("#" + val);
            }
        }
        catch (Exception) { } // missing or damaged file: keep whatever parsed, defaults for the rest
        return st;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllLines(FilePath, new[] {
            "monitor=" + Monitor,
            "corner=" + Corner,
            "color=" + (Accent.ToArgb() & 0xFFFFFF).ToString("X6")
        });
    }

    public Screen PickScreen()
    {
        foreach (var sc in Screen.AllScreens)
            if (sc.DeviceName == Monitor) return sc;
        return Screen.PrimaryScreen;
    }
}

// Draws the panel. With g == null it only advances y, which is how the height is measured.
class Painter : IDisposable
{
    const int BaseWidth = 300;
    static readonly Color Text = Color.FromArgb(235, 238, 242);
    static readonly Color Dim = Color.FromArgb(150, 158, 170);
    static readonly Color Warn = Color.FromArgb(240, 180, 80);
    static readonly Color Hot = Color.FromArgb(240, 100, 90);
    static readonly Color Track = Color.FromArgb(70, 255, 255, 255);
    static readonly Color Panel = Color.FromArgb(175, 16, 18, 22);

    readonly float s;
    readonly Font label, value, small;
    readonly SolidBrush rowBrush = new SolidBrush(Color.White);
    readonly StringFormat rightFormat = new StringFormat { Alignment = StringAlignment.Far };
    public Color Accent;
    float y;
    public int Width { get { return (int)(BaseWidth * s); } }
    float Pad { get { return 14 * s; } }

    public Painter(float scale)
    {
        s = scale;
        label = new Font("Segoe UI Semibold", 13f * s, GraphicsUnit.Pixel);
        value = new Font("Consolas", 13f * s, GraphicsUnit.Pixel);
        small = new Font("Segoe UI", 11.5f * s, GraphicsUnit.Pixel);
    }

    public void Dispose() { label.Dispose(); value.Dispose(); small.Dispose(); rowBrush.Dispose(); rightFormat.Dispose(); }

    static string Gb(double b) { return (b / 1073741824.0).ToString("0.0"); }

    static string Rate(double b)
    {
        if (b >= 1048576) return (b / 1048576).ToString("0.0") + " MB/s";
        return (b / 1024).ToString("0") + " KB/s";
    }

    Color Level(double frac) { return frac >= 0.9 ? Hot : frac >= 0.75 ? Warn : Accent; }

    float LineH(Font f) { return f.Size * 1.45f; }

    void Row(Graphics g, Font lf, Color lc, string left, string right, Color rc)
    {
        if (g != null)
        {
            rowBrush.Color = lc;
            g.DrawString(left, lf, rowBrush, Pad, y);
            rowBrush.Color = rc;
            g.DrawString(right, value, rowBrush, new RectangleF(Pad, y, Width - 2 * Pad, LineH(value)), rightFormat);
        }
        y += LineH(lf);
    }

    void Bar(Graphics g, double frac)
    {
        float h = 5 * s;
        if (g != null)
        {
            frac = Math.Max(0, Math.Min(1, frac));
            float w = Width - 2 * Pad;
            using (var t = new SolidBrush(Track)) g.FillRectangle(t, Pad, y, w, h);
            using (var b = new SolidBrush(Level(frac))) g.FillRectangle(b, Pad, y, (float)(w * frac), h);
        }
        y += h + 5 * s;
    }

    void Spark(Graphics g, Queue<double> hist)
    {
        float h = 30 * s;
        if (g != null)
        {
            float w = Width - 2 * Pad, bottom = y + h;
            using (var t = new SolidBrush(Color.FromArgb(25, 255, 255, 255))) g.FillRectangle(t, Pad, y, w, h);
            if (hist.Count > 1)
            {
                float step = w / 59f, x0 = Pad + w - (hist.Count - 1) * step;
                var pts = new PointF[hist.Count + 2];
                pts[0] = new PointF(x0, bottom);
                int i = 0;
                foreach (double v in hist)
                {
                    pts[i + 1] = new PointF(x0 + i * step, bottom - (float)(v / 100.0) * h);
                    i++;
                }
                pts[pts.Length - 1] = new PointF(Pad + w, bottom);
                using (var f = new SolidBrush(Color.FromArgb(120, Accent))) g.FillPolygon(f, pts);
            }
        }
        y += h + 6 * s;
    }

    void Gap() { y += 7 * s; }

    void Content(Graphics g, Stats st)
    {
        y = Pad - 2 * s;
        var up = st.Uptime;
        Row(g, small, Dim, Environment.MachineName, "up " + (int)up.TotalDays + "d " + up.Hours + "h " + up.Minutes + "m", Dim);
        Gap();

        Row(g, label, Accent, "CPU", st.Cpu.ToString("0") + "%", Text);
        Spark(g, st.CpuHist);

        double memFrac = st.MemTotal > 0 ? st.MemUsed / st.MemTotal : 0;
        Row(g, label, Accent, "RAM", Gb(st.MemUsed) + " / " + Gb(st.MemTotal) + " GB", Text);
        Bar(g, memFrac);
        Row(g, small, Dim, "Commit", Gb(st.CommitUsed) + " / " + Gb(st.CommitTotal) + " GB", Dim);
        Gap();

        if (st.GpuOk)
        {
            Row(g, label, Accent, "GPU", st.GpuLoad.ToString("0") + "%  " + st.GpuTemp.ToString("0") + "°C  " + st.GpuWatts.ToString("0") + "W", Text);
            Spark(g, st.GpuHist);
            Row(g, small, Dim, "VRAM", Gb(st.VramUsed) + " / " + Gb(st.VramTotal) + " GB", Text);
            Bar(g, st.VramTotal > 0 ? st.VramUsed / st.VramTotal : 0);
        }
        else
        {
            Row(g, label, Accent, "GPU", "n/a", Dim);
        }
        Gap();

        Row(g, label, Accent, "Disk", "", Text);
        foreach (var d in st.Drives)
        {
            Row(g, small, Dim, d.Name, Gb(d.Used) + " / " + Gb(d.Total) + " GB", Text);
            Bar(g, d.Total > 0 ? d.Used / d.Total : 0);
        }
        Row(g, small, Dim, "Read", Rate(st.DiskRead), Text);
        Row(g, small, Dim, "Write", Rate(st.DiskWrite), Text);
        Gap();

        Row(g, label, Accent, "Network", "", Text);
        Row(g, small, Dim, "Down", Rate(st.NetDown), Text);
        Row(g, small, Dim, "Up", Rate(st.NetUp), Text);
    }

    public Bitmap Render(Stats st)
    {
        Content(null, st);
        int height = (int)(y + Pad - 4 * s);
        var bmp = new Bitmap(Width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float r = 10 * s * 2;
            using (var path = new GraphicsPath())
            using (var pb = new SolidBrush(Panel))
            {
                path.AddArc(0, 0, r, r, 180, 90);
                path.AddArc(Width - r - 1, 0, r, r, 270, 90);
                path.AddArc(Width - r - 1, height - r - 1, r, r, 0, 90);
                path.AddArc(0, height - r - 1, r, r, 90, 90);
                path.CloseFigure();
                g.FillPath(pb, path);
            }
            Content(g, st);
        }
        return bmp;
    }
}

class Overlay : Form
{
    const int MarginDip = 24;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static readonly string[][] CornerNames = {
        new[] { "Top Left", "TL" }, new[] { "Top Right", "TR" },
        new[] { "Bottom Left", "BL" }, new[] { "Bottom Right", "BR" }
    };

    static readonly KeyValuePair<string, Color>[] Presets = {
        new KeyValuePair<string, Color>("Teal", Color.FromArgb(94, 200, 216)),
        new KeyValuePair<string, Color>("Blue", Color.FromArgb(91, 157, 245)),
        new KeyValuePair<string, Color>("Green", Color.FromArgb(111, 207, 127)),
        new KeyValuePair<string, Color>("Purple", Color.FromArgb(177, 140, 240)),
        new KeyValuePair<string, Color>("Pink", Color.FromArgb(240, 140, 192)),
        new KeyValuePair<string, Color>("White", Color.FromArgb(235, 238, 242))
    };

    readonly Settings settings;
    readonly Stats stats;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenu menu = new ContextMenu();
    Painter painter;
    float painterScale;

    public Overlay(Settings settings, Stats stats)
    {
        this.settings = settings;
        this.stats = stats;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        // Rebuilt on every open, so it always reflects the plugged-in monitors and the registry.
        menu.Popup += delegate { BuildMenu(); };
        using (var icon = typeof(Overlay).Assembly.GetManifestResourceStream("brain.ico"))
            tray.Icon = new Icon(icon, SystemInformation.SmallIconSize);
        tray.Text = "deskmon";
        tray.ContextMenu = menu;
        tray.Visible = true;

        timer.Interval = 2000;
        timer.Tick += delegate { stats.Sample(); Redraw(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        stats.Sample();
        Redraw();
        timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        tray.Visible = false;
        if (painter != null) painter.Dispose();
        base.OnFormClosed(e);
    }

    static MenuItem Radio(string text, bool on, Action click)
    {
        return new MenuItem(text, delegate { click(); }) { RadioCheck = true, Checked = on };
    }

    void Change(Action apply)
    {
        apply();
        settings.Save();
        Redraw();
    }

    void BuildMenu()
    {
        menu.MenuItems.Clear();

        var monitor = menu.MenuItems.Add("Monitor");
        var current = settings.PickScreen();
        int number = 1;
        foreach (var sc in Screen.AllScreens)
        {
            string device = sc.DeviceName;
            string text = number++ + ":  " + sc.Bounds.Width + " x " + sc.Bounds.Height + (sc.Primary ? "  (primary)" : "");
            monitor.MenuItems.Add(Radio(text, device == current.DeviceName, delegate { Change(delegate { settings.Monitor = device; }); }));
        }

        var corner = menu.MenuItems.Add("Corner");
        foreach (var c in CornerNames)
        {
            string code = c[1];
            corner.MenuItems.Add(Radio(c[0], code == settings.Corner, delegate { Change(delegate { settings.Corner = code; }); }));
        }

        var options = menu.MenuItems.Add("Settings");
        options.MenuItems.Add(new MenuItem("Start with Windows", delegate { AutoStart = !AutoStart; }) { Checked = AutoStart });

        var color = options.MenuItems.Add("Color");
        bool custom = true;
        foreach (var p in Presets)
        {
            Color value = p.Value;
            bool on = value.ToArgb() == settings.Accent.ToArgb();
            custom &= !on;
            color.MenuItems.Add(Radio(p.Key, on, delegate { Change(delegate { settings.Accent = value; }); }));
        }
        color.MenuItems.Add("-");
        color.MenuItems.Add(Radio("Custom...", custom, PickColor));

        menu.MenuItems.Add("-");
        menu.MenuItems.Add("Exit deskmon", delegate { Close(); });
    }

    void PickColor()
    {
        using (var dialog = new ColorDialog { Color = settings.Accent, FullOpen = true })
            if (dialog.ShowDialog() == DialogResult.OK)
                Change(delegate { settings.Accent = dialog.Color; });
    }

    static bool AutoStart
    {
        get
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue("deskmon") != null;
        }
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value) key.SetValue("deskmon", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("deskmon", false);
            }
        }
    }

    public static float ScaleFor(Screen sc)
    {
        var pt = new Win32.POINT { X = sc.Bounds.Left + 1, Y = sc.Bounds.Top + 1 };
        uint dx, dy;
        var mon = Win32.MonitorFromPoint(pt, 2);
        return Win32.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 ? dx / 96f : 1f;
    }

    void Redraw()
    {
        // Re-read the screen each time so resolution and scaling changes need no restart.
        var sc = settings.PickScreen();
        float scale = ScaleFor(sc);
        if (painter == null || scale != painterScale)
        {
            if (painter != null) painter.Dispose();
            painter = new Painter(scale);
            painterScale = scale;
        }
        painter.Accent = settings.Accent;

        using (var bmp = painter.Render(stats))
        {
            int margin = (int)(MarginDip * scale);
            var area = sc.WorkingArea;
            int x = settings.Corner[1] == 'L' ? area.Left + margin : area.Right - bmp.Width - margin;
            int y = settings.Corner[0] == 'T' ? area.Top + margin : area.Bottom - bmp.Height - margin;
            Present(bmp, x, y);
        }
        KeepAboveDesktop();
    }

    void Present(Bitmap bmp, int x, int y)
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Win32.SelectObject(memDc, hbmp);
        try
        {
            var pos = new Win32.POINT { X = x, Y = y };
            var size = new Win32.SIZE { W = bmp.Width, H = bmp.Height };
            var src = new Win32.POINT();
            var blend = new Win32.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            Win32.UpdateLayeredWindow(Handle, screenDc, ref pos, ref size, memDc, ref src, 0, ref blend, Win32.ULW_ALPHA);
        }
        finally
        {
            Win32.SelectObject(memDc, old);
            Win32.DeleteObject(hbmp);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // HWND_BOTTOM would land under the desktop and hide the panel, so slot in directly above
    // the window hosting the desktop icons. Re-checked every tick, which also recovers from
    // Show Desktop and Explorer restarts.
    void KeepAboveDesktop()
    {
        IntPtr host = IntPtr.Zero;
        Win32.EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (Win32.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            host = h;
            return false;
        }, IntPtr.Zero);
        if (host == IntPtr.Zero) return;

        IntPtr above = Win32.GetWindow(host, Win32.GW_HWNDPREV);
        if (above == Handle) return;
        Win32.SetWindowPos(Handle, above, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Win32.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2
        var settings = Settings.Load();
        var stats = new Stats();

        int png = Array.IndexOf(args, "--png");
        if (png >= 0 && png + 1 < args.Length)
        {
            // One frame to a file: the live panel sits behind windows, so screenshots can't show it.
            for (int i = 0; i < 12; i++) { stats.Sample(); Thread.Sleep(250); }
            using (var p = new Painter(Overlay.ScaleFor(settings.PickScreen())) { Accent = settings.Accent })
            using (var bmp = p.Render(stats))
                bmp.Save(args[png + 1], ImageFormat.Png);
            return 0;
        }

        bool first;
        using (new Mutex(true, "Local\\deskmon", out first))
        {
            if (!first) return 0;
            Application.Run(new Overlay(settings, stats));
        }
        return 0;
    }
}
