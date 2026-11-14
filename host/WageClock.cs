using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WageClock
{
    internal static class Program
    {
        public const string AppName = "WageClock";

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [STAThread]
        private static void Main(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--demo")
                {
                    DateTime demo;
                    if (DateTime.TryParse(args[i + 1], out demo))
                    {
                        App.DemoBase = demo;
                        Paths.Log("demo time base " + demo);
                    }
                }
            }

            if (Array.IndexOf(args, "--selftest") >= 0)
            {
                int failed = SelfTest.Run();
                string report = SelfTest.Report();
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.txt"),
                        report, new System.Text.UTF8Encoding(false));
                }
                catch (Exception) { }
                Paths.Log(report);
                Environment.Exit(failed == 0 ? 0 : 1);
                return;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Global\\WageClock.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // already running: either ask it to quit, or pull the hidden widget back
                    try
                    {
                        string evt = Array.IndexOf(args, "--quit") >= 0
                            ? "Global\\WageClock.Quit"
                            : "Global\\WageClock.Activate";
                        using (var signal = EventWaitHandle.OpenExisting(evt))
                        {
                            signal.Set();
                        }
                    }
                    catch (Exception) { }
                    return;
                }

                try { SetCurrentProcessExplicitAppUserModelID("WageClock.Desktop"); } catch (Exception) { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Paths.Log("UI exception: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Paths.Log("Fatal: " + e.ExceptionObject);
                };

                

                bool startInTray = Array.IndexOf(args, "--tray") >= 0;
                Application.Run(new App(false, startInTray));
                // WebView2 teardown during finalization is racy; leave the process immediately.
                Environment.Exit(0);
            }
        }
    }

    internal static class Paths
    {
        public static string ExeDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static string SrcDir
        {
            get { return Path.Combine(ExeDir, "src"); }
        }

        public static string IconPath
        {
            get { return Path.Combine(ExeDir, "assets", "icon.ico"); }
        }

        private static string _dataDir;

        /// <summary>%APPDATA%\WageClock, or a portable .\data folder when that is not writable.</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                string over = Environment.GetEnvironmentVariable("WAGE_CLOCK_DATA");
                if (!string.IsNullOrEmpty(over))
                {
                    _dataDir = EnsureWritable(over);
                    return _dataDir;
                }
                string roaming = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Program.AppName);
                try
                {
                    _dataDir = EnsureWritable(roaming);
                }
                catch (Exception)
                {
                    _dataDir = EnsureWritable(Path.Combine(ExeDir, "data"));
                }
                return _dataDir;
            }
        }

        private static string EnsureWritable(string dir)
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".writable");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return dir;
        }

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(ExeDir, "wage-clock.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }

        public static string SettingsFile
        {
            get { return Path.Combine(DataDir, "settings.json"); }
        }

        public static string WebViewData
        {
            get
            {
                string dir = Path.Combine(DataDir, "webview");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }

    internal static class Native
    {
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;

        public static void DragWindow(IntPtr handle)
        {
            ReleaseCapture();
            SendMessage(handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        /// <summary>Windows 11 lets DWM round and anti-alias the window corners; fall back to a 1-bit region elsewhere.</summary>
        public static bool TryRoundCorners(Form form, int radiusDip)
        {
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                try
                {
                    int pref = DWMWCP_ROUND;
                    if (DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4) == 0)
                        return true;
                }
                catch (Exception) { }
            }
            RoundCorners(form, radiusDip);
            return false;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;
        public const int WM_HOTKEY = 0x0312;

        public static void RoundCorners(Form form, int radiusDip)
        {
            float scale = DpiScale;
            int w = form.Width;
            int h = form.Height;
            if (w <= 0 || h <= 0) return;
            int d = (int)Math.Round(radiusDip * 2 * scale);
            IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
            Region old = form.Region;
            form.Region = Region.FromHrgn(rgn);
            DeleteObject(rgn);
            if (old != null) old.Dispose();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int needed);

        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenIntegrityLevel = 25;

        /// <summary>
        /// Mandatory integrity RID of this process (0x1000 low, 0x2000 medium, 0x3000 high, -1 unknown).
        /// UIPI forbids a low-integrity process from talking to the taskbar, so Shell_NotifyIcon
        /// fails with ERROR_ACCESS_DENIED and no tray icon can ever appear.
        /// </summary>
        public static int IntegrityLevel
        {
            get
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return -1;
                try
                {
                    int needed;
                    GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out needed);
                    if (needed <= 0) return -1;
                    IntPtr buffer = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out needed)) return -1;
                        IntPtr sid = Marshal.ReadIntPtr(buffer);
                        byte subAuthorityCount = Marshal.ReadByte(sid, 1);
                        int last = 0;
                        for (int i = 0; i < subAuthorityCount; i++)
                        {
                            last = Marshal.ReadInt32(sid, 8 + i * 4);
                        }
                        return last;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { CloseHandle(token); }
            }
        }

        /// <summary>Tray icons only work at medium integrity or above (UIPI blocks lower levels).</summary>
        public static bool TrayAvailable
        {
            get
            {
                int level = IntegrityLevel;
                return level < 0 || level >= 0x2000;
            }
        }

        public static float DpiScale
        {
            get
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return g.DpiX / 96f;
                }
            }
        }

        public static int Scale(int dip)
        {
            return (int)Math.Round(dip * DpiScale);
        }
    }

    /// <summary>Flat JSON bag persisted to %APPDATA%\WageClock\settings.json.</summary>
    internal sealed class Store
    {
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();
        public Dictionary<string, object> Data = new Dictionary<string, object>();

        public static JavaScriptSerializer Json
        {
            get { return Ser; }
        }

        public void Load()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    string raw = File.ReadAllText(Paths.SettingsFile);
                    Dictionary<string, object> parsed = Ser.Deserialize<Dictionary<string, object>>(raw);
                    if (parsed != null) Data = parsed;
                }
            }
            catch (Exception) { Data = new Dictionary<string, object>(); }
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(Paths.SettingsFile, Ser.Serialize(Data));
            }
            catch (Exception) { }
        }

        public string Str(string key, string fallback)
        {
            object v;
            if (Data.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return fallback;
        }

        public bool Flag(string key, bool fallback)
        {
            object v;
            if (Data.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToBoolean(v); } catch (Exception) { return fallback; }
            }
            return fallback;
        }

        public Point? WindowPos
        {
            get
            {
                object v;
                if (!Data.TryGetValue("windowPos", out v) || v == null) return null;
                var d = v as Dictionary<string, object>;
                if (d == null) return null;
                try
                {
                    return new Point(Convert.ToInt32(d["x"]), Convert.ToInt32(d["y"]));
                }
                catch (Exception) { return null; }
            }
            set
            {
                if (value.HasValue)
                {
                    var d = new Dictionary<string, object>();
                    d["x"] = value.Value.X;
                    d["y"] = value.Value.Y;
                    Data["windowPos"] = d;
                }
                else
                {
                    Data.Remove("windowPos");
                }
            }
        }
    }

    internal sealed class App : ApplicationContext
    {
        public readonly Store Config = new Store();

        /// <summary>--demo 用的起始时间：从这一刻开始按真实节奏往前走。</summary>
        public static DateTime? DemoBase;
        static readonly System.Diagnostics.Stopwatch DemoWatch = System.Diagnostics.Stopwatch.StartNew();

        public DateTime Now
        {
            get { return DemoBase.HasValue ? DemoBase.Value.Add(DemoWatch.Elapsed) : DateTime.Now; }
        }


        private Form _widget;
        private Form _settings;
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;

        public App()
            : this(false, false)
        {
        }

        public App(bool silent)
            : this(silent, false)
        {
        }

        public App(bool silent, bool startInTray)
        {
            _silent = silent;
            Config.Load();
            if (silent) return;
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\WageClock.Activate");
            _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\WageClock.Quit");
            var listen = new Thread(ListenForActivate);
            listen.IsBackground = true;
            listen.Start();
            CreateTray();
            Paths.Log("start: il=" + Native.IntegrityLevel + " tray=" + (TrayAvailable ? "yes" : "no") +
                      " data=" + Paths.DataDir + " trayStart=" + startInTray);
            bool hideAtStart = startInTray && TrayAvailable;
            if (startInTray && !TrayAvailable)
            {
                Paths.Log("started with --tray but integrity level is " + Native.IntegrityLevel +
                          " (UIPI blocks tray registration) — showing the window instead");
            }
            ShowWidget(hideAtStart);
            ApplyAutoLaunch(Config.Flag("autoLaunch", false));
            if (startInTray) ShowTrayHintOnce();
        }

        private EventWaitHandle _activateSignal;
        private EventWaitHandle _quitSignal;

        private void ListenForActivate()
        {
            WaitHandle[] handles = new WaitHandle[] { _activateSignal, _quitSignal };
            while (true)
            {
                int index;
                try
                {
                    index = WaitHandle.WaitAny(handles);
                }
                catch (Exception) { return; }

                if (index == 1)
                {
                    try
                    {
                        if (_widget != null && !_widget.IsDisposed)
                            _widget.BeginInvoke(new Action(delegate { Quit(); }));
                    }
                    catch (Exception) { }
                    return;
                }

                if (_widget == null || _widget.IsDisposed) continue;
                try
                {
                    _widget.BeginInvoke(new Action(delegate { ShowWidget(); }));
                }
                catch (Exception) { }
            }
        }

        private bool _silent;

        public bool TrayAvailable
        {
            // the screenshot harness runs inside the sandbox; show the normal UI there
            get { return _silent || Native.TrayAvailable; }
        }

        public bool AlwaysOnTop
        {
            get { return Config.Flag("alwaysOnTop", true); }
        }



        public Form Widget
        {
            get { return _widget; }
        }

        public void ShowWidget()
        {
            ShowWidget(false);
        }

        /// <summary>startHidden creates and warms the window off-screen without showing it.</summary>
        // ---- 卡片实现（这条分支上是原生自绘，另一条分支是 WebView2）----
        WageSettings _wageSettings;
        bool _wageSettingsDirty = true;

        public WageSettings Settings
        {
            get
            {
                if (_wageSettings == null || _wageSettingsDirty)
                {
                    _wageSettings = WageSettings.FromConfig(Config);
                    _wageSettingsDirty = false;
                }
                return _wageSettings;
            }
        }

        public void InvalidateSettings()
        {
            _wageSettingsDirty = true;
        }

        public bool IsLightTheme()
        {
            string theme = Config.Str("theme", "auto");
            if (theme == "light") return true;
            if (theme == "dark") return false;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key != null ? key.GetValue("AppsUseLightTheme") : null;
                    return v != null && Convert.ToInt32(v) == 1;
                }
            }
            catch (Exception) { return false; }
        }

        Form NewWidget(bool startHidden)
        {
            var card = new CardWindow(this, IsLightTheme());
            card.TopMost = AlwaysOnTop;
            return card;
        }

        bool WidgetReady()
        {
            // 原生卡片没有"页面就绪"这一层
            return true;
        }

        void WidgetPost(string json)
        {
            // 原生卡片直接读配置，没有 IPC
        }

        void WidgetRefresh()
        {
            var card = _widget as CardWindow;
            if (card != null) card.SetTheme(IsLightTheme());
        }

        public void ShowContextMenu(Control owner, Point screenPoint)
        {
            if (_menu == null) return;
            _menu.Show(owner, screenPoint);
        }
        public void ShowWidget(bool startHidden)
        {
            if (_widget == null || _widget.IsDisposed)
            {
                _widget = NewWidget(startHidden);
                _widget.Show();
            }
            else
            {
                _widget.Show();
                _widget.Activate();
            }
        }

        /// <summary>按「关闭行为」设置决定点 × 之后做什么。</summary>
        public void RequestClose()
        {
            string mode = Config.Str("closeAction", "auto");
            if (mode == "quit") { Quit(); return; }
            if (mode == "tray") { HideToTray(); return; }
            // auto：能收托盘就收，没有托盘就直接退出
            if (TrayAvailable) HideToTray();
            else Quit();
        }
        public void HideWidget()
        {
            if (_widget != null) _widget.Hide();
        }

        /// <summary>收进托盘：确保图标在场，并提示怎么叫回来。</summary>
        public void HideToTray()
        {
            HideWidget();
            if (_tray != null)
            {
                _tray.Visible = true;      // 有些环境会因刷新丢掉图标，这里再确认一次
                Paths.Log("hidden to tray, tray.Visible=" + _tray.Visible);
                ShowTrayHintOnce();
            }
            else
            {
                Paths.Log("hidden to tray, but tray icon is missing");
            }
        }

        public void ToggleWidget()
        {
            if (_widget != null && _widget.Visible) HideWidget();
            else ShowWidget();
        }

        public void OpenSettings()
        {
            // 已经开着就把它叫到前面，别再开第二个
            if (_settings != null && !_settings.IsDisposed)
            {
                if (!_settings.Visible) _settings.Show();
                if (_settings.WindowState == FormWindowState.Minimized)
                    _settings.WindowState = FormWindowState.Normal;
                _settings.Activate();
                _settings.BringToFront();
                return;
            }

            var form = new NativeSettingsForm(this);
            form.FormClosed += delegate { _settings = null; };
            _settings = form;
            form.Show();
            form.Activate();
        }

        /// <summary>Loads the settings page in an off-screen window so the first open is instant.</summary>
        public void PreloadSettings()
        {
            // 原生设置窗口没有可预热的 WebView，打开即用
        }

        public void CloseSettings()
        {
            if (_settings != null && !_settings.IsDisposed) _settings.Close();
        }

        /// <summary>设置保存后，把主题、置顶、自启等立刻生效。</summary>
        public void ApplySettingsChange()
        {
            if (_widget != null && !_widget.IsDisposed) _widget.TopMost = AlwaysOnTop;
            WidgetRefresh();
            string close = Config.Str("closeAction", "auto");
            Paths.Log("settings applied: theme=" + Config.Str("theme", "auto") + " topmost=" + AlwaysOnTop + " close=" + close);
        }

        public void BroadcastSettings()
        {
            var payload = new Dictionary<string, object>();
            payload["settings"] = Config.Data;
            payload["theme"] = Config.Str("theme", "auto");
            payload["autoLaunchError"] = AutoLaunchError;
            payload["trayAvailable"] = TrayAvailable;
            var msg = new Dictionary<string, object>();
            msg["type"] = "settings";
            msg["payload"] = payload;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAGE_CLOCK_DEBUG")))
                Paths.Log("broadcast theme=" + Config.Str("theme", "auto") + " widgetReady=" + (_widget != null && WidgetReady()));

            string json = Store.Json.Serialize(msg);
            if (_widget != null && WidgetReady()) WidgetPost(json);
            if (_widget != null && !_widget.IsDisposed) WidgetRefresh();

        }

        public void BroadcastPinned()
        {
            var msg = new Dictionary<string, object>();
            msg["type"] = "pinned";
            msg["payload"] = AlwaysOnTop;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAGE_CLOCK_DEBUG")))
                Paths.Log("broadcast theme=" + Config.Str("theme", "auto") + " widgetReady=" + (_widget != null && WidgetReady()));

            string json = Store.Json.Serialize(msg);
            if (_widget != null && WidgetReady()) WidgetPost(json);
        }

        public void SetPinned(bool pinned)
        {
            Config.Data["alwaysOnTop"] = pinned;
            Config.Save();
            if (_widget != null) _widget.TopMost = pinned;
            BroadcastPinned();
            RebuildTray();
        }

        public string AutoLaunchError { get; private set; }

        public bool ApplyAutoLaunch(bool enabled)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null)
                    {
                        AutoLaunchError = "打不开注册表 Run 键";
                        return false;
                    }
                    if (enabled) key.SetValue(Program.AppName, "\"" + Application.ExecutablePath + "\" --tray");
                    else key.DeleteValue(Program.AppName, false);
                }
                AutoLaunchError = null;
                Paths.Log("autolaunch " + enabled + " -> " + (enabled ? "set" : "cleared"));
                return true;
            }
            catch (Exception ex)
            {
                AutoLaunchError = enabled ? "无法写入开机启动项（可能被安全软件拦截）：" + ex.Message : null;
                Paths.Log("autolaunch failed: " + ex.Message);
                return false;
            }
        }

        public void Quit()
        {
            Quitting = true;
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            ExitThread();
        }

        public bool Quitting { get; private set; }

        /// <summary>First tray boot: tell the user where the window went (Win11 folds the icon).</summary>
        private void ShowTrayHintOnce()
        {
            if (Config.Flag("trayHintShown", false)) return;
            Config.Data["trayHintShown"] = true;
            Config.Save();

            var hint = new System.Windows.Forms.Timer();
            hint.Interval = 1500;
            hint.Tick += delegate
            {
                hint.Stop();
                hint.Dispose();
                if (_tray == null) return;
                _tray.BalloonTipTitle = "工资时钟已在后台运行";
                _tray.BalloonTipText = "窗口收在托盘里（Win11 可能折叠进 ^）。按 Ctrl+Alt+W 或再运行一次程序即可叫出来。";
                _tray.ShowBalloonTip(6000);
            };
            hint.Start();
        }

        /// <summary>
        /// Windows 11 folds every new tray icon into the overflow by default. The shell keeps that
        /// choice in HKCU\Control Panel\NotifyIconSettings\{guid}\IsPromoted; flipping it for our own
        /// exe is the only way to ask for a visible icon (it is undocumented but stable since 22H2).
        /// </summary>
        private void ScheduleTrayPromotion()
        {
            int attempts = 0;
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 1500;
            timer.Tick += delegate
            {
                attempts++;
                bool found = PromoteTrayIcon();
                if (found && _tray != null)
                {
                    // nudge Explorer so it re-reads the flag without waiting for a reboot
                    _tray.Visible = false;
                    _tray.Visible = true;
                }
                if (found || attempts >= 4)
                {
                    timer.Stop();
                    timer.Dispose();
                }
            };
            timer.Start();
        }

        private static bool PromoteTrayIcon()
        {
            bool found = false;
            try
            {
                string exe = Application.ExecutablePath;
                using (Microsoft.Win32.RegistryKey root =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return false;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using (Microsoft.Win32.RegistryKey key = root.OpenSubKey(name, true))
                        {
                            if (key == null) continue;
                            string path = key.GetValue("ExecutablePath") as string;
                            if (path == null || !string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                            found = true;
                            object current = key.GetValue("IsPromoted");
                            int value = current == null ? 0 : Convert.ToInt32(current);
                            if (value != 1)
                            {
                                key.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return found;
        }

        private void CreateTray()
        {
            _tray = new NotifyIcon();
            bool fromFile = true;
            try { _tray.Icon = new Icon(Paths.IconPath); }
            catch (Exception ex)
            {
                fromFile = false;
                Paths.Log("tray icon load failed: " + ex.Message);
                _tray.Icon = SystemIcons.Application;
            }
            _tray.Text = "Wage Clock · 工资时钟";
            // NotifyIcon.Click 对左右键都会触发，右键必须留给菜单
            _tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    Paths.Log("tray left click -> toggle widget");
                    ToggleWidget();
                }
                else
                {
                    Paths.Log("tray " + e.Button + " click -> menu");
                }
            };
            RebuildTray();
            ScheduleTrayPromotion();

            // register with the shell once the message loop is actually running
            var show = new System.Windows.Forms.Timer();
            show.Interval = 400;
            show.Tick += delegate
            {
                show.Stop();
                show.Dispose();
                try
                {
                    _tray.Visible = true;
                    Paths.Log("tray registered: visible=" + _tray.Visible + " iconFromFile=" + fromFile);
                }
                catch (Exception ex)
                {
                    Paths.Log("tray register failed: " + ex.Message);
                }
            };
            show.Start();
        }

        public void RebuildTray()
        {
            if (_menu != null) _menu.Dispose();
            _menu = new ContextMenuStrip();
            _menu.Items.Add("显示 / 隐藏", null, delegate { ToggleWidget(); });
            _menu.Items.Add("设置…", null, delegate { OpenSettings(); });
            _menu.Items.Add(new ToolStripSeparator());

            var pin = new ToolStripMenuItem("窗口置顶");
            pin.Checked = AlwaysOnTop;
            pin.Click += delegate { SetPinned(!AlwaysOnTop); };
            _menu.Items.Add(pin);

            var boot = new ToolStripMenuItem("开机自启");
            boot.Checked = Config.Flag("autoLaunch", false);
            boot.Click += delegate
            {
                bool next = !boot.Checked;
                Config.Data["autoLaunch"] = next;
                Config.Save();
                ApplyAutoLaunch(next);
                RebuildTray();
                BroadcastSettings();
            };
            _menu.Items.Add(boot);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出", null, delegate { Quit(); });

            _menu.Opening += delegate { Paths.Log("tray menu opening"); };

            if (_tray != null) _tray.ContextMenuStrip = _menu;
        }
    }

}