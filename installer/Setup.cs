// 工资时钟安装程序：向导式安装、依赖检测、可选目录、自带卸载。
// 编译：csc /target:winexe /codepage:65001 /win32icon:assets\icon.ico
//       /resource:build\payload.zip,payload.zip /r:System.IO.Compression.FileSystem.dll
//       /out:build\WageClockSetup.exe installer\Setup.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WageClockSetup
{
    static class Log
    {
        static readonly object Gate = new object();

        public static string FilePath
        {
            get { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wageclock-setup.log"); }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    static class AppInfo
    {
        public const string Name = "工资时钟";
        public const string Version = "1.0.1";
        public const string Publisher = "DHLlib";
        public const string ExeName = "WageClock.exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WageClock";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "WageClock";
        public const string WebView2Client = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        public static string DefaultDir
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(Path.Combine(local, "Programs"), "WageClock");
            }
        }

        public static string InstalledDir
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    {
                        if (k != null)
                        {
                            string dir = k.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                        }
                    }
                }
                catch { }
                return null;
            }
        }
    }

    static class Fs
    {
        public static string ExeDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static void CopyDir(string from, string to)
        {
            if (!Directory.Exists(to)) Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                string rel = dir.Substring(from.Length).TrimStart('\\');
                string target = Path.Combine(to, rel);
                if (!Directory.Exists(target)) Directory.CreateDirectory(target);
            }
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(from.Length).TrimStart('\\');
                File.Copy(file, Path.Combine(to, rel), true);
            }
        }

        public static bool TryDeleteDir(string dir)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    return true;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
            return !Directory.Exists(dir);
        }

        public static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }
    }

    static class Shortcuts
    {
        // WScript.Shell 晚绑定，避免额外引用 COM 程序集
        public static bool Create(string linkPath, string target, string workDir, string description)
        {
            try
            {
                string dir = Path.GetDirectoryName(linkPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workDir });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                return File.Exists(linkPath);
            }
            catch
            {
                return false;
            }
        }

        public static string DesktopLink
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.Name + ".lnk");
            }
        }

        public static string StartMenuLink
        {
            get
            {
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name), AppInfo.Name + ".lnk");
            }
        }

        public static void DeleteAll()
        {
            try { if (File.Exists(DesktopLink)) File.Delete(DesktopLink); } catch { }
            try { if (File.Exists(StartMenuLink)) File.Delete(StartMenuLink); } catch { }
            try
            {
                string dir = Path.GetDirectoryName(StartMenuLink);
                if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0) Directory.Delete(dir);
            }
            catch { }
        }
    }

    static class Requirements
    {
        // 返回值：检测到的版本号，或 null 表示没装
        public static string WebView2Version()
        {
            string[] keys = new string[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + AppInfo.WebView2Client,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + AppInfo.WebView2Client
            };
            foreach (RegistryKey root in new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string key in keys)
                {
                    try
                    {
                        using (RegistryKey k = root.OpenSubKey(key))
                        {
                            if (k == null) continue;
                            string pv = k.GetValue("pv") as string;
                            if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return pv;
                        }
                    }
                    catch { }
                }
            }
            // 兜底：找 msedgewebview2.exe
            try
            {
                string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\EdgeWebView\Application");
                if (Directory.Exists(baseDir))
                {
                    string[] versions = Directory.GetDirectories(baseDir);
                    Array.Sort(versions);
                    for (int i = versions.Length - 1; i >= 0; i--)
                    {
                        if (File.Exists(Path.Combine(versions[i], "msedgewebview2.exe")))
                            return Path.GetFileName(versions[i]);
                    }
                }
            }
            catch { }
            return null;
        }

        // .NET Framework 4.8 的 Release 值 >= 528040
        public static bool DotNet48()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("Release");
                    if (v == null) return false;
                    return Convert.ToInt32(v) >= 528040;
                }
            }
            catch { return false; }
        }

        public static bool DirWritable(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    string probe = Path.Combine(dir, ".wc_probe");
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                    return true;
                }
                string parent = Path.GetDirectoryName(dir);
                while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    parent = Path.GetDirectoryName(parent);
                if (string.IsNullOrEmpty(parent)) return false;
                string tmp = Path.Combine(parent, ".wc_probe");
                File.WriteAllText(tmp, "x");
                File.Delete(tmp);
                return true;
            }
            catch { return false; }
        }
    }

    class InstallOptions
    {
        public string Dir;
        public bool Desktop = true;
        public bool StartMenu = true;
        public bool AutoStart = false;
        public bool Launch = true;
    }

    static class Installer
    {
        public delegate void Report(string message, int percent);

        public static bool PayloadAvailable
        {
            get { return Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") != null; }
        }

        public static void Run(InstallOptions opt, Report report)
        {
            Log.Write("install -> " + opt.Dir);
            report("准备安装目录…", 5);
            if (!Directory.Exists(opt.Dir)) Directory.CreateDirectory(opt.Dir);

            report("关闭正在运行的工资时钟…", 10);
            KillRunning(opt.Dir);

            report("解压程序文件…", 20);
            Log.Write("extracting payload");
            ExtractPayload(opt.Dir, report);

            report("写入程序信息…", 78);
            Log.Write("writing uninstall entry");
            WriteUninstallEntry(opt);

            report("复制卸载程序…", 82);
            string uninstaller = Path.Combine(opt.Dir, "uninstall.exe");
            File.Copy(Application.ExecutablePath, uninstaller, true);

            report("创建快捷方式…", 86);
            string exe = Path.Combine(opt.Dir, AppInfo.ExeName);
            if (opt.Desktop) Shortcuts.Create(Shortcuts.DesktopLink, exe, opt.Dir, AppInfo.Name);
            else { try { if (File.Exists(Shortcuts.DesktopLink)) File.Delete(Shortcuts.DesktopLink); } catch { } }
            if (opt.StartMenu) Shortcuts.Create(Shortcuts.StartMenuLink, exe, opt.Dir, AppInfo.Name);
            else { try { if (File.Exists(Shortcuts.StartMenuLink)) File.Delete(Shortcuts.StartMenuLink); } catch { } }

            report(opt.AutoStart ? "设置开机自启…" : "清理开机自启…", 92);
            SetAutoStart(opt.AutoStart ? "\"" + exe + "\" --tray" : null);

            report("完成", 100);
        }

        static void ExtractPayload(string dir, Report report)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (stream == null) throw new InvalidOperationException("安装包内没有找到程序文件（payload.zip）。");
                using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    int total = zip.Entries.Count;
                    int done = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string target = Path.Combine(dir, entry.FullName.Replace('/', '\\'));
                        if (entry.FullName.EndsWith("/"))
                        {
                            if (!Directory.Exists(target)) Directory.CreateDirectory(target);
                        }
                        else
                        {
                            string parent = Path.GetDirectoryName(target);
                            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                            entry.ExtractToFile(target, true);
                        }
                        done++;
                        if (total > 0) report("解压程序文件… " + entry.Name, 20 + (int)(52.0 * done / total));
                    }
                }
            }
        }

        public static string EmbeddedBootstrapperPath()
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("webview2setup.exe");
            if (s == null) return null;
            string path = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (s)
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = s.Read(buffer, 0, buffer.Length)) > 0) fs.Write(buffer, 0, read);
            }
            return path;
        }

        public static bool InstallWebView2()
        {
            string bootstrapper = EmbeddedBootstrapperPath();
            if (bootstrapper == null) return false;
            ProcessStartInfo psi = new ProcessStartInfo(bootstrapper);
            psi.Arguments = "/silent /install";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(300000);
                if (!p.HasExited) return false;
            }
            return Requirements.WebView2Version() != null;
        }

        public static void KillRunning(string dir)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("WageClock"))
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        if (!string.IsNullOrEmpty(dir) && !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static void SetAutoStart(string command)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AppInfo.RunKey, true))
                {
                    if (k == null) return;
                    if (command == null) k.DeleteValue(AppInfo.RunValue, false);
                    else k.SetValue(AppInfo.RunValue, command);
                }
            }
            catch { }
        }

        static void WriteUninstallEntry(InstallOptions opt)
        {
            try
            {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AppInfo.UninstallKey))
            {
                string exe = Path.Combine(opt.Dir, AppInfo.ExeName);
                k.SetValue("DisplayName", AppInfo.Name);
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", AppInfo.Publisher);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", opt.Dir);
                k.SetValue("UninstallString", "\"" + Path.Combine(opt.Dir, "uninstall.exe") + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + Path.Combine(opt.Dir, "uninstall.exe") + "\" /uninstall /silent");
                k.SetValue("EstimatedSize", (int)(Fs.DirSize(opt.Dir) / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "没有权限写入当前用户的注册表。\n\n通常是安装包被从受限目录直接运行（例如沙箱、压缩包内预览）。" +
                    "请先把 WageClockSetup.exe 复制到桌面或下载目录，再运行。");
            }
        }

        public static void Uninstall(string dir, bool keepSettings, bool silent, Report report)
        {
            Log.Write("uninstall begin dir=" + dir + " keepSettings=" + keepSettings);
            if (report != null) report("关闭正在运行的工资时钟…", 10);
            KillRunning(dir);

            if (report != null) report("删除快捷方式…", 25);
            Shortcuts.DeleteAll();

            if (report != null) report("清理开机自启…", 40);
            SetAutoStart(null);

            if (report != null) report("删除注册表项…", 55);
            try { Registry.CurrentUser.DeleteSubKeyTree(AppInfo.UninstallKey, false); } catch { }

            if (!keepSettings)
            {
                if (report != null) report("删除设置与缓存…", 65);
                string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WageClock");
                Fs.TryDeleteDir(roaming);
            }

            if (report != null) report("删除程序文件…", 75);
            bool ok = Fs.TryDeleteDir(dir);
            Log.Write("dir removed=" + ok);

            if (report != null) report(ok ? "卸载完成" : "部分文件未能删除，请重启后再删一次", 100);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Log.Write("start: " + string.Join(" ", args));
            bool silent = HasArg(args, "/silent") || HasArg(args, "/s");
            bool uninstall = HasArg(args, "/uninstall");
            string dirArg = ArgValue(args, "/dir");

            if (uninstall)
            {
                string dir = dirArg != null ? dirArg : AppInfo.InstalledDir;
                if (dir == null)
                {
                    if (!silent) MessageBox.Show("没有找到已安装的工资时钟。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // 把自己挪到临时目录，才能删掉安装目录里的 uninstall.exe
                string temp = Path.Combine(Path.GetTempPath(), "WageClockUninstall.exe");
                try
                {
                    if (!string.Equals(Application.ExecutablePath, temp, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(Application.ExecutablePath, temp, true);
                        string quit = "/uninstall /dir=\"" + dir + "\" /relocated" + (silent ? " /silent" : "");
                        if (HasArg(args, "/purge")) quit += " /purge";
                        Log.Write("relocate -> " + temp + "  args: " + quit);
                        Process.Start(temp, quit);
                        return;
                    }
                }
                catch (Exception ex) { Log.Write("relocate failed: " + ex.Message); }

                bool purge = HasArg(args, "/purge");
                if (!silent)
                {
                    if (MessageBox.Show("确定要卸载" + AppInfo.Name + "吗？\n\n程序文件与快捷方式会被删除。", AppInfo.Name,
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                        return;
                    purge = MessageBox.Show("要同时删除个人设置吗？\n\n包括月薪、班次、窗口位置等配置。选“否”会保留，重装后仍然有效。",
                        AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                }
                Log.Write("uninstall dir=" + dir + " purge=" + purge);

                Installer.Uninstall(dir, !purge, silent, null);
                Log.Write("uninstall done");
                if (!silent) MessageBox.Show("卸载完成。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                try { Process.Start("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del \"" + temp + "\""); } catch { }
                return;
            }

            if (silent)
            {
                InstallOptions opt = new InstallOptions();
                opt.Dir = dirArg != null ? dirArg : AppInfo.DefaultDir;
                opt.Launch = !HasArg(args, "/nostart");
                Installer.Run(opt, delegate(string m, int p) { });
                if (opt.Launch)
                {
                    try { Process.Start(Path.Combine(opt.Dir, AppInfo.ExeName)); } catch { }
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(dirArg != null ? dirArg : AppInfo.DefaultDir));
        }

        static bool HasArg(string[] args, string name)
        {
            foreach (string a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string ArgValue(string[] args, string name)
        {
            foreach (string a in args)
            {
                if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return a.Substring(name.Length + 1).Trim('"');
            }
            return null;
        }
    }

    class SetupForm : Form
    {
        static readonly Color Bg = Color.FromArgb(0xF7, 0xF8, 0xF7);
        static readonly Color Card = Color.White;
        static readonly Color Ink = Color.FromArgb(0x16, 0x20, 0x1A);
        static readonly Color Muted = Color.FromArgb(0x5E, 0x6B, 0x62);
        static readonly Color Accent = Color.FromArgb(0x04, 0x89, 0x5A);
        static readonly Color Line = Color.FromArgb(0xDD, 0xE3, 0xDE);
        static readonly Color Warn = Color.FromArgb(0xC0, 0x64, 0x00);

        const int PageWelcome = 0;
        const int PageDir = 1;
        const int PageCheck = 2;
        const int PageTasks = 3;
        const int PageInstall = 4;
        const int PageDone = 5;

        int _page;
        Panel _body;
        Button _back;
        Button _next;
        Button _cancel;
        Label _heading;
        Label _sub;
        ProgressBar _bar;
        Label _status;

        TextBox _dirBox;
        CheckBox _desktop;
        CheckBox _startMenu;
        CheckBox _autoStart;
        CheckBox _launch;

        bool _webViewOk;
        string _webViewVersion;
        bool _dotnetOk;
        bool _dirOk;
        Label _webViewLine;
        Label _dotnetLine;
        Label _dirLine;
        Button _fixWebView;

        bool _installing;

        public SetupForm(string dir)
        {
            Text = AppInfo.Name + " 安装程序";
            ClientSize = new Size(600, 430);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = null;

            _heading = new Label();
            _heading.SetBounds(28, 22, 544, 30);
            _heading.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            _heading.ForeColor = Ink;
            Controls.Add(_heading);

            _sub = new Label();
            _sub.SetBounds(28, 56, 544, 22);
            _sub.ForeColor = Muted;
            Controls.Add(_sub);

            Panel rule = new Panel();
            rule.SetBounds(28, 86, 544, 1);
            rule.BackColor = Line;
            Controls.Add(rule);

            _body = new Panel();
            _body.SetBounds(24, 96, 552, 268);
            _body.BackColor = Bg;
            Controls.Add(_body);

            _back = MakeButton("上一步", 300, 378, 88);
            _back.Click += delegate { Go(_page - 1); };
            Controls.Add(_back);

            _next = MakeButton("下一步", 396, 378, 88);
            _next.BackColor = Accent;
            _next.ForeColor = Color.White;
            _next.FlatAppearance.BorderColor = Accent;
            _next.Click += delegate { OnNext(); };
            Controls.Add(_next);

            _cancel = MakeButton("取消", 492, 378, 80);
            _cancel.Click += delegate { Cancel(); };
            Controls.Add(_cancel);

            _dirBox = new TextBox();
            _dirBox.Text = dir;

            BuildWelcome();
            Go(PageWelcome);
        }

        Button MakeButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 30);
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Card;
            b.ForeColor = Ink;
            b.FlatAppearance.BorderColor = Line;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        void Go(int page)
        {
            _page = page;
            _body.Controls.Clear();
            switch (page)
            {
                case PageWelcome: ShowWelcome(); break;
                case PageDir: ShowDir(); break;
                case PageCheck: ShowCheck(); break;
                case PageTasks: ShowTasks(); break;
                case PageInstall: ShowInstall(); break;
                case PageDone: ShowDone(); break;
            }
            _back.Enabled = page > PageWelcome && page < PageInstall;
            _back.Visible = page < PageInstall;
            _cancel.Visible = page <= PageTasks;
            if (page == PageDone)
            {
                _next.Text = "完成";
                _next.Visible = true;
            }
            else if (page == PageInstall)
            {
                _next.Visible = false;
            }
            else
            {
                _next.Text = page == PageTasks ? "安装" : "下一步";
                _next.Visible = true;
            }
        }

        void OnNext()
        {
            if (_page == PageDone)
            {
                Close();
                return;
            }
            if (_page == PageDir)
            {
                ValidateDir();
                if (!_dirOk) return;
            }
            if (_page == PageCheck)
            {
                if (!_webViewOk)
                {
                    DialogResult r = MessageBox.Show(this,
                        "还没有安装 WebView2 运行时，工资时钟无法显示界面。\n\n现在自动安装吗？（需要联网，约 2 MB）",
                        AppInfo.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) return;
                    if (r == DialogResult.Yes && !InstallWebView()) return;
                }
            }
            if (_page == PageTasks)
            {
                StartInstall();
                return;
            }
            Go(_page + 1);
        }

        void Cancel()
        {
            if (_page == PageInstall)
            {
                if (!_installing) Close();
                return;
            }
            if (MessageBox.Show(this, "确定要退出安装吗？", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Close();
        }

        // ---------- 页面 ----------

        void BuildWelcome()
        {
        }

        void ShowWelcome()
        {
            _heading.Text = "安装" + AppInfo.Name;
            _sub.Text = "把上班时间换算成钱的小挂件 · 版本 " + AppInfo.Version;

            Label body = new Label();
            body.SetBounds(8, 10, 536, 150);
            body.ForeColor = Ink;
            body.Text = "安装程序会把程序文件放到你选择的目录，并创建开始菜单和桌面快捷方式。\n\n" +
                        "· 实时显示今天已经赚了多少，按秒滚动\n" +
                        "· 两段班次、托盘常驻、可选开机自启\n" +
                        "· 托盘常驻，可选开机自启";
            _body.Controls.Add(body);

            Label tip = new Label();
            tip.SetBounds(8, 190, 536, 44);
            tip.ForeColor = Muted;
            tip.Text = "为当前用户安装，不需要管理员权限。\n下一步会检查 WebView2 运行时等依赖。";
            _body.Controls.Add(tip);
        }

        void ShowDir()
        {
            _heading.Text = "选择安装位置";
            _sub.Text = "程序文件会被复制到这个目录";

            Label l = new Label();
            l.SetBounds(8, 10, 200, 20);
            l.ForeColor = Ink;
            l.Text = "安装到：";
            _body.Controls.Add(l);

            _dirBox.SetBounds(8, 34, 420, 26);
            _body.Controls.Add(_dirBox);

            Button browse = MakeButton("浏览…", 436, 33, 76);
            browse.Click += delegate
            {
                FolderBrowserDialog d = new FolderBrowserDialog();
                d.Description = "选择安装目录";
                d.SelectedPath = _dirBox.Text;
                if (d.ShowDialog(this) == DialogResult.OK) _dirBox.Text = d.SelectedPath;
            };
            _body.Controls.Add(browse);

            _dirLine = new Label();
            _dirLine.SetBounds(8, 74, 536, 60);
            _dirLine.ForeColor = Muted;
            _body.Controls.Add(_dirLine);

            _dirBox.TextChanged += delegate { ShowDirHint(); };
            ShowDirHint();

            Label tip = new Label();
            tip.SetBounds(8, 170, 536, 60);
            tip.ForeColor = Muted;
            tip.Text = "建议放在用户目录下（默认位置），这样可以正常显示托盘图标、写入开机自启。\n" +
                       "放在受保护目录需要管理员权限，安装程序不会提权。";
            _body.Controls.Add(tip);
            ValidateDir();
        }

        void ShowDirHint()
        {
            if (_dirLine == null) return;
            string dir = _dirBox.Text.Trim();
            if (dir.Length == 0)
            {
                _dirLine.Text = "";
                return;
            }
            long size = Directory.Exists(dir) ? Fs.DirSize(dir) : 0;
            string state = Directory.Exists(dir)
                ? (size > 0 ? "目录已存在，将覆盖同名文件（原有设置不受影响）" : "目录已存在且为空")
                : "目录不存在，安装时创建";
            _dirLine.Text = state;
            _dirLine.ForeColor = Muted;
        }

        void ValidateDir()
        {
            string dir = _dirBox.Text.Trim();
            _dirOk = false;
            string problem = null;
            if (dir.Length == 0) problem = "请填写安装目录。";
            else if (dir.Length < 4 || dir.IndexOf(':') != 1) problem = "请填写完整的绝对路径，例如 C:\\Users\\你\\Programs\\WageClock。";
            else if (!Requirements.DirWritable(dir)) problem = "这个目录不可写，请换一个（例如默认的用户目录）。";
            else if (Path.GetPathRoot(dir) == dir) problem = "不能安装到盘符根目录。";

            if (problem != null)
            {
                _dirLine.Text = problem;
                _dirLine.ForeColor = Warn;
            }
            else
            {
                _dirOk = true;
                ShowDirHint();
            }
        }

        void ShowCheck()
        {
            _heading.Text = "检查运行环境";
            _sub.Text = "安装前确认必要的组件";

            _webViewLine = AddCheckRow(0, "WebView2 运行时", "界面渲染依赖它");
            _dotnetLine = AddCheckRow(70, ".NET Framework 4.8", "程序本身的运行环境");
            _dirLine = AddCheckRow(140, "安装目录", _dirBox.Text.Trim());

            _fixWebView = MakeButton("自动安装 WebView2", 28, 206, 160);
            _fixWebView.Click += delegate
            {
                if (InstallWebView()) RefreshChecks();
            };
            _body.Controls.Add(_fixWebView);

            Button rescan = MakeButton("重新检测", 196, 206, 100);
            rescan.Click += delegate { RefreshChecks(); };
            _body.Controls.Add(rescan);

            RefreshChecks();
        }

        Label AddCheckRow(int y, string title, string detail)
        {
            Label t = new Label();
            t.SetBounds(8, y + 4, 300, 20);
            t.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            t.ForeColor = Ink;
            t.Text = title;
            _body.Controls.Add(t);

            Label d = new Label();
            d.SetBounds(8, y + 26, 460, 20);
            d.ForeColor = Muted;
            d.Text = detail;
            _body.Controls.Add(d);

            Label s = new Label();
            s.SetBounds(360, y + 4, 180, 22);
            s.TextAlign = ContentAlignment.MiddleRight;
            s.ForeColor = Muted;
            s.Text = "检测中…";
            _body.Controls.Add(s);
            return s;
        }

        void RefreshChecks()
        {
            _webViewVersion = Requirements.WebView2Version();
            _webViewOk = _webViewVersion != null;
            _webViewLine.Text = _webViewOk ? "已安装 " + _webViewVersion : "未安装";
            _webViewLine.ForeColor = _webViewOk ? Accent : Warn;

            _dotnetOk = Requirements.DotNet48();
            _dotnetLine.Text = _dotnetOk ? "已安装" : "缺少";
            _dotnetLine.ForeColor = _dotnetOk ? Accent : Warn;

            _dirOk = Requirements.DirWritable(_dirBox.Text.Trim());
            _dirLine.Text = _dirOk ? "可写" : "不可写";
            _dirLine.ForeColor = _dirOk ? Accent : Warn;

            _fixWebView.Visible = !_webViewOk;
        }

        bool InstallWebView()
        {
            try
            {
                _sub.Text = "正在安装 WebView2 运行时，请稍候…";
                Application.DoEvents();
                bool ok = Installer.InstallWebView2();
                _sub.Text = "安装前确认必要的组件";
                if (!ok)
                    MessageBox.Show(this, "自动安装没有成功。请到微软官网下载「WebView2 运行时」安装后再继续：\n\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshChecks();
                return ok;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "自动安装失败：" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        void ShowTasks()
        {
            _heading.Text = "选择附加任务";
            _sub.Text = "这些选项之后都能再改";

            _desktop = AddTask(6, "创建桌面快捷方式", true);
            _startMenu = AddTask(46, "添加到开始菜单", true);
            _autoStart = AddTask(86, "开机时自动启动（静默收进托盘）", false);
            _launch = AddTask(126, "安装完成后启动工资时钟", true);

            Label note = new Label();
            note.SetBounds(8, 190, 520, 60);
            note.ForeColor = Muted;
            note.Text = "关闭按钮的行为可以在设置里改：跟随开机自启 / 收进托盘 / 直接退出。";
            _body.Controls.Add(note);
        }

        CheckBox AddTask(int y, string text, bool check)
        {
            CheckBox c = new CheckBox();
            c.SetBounds(8, y, 520, 24);
            c.Text = text;
            c.Checked = check;
            c.ForeColor = Ink;
            _body.Controls.Add(c);
            return c;
        }

        void ShowInstall()
        {
            _heading.Text = "正在安装";
            _sub.Text = "请稍候，不要关闭窗口";

            _bar = new ProgressBar();
            _bar.SetBounds(8, 20, 536, 14);
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _body.Controls.Add(_bar);

            _status = new Label();
            _status.SetBounds(8, 46, 536, 24);
            _status.ForeColor = Muted;
            _status.Text = "准备中…";
            _body.Controls.Add(_status);

            Label tip = new Label();
            tip.SetBounds(8, 110, 536, 80);
            tip.ForeColor = Muted;
            tip.Text = "安装过程会关闭正在运行的工资时钟。\n你的设置（月薪、班次、窗口位置）保存在用户目录，升级时不会丢失。";
            _body.Controls.Add(tip);
        }

        void StartInstall()
        {
            Go(PageInstall);
            _installing = true;
            _cancel.Visible = false;

            InstallOptions opt = new InstallOptions();
            opt.Dir = _dirBox.Text.Trim();
            opt.Desktop = _desktop == null || _desktop.Checked;
            opt.StartMenu = _startMenu == null || _startMenu.Checked;
            opt.AutoStart = _autoStart != null && _autoStart.Checked;
            opt.Launch = _launch == null || _launch.Checked;

            Thread t = new Thread(delegate()
            {
                string error = null;
                try
                {
                    Installer.Run(opt, delegate(string message, int percent)
                    {
                        ReportProgress(message, percent);
                    });
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                FinishInstall(opt, error);
            });
            t.IsBackground = true;
            t.Start();
        }

        void ReportProgress(string message, int percent)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (_status != null) _status.Text = message;
                    if (_bar != null && percent >= 0 && percent <= 100) _bar.Value = percent;
                });
            }
            catch { }
        }

        void FinishInstall(InstallOptions opt, string error)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _installing = false;
                    if (error != null)
                    {
                        MessageBox.Show(this, "安装失败：" + error, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Go(PageTasks);
                        return;
                    }
                    Go(PageDone);
                    if (opt.Launch)
                    {
                        try { Process.Start(Path.Combine(opt.Dir, AppInfo.ExeName)); } catch { }
                    }
                });
            }
            catch { }
        }

        void ShowDone()
        {
            _heading.Text = "安装完成";
            _sub.Text = AppInfo.Name + " 已经可以使用了";

            Label body = new Label();
            body.SetBounds(8, 16, 536, 90);
            body.ForeColor = Ink;
            body.Text = "程序位置：" + _dirBox.Text.Trim() + "\n\n" +
                        "托盘图标：右键可以打开设置或退出；左键切换显示。\n" +
                        "快捷键：Ctrl + Alt + W 显示 / 隐藏。";
            _body.Controls.Add(body);

            Label tip = new Label();
            tip.SetBounds(8, 130, 536, 40);
            tip.ForeColor = Muted;
            tip.Text = "要卸载的话，可以在「设置 → 应用」里找到工资时钟，或运行安装目录下的 uninstall.exe。";
            _body.Controls.Add(tip);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_installing && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
