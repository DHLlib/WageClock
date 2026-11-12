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
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

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

                if (args.Length >= 2 && args[0] == "--shot")
                {
                    Application.Run(new ShotContext(args[1]));
                    return;
                }

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

        // ---- 背景取样 ----
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        private const uint WDA_NONE = 0x0;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        /// <summary>把自己从截图里排除，于是抓到的就是窗口背后的画面。</summary>
        public static void ExcludeFromCapture(IntPtr hwnd, bool exclude)
        {
            try { SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE); }
            catch { }
        }

        /// <summary>抓屏幕区域，缩小再放大做廉价模糊，存 PNG。</summary>
        public static bool CaptureBlurred(Rectangle area, string path, int factor)
        {
            try
            {
                Rectangle screen = Screen.PrimaryScreen.Bounds;
                area.Intersect(screen);
                if (area.Width < 8 || area.Height < 8) return false;
                using (Bitmap full = new Bitmap(area.Width, area.Height))
                {
                    using (Graphics g = Graphics.FromImage(full))
                    {
                        g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
                    }
                    int w = Math.Max(1, area.Width / factor);
                    int h = Math.Max(1, area.Height / factor);
                    using (Bitmap small = new Bitmap(w, h))
                    {
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.DrawImage(full, 0, 0, w, h);
                        }
                        using (Bitmap blurred = new Bitmap(area.Width, area.Height))
                        {
                            using (Graphics g = Graphics.FromImage(blurred))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                                g.DrawImage(small, 0, 0, area.Width, area.Height);
                            }
                            blurred.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
                return true;
            }
            catch { return false; }
        }
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

        public double Dbl(string key, double fallback)
        {
            object v;
            if (Data == null || !Data.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            double d;
            return double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : fallback;
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
        public CoreWebView2Environment Env;

        private WidgetForm _widget;
        private SettingsForm _settings;
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

        public async Task<CoreWebView2Environment> EnsureEnv()
        {
            if (Env == null)
            {
                Env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewData, null);
            }
            return Env;
        }

        public WidgetForm Widget
        {
            get { return _widget; }
        }

        public void ShowWidget()
        {
            ShowWidget(false);
        }

        /// <summary>startHidden creates and warms the window off-screen without showing it.</summary>
        public void ShowWidget(bool startHidden)
        {
            if (_widget == null || _widget.IsDisposed)
            {
                _widget = new WidgetForm(this, startHidden);
                _widget.Show();
            }
            else
            {
                _widget.Show();
                _widget.Activate();
            }
        }

        public void HideWidget()
        {
            if (_widget != null) _widget.Hide();
        }

        public void ToggleWidget()
        {
            if (_widget != null && _widget.Visible) HideWidget();
            else ShowWidget();
        }

        public void OpenSettings()
        {
            if (_settings == null || _settings.IsDisposed)
            {
                _settings = new SettingsForm(this);
                _settings.ShowForUser();
            }
            else
            {
                _settings.ShowForUser();
            }
        }

        /// <summary>Loads the settings page in an off-screen window so the first open is instant.</summary>
        public void PreloadSettings()
        {
            if (_settings != null && !_settings.IsDisposed) return;
            _settings = new SettingsForm(this);
            _settings.WarmUp();
        }

        public void CloseSettings()
        {
            if (_settings != null && !_settings.IsDisposed) _settings.Hide();
        }

        public void RefreshWidgetBackdrop()
        {
            if (_widget != null && !_widget.IsDisposed) _widget.RefreshBackdrop();
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
                Paths.Log("broadcast theme=" + Config.Str("theme", "auto") + " widgetReady=" + (_widget != null && _widget.Ready));

            string json = Store.Json.Serialize(msg);
            if (_widget != null && _widget.Ready) _widget.Post(json);
            if (_settings != null && _settings.Ready) _settings.Post(json);
            if (_widget != null && !_widget.IsDisposed) _widget.RefreshChrome();
            if (_settings != null && !_settings.IsDisposed) _settings.RefreshChrome();
        }

        public void BroadcastPinned()
        {
            var msg = new Dictionary<string, object>();
            msg["type"] = "pinned";
            msg["payload"] = AlwaysOnTop;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAGE_CLOCK_DEBUG")))
                Paths.Log("broadcast theme=" + Config.Str("theme", "auto") + " widgetReady=" + (_widget != null && _widget.Ready));

            string json = Store.Json.Serialize(msg);
            if (_widget != null && _widget.Ready) _widget.Post(json);
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

    /// <summary>Shared plumbing for a WebView2 page that talks to the host.</summary>
    internal class WebHostForm : Form
    {
        protected readonly App App;
        protected WebView2 Web;
        protected double ZoomFactor = 1;
        public bool Ready { get; protected set; }

        public WebHostForm(App app, int widthDip, int heightDip, Color bg, bool topMost)
        {
            App = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = bg;
            Width = Native.Scale(widthDip);
            Height = Native.Scale(heightDip);
            TopMost = topMost;

            Web = new WebView2();
            Web.Dock = DockStyle.Fill;
            Web.DefaultBackgroundColor = bg;
            Controls.Add(Web);
        }

        protected async Task Navigate(string relativeUrl, Dictionary<string, string> query)
        {
            CoreWebView2Environment env = await App.EnsureEnv();
            await Web.EnsureCoreWebView2Async(env);
            if (ZoomFactor != 1) Web.ZoomFactor = ZoomFactor;

            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local", Paths.SrcDir, CoreWebView2HostResourceAccessKind.Allow);
            try
            {
                // 背景快照放在数据目录
                Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "bg.local", Paths.DataDir, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception ex) { Paths.Log("bg mapping failed: " + ex.Message); }

            CoreWebView2Settings s = Web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDevToolsEnabled = true;
            s.IsZoomControlEnabled = false;

            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.CoreWebView2.Navigate(BuildUrl(relativeUrl, query));
        }

        private static string BuildUrl(string relative, Dictionary<string, string> query)
        {
            string url = "https://app.local/" + relative;
            if (query != null && query.Count > 0)
            {
                var parts = new List<string>();
                foreach (KeyValuePair<string, string> kv in query)
                {
                    parts.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value));
                }
                url += "?" + string.Join("&", parts.ToArray());
            }
            return url;
        }

        public void Post(string json)
        {
            if (Web.CoreWebView2 != null) Web.CoreWebView2.PostWebMessageAsJson(json);
        }

        public async Task CapturePngAsync(string path)
        {
            using (var stream = File.Create(path))
            {
                await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try
            {
                msg = Store.Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
            }
            catch (Exception) { return; }
            if (msg == null) return;

            object rawId = msg.ContainsKey("id") ? msg["id"] : null;
            string action = msg.ContainsKey("action") ? Convert.ToString(msg["action"]) : "";
            Dictionary<string, object> payload = msg.ContainsKey("payload")
                ? msg["payload"] as Dictionary<string, object>
                : null;

            if (action == "ready")
            {
                OnReady();
                return;
            }
            if (action == "event" && payload != null && Convert.ToString(payload["type"]) == "ready")
            {
                OnReady();
                return;
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAGE_CLOCK_DEBUG")))
                Paths.Log("ipc: " + action + " from " + GetType().Name);

            object data;
            try
            {
                data = OnAction(action, payload);
            }
            catch (Exception ex)
            {
                Paths.Log("action " + action + " threw: " + ex.GetType().Name + " " + ex.Message);
                data = null;
            }
            if (rawId != null)
            {
                var reply = new Dictionary<string, object>();
                reply["replyId"] = rawId;
                reply["data"] = data;
                Post(Store.Json.Serialize(reply));
            }
        }

        protected virtual void OnReady()
        {
            Ready = true;
        }

        protected virtual object OnAction(string action, Dictionary<string, object> payload)
        {
            switch (action)
            {
                case "init":
                    var init = new Dictionary<string, object>();
                    init["settings"] = App.Config.Data;
                    init["pinned"] = App.AlwaysOnTop;
                    init["autoLaunchError"] = App.AutoLaunchError;
                    init["trayAvailable"] = App.TrayAvailable;
                    init["integrityLevel"] = Native.IntegrityLevel;
                    return init;

                case "save":
                    if (payload != null)
                    {
                        object windowPos;
                        Dictionary<string, object> merged = payload;
                        // keep the host-owned window position
                        if (App.Config.Data.TryGetValue("windowPos", out windowPos)) merged["windowPos"] = windowPos;
                        App.Config.Data = merged;
                        App.Config.Save();
                        App.RebuildTray();
                        App.BroadcastSettings();
                        ApplyChrome();
                    }
                    return new Dictionary<string, object> { { "settings", App.Config.Data } };

                case "drag":
                    // let the current IPC message finish first, otherwise the modal move loop
                    // sometimes starts while the button-up is still queued and the drag stalls
                    BeginInvoke(new Action(delegate { Native.DragWindow(Handle); }));
                    return null;

                case "open-settings":
                    App.OpenSettings();
                    return null;

                case "close-settings":
                    App.CloseSettings();
                    return null;

                case "toggle-pin":
                    App.SetPinned(!App.AlwaysOnTop);
                    return null;

                case "quit":
                    App.Quit();
                    return null;

                case "js-error":
                    Paths.Log("js error: " + (payload != null && payload.ContainsKey("message") ? Convert.ToString(payload["message"]) : "?"));
                    return null;

                case "set-theme":
                    object th;
                    string theme = payload != null && payload.TryGetValue("theme", out th) ? Convert.ToString(th) : null;
                    if (theme == "auto" || theme == "dark" || theme == "light")
                    {
                        App.Config.Data["theme"] = theme;
                        App.Config.Save();
                        App.BroadcastSettings();
                        Paths.Log("theme switched live: " + theme);
                    }
                    return null;

                case "set-opacity":
                    object ov;
                    double op = 1;
                    if (payload != null && payload.TryGetValue("opacity", out ov))
                    {
                        double.TryParse(Convert.ToString(ov, System.Globalization.CultureInfo.InvariantCulture),
                            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out op);
                    }
                    if (op < 0.35) op = 0.35;
                    if (op > 1) op = 1;
                    App.Config.Data["opacity"] = op;
                    App.Config.Save();
                    App.BroadcastSettings();
                    App.RefreshWidgetBackdrop();
                    Paths.Log("opacity set to " + op.ToString("0.00"));
                    return null;

                case "cancelled":
                    App.BroadcastSettings(); // puts the form back to the stored values
                    return null;
            }
            return null;
        }

        public void RefreshChrome() { ApplyChrome(); }

        protected virtual void ApplyChrome()
        {
            string theme = App.Config.Str("theme", "auto");
            bool light = theme == "light";
            if (theme == "auto")
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        object v = key != null ? key.GetValue("AppsUseLightTheme") : null;
                        light = v != null && Convert.ToInt32(v) == 1;
                    }
                }
                catch (Exception) { }
            }
            Color bg = light ? Color.FromArgb(247, 248, 250) : Color.FromArgb(17, 20, 25);
            double opacity = App.Config.Dbl("opacity", 1.0);
            bool glass = opacity < 0.999;
            BackColor = bg;
            if (Web != null)
            {
                // 背景交给页面画：半透明底色 + 宿主播过来的模糊快照
                Web.DefaultBackgroundColor = bg;
            }
            if (glass) Native.ExcludeFromCapture(Handle, true);
            else Native.ExcludeFromCapture(Handle, false);
        }
    }

    internal sealed class WidgetForm : WebHostForm
    {
        private const int HotkeyId = 0xA1;
        private readonly System.Windows.Forms.Timer _posTimer = new System.Windows.Forms.Timer();

        private readonly bool _startHidden;
        private readonly Point _home;
        private bool _warming;

        public WidgetForm(App app, bool startHidden = false)
            : base(app, 400, 272, Color.FromArgb(17, 20, 25), app.AlwaysOnTop)
        {
            Text = "Wage Clock";
            ShowInTaskbar = true; // always findable from the taskbar, even after Win+D
            try { Icon = new Icon(Paths.IconPath); } catch (Exception) { }

            Point? saved = app.Config.WindowPos;
            _home = ClampToScreens(saved.HasValue ? saved.Value : DefaultLocation());
            _startHidden = startHidden;
            Location = startHidden ? new Point(-20000, -20000) : _home;

            _posTimer.Interval = 700;
            _posTimer.Tick += delegate
            {
                _posTimer.Stop();
                if (WindowState != FormWindowState.Normal) return;
                if (_warming || _startHidden) return;
                app.Config.WindowPos = Location;
                app.Config.Save();
            };
            LocationChanged += delegate
            {
                if (_warming || _startHidden) return;
                _posTimer.Stop();
                _posTimer.Start();
            };

            Shown += async delegate
            {
                ApplyChrome();
                Native.TryRoundCorners(this, 10);
                await Navigate("index.html", null);
            };

            Resize += delegate { Native.TryRoundCorners(this, 10); };
        }

        private Point DefaultLocation()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            return new Point(area.Right - Width - Native.Scale(24), area.Bottom - Height - Native.Scale(24));
        }

        /// <summary>Keep the window reachable when monitors were removed or the resolution shrank.</summary>
        private Point ClampToScreens(Point wanted)
        {
            const int minVisible = 60;
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle a = s.WorkingArea;
                bool visible =
                    wanted.X + Width > a.Left + minVisible &&
                    wanted.X < a.Right - minVisible &&
                    wanted.Y + Height > a.Top + minVisible &&
                    wanted.Y < a.Bottom - minVisible;
                if (visible) return wanted;
            }
            return DefaultLocation();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RegisterHotKey(Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.W);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.UnregisterHotKey(Handle, HotkeyId);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                App.ToggleWidget();
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// With autostart on, the window is a tray resident: × hides it and only the tray menu exits.
        /// Without autostart there is nothing to come back to, so × really closes the app.
        /// </summary>
        private void CloseOrHide()
        {
            if (!App.TrayAvailable)
            {
                // hiding would strand the user with no way back
                App.Quit();
                return;
            }

            string action = App.Config.Str("closeAction", "auto");
            bool tray;
            if (action == "tray") tray = true;
            else if (action == "quit") tray = false;
            else tray = App.Config.Flag("autoLaunch", false); // follow autostart

            if (tray) Hide();
            else App.Quit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (App.Quitting)
            {
                base.OnFormClosing(e);
                return;
            }
            e.Cancel = true;
            CloseOrHide();
        }

        protected override object OnAction(string action, Dictionary<string, object> payload)
        {
            if (action == "close-widget")
            {
                CloseOrHide();
                return null;
            }
            if (action == "context-menu")
            {
                ShowContextMenu();
                return null;
            }
            return base.OnAction(action, payload);
        }

        private ContextMenuStrip _ctx;
        private ToolStripMenuItem _ctxPin;

        private void ShowContextMenu()
        {
            if (_ctx == null)
            {
                _ctx = new ContextMenuStrip();
                _ctx.Items.Add("设置…", null, delegate { App.OpenSettings(); });
                _ctx.Items.Add("隐藏窗口", null, delegate { Hide(); });
                _ctx.Items.Add(new ToolStripSeparator());

                _ctxPin = new ToolStripMenuItem("窗口置顶");
                _ctxPin.Click += delegate { App.SetPinned(!App.AlwaysOnTop); };
                _ctx.Items.Add(_ctxPin);

                _ctx.Items.Add(new ToolStripSeparator());
                _ctx.Items.Add("退出", null, delegate { App.Quit(); });

                // rebuilding or disposing the strip while it is closing crashes the modal menu
                // filter; keep one instance and just refresh its state
                _ctx.Opening += delegate { _ctxPin.Checked = App.AlwaysOnTop; };
            }
            _ctx.Show(Cursor.Position);
        }

        private System.Windows.Forms.Timer _backdropTimer;
        private int _backdropTick;

        /// <summary>透明度小于 1 时，定时把自己背后的画面拍成模糊快照喂给页面。</summary>
        public void RefreshBackdrop()
        {
            bool glass = App.Config.Dbl("opacity", 1.0) < 0.999;
            Native.ExcludeFromCapture(Handle, glass);
            if (!glass)
            {
                if (_backdropTimer != null) { _backdropTimer.Stop(); _backdropTimer.Dispose(); _backdropTimer = null; }
                Post("{\"type\":\"backdrop\",\"payload\":{\"enabled\":false}}");
                return;
            }
            CaptureOnce();
            if (_backdropTimer == null)
            {
                _backdropTimer = new System.Windows.Forms.Timer();
                _backdropTimer.Interval = 1500;
                _backdropTimer.Tick += delegate { CaptureOnce(); };
                _backdropTimer.Start();
            }
        }

        private void CaptureOnce()
        {
            if (!Visible) return;
            string path = Path.Combine(Paths.DataDir, "backdrop.png");
            if (Native.CaptureBlurred(Bounds, path, 9))
            {
                _backdropTick++;
                Post("{\"type\":\"backdrop\",\"payload\":{\"enabled\":true,\"v\":" + _backdropTick + "}}");
            }
        }

        protected override void OnReady()
        {
            base.OnReady();
            ApplyChrome();
            RefreshBackdrop();
            App.BroadcastSettings();

            if (_startHidden)
            {
                // warmed off-screen: park it at the real spot, still hidden
                _warming = true;
                Hide();
                Location = _home;
                _warming = false;
            }

            // warm the settings window in the background so opening it is instant
            var warm = new System.Windows.Forms.Timer();
            warm.Interval = 900;
            warm.Tick += delegate
            {
                warm.Stop();
                warm.Dispose();
                App.PreloadSettings();
            };
            warm.Start();
        }
    }

    internal sealed class SettingsForm : WebHostForm
    {
        private bool _warmingUp;

        public SettingsForm(App app)
            : base(app, 440, 820, Color.FromArgb(17, 20, 25), true)
        {
            Text = "设置 · Wage Clock";
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            try { Icon = new Icon(Paths.IconPath); } catch (Exception) { }

            Shown += async delegate
            {
                ApplyChrome();
                Native.TryRoundCorners(this, 10);
                try { await Navigate("settings.html", null); }
                catch (Exception ex) { Paths.Log("settings navigate failed: " + ex.Message); }
            };
        }

        /// <summary>Render the page off-screen once, so the first real open has nothing to wait for.</summary>
        public void WarmUp()
        {
            if (IsDisposed) return;
            _warmingUp = true;
            Location = new Point(-20000, -20000);
            Show();
        }

        public void ShowForUser()
        {
            if (IsDisposed) return;
            if (Location.X < -10000 || Location.Y < -10000)
            {
                Location = CenteredLocation();
            }
            _warmingUp = false;
            Show();
            Activate();
            BringToFront();
        }

        private Point CenteredLocation()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            return new Point(
                area.X + (area.Width - Width) / 2,
                area.Y + (area.Height - Height) / 2);
        }

        protected override void OnReady()
        {
            base.OnReady();
            ApplyChrome();
            if (_warmingUp)
            {
                _warmingUp = false;
                Hide();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!App.Quitting)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
