// LEDBRAIN Bridge — окно и значок в трее. Ядро (Core.cs) работает в этом же процессе, окно показывает источники NDI / Spout
// и кто их смотрит (опрос http://127.0.0.1:47800/status раз в секунду). Закрытие окна прячет в трей, «Выход» — из меню.
// Сборка: build.cmd (csc из .NET Framework 4.8, есть в Windows). C# 5 — без новых возможностей языка.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;

// сведения о файле: видны в «Свойствах» exe и в окне SmartScreen
[assembly: AssemblyTitle("LEDBRAIN Bridge")]
[assembly: AssemblyProduct("LEDBRAIN Bridge")]
[assembly: AssemblyDescription("NDI and Spout → LEDBRAIN 3D view (ledbrain.pro)")]
[assembly: AssemblyCompany("LEDBRAIN")]
[assembly: AssemblyCopyright("© 2026 Egor Gladkov")]
[assembly: AssemblyVersion("0.3.1.0")]
[assembly: AssemblyFileVersion("0.3.1.0")]

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        bool fresh;
        var single = new Mutex(true, "LEDBRAIN.Bridge.Single", out fresh);
        var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "LEDBRAIN.Bridge.Show");
        if (!fresh) { showEvt.Set(); return; } // уже запущен — показать его окно
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var app = new BridgeForm(showEvt);
        Application.Run(app);
        GC.KeepAlive(single);
    }
}

class Src
{
    public string Name; public bool Active; public int Clients; public double Fps; public double SrcFps; public int W; public int H; public string Gpu;
}

