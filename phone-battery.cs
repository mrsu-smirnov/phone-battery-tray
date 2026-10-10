// Phone battery in the Windows tray, read over adb (USB or Wi-Fi).
// The icon is a phone outline filled to the charge level; hover for details,
// double-click wakes the phone screen. Build and internals: README.md.
// Written in C# 5: the csc.exe that ships with Windows understands nothing newer.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Phone battery tray")]
[assembly: AssemblyProduct("Phone battery tray")]
[assembly: AssemblyDescription("Android phone battery in the Windows tray via adb")]
[assembly: AssemblyVersion("1.0.0.0")]

static class PhoneBattery
{
    static readonly string Adb = FindAdb();
    const int Port = 5555;
    const int PollSec = 60;
    const int LowLevel = 30;          // red icon and a notification at 30 % and below
    static readonly string IpFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "phone-ip.txt");
    static string HistoryFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history.csv");
    const int GapMin = 5;             // samples more than 5 minutes apart leave a gap on the chart
    const int KeepDays = 30;          // history older than a month is trimmed at startup
    // chart periods in the Details window; the chosen one is kept while the program runs
    static readonly double[] SpanHours = { 6, 12, 24, 24 * 7 };
    static int spanIdx = 0;
    static readonly object historyLock = new object();
    // Interface language follows Windows: Russian on a Russian system, English otherwise.
    // PHONE_BATTERY_LANG=en or ru overrides it (handy for screenshots)
    static readonly bool RuUi = (Environment.GetEnvironmentVariable("PHONE_BATTERY_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "ru";
    static string L(string ru, string en) { return RuUi ? ru : en; }
    static readonly string WindowTitle = L("Заряд телефона", "Phone battery");

    // adb.exe: next to the program, in platform-tools next to it or one level up, in the Android SDK, else from PATH
    static string FindAdb()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        var places = new[] {
            Path.Combine(dir, "adb.exe"),
            Path.Combine(dir, "platform-tools", "adb.exe"),
            Path.Combine(dir, "..", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe") };
        foreach (string p in places)
            if (File.Exists(p)) return Path.GetFullPath(p);
        return "adb.exe";
    }

    static readonly CultureInfo UiCulture = new CultureInfo(RuUi ? "ru-RU" : "en-US");
    static readonly string[] SpanTitles = { L("Последние 6 часов", "Last 6 hours"), L("Последние 12 часов", "Last 12 hours"), L("Последние 24 часа", "Last 24 hours"), L("Последняя неделя", "Last week") };
    static readonly string[] SpanNames = { L("6 ч", "6 h"), L("12 ч", "12 h"), L("24 ч", "24 h"), L("Неделя", "Week") };
    static readonly Color LineDischarge = Color.FromArgb(0, 103, 192);
    static readonly Color LineCharge = Color.FromArgb(16, 137, 62);

    struct Sample { public DateTime T; public int Level; public bool Charging; public bool Phone; }   // Phone: rebuilt from the phone's own history
    // per-minute charge rebuilt from the phone's own history (dumpsys batterystats): fills gaps while the PC slept
    static readonly string PhoneHistoryFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "phone-history.csv");
    static DateTime lastOk = DateTime.MinValue;   // time of the last successful reading
    static bool phoneFilled;                      // phone history already fetched since startup

    const string GlyphPhone = "\uE8EA";
    const string GlyphBolt = "\uE945";
    static readonly Color White = Color.White;
    static readonly Color Red = Color.FromArgb(255, 99, 99);
    static readonly Color Gray = Color.FromArgb(150, 150, 150);

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    static NotifyIcon tray;
    static Control ui;
    static IntPtr hIcon = IntPtr.Zero;
    static readonly AutoResetEvent kick = new AutoResetEvent(false);
    static volatile string dev;
    static volatile Dictionary<string, string> last;
    static bool? wasCharging;
    static bool lowWarned, lostWarned;
    static int failCount;
    static volatile bool adbMissing;    // adb.exe could not be started
    static volatile bool unauthorized;  // the phone is on USB but has not allowed USB debugging yet
    static string tcpipDone;            // USB serial already switched to Wi-Fi, so adbd is not restarted every minute

    [STAThread]
    static void Main(string[] args)
    {
        SetProcessDPIAware();
        // phone-battery.exe --preview file.png: draw the tray icon in every state
        if (args.Length == 2 && args[0] == "--preview") { Preview(args[1]); return; }
        // phone-battery.exe --details file.png [history.csv]: draw the Details window with sample values
        if ((args.Length == 2 || args.Length == 3) && args[0] == "--details")
        {
            if (args.Length == 3) HistoryFile = args[2];
            PreviewDetails(args[1]);
            return;
        }
        // phone-battery.exe --chart file.png [history.csv]: draw the 24-hour chart
        if ((args.Length == 2 || args.Length == 3) && args[0] == "--chart")
        {
            if (args.Length == 3) HistoryFile = args[2];
            var h = LoadHistory(DateTime.Now.AddHours(-24));
            ChartGeo geo;
            using (var bmp = DrawChart(h, DateTime.Now, 24, 1.5f, Theme.Get(SystemDark()), 840, 375, out geo)) bmp.Save(args[1]);
            Console.WriteLine(Speed(h, DateTime.Now));
            return;
        }

        bool created;
        var mutex = new Mutex(true, @"Local\PhoneBatteryTray", out created);
        if (!created) return;

        Application.EnableVisualStyles();
        ui = new Control();
        IntPtr unused = ui.Handle;

        tray = new NotifyIcon();
        var menu = new ContextMenuStrip();
        var wake = menu.Items.Add(L("Разбудить телефон", "Wake phone"));
        wake.Font = new Font(wake.Font, FontStyle.Bold);
        wake.Click += delegate { Wake(); };
        menu.Items.Add(L("Подробнее…", "Details…")).Click += delegate { ShowDetails(); };
        menu.Items.Add(L("Обновить сейчас", "Refresh now")).Click += delegate { kick.Set(); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L("Выход", "Exit")).Click += delegate { Application.Exit(); };
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { Wake(); };

        SetIcon(-1, false);
        SetTip(L("Телефон: подключаюсь…", "Phone: connecting…"));
        tray.Visible = true;

        // Start the adb server separately and without capturing output: otherwise it holds the pipe and reads hang
        RunAdbDetached("start-server");
        var poller = new Thread(PollLoop);
        poller.IsBackground = true;
        poller.Start();

        Application.Run();

        tray.Visible = false;
        tray.Dispose();
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        GC.KeepAlive(mutex);
    }

    // ---------- adb ----------
    static string RunAdb(string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(Adb, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (var p = Process.Start(psi))
            {
                adbMissing = false;
                var output = p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
                return output.Wait(2000) ? output.Result : "";
            }
        }
        catch (System.ComponentModel.Win32Exception) { adbMissing = true; return ""; }
        catch { return ""; }
    }

    static void RunAdbDetached(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(Adb, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (var p = Process.Start(psi)) p.WaitForExit(10000);
        }
        catch { }
    }

    static string PhoneIp()
    {
        try
        {
            if (File.Exists(IpFile))
            {
                string ip = File.ReadAllText(IpFile).Trim();
                if (ip.Length > 0) return ip;
            }
        }
        catch { }
        return null;
    }

    static Dictionary<string, string> Devices()
    {
        var list = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(RunAdb("devices", 8000), @"(?m)^(\S+)\s+(device|offline|unauthorized)\s*$"))
            list[m.Groups[1].Value] = m.Groups[2].Value;
        return list;
    }

    static string State(Dictionary<string, string> devs, string key)
    {
        string s;
        return key != null && devs.TryGetValue(key, out s) ? s : null;
    }

    // Device id for adb commands, or null.
    // With a USB cable attached, switches adb to Wi-Fi by itself and remembers the phone's address.
    static string Connect()
    {
        string ip = PhoneIp();
        string addr = ip == null ? null : ip + ":" + Port;
        var devs = Devices();
        string usb = null;
        unauthorized = false;
        foreach (var kv in devs)
        {
            if (kv.Value == "unauthorized") unauthorized = true;
            if (!kv.Key.Contains(":") && kv.Value == "device") { usb = kv.Key; break; }
        }
        if (usb == null) tcpipDone = null;

        if (usb != null)
        {
            var m = Regex.Match(RunAdb("-s " + usb + " shell ip -f inet addr show wlan0", 8000), @"inet (\d+\.\d+\.\d+\.\d+)/");
            if (m.Success)
            {
                addr = m.Groups[1].Value + ":" + Port;
                try { File.WriteAllText(IpFile, m.Groups[1].Value); } catch { }
            }
            if (addr != null && State(devs, addr) != "device" && tcpipDone != usb)
            {
                tcpipDone = usb;
                RunAdb("-s " + usb + " tcpip " + Port, 8000);
                Thread.Sleep(2000);
                RunAdb("connect " + addr, 6000);
            }
            return usb;
        }
        if (addr == null)
        {
            // address not known yet: use a Wi-Fi device connected by hand (adb connect), else wait for USB
            foreach (var kv in devs)
                if (kv.Key.Contains(":") && kv.Value == "device") return kv.Key;
            return null;
        }
        if (State(devs, addr) == "device") return addr;
        if (State(devs, addr) == "offline") RunAdb("disconnect " + addr, 5000);
        RunAdb("connect " + addr, 6000);
        return State(Devices(), addr) == "device" ? addr : null;
    }

    static Dictionary<string, string> ReadBattery(string device)
    {
        string raw = RunAdb("-s " + device + " shell \"dumpsys battery; dumpsys power | grep mWakefulness=\"", 8000);
        if (!Regex.IsMatch(raw, @"(?m)^\s*level:\s*\d+")) return null;
        var v = new Dictionary<string, string>();
        foreach (string k in new[] { "level", "status", "health", "temperature", "voltage", "AC powered", "USB powered", "Wireless powered", "mSavedBatteryAsoc", "mWakefulness" })
        {
            var m = Regex.Match(raw, @"(?m)^\s*" + Regex.Escape(k) + @"[:=]\s*(\S+)");
            if (m.Success) v[k] = m.Groups[1].Value;
        }
        return v;
    }

    // ---------- polling ----------
    static void PollLoop()
    {
        TrimHistory();
        while (true)
        {
            try { UpdateStatus(); } catch { }
            kick.WaitOne(PollSec * 1000);
        }
    }

    static string Get(Dictionary<string, string> v, string k)
    {
        string s;
        return v.TryGetValue(k, out s) ? s : "";
    }

    static bool Plugged(Dictionary<string, string> v)
    {
        return Get(v, "AC powered") == "true" || Get(v, "USB powered") == "true" || Get(v, "Wireless powered") == "true";
    }

    static string Power(Dictionary<string, string> v)
    {
        if (Get(v, "AC powered") == "true") return L("от сети", "AC");
        if (Get(v, "USB powered") == "true") return L("от USB", "USB");
        if (Get(v, "Wireless powered") == "true") return L("беспроводная", "wireless");
        return L("нет", "none");
    }

    static string StatusName(string s)
    {
        switch (s)
        {
            case "2": return L("заряжается", "charging");
            case "3": return L("разряжается", "discharging");
            case "4": return L("не заряжается", "not charging");
            case "5": return L("полностью заряжен", "full");
            default: return L("неизвестно", "unknown");
        }
    }

    static string HealthName(string s)
    {
        switch (s)
        {
            case "2": return L("хорошее", "good");
            case "3": return L("перегрев", "overheat");
            case "4": return L("батарея изношена", "dead");
            case "5": return L("перенапряжение", "over voltage");
            case "7": return L("холодно", "cold");
            default: return L("неизвестно", "unknown");
        }
    }

    static string Temp(Dictionary<string, string> v)
    {
        double t;
        return double.TryParse(Get(v, "temperature"), out t) ? (t / 10).ToString("0.0", UiCulture) : "?";
    }

    static void OnUi(Action a)
    {
        try { ui.BeginInvoke(a); } catch { }
    }

    static void UpdateStatus()
    {
        dev = Connect();
        var v = dev != null ? ReadBattery(dev) : null;
        string now = DateTime.Now.ToString("HH:mm");

        if (v == null)
        {
            failCount++;
            // tray tips are cut at 63 characters
            string lostTip, lostTitle, lostText;
            if (adbMissing)
            {
                lostTip = L("Телефон: не найден adb.exe", "Phone: adb.exe not found");
                lostTitle = L("Не найден adb.exe", "adb.exe not found");
                lostText = L("Положите папку platform-tools из Android SDK рядом с программой — подробности в README.", "Put the Android SDK platform-tools folder next to the program. See the README.");
            }
            else if (unauthorized)
            {
                lostTip = L("Телефон: разрешите отладку по USB", "Phone: allow USB debugging");
                lostTitle = L("Разрешите отладку по USB", "Allow USB debugging");
                lostText = L("На экране телефона: «Всегда разрешать с этого компьютера» и «Разрешить».", "On the phone screen: tick \"Always allow from this computer\" and tap Allow.");
            }
            else
            {
                lostTip = L("Телефон: нет связи (", "Phone: no connection (") + now + L(")\nПосле перезагрузки — USB-кабель", ")\nAfter a reboot: USB for a minute");
                lostTitle = L("Телефон не отвечает", "Phone is not responding");
                lostText = L("Если телефон перезагружался, подключите его кабелем к компьютеру на минуту — программа сама включит связь по Wi-Fi.", "If the phone has rebooted, connect it to the computer by USB for a minute and the program will switch adb back to Wi-Fi.");
            }
            bool warn = (adbMissing || unauthorized || failCount >= 3) && !lostWarned;
            if (warn) lostWarned = true;
            OnUi(delegate
            {
                SetIcon(-1, false);
                SetTip(lostTip);
                if (warn) tray.ShowBalloonTip(10000, lostTitle, lostText, ToolTipIcon.Warning);
            });
            return;
        }

        failCount = 0;
        lostWarned = false;
        v["time"] = now;
        last = v;
        int level = int.Parse(Get(v, "level"));
        bool charging = Get(v, "status") == "2" || Get(v, "status") == "5" || Plugged(v);
        bool unplugged = wasCharging == true && !charging;
        bool low = !charging && level <= LowLevel;
        bool lowNow = low && !lowWarned;
        if (lowNow) lowWarned = true;
        if (!low) lowWarned = false;
        wasCharging = charging;
        // at startup and after a gap (PC slept, no connection), fetch the phone's own history
        if (!phoneFilled || (DateTime.Now - lastOk).TotalMinutes > GapMin)
        {
            FillFromPhone(dev);
            phoneFilled = true;
        }
        lastOk = DateTime.Now;
        AppendHistory(level, charging);
        string screen = Get(v, "mWakefulness") == "Awake" ? L("экран вкл", "screen on") : L("экран выкл", "screen off");
        string tip = L("Телефон: ", "Phone: ") + level + " %, " + StatusName(Get(v, "status")) + "\n" + Temp(v) + " °C · " + screen + " · " + now;

        OnUi(delegate
        {
            SetIcon(level, charging);
            SetTip(tip);
            if (unplugged) tray.ShowBalloonTip(10000, L("Зарядка отключилась", "Charger disconnected"), L("Заряд ", "Battery ") + level + L(" %. Проверьте кабель.", " %. Check the cable."), ToolTipIcon.Warning);
            if (lowNow) tray.ShowBalloonTip(10000, L("Заряд телефона ", "Phone battery ") + level + " %", L("Подключите зарядку.", "Plug in the charger."), ToolTipIcon.Warning);
        });
    }

    static void Wake()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            string d = dev;
            if (d == null)
            {
                OnUi(delegate { tray.ShowBalloonTip(5000, L("Телефон не отвечает", "Phone is not responding"), L("Разбудить не получилось: нет связи с телефоном.", "Could not wake the phone: no connection."), ToolTipIcon.Warning); });
                return;
            }
            RunAdb("-s " + d + " shell \"input keyevent 224; sleep 1; wm dismiss-keyguard\"", 8000);
            Thread.Sleep(800);
            kick.Set();
        });
    }

    // ---------- Details window ----------
    // The whole window is drawn as one picture in the style of Windows 11 Settings: cards, Segoe UI Variable,
    // light or dark like Windows apps. The Close button is drawn too; the picture catches clicks on it.
    class Theme
    {
        public bool Dark;
        public Color Bg, Card, CardBorder, Text, Sub, Grid, Accent, Charge, Low, Track, Button, ButtonBorder;
        public static Theme Get(bool dark)
        {
            var t = new Theme();
            t.Dark = dark;
            if (dark)
            {
                t.Bg = Color.FromArgb(32, 32, 32); t.Card = Color.FromArgb(43, 43, 43); t.CardBorder = Color.FromArgb(28, 28, 28);
                t.Text = Color.White; t.Sub = Color.FromArgb(165, 165, 165); t.Grid = Color.FromArgb(60, 60, 60);
                t.Accent = Color.FromArgb(96, 205, 255); t.Charge = Color.FromArgb(108, 203, 95); t.Low = Color.FromArgb(255, 153, 164);
                t.Track = Color.FromArgb(72, 72, 72); t.Button = Color.FromArgb(55, 55, 55); t.ButtonBorder = Color.FromArgb(68, 68, 68);
            }
            else
            {
                t.Bg = Color.FromArgb(243, 243, 243); t.Card = Color.White; t.CardBorder = Color.FromArgb(229, 229, 229);
                t.Text = Color.FromArgb(26, 26, 26); t.Sub = Color.FromArgb(96, 96, 96); t.Grid = Color.FromArgb(232, 232, 232);
                t.Accent = Color.FromArgb(0, 95, 184); t.Charge = Color.FromArgb(15, 123, 15); t.Low = Color.FromArgb(196, 43, 28);
                t.Track = Color.FromArgb(222, 222, 222); t.Button = Color.FromArgb(251, 251, 251); t.ButtonBorder = Color.FromArgb(214, 214, 214);
            }
            return t;
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static bool SystemDark()
    {
        try
        {
            object o = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return o is int && (int)o == 0;
        }
        catch { return false; }
    }

    static bool TaskbarLight()
    {
        try
        {
            object o = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
            return o is int && (int)o == 1;
        }
        catch { return false; }
    }

    // Icon glyphs: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets (same code points) on Windows 10
    static Font IconFont(float px)
    {
        var f = new Font("Segoe Fluent Icons", px, GraphicsUnit.Pixel);
        if (f.Name == "Segoe Fluent Icons") return f;
        f.Dispose();
        return new Font("Segoe MDL2 Assets", px, GraphicsUnit.Pixel);
    }

    static Font UiFont(float px, bool semibold, bool display)
    {
        string name = display ? (semibold ? "Segoe UI Variable Display Semib" : "Segoe UI Variable Display")
                              : (semibold ? "Segoe UI Variable Text Semibold" : "Segoe UI Variable Text");
        var f = new Font(name, px, GraphicsUnit.Pixel);
        if (f.Name == name) return f;
        f.Dispose();
        return new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", px, GraphicsUnit.Pixel);
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void FillRounded(Graphics g, Color fill, Color border, RectangleF r, float rad, float k)
    {
        using (var path = Rounded(r, rad))
        using (var b = new SolidBrush(fill))
        {
            g.FillPath(b, path);
            if (border != Color.Empty)
                using (var pen = new Pen(border, Math.Max(1, k))) g.DrawPath(pen, path);
        }
    }

    static void Write(Graphics g, string s, Font f, Color c, float x, float y)
    {
        using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y, StringFormat.GenericTypographic);
    }

    static float Width(Graphics g, string s, Font f)
    {
        return g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width;
    }

    static string Cap(string s)
    {
        return s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1);
    }

    static List<string[]> DetailRows(Dictionary<string, string> v)
    {
        var r = new List<string[]>();
        double volt;
        r.Add(new[] { L("Температура", "Temperature"), Temp(v) + " °C" });
        if (double.TryParse(Get(v, "voltage"), out volt)) r.Add(new[] { L("Напряжение", "Voltage"), (volt / 1000).ToString("0.00", UiCulture) + L(" В", " V") });
        r.Add(new[] { L("Питание", "Power"), Cap(Power(v)) });
        r.Add(new[] { L("Здоровье", "Health"), Cap(HealthName(Get(v, "health"))) });
        if (Get(v, "mSavedBatteryAsoc") != "") r.Add(new[] { L("Ёмкость от заводской", "Capacity vs. new"), Get(v, "mSavedBatteryAsoc") + " %" });
        r.Add(new[] { L("Экран", "Screen"), Get(v, "mWakefulness") == "Awake" ? L("Включён", "On") : L("Выключен", "Off") });
        return r;
    }

    static Bitmap DrawDetails(Dictionary<string, string> v, string device, List<Sample> h, DateTime now, Theme t, float k,
                              out RectangleF button, out ChartGeo geo, out RectangleF[] spans)
    {
        spans = new RectangleF[SpanNames.Length];
        float W = 600 * k, pad = 20 * k, gap = 8 * k, cw = W - 2 * pad, x = pad;
        var rows = DetailRows(v);
        const int Cols = 3;
        float heroH = 112 * k, chartH = 230 * k, chartCardH = 44 * k + chartH + 8 * k;
        float cellH = 66 * k, footH = 32 * k;
        float infoH = ((rows.Count + Cols - 1) / Cols) * (cellH + gap) - gap;
        float H = pad + heroH + 12 * k + chartCardH + 12 * k + infoH + 20 * k + footH + pad;

        var bmp = new Bitmap((int)W, (int)Math.Ceiling(H));
        using (var g = Graphics.FromImage(bmp))
        using (var big = UiFont(46 * k, true, true))
        using (var title = UiFont(18 * k, true, false))
        using (var body = UiFont(14 * k, false, false))
        using (var value = UiFont(15 * k, true, false))
        using (var head = UiFont(14 * k, true, false))
        using (var small = UiFont(12 * k, false, false))
        {
            g.Clear(t.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float y = pad;

            // top card: level, state, rate, level bar
            int level;
            int.TryParse(Get(v, "level"), out level);
            bool charging = Get(v, "status") == "2" || Get(v, "status") == "5" || Plugged(v);
            FillRounded(g, t.Card, t.CardBorder, new RectangleF(x, y, cw, heroH), 8 * k, k);
            string pct = level + " %";
            Write(g, pct, big, t.Text, x + 20 * k, y + 14 * k);
            float tx = x + 20 * k + Width(g, pct, big) + 28 * k;
            Write(g, Cap(StatusName(Get(v, "status"))), title, t.Text, tx, y + 20 * k);
            Write(g, SpeedDetail(h, now), body, t.Sub, tx, y + 48 * k);
            var track = new RectangleF(x + 20 * k, y + heroH - 24 * k, cw - 40 * k, 6 * k);
            FillRounded(g, t.Track, Color.Empty, track, 3 * k, k);
            Color bar = charging ? t.Charge : (level <= LowLevel ? t.Low : t.Accent);
            if (level > 0)
                FillRounded(g, bar, Color.Empty, new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * Math.Min(100, level) / 100f), track.Height), 3 * k, k);
            y += heroH + 12 * k;

            // chart
            FillRounded(g, t.Card, t.CardBorder, new RectangleF(x, y, cw, chartCardH), 8 * k, k);
            Write(g, SpanTitles[spanIdx], head, t.Text, x + 16 * k, y + 16 * k);
            // period switch on the right of the card header
            float segH = 26 * k, segPad = 3 * k, sx = x + cw - 12 * k;
            for (int i = SpanNames.Length - 1; i >= 0; i--)
            {
                float w = Width(g, SpanNames[i], small) + 20 * k;
                sx -= w;
                spans[i] = new RectangleF(sx, y + 12 * k + segPad, w, segH - 2 * segPad);
            }
            FillRounded(g, t.Bg, t.CardBorder, new RectangleF(sx - segPad, y + 12 * k, x + cw - 12 * k - sx + 2 * segPad, segH), 6 * k, k);
            for (int i = 0; i < SpanNames.Length; i++)
            {
                var r = spans[i];
                if (i == spanIdx) FillRounded(g, t.Button, t.ButtonBorder, r, 4 * k, k);
                Write(g, SpanNames[i], small, i == spanIdx ? t.Text : t.Sub, r.X + 10 * k, r.Y + (r.Height - small.Height) / 2);
            }
            using (var ch = DrawChart(h, now, SpanHours[spanIdx], k, t, (int)(cw - 16 * k), (int)chartH, out geo))
                g.DrawImage(ch, x + 8 * k, y + 44 * k, ch.Width, ch.Height);
            geo.Plot.Offset(x + 8 * k, y + 44 * k);
            y += chartCardH + 12 * k;

            // other values: small cards, three per row
            float cellW = (cw - gap * (Cols - 1)) / Cols;
            for (int i = 0; i < rows.Count; i++)
            {
                float cx = x + (i % Cols) * (cellW + gap), cy = y + (i / Cols) * (cellH + gap);
                FillRounded(g, t.Card, t.CardBorder, new RectangleF(cx, cy, cellW, cellH), 8 * k, k);
                Write(g, rows[i][0], small, t.Sub, cx + 14 * k, cy + 12 * k);
                Write(g, rows[i][1], value, t.Text, cx + 14 * k, cy + 34 * k);
            }
            y += infoH + 20 * k;

            // footer: connection and button
            Write(g, L("Связь ", "Connected ") + device + L(" · обновлено ", " · updated ") + Get(v, "time"), small, t.Sub, x, y + (footH - small.Height) / 2);
            button = new RectangleF(x + cw - 120 * k, y, 120 * k, footH);
            FillRounded(g, t.Button, t.ButtonBorder, button, 4 * k, k);
            Write(g, L("Закрыть", "Close"), body, t.Text, button.X + (button.Width - Width(g, L("Закрыть", "Close"), body)) / 2, button.Y + (footH - body.Height) / 2);
        }
        return bmp;
    }

    static void ShowDetails()
    {
        var v = last;
        if (v == null) { MessageBox.Show(L("Данных с телефона пока нет.", "No data from the phone yet."), WindowTitle); return; }
        float k;
        using (var g = ui.CreateGraphics()) k = g.DpiX / 96f;
        var t = Theme.Get(SystemDark());
        RectangleF btn = RectangleF.Empty;
        ChartGeo geo = null;
        RectangleF[] spans = null;
        Bitmap bmp = null;
        var h = LoadHistory(DateTime.Now.AddHours(-SpanHours[SpanHours.Length - 1]));
        DateTime now = DateTime.Now;
        Point? mouse = null;
        Action render = delegate
        {
            var old = bmp;
            bmp = DrawDetails(v, dev, h, now, t, k, out btn, out geo, out spans);
            if (old != null) old.Dispose();
        };
        render();

        var form = new Form();
        form.Text = WindowTitle;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.AutoScaleMode = AutoScaleMode.None;
        form.BackColor = t.Bg;
        form.ClientSize = bmp.Size;
        form.KeyPreview = true;
        form.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) form.Close(); };
        // dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE)
        form.HandleCreated += delegate { int on = t.Dark ? 1 : 0; DwmSetWindowAttribute(form.Handle, 20, ref on, 4); };

        var pic = new PictureBox();
        pic.Image = bmp;
        pic.SizeMode = PictureBoxSizeMode.AutoSize;
        Func<Point, int> spanAt = p => { for (int i = 0; i < spans.Length; i++) if (spans[i].Contains(p)) return i; return -1; };
        pic.MouseMove += delegate(object s, MouseEventArgs e)
        {
            pic.Cursor = btn.Contains(e.Location) || spanAt(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
            mouse = e.Location;
            pic.Invalidate();
        };
        pic.MouseLeave += delegate { mouse = null; pic.Invalidate(); };
        pic.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (btn.Contains(e.Location)) { form.Close(); return; }
            int i = spanAt(e.Location);
            if (i < 0 || i == spanIdx) return;
            spanIdx = i;
            render();
            pic.Image = bmp;
        };
        // the hover tooltip is painted over the window picture
        pic.Paint += delegate(object s, PaintEventArgs e) { if (mouse.HasValue) DrawHover(e.Graphics, geo, h, mouse.Value, t, k); };
        form.Controls.Add(pic);

        // window and taskbar icon: the same phone with the charge level as in the tray
        int lv;
        int.TryParse(Get(v, "level"), out lv);
        bool chg = Get(v, "status") == "2" || Get(v, "status") == "5" || Plugged(v);
        Color fore = TaskbarLight() ? Color.FromArgb(26, 26, 26) : White;
        IntPtr hBig;
        using (var ib = DrawIcon(Math.Max(32, GetSystemMetrics(11)), lv, chg, fore)) hBig = ib.GetHicon();   // SM_CXICON
        form.Icon = Icon.FromHandle(hBig);

        form.ShowDialog();
        DestroyIcon(hBig);
        bmp.Dispose();
        form.Dispose();
    }

    // phone-battery.exe --details file.png [history.csv]: the window with sample values, every period in dark and 24 h in light
    static void PreviewDetails(string path)
    {
        var v = new Dictionary<string, string>();
        v["status"] = "3"; v["health"] = "2"; v["temperature"] = "288"; v["voltage"] = "4050";
        v["mSavedBatteryAsoc"] = "87"; v["mWakefulness"] = "Asleep"; v["time"] = DateTime.Now.ToString("HH:mm");
        var h = LoadHistory(DateTime.Now.AddHours(-SpanHours[SpanHours.Length - 1]));
        v["level"] = h.Count > 0 ? h[h.Count - 1].Level.ToString() : "88";
        float k = 1.25f;
        // the hover tooltip is shown on the sample 90 minutes before the last one
        var shots = new List<Bitmap>();
        for (int i = 0; i <= SpanNames.Length; i++)
        {
            bool dark = i < SpanNames.Length;
            spanIdx = dark ? i : 2;
            var t = Theme.Get(dark);
            RectangleF btn; ChartGeo geo; RectangleF[] spans;
            var bmp = DrawDetails(v, "192.168.0.10:5555", h, DateTime.Now, t, k, out btn, out geo, out spans);
            if (h.Count > 0)
            {
                DateTime at = h[h.Count - 1].T.AddMinutes(-90);
                float px = geo.Plot.X + (float)((at - geo.From).TotalHours / geo.Hours) * geo.Plot.Width;
                using (var g = Graphics.FromImage(bmp)) DrawHover(g, geo, h, new Point((int)px, (int)(geo.Plot.Y + geo.Plot.Height / 2)), t, k);
            }
            shots.Add(bmp);
        }
        int gapPx = 30, W = gapPx, H = 0;
        foreach (var s in shots) { W += s.Width + gapPx; H = Math.Max(H, s.Height); }
        using (var outBmp = new Bitmap(W, H + 2 * gapPx))
        using (var g = Graphics.FromImage(outBmp))
        {
            g.Clear(Color.FromArgb(90, 90, 90));
            int px = gapPx;
            foreach (var s in shots) { g.DrawImage(s, px, gapPx, s.Width, s.Height); px += s.Width + gapPx; s.Dispose(); }
            outBmp.Save(path);
        }
    }

    // ---------- history and chart ----------
    static void AppendHistory(int level, bool charging)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ";" + level + ";" + (charging ? 1 : 0) + "\n";
        lock (historyLock)
            try { File.AppendAllText(HistoryFile, line); } catch { }
    }

    // Own readings; where there are none for longer than GapMin, per-minute points from the phone's history
    static List<Sample> LoadHistory(DateTime from)
    {
        var own = ReadSamples(HistoryFile, from, false);
        var phone = ReadSamples(PhoneHistoryFile, from, true);
        if (phone.Count == 0) return own;
        var list = new List<Sample>(own);
        int j = 0;
        foreach (var s in phone)
        {
            while (j < own.Count && own[j].T < s.T) j++;
            bool near = (j < own.Count && (own[j].T - s.T).TotalMinutes <= GapMin / 2.0)
                     || (j > 0 && (s.T - own[j - 1].T).TotalMinutes <= GapMin / 2.0);
            if (!near) list.Add(s);
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        return list;
    }

    static List<Sample> ReadSamples(string file, DateTime from, bool phone)
    {
        var list = new List<Sample>();
        string[] lines;
        lock (historyLock)
        {
            try { if (!File.Exists(file)) return list; lines = File.ReadAllLines(file); }
            catch { return list; }
        }
        foreach (string line in lines)
        {
            string[] p = line.Split(';');
            Sample s;
            if (p.Length != 3) continue;
            if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out s.T)) continue;
            if (!int.TryParse(p[1], out s.Level)) continue;
            s.Charging = p[2] == "1";
            s.Phone = phone;
            if (s.T >= from) list.Add(s);
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        return list;
    }

    // Fetch the phone's battery history and lay it out per minute in phone-history.csv.
    // The phone logs an event at least every 30 seconds: "offset (n) 093 status=...", where offset counts
    // from the last "TIME: yyyy-mm-dd-hh-mm-ss" mark. The phone wipes this history after every charge, so it is
    // fetched after every gap. A pause over 10 minutes, SHUTDOWN or START means the phone was off: not filled
    static readonly Regex HistLine = new Regex(@"^\s*(0|\+[0-9dhms]+)\s+\(\d+\)\s+(.*)$");
    static readonly Regex HistOffset = new Regex(@"(\d+)(ms|d|h|m|s)");
    static readonly Regex HistTime = new Regex(@"TIME: (\d{4}-\d\d-\d\d-\d\d-\d\d-\d\d)");
    static readonly Regex HistLevel = new Regex(@"^(\d{3})\b");
    static readonly Regex HistStatus = new Regex(@"\bstatus=([a-z-]+)");

    static bool FillFromPhone(string device)
    {
        string raw = RunAdb("-s " + device + " shell dumpsys batterystats --history", 60000);
        if (raw.Length == 0) return false;
        var blocks = new List<List<Sample>>();
        var cur = new List<Sample>();
        DateTime baseT = DateTime.MinValue;
        double prevSec = -1;
        bool charging = false;
        foreach (string line in raw.Split('\n'))
        {
            var m = HistLine.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            double sec = 0;
            foreach (Match o in HistOffset.Matches(m.Groups[1].Value))
            {
                double n = double.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture);
                string u = o.Groups[2].Value;
                sec += u == "d" ? n * 86400 : u == "h" ? n * 3600 : u == "m" ? n * 60 : u == "s" ? n : n / 1000;
            }
            string rest = m.Groups[2].Value;
            bool power = rest.StartsWith("SHUTDOWN") || rest.StartsWith("START");
            bool brk = power || (prevSec >= 0 && sec - prevSec > 600);
            prevSec = sec;
            if (brk) { if (cur.Count > 0) blocks.Add(cur); cur = new List<Sample>(); }
            if (power) baseT = DateTime.MinValue;   // after power-on, time only from a new TIME mark
            var tm = HistTime.Match(rest);
            if (tm.Success)
            {
                DateTime wall;
                if (DateTime.TryParseExact(tm.Groups[1].Value, "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out wall))
                    baseT = wall.AddSeconds(-sec);
                continue;
            }
            var lv = HistLevel.Match(rest);
            if (!lv.Success || baseT == DateTime.MinValue) continue;
            var st = HistStatus.Match(rest);
            if (st.Success) charging = st.Groups[1].Value == "charging" || st.Groups[1].Value == "full";
            Sample s;
            s.T = baseT.AddSeconds(sec);
            s.Level = int.Parse(lv.Groups[1].Value);
            s.Charging = charging;
            s.Phone = true;
            if (s.T <= DateTime.Now.AddMinutes(1)) cur.Add(s);
        }
        if (cur.Count > 0) blocks.Add(cur);
        if (blocks.Count == 0) return false;

        // per-minute points inside each continuous block
        var lines = new List<string>();
        foreach (var b in blocks)
        {
            DateTime t = b[0].T.AddTicks(-(b[0].T.Ticks % TimeSpan.TicksPerMinute));
            if (t < b[0].T) t = t.AddMinutes(1);
            int j = 0;
            for (; t <= b[b.Count - 1].T; t = t.AddMinutes(1))
            {
                while (j + 1 < b.Count && b[j + 1].T <= t) j++;
                lines.Add(t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ";" + b[j].Level + ";" + (b[j].Charging ? 1 : 0));
            }
        }
        if (lines.Count == 0) return false;

        // keep old lines only before the new block and not older than KeepDays
        string covered = blocks[0][0].T.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string cut = DateTime.Now.AddDays(-KeepDays).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        lock (historyLock)
        {
            try
            {
                var keep = new List<string>();
                if (File.Exists(PhoneHistoryFile))
                    foreach (string l in File.ReadAllLines(PhoneHistoryFile))
                        if (string.CompareOrdinal(l, covered) < 0 && string.CompareOrdinal(l, cut) >= 0) keep.Add(l);
                keep.AddRange(lines);
                WriteWhole(PhoneHistoryFile, string.Join("\n", keep.ToArray()) + "\n");
            }
            catch { return false; }
        }
        return true;
    }

    // Rewrite a whole file without risking an empty or cut file: write a temp file next to it and swap
    // in one step, so a power loss halfway leaves the old version intact
    static void WriteWhole(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    static void TrimHistory()
    {
        lock (historyLock)
        {
            try
            {
                if (!File.Exists(HistoryFile) || new FileInfo(HistoryFile).Length < 1024 * 1024) return;
                string cut = DateTime.Now.AddDays(-KeepDays).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var keep = new List<string>();
                foreach (string line in File.ReadAllLines(HistoryFile))
                    if (string.CompareOrdinal(line, cut) >= 0) keep.Add(line);
                WriteWhole(HistoryFile, string.Join("\n", keep.ToArray()) + "\n");
            }
            catch { }
        }
    }

    static string Duration(double hours)
    {
        int min = (int)Math.Round(hours * 60);
        if (min < 60) return Math.Max(1, min) + L(" мин", " min");
        return (min / 60) + L(" ч", " h") + (min % 60 > 0 ? " " + (min % 60) + L(" мин", " min") : "");
    }

    // Rate over the last continuous stretch in one state, at most two hours back
    static string SpeedDetail(List<Sample> h, DateTime now)
    {
        const double MinHours = 0.25;
        if (h.Count == 0) return L("скорость появится, когда накопится история", "rate appears once history builds up");
        if ((now - h[h.Count - 1].T).TotalMinutes > GapMin) return L("нет свежих замеров", "no recent readings");
        Sample end = h[h.Count - 1];
        int j = h.Count - 1;
        while (j > 0 && h[j - 1].Charging == end.Charging
               && (h[j].T - h[j - 1].T).TotalMinutes <= GapMin
               && (end.T - h[j - 1].T).TotalHours <= 2) j--;
        double hours = (end.T - h[j].T).TotalHours;
        if (hours < MinHours) return L("скорость — через ", "rate in ") + Duration(MinHours - hours);
        double rate = (end.Level - h[j].Level) / hours;
        if (end.Charging)
        {
            if (end.Level >= 100) return L("заряжен полностью", "fully charged");
            if (rate < 1) return L("меньше 1 %/ч", "under 1 %/h");
            return rate.ToString("0") + L(" %/ч · до 100 % около ", " %/h · full in about ") + Duration((100 - end.Level) / rate);
        }
        if (-rate < 1) return L("меньше 1 %/ч", "under 1 %/h");
        return (-rate).ToString("0") + L(" %/ч · хватит примерно на ", " %/h · lasts about ") + Duration(end.Level / -rate);
    }

    static string Speed(List<Sample> h, DateTime now)
    {
        return L("Скорость: ", "Rate: ") + SpeedDetail(h, now);
    }

    // Where the plot area is on the picture and what time it shows: for the hover tooltip
    class ChartGeo
    {
        public RectangleF Plot;
        public DateTime From;
        public double Hours;
    }

    // Line points for stretch a..b: the middle of each one-percent "step" plus both ends.
    // The level comes in whole percents, so a line through every reading looks like a staircase
    static List<PointF> StepMids(List<Sample> h, int a, int b, Func<DateTime, float> X, Func<int, float> Y)
    {
        var pts = new List<PointF>();
        pts.Add(new PointF(X(h[a].T), Y(h[a].Level)));
        float endX = X(h[b].T);
        int i = a;
        while (i <= b)
        {
            int j = i;
            while (j + 1 <= b && h[j + 1].Level == h[i].Level) j++;
            float mx = X(h[i].T + TimeSpan.FromTicks((h[j].T - h[i].T).Ticks / 2));
            if (mx > pts[pts.Count - 1].X + 0.5f && mx < endX - 0.5f) pts.Add(new PointF(mx, Y(h[i].Level)));
            i = j + 1;
        }
        pts.Add(new PointF(endX, Y(h[b].Level)));
        return pts;
    }

    // Chart of the `hours` before now, W x H pixels. k is the display scale (1 = 96 dpi)
    static Bitmap DrawChart(List<Sample> h, DateTime now, double hours, float k, Theme t, int W, int H, out ChartGeo geo)
    {
        float left = 46 * k, right = 10 * k, top = 8 * k, bottom = 52 * k;
        float pw = W - left - right, ph = H - top - bottom;
        DateTime from = now.AddHours(-hours);
        geo = new ChartGeo();
        geo.Plot = new RectangleF(left, top, pw, ph);
        geo.From = from;
        geo.Hours = hours;
        var bmp = new Bitmap(W, H);
        using (var g = Graphics.FromImage(bmp))
        using (var small = UiFont(11 * k, false, false))
        using (var grid = new Pen(t.Grid, Math.Max(1, k)))
        using (var axisText = new SolidBrush(t.Sub))
        {
            g.Clear(t.Card);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Func<DateTime, float> X = tm => left + (float)((tm - from).TotalHours / hours) * pw;
            Func<int, float> Y = lv => top + (100 - Math.Max(0, Math.Min(100, lv))) / 100f * ph;

            var sfRight = new StringFormat(); sfRight.Alignment = StringAlignment.Far; sfRight.LineAlignment = StringAlignment.Center;
            var sfCenter = new StringFormat(); sfCenter.Alignment = StringAlignment.Center;
            for (int lv = 0; lv <= 100; lv += 25)
            {
                g.DrawLine(grid, left, Y(lv), left + pw, Y(lv));
                g.DrawString(lv + " %", small, axisText, left - 6 * k, Y(lv), sfRight);
            }
            if (hours > 48)
            {
                // week: a line at midnight, the day label at noon
                for (DateTime day = from.Date.AddDays(1); day <= now; day = day.AddDays(1))
                    g.DrawLine(grid, X(day), top, X(day), top + ph);
                for (DateTime day = from.Date; day <= now; day = day.AddDays(1))
                {
                    DateTime noon = day.AddHours(12);
                    if (noon > from && noon < now)
                        g.DrawString(day.ToString("ddd d", UiCulture), small, axisText, X(noon), top + ph + 4 * k, sfCenter);
                }
            }
            else
            {
                int step = hours <= 6 ? 1 : hours <= 12 ? 2 : 3;
                DateTime tick = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0).AddHours(1);
                while (tick.Hour % step != 0) tick = tick.AddHours(1);
                for (; tick <= now; tick = tick.AddHours(step))
                {
                    g.DrawLine(grid, X(tick), top, X(tick), top + ph);
                    g.DrawString(tick.ToString("HH:mm"), small, axisText, X(tick), top + ph + 4 * k, sfCenter);
                }
            }

            int start = 0;
            while (start < h.Count && h[start].T < from) start++;
            if (h.Count - start < 2)
            {
                var sfMid = new StringFormat(); sfMid.Alignment = StringAlignment.Center; sfMid.LineAlignment = StringAlignment.Center;
                g.DrawString(L("История копится: замер раз в минуту,\nлиния появится через пару минут", "Collecting history: one reading a minute,\nthe line appears in a couple of minutes"), small, axisText, new RectangleF(left, top, pw, ph), sfMid);
            }

            using (var lineD = new Pen(t.Accent, 2 * k))
            using (var lineC = new Pen(t.Charge, 2 * k))
            using (var fillD = new SolidBrush(Color.FromArgb(t.Dark ? 45 : 40, t.Accent)))
            using (var fillC = new SolidBrush(Color.FromArgb(t.Dark ? 45 : 50, t.Charge)))
            using (var dash = new Pen(t.Sub, 1.5f * k))
            {
                lineD.StartCap = lineD.EndCap = LineCap.Round;
                lineC.StartCap = lineC.EndCap = LineCap.Round;
                lineD.LineJoin = lineC.LineJoin = LineJoin.Round;
                dash.DashPattern = new float[] { 3, 3 };
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // run: readings with no gap over GapMin; between runs, a dashed "no data" line
                PointF lastEnd = PointF.Empty;
                bool haveLast = false;
                int r = start;
                while (r < h.Count)
                {
                    int re = r;
                    while (re + 1 < h.Count && (h[re + 1].T - h[re].T).TotalMinutes <= GapMin) re++;
                    var first = new PointF(X(h[r].T), Y(h[r].Level));
                    if (haveLast) g.DrawLine(dash, lastEnd, first);
                    if (r == re)
                        using (var dot = new SolidBrush(h[r].Charging ? t.Charge : t.Accent))
                            g.FillEllipse(dot, first.X - 2.5f * k, first.Y - 2.5f * k, 5 * k, 5 * k);
                    // stretch: readings in one state inside a run; neighbours share the boundary point
                    int a = r;
                    while (a < re)
                    {
                        bool c = h[a + 1].Charging;
                        int b = a + 1;
                        while (b + 1 <= re && h[b + 1].Charging == c) b++;
                        var pts = StepMids(h, a, b, X, Y).ToArray();
                        using (var line = new GraphicsPath())
                        using (var area = new GraphicsPath())
                        {
                            if (pts.Length > 2) { line.AddCurve(pts, 0.5f); area.AddCurve(pts, 0.5f); }
                            else { line.AddLines(pts); area.AddLines(pts); }
                            PointF last = pts[pts.Length - 1];
                            area.AddLine(last.X, last.Y, last.X, top + ph);
                            area.AddLine(last.X, top + ph, pts[0].X, top + ph);
                            area.CloseFigure();
                            g.FillPath(c ? fillC : fillD, area);
                            g.DrawPath(c ? lineC : lineD, line);
                        }
                        a = b;
                    }
                    lastEnd = new PointF(X(h[re].T), Y(h[re].Level));
                    haveLast = true;
                    r = re + 1;
                }
            }

            // legend
            float ly = H - 18 * k, lx = left;
            using (var bD = new SolidBrush(t.Accent))
            using (var bC = new SolidBrush(t.Charge))
            using (var dl = new Pen(t.Sub, 1.5f * k))
            {
                g.FillRectangle(bD, lx, ly + 4 * k, 14 * k, 4 * k);
                g.DrawString(L("разряжается", "discharging"), small, axisText, lx + 18 * k, ly - 2 * k);
                lx += 18 * k + g.MeasureString(L("разряжается", "discharging"), small).Width + 14 * k;
                g.FillRectangle(bC, lx, ly + 4 * k, 14 * k, 4 * k);
                g.DrawString(L("заряжается", "charging"), small, axisText, lx + 18 * k, ly - 2 * k);
                lx += 18 * k + g.MeasureString(L("заряжается", "charging"), small).Width + 14 * k;
                dl.DashPattern = new float[] { 3, 3 };
                g.DrawLine(dl, lx, ly + 6 * k, lx + 14 * k, ly + 6 * k);
                g.DrawString(L("нет данных", "no data"), small, axisText, lx + 18 * k, ly - 2 * k);
            }
        }
        return bmp;
    }

    // Hover tooltip: the reading nearest to the pointer, with a line, a dot and a "time · percent" box
    static void DrawHover(Graphics g, ChartGeo geo, List<Sample> h, Point m, Theme t, float k)
    {
        if (geo == null) return;
        var plot = geo.Plot;
        if (m.X < plot.Left || m.X > plot.Right || m.Y < plot.Top || m.Y > plot.Bottom + 24 * k) return;
        Func<DateTime, float> X = tm => plot.X + (float)((tm - geo.From).TotalHours / geo.Hours) * plot.Width;
        int best = -1;
        float bd = float.MaxValue;
        for (int i = 0; i < h.Count; i++)
        {
            if (h[i].T < geo.From) continue;
            float d = Math.Abs(X(h[i].T) - m.X);
            if (d < bd) { bd = d; best = i; }
        }
        if (best < 0 || bd > 12 * k) return;
        Sample s = h[best];
        float px = X(s.T), py = plot.Y + (100 - Math.Max(0, Math.Min(100, s.Level))) / 100f * plot.Height;
        Color c = s.Charging ? t.Charge : t.Accent;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var vl = new Pen(Color.FromArgb(120, t.Sub), Math.Max(1, k))) g.DrawLine(vl, px, plot.Top, px, plot.Bottom);
        using (var ring = new SolidBrush(t.Card)) g.FillEllipse(ring, px - 6 * k, py - 6 * k, 12 * k, 12 * k);
        using (var dot = new SolidBrush(c)) g.FillEllipse(dot, px - 4 * k, py - 4 * k, 8 * k, 8 * k);

        string when = geo.Hours > 48 ? s.T.ToString("ddd d MMM, HH:mm", UiCulture) : s.T.ToString("HH:mm");
        string line1 = when + " · " + s.Level + " %";
        string line2 = (s.Charging ? L("заряжается", "charging") : L("разряжается", "discharging")) + (s.Phone ? L(" · по истории телефона", " · from phone history") : "");
        using (var f1 = UiFont(13 * k, true, false))
        using (var f2 = UiFont(12 * k, false, false))
        {
            float bw = Math.Max(Width(g, line1, f1), Width(g, line2, f2)) + 20 * k;
            float bh = f1.Height + f2.Height + 14 * k;
            float bx = px + 12 * k, by = py - bh - 10 * k;
            if (bx + bw > plot.Right) bx = px - 12 * k - bw;
            if (by < plot.Top) by = py + 12 * k;
            FillRounded(g, t.Button, t.ButtonBorder, new RectangleF(bx, by, bw, bh), 6 * k, k);
            Write(g, line1, f1, t.Text, bx + 10 * k, by + 6 * k);
            Write(g, line2, f2, c, bx + 10 * k, by + 8 * k + f1.Height);
        }
    }

    // ---------- tray icon ----------
    static void SetTip(string text)
    {
        tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }

    static void SetIcon(int level, bool charging)
    {
        int size = Math.Max(16, GetSystemMetrics(49));   // SM_CXSMICON: tray icon size for the current scale
        using (var bmp = DrawIcon(size, level, charging))
        {
            IntPtr h = bmp.GetHicon();
            IntPtr old = hIcon;
            tray.Icon = Icon.FromHandle(h);
            hIcon = h;
            if (old != IntPtr.Zero) DestroyIcon(old);
        }
    }

    static void DrawCentered(Graphics g, string text, Font font, Color color, RectangleF rect)
    {
        using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
        using (var b = new SolidBrush(color))
        {
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            g.DrawString(text, font, b, rect, sf);
        }
    }

    // level < 0: no connection, a grey outline with a question mark
    static Bitmap DrawIcon(int S, int level, bool charging)
    {
        return DrawIcon(S, level, charging, White);
    }

    // fore: outline colour, white for a dark taskbar, dark for a light one
    static Bitmap DrawIcon(int S, int level, bool charging, Color fore)
    {
        var bmp = new Bitmap(S, S);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Color c = level < 0 ? Gray : (!charging && level <= LowLevel ? Red : fore);
            using (var f = IconFont(S * 0.9f))
                DrawCentered(g, GlyphPhone, f, c, new RectangleF(0, 0, S, S));

            float inX = S * 0.31f, inW = S * 0.38f, top = S * 0.17f, h = S * 0.62f;
            if (level < 0)
            {
                using (var f = new Font("Segoe UI", S * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
                    DrawCentered(g, "?", f, c, new RectangleF(inX - S * 0.1f, top, inW + S * 0.2f, h));
                return bmp;
            }
            float fh = h * Math.Min(100, level) / 100f;
            using (var b = new SolidBrush(Color.FromArgb(110, c)))
                g.FillRectangle(b, inX, top + h - fh, inW, fh);
            if (charging)
                using (var f = IconFont(S * 0.34f))
                    DrawCentered(g, GlyphBolt, f, fore, new RectangleF(0, 0, S, S));
        }
        return bmp;
    }

    static void Preview(string path)
    {
        int S = Math.Max(16, GetSystemMetrics(49)), scale = 3, cell = S * scale + 20;
        var states = new[] { new object[] { 73, true }, new object[] { 67, false }, new object[] { 31, false }, new object[] { 30, false }, new object[] { 15, true }, new object[] { -1, false } };
        using (var outBmp = new Bitmap(cell * states.Length + 10, S * scale + S + 30))
        using (var g = Graphics.FromImage(outBmp))
        {
            g.Clear(Color.FromArgb(32, 32, 32));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int i = 0; i < states.Length; i++)
                using (var icon = DrawIcon(S, (int)states[i][0], (bool)states[i][1]))
                {
                    g.DrawImage(icon, 10 + i * cell, 10, S * scale, S * scale);
                    g.DrawImage(icon, 10 + i * cell, 20 + S * scale);
                }
            outBmp.Save(path);
        }
    }
}