class BridgeForm : Form
{
    // язык: сохранённый выбор (HKCU\\Software\\LEDBRAIN\\Bridge, Lang) или язык Windows — русский для ru/uk/be/kk, иначе английский
    public static bool EN = PickLang();
    static string T(string ru, string en) { return EN ? en : ru; }
    const string CfgKey = @"Software\LEDBRAIN\Bridge";
    static bool PickLang()
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(CfgKey)) { var v = k == null ? null : k.GetValue("Lang") as string; if (v == "en") return true; if (v == "ru") return false; } } catch { }
        var l = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return !(l == "ru" || l == "uk" || l == "be" || l == "kk");
    }
    void SetLang(bool en)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(CfgKey)) k.SetValue("Lang", en ? "en" : "ru"); } catch { }
        EN = en; Server.EN = en;
        // окно и меню собираются заново на новом языке; Bridge не перезапускается
        var st = state; Controls.Clear(); BuildUi(); BuildMenu(); SetStatus(st, statusText); Poll();
    }
    // цвета LEDBRAIN
    static readonly Color Bg = Color.FromArgb(11, 14, 18), Surf = Color.FromArgb(20, 24, 31), Line = Color.FromArgb(38, 45, 56),
        Text1 = Color.FromArgb(231, 235, 241), Muted = Color.FromArgb(141, 150, 165), Accent = Color.FromArgb(47, 107, 255),
        Ok = Color.FromArgb(52, 199, 89), Warn = Color.FromArgb(245, 165, 36), Bad = Color.FromArgb(255, 77, 79);
    const int Port = 47800;
    const string SiteUrl = "https://ledbrain.pro";

    readonly string dir = AppDomain.CurrentDomain.BaseDirectory;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
    readonly EventWaitHandle showEvt;
    readonly Panel list = new DoubleBufferedPanel();
    readonly TextBox log = new TextBox();
    readonly Panel statusBox = new DoubleBufferedPanel();
    readonly Label hint = new Label(), verLbl = new Label();
    string statusText = ""; Color statusColor = Color.FromArgb(245, 165, 36);
    readonly ToolStripMenuItem autoItem = new ToolStripMenuItem();
    bool quitting, hintShown, polling;
    string state = "start"; // start | ok | busy | error
    string version = "", ndiPath = "";
    List<Src> sources = new List<Src>();
    Icon iconIdle, iconLive, iconBad;
    int failCount;

    public BridgeForm(EventWaitHandle evt)
    {
        showEvt = evt;
        Text = "LEDBRAIN Bridge";
        BackColor = Bg; ForeColor = Text1;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        using (var g0 = CreateGraphics()) K = g0.DpiX / 96f;
        ClientSize = new Size(S(480), S(572));
        StartPosition = FormStartPosition.CenterScreen;
        iconIdle = MakeIcon(Color.Empty); iconLive = MakeIcon(Ok); iconBad = MakeIcon(Bad);
        Icon = iconIdle;
        BuildUi();
        BuildTray();
        // второй запуск exe — показать это окно
        new Thread(() => { while (true) { showEvt.WaitOne(); try { BeginInvoke(new Action(ShowWindow)); } catch { return; } } }) { IsBackground = true }.Start();
        poll.Interval = 1000; poll.Tick += (s, e) => Poll(); poll.Start();
        StartCore();
        // --menu-preview: показать меню трея у правого нижнего угла через 3 с (для проверки вида)
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--menu-preview") >= 0)
        {
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += (s, e) => { t.Stop(); var wa = Screen.PrimaryScreen.WorkingArea; tray.ContextMenuStrip.Show(new Point(wa.Right - S(40), wa.Bottom - S(10)), ToolStripDropDownDirection.AboveLeft); };
            t.Start();
        }
    }

    // ---------- значок: четыре квадрата LEDBRAIN, точка состояния в углу ----------
    static Icon MakeIcon(Color dot)
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var b = new SolidBrush(Accent)) using (var b2 = new SolidBrush(Color.FromArgb(122, 166, 255)))
            {
                g.FillRectangle(b, 3, 3, 12, 12); g.FillRectangle(b2, 17, 3, 12, 12);
                g.FillRectangle(b2, 3, 17, 12, 12); g.FillRectangle(b, 17, 17, 12, 12);
            }
            if (dot != Color.Empty)
            {
                using (var p = new SolidBrush(Bg)) g.FillEllipse(p, 17, 17, 15, 15);
                using (var p = new SolidBrush(dot)) g.FillEllipse(p, 19, 19, 11, 11);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ---------- окно: все размеры в «пикселях при 100 %», S() переводит в масштаб экрана ----------
    static float K = 1f;
    static int S(double v) { return (int)Math.Round(v * K); }
    static Rectangle R(int x, int y, int w, int h) { return new Rectangle(S(x), S(y), S(w), S(h)); }
    static readonly Color Surf2 = Color.FromArgb(27, 33, 43), Hover = Color.FromArgb(36, 43, 55);

    // скруглённый прямоугольник — как карточки и кнопки на сайте
    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        if (d > r.Height) d = r.Height; if (d > r.Width) d = r.Width;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    public static void Card(Graphics g, Rectangle r, Color fill, Color border, float rad)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), rad))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (border != Color.Empty) using (var p = new Pen(border)) g.DrawPath(p, path);
        }
    }

    // тёмный заголовок окна и скруглённые углы Windows 11
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string id);
    public static void DarkWindow(IntPtr h, Color caption)
    {
        try
        {
            int on = 1, round = 2, col = caption.R | (caption.G << 8) | (caption.B << 16), txt = 0xF1EBE7;
            DwmSetWindowAttribute(h, 20, ref on, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(h, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
            DwmSetWindowAttribute(h, 35, ref col, 4);   // DWMWA_CAPTION_COLOR
            DwmSetWindowAttribute(h, 36, ref txt, 4);   // DWMWA_TEXT_COLOR
        }
        catch { }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); DarkWindow(Handle, Bg); }

    void BuildUi()
    {
        var logo = new PictureBox { Image = iconIdle.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = R(22, 22, 30, 30) };
        var title = new Label { Text = "LEDBRAIN Bridge", Font = new Font("Segoe UI Semibold", 13.5f), AutoSize = true, Location = new Point(S(62), S(16)), ForeColor = Text1, BackColor = Bg };
        verLbl.AutoSize = true; verLbl.Location = new Point(S(64), S(46)); verLbl.ForeColor = Muted; verLbl.BackColor = Bg; verLbl.Text = T("NDI и Spout → 3D-вид LEDBRAIN", "NDI and Spout → LEDBRAIN 3D view");

        // плашка состояния: точка и текст на скруглённой подложке
        statusBox.Bounds = R(20, 82, 440, 44);
        statusBox.Paint += (s, e) =>
        {
            var g = e.Graphics; g.Clear(Bg);
            Card(g, new Rectangle(0, 0, statusBox.Width, statusBox.Height), Color.FromArgb(28, statusColor), Color.FromArgb(70, statusColor), S(14));
            using (var b = new SolidBrush(statusColor)) g.FillEllipse(b, S(16), statusBox.Height / 2 - S(5), S(10), S(10));
            using (var f = new Font("Segoe UI Semibold", 10f))
                TextRenderer.DrawText(g, statusText, f, new Rectangle(S(36), 0, statusBox.Width - S(46), statusBox.Height), Text1, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        var srcTitle = new Label { Text = T("ИСТОЧНИКИ", "SOURCES"), AutoSize = true, Location = new Point(S(24), S(142)), ForeColor = Muted, BackColor = Bg, Font = new Font("Segoe UI Semibold", 8.5f) };
        list.Bounds = R(20, 164, 440, 140);
        list.Paint += PaintList;

        hint.AutoSize = false; hint.Bounds = R(24, 312, 432, 42); hint.ForeColor = Muted; hint.BackColor = Bg;
        hint.Text = T("В LEDBRAIN: «Сцена» → 3D → «Bridge · NDI / Spout». Нет источников — включите в Resolume выход NDI или Spout.", "In LEDBRAIN: Stage → 3D → Bridge · NDI / Spout. No sources? Turn on NDI or Spout output in Resolume.");

        var open = new RoundButton(T("Открыть LEDBRAIN", "Open LEDBRAIN"), true) { Bounds = R(20, 360, 214, 42) };
        open.Click += (s, e) => OpenSite();
        var hide = new RoundButton(T("Свернуть в трей", "Hide to tray"), false) { Bounds = R(246, 360, 214, 42) };
        hide.Click += (s, e) => HideToTray();

        var logTitle = new Label { Text = T("ЖУРНАЛ", "LOG"), AutoSize = true, Location = new Point(S(24), S(418)), ForeColor = Muted, BackColor = Bg, Font = new Font("Segoe UI Semibold", 8.5f) };
        var logCard = new DoubleBufferedPanel { Bounds = R(20, 440, 440, 116) };
        logCard.Paint += (s, e) => { e.Graphics.Clear(Bg); Card(e.Graphics, new Rectangle(0, 0, logCard.Width, logCard.Height), Surf, Line, S(14)); };
        log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.BorderStyle = BorderStyle.None;
        log.BackColor = Surf; log.ForeColor = Muted; log.Font = new Font("Consolas", 8.5f); log.Bounds = R(12, 10, 420, 96);
        log.HandleCreated += (s, e) => SetWindowTheme(log.Handle, "DarkMode_Explorer", null); // тёмная полоса прокрутки
        logCard.Controls.Add(log);

        Controls.AddRange(new Control[] { logo, title, verLbl, statusBox, srcTitle, list, hint, open, hide, logTitle, logCard });
        SetStatus("start", T("Запускается…", "Starting…"));
    }

    // строка источника: точка (идёт картинка / ждёт), метка NDI или SPOUT, имя; ниже — кто смотрит и с какой частотой
    void PaintList(object sender, PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Bg);
        Card(g, new Rectangle(0, 0, list.Width, list.Height), Surf, Line, S(14));
        if (sources.Count == 0)
        {
            TextRenderer.DrawText(g, state == "ok" ? T("Источников пока нет", "No sources yet") : "—", Font, new Rectangle(0, 0, list.Width, list.Height), Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        using (var small = new Font("Segoe UI", 8.5f))
        {
            int lineBig = TextRenderer.MeasureText("Ag", Font).Height, lineSmall = TextRenderer.MeasureText("Ag", small).Height;
            int rowH = lineBig + lineSmall + S(18), y = S(6);
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (y + rowH > list.Height) break;
                if (s.Active) Card(g, new Rectangle(S(6), y, list.Width - S(12), rowH - S(4)), Color.FromArgb(22, Ok), Color.Empty, S(10));
                bool spout = s.Name.StartsWith("Spout");
                string kind = spout ? "SPOUT" : "NDI", name = s.Name.Substring(s.Name.IndexOf('·') + 1).Trim();
                var ts = TextRenderer.MeasureText(kind, small);
                int ty = y + S(7);
                var tag = new Rectangle(S(32), ty + (lineBig - ts.Height) / 2, ts.Width + S(8), ts.Height);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(s.Active ? Ok : Color.FromArgb(70, 78, 92))) g.FillEllipse(b, S(15), ty + lineBig / 2 - S(4), S(9), S(9));
                Card(g, tag, spout ? Color.FromArgb(52, 38, 82) : Color.FromArgb(28, 48, 92), Color.Empty, S(6));
                TextRenderer.DrawText(g, kind, small, tag, spout ? Color.FromArgb(200, 170, 255) : Color.FromArgb(150, 185, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, name, Font, new Rectangle(tag.Right + S(8), ty, list.Width - tag.Right - S(20), lineBig), Text1, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                string info = s.Active
                    ? string.Format(T("смотрят: {0} · {1} к/с · {2}×{3}{4}", "watching: {0} · {1} fps · {2}×{3}{4}"), s.Clients, s.Fps, s.W, s.H, string.IsNullOrEmpty(s.Gpu) ? "" : " · " + s.Gpu.Replace("NVIDIA GeForce ", "").Replace(" Laptop GPU", ""))
                    : T("ожидает — выберите в LEDBRAIN", "waiting — pick it in LEDBRAIN");
                TextRenderer.DrawText(g, info, small, new Rectangle(S(32), ty + lineBig + S(2), list.Width - S(44), lineSmall), s.Active ? Ok : Muted, TextFormatFlags.EndEllipsis);
                y += rowH;
            }
        }
    }

    void SetStatus(string st, string text)
    {
        state = st;
        statusText = text;
        statusColor = st == "ok" || st == "busy" ? Ok : st == "start" ? Warn : Bad;
        statusBox.Invalidate();
        bool live = false; foreach (var s in sources) if (s.Active) live = true;
        var ic = st == "ok" ? (live ? iconLive : iconIdle) : st == "start" ? iconIdle : iconBad;
        if (tray.Icon != ic) tray.Icon = ic;
        string tip = "LEDBRAIN Bridge — " + text;
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    // ---------- трей: тёмное меню со скруглёнными углами, сверху — состояние ----------
    readonly ToolStripMenuItem stateItem = new ToolStripMenuItem(), watchItem = new ToolStripMenuItem();
    void BuildTray()
    {
        BuildMenu();
        tray.Icon = iconIdle; tray.Text = "LEDBRAIN Bridge"; tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
    }
    void BuildMenu()
    {
        var old = tray.ContextMenuStrip;
        var menu = new ContextMenuStrip { Renderer = new TrayRenderer(), ShowImageMargin = false, ShowCheckMargin = false, Font = new Font("Segoe UI", 10f), Padding = new Padding(S(4), S(6), S(4), S(6)), BackColor = TrayRenderer.MenuBg, ForeColor = Text1, DropShadowEnabled = true };
        menu.HandleCreated += (s, e) => DarkWindow(menu.Handle, TrayRenderer.MenuBg);
        stateItem.Enabled = false; watchItem.Enabled = false;
        var openWin = new ToolStripMenuItem(T("Открыть окно", "Show window"), null, (s, e) => ShowWindow()) { Font = new Font("Segoe UI Semibold", 10f) };
        var openSite = new ToolStripMenuItem(T("Открыть LEDBRAIN", "Open LEDBRAIN"), null, (s, e) => OpenSite());
        autoItem.Click += (s, e) => { AutoStart(!AutoStart()); };
        var langItem = new ToolStripMenuItem(EN ? "Language · Русский" : "Язык · English", null, (s, e) => SetLang(!EN));
        var quit = new ToolStripMenuItem(T("Выход", "Quit"), null, (s, e) => Quit());
        menu.Items.AddRange(new ToolStripItem[] { stateItem, watchItem, new ToolStripSeparator(), openWin, openSite, new ToolStripSeparator(), autoItem, langItem, new ToolStripSeparator(), quit });
        foreach (ToolStripItem it in menu.Items) if (!(it is ToolStripSeparator)) { it.Padding = new Padding(S(10), S(7), S(24), S(7)); it.AutoSize = true; }
        menu.Opening += (s, e) =>
        {
            stateItem.Text = "●  " + statusText; stateItem.Tag = statusColor;
            var on = new List<string>(); foreach (var x in sources) if (x.Active) on.Add(x.Name);
            watchItem.Text = on.Count > 0 ? T("Смотрят: ", "Watching: ") + string.Join(", ", on.ToArray()) : T("Никто не смотрит", "Nobody is watching");
            autoItem.Text = (AutoStart() ? "✓  " : "") + T("Запускать при входе в Windows", "Start with Windows");
        };
        tray.ContextMenuStrip = menu;
        if (old != null) old.Dispose();
    }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    bool AutoStart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("LEDBRAIN Bridge") != null;
    }
    void AutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("LEDBRAIN Bridge", "\"" + Application.ExecutablePath + "\" --tray");
            else k.DeleteValue("LEDBRAIN Bridge", false);
        }
    }
    protected override void SetVisibleCore(bool value)
    {
        // запуск с --tray (автозапуск) — сразу в трей, без окна
        if (!IsHandleCreated && Array.IndexOf(Environment.GetCommandLineArgs(), "--tray") >= 0) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }
    void ShowWindow() { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
    void HideToTray()
    {
        Hide();
        if (!hintShown) { hintShown = true; tray.ShowBalloonTip(3000, "LEDBRAIN Bridge", T("Работает в трее. Клик по значку — открыть окно, правый клик — меню.", "Running in the tray. Click the icon to open the window, right-click for the menu."), ToolTipIcon.Info); }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideToTray(); return; }
        base.OnFormClosing(e);
    }
    void OpenSite() { try { Process.Start(SiteUrl); } catch { } }
    void Quit()
    {
        quitting = true;
        Server.StopAll();
        tray.Visible = false;
        Application.Exit();
    }

    // ---------- ядро Bridge (Core.cs) в этом же процессе; порт занят — показываем состояние уже запущенного Bridge ----------
    bool external;
    void StartCore()
    {
        Server.EN = EN;
        Server.OnLog = Log;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool own = Server.Start();
            try { BeginInvoke(new Action(() => { external = !own; if (external) SetStatus("busy", T("Работает другой Bridge — показываю его", "Another Bridge is running — showing it")); })); } catch { }
        });
    }
    readonly Queue<string> lines = new Queue<string>();
    void Log(string s)
    {
        if (InvokeRequired) { try { BeginInvoke(new Action<string>(Log), s); } catch { } return; }
        lines.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
        while (lines.Count > 200) lines.Dequeue();
        log.Lines = lines.ToArray();
        log.SelectionStart = log.TextLength; log.ScrollToCaret();
    }

    // ---------- состояние: /status раз в секунду ----------
    void Poll()
    {
        if (!external)
        {
            if (polling) return;
            polling = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, object> st = null;
                try { st = Server.Status(); } catch { }
                try { BeginInvoke(new Action(() => { polling = false; if (st != null && Server.Running) Apply(st); })); } catch { polling = false; }
            });
            return;
        }
        if (polling) return;
        polling = true;
        var wc = new WebClient { Encoding = Encoding.UTF8 };
        wc.DownloadStringCompleted += (s, e) =>
        {
            polling = false; wc.Dispose();
            if (e.Error != null)
            {
                failCount++;
                if (failCount > 3 && state != "error") { sources.Clear(); list.Invalidate(); SetStatus("error", T("Bridge не отвечает", "Bridge is not responding")); }
                return;
            }
            failCount = 0;
            try { Apply(new JavaScriptSerializer().DeserializeObject(e.Result) as Dictionary<string, object>); } catch (Exception ex) { Log("status: " + ex.Message); }
        };
        try { wc.DownloadStringAsync(new Uri("http://127.0.0.1:" + Port + "/status")); } catch { polling = false; }
    }
    static double Num(object o) { try { return Convert.ToDouble(o); } catch { return 0; } }
    void Apply(Dictionary<string, object> j)
    {
        if (j == null) return;
        version = Convert.ToString(j["version"]);
        ndiPath = j.ContainsKey("ndi") ? Convert.ToString(j["ndi"]) : "";
        var active = new Dictionary<string, Dictionary<string, object>>();
        foreach (var a in (IEnumerable)j["active"]) { var d = a as Dictionary<string, object>; if (d != null) active[Convert.ToString(d["src"])] = d; }
        var list2 = new List<Src>();
        foreach (var n in (IEnumerable)j["sources"])
        {
            var name = Convert.ToString(n);
            var s = new Src { Name = name };
            Dictionary<string, object> d;
            if (active.TryGetValue(name, out d))
            {
                s.Active = Num(d["clients"]) > 0; s.Clients = (int)Num(d["clients"]); s.Fps = Num(d["fps"]); s.SrcFps = Num(d["srcFps"]);
                s.W = (int)Num(d["w"]); s.H = (int)Num(d["h"]); s.Gpu = d.ContainsKey("gpu") ? Convert.ToString(d["gpu"]) : "";
            }
            list2.Add(s);
        }
        sources = list2;
        verLbl.Text = T("версия ", "version ") + version + T(" · NDI и Spout → 3D-вид LEDBRAIN", " · NDI and Spout → LEDBRAIN 3D view");
        int watching = 0; foreach (var s in sources) if (s.Active) watching++;
        SetStatus("ok", watching > 0 ? T("Работает · идёт картинка в LEDBRAIN", "Running · streaming to LEDBRAIN") : T("Работает · 127.0.0.1:", "Running · 127.0.0.1:") + Port);
        list.Invalidate();
    }
}

// кнопка со скруглёнными углами: основная (синяя) и обычная (тёмная с рамкой)
class RoundButton : Control
{
    readonly bool primary; bool hover, down;
    public RoundButton(string text, bool primary)
    {
        this.primary = primary; Text = text; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI Semibold", 9.5f);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Color.FromArgb(11, 14, 18));
        Color fill = primary ? (down ? Color.FromArgb(38, 92, 230) : hover ? Color.FromArgb(74, 128, 255) : Color.FromArgb(47, 107, 255))
            : (down ? Color.FromArgb(24, 29, 37) : hover ? Color.FromArgb(36, 43, 55) : Color.FromArgb(20, 24, 31));
        BridgeForm.Card(g, new Rectangle(0, 0, Width, Height), fill, primary ? Color.Empty : Color.FromArgb(46, 54, 67), Height * 0.3f);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), primary ? Color.White : Color.FromArgb(231, 235, 241), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// меню трея в стиле LEDBRAIN: тёмный фон, мягкая подсветка пункта, тонкие разделители (углы скругляет Windows 11)
class TrayRenderer : ToolStripProfessionalRenderer
{
    public static readonly Color MenuBg = Color.FromArgb(22, 27, 35);
    public TrayRenderer() { RoundedEdges = false; }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(MenuBg); }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var p = new Pen(Color.FromArgb(40, 47, 59))) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        BridgeForm.Card(e.Graphics, new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2), Color.FromArgb(38, 46, 60), Color.Empty, 7);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var c = e.Item.Tag is Color ? (Color)e.Item.Tag : e.Item.Enabled ? Color.FromArgb(231, 235, 241) : Color.FromArgb(141, 150, 165);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, c, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (var p = new Pen(Color.FromArgb(40, 47, 59))) e.Graphics.DrawLine(p, 12, y, e.Item.Width - 12, y);
    }
}

class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}
