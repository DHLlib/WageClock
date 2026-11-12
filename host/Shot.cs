using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace WageClock
{
    /// <summary>
    /// Screenshot harness: renders the real pages at fixed clock times so the UI can be
    /// reviewed deterministically.   WageClock.exe --shot shots
    /// </summary>
    internal sealed class ShotContext : ApplicationContext
    {
        private readonly string _outDir;
        private readonly Form _pump;
        private readonly App _app;
        private readonly List<ShotCase> _cases = new List<ShotCase>();

        public ShotContext(string outDir)
        {
            _outDir = outDir;
            _app = new App(true);

            _pump = new Form();
            _pump.FormBorderStyle = FormBorderStyle.None;
            _pump.ShowInTaskbar = false;
            _pump.StartPosition = FormStartPosition.Manual;
            _pump.Location = new Point(-6000, -6000);
            _pump.Size = new Size(1, 1);
            _pump.Show();

            BuildCases();
            _pump.BeginInvoke(new Action(async delegate { await RunAll(); }));
        }

        private void BuildCases()
        {
            Add("01-working-dark", "index.html", "2026-11-12T10:42:00", "dark", null);
            Add("02-lunch-dark", "index.html", "2026-11-12T12:31:00", "dark", null);
            Add("03-before-dark", "index.html", "2026-11-12T08:12:00", "dark", null);
            Add("04-done-dark", "index.html", "2026-11-12T18:40:00", "dark", null);
            Add("05-off-dark", "index.html", "2026-11-14T10:00:00", "dark", null);
            Add("06-working-light", "index.html", "2026-11-12T10:42:00", "light", null);
            Add("07-lunch-light", "index.html", "2026-11-12T12:31:00", "light", null);
            Add("08-off-light", "index.html", "2026-11-14T10:00:00", "light", null);
            Add("09-settings-dark", "settings.html", "2026-11-12T10:42:00", "dark", null);
            Add("10-settings-light", "settings.html", "2026-11-12T10:42:00", "light", null);
            Add("11-big-salary", "index.html", "2026-11-12T16:20:00", "dark", "{\"monthlySalary\":42000}");
        }

        private void Add(string name, string page, string now, string theme, string settingsJson)
        {
            var c = new ShotCase();
            c.Name = name;
            c.Page = page;
            c.Now = now;
            c.Theme = theme;
            c.SettingsJson = settingsJson;
            c.Width = page == "settings.html" ? 440 : 400;
            c.Height = page == "settings.html" ? 820 : 272;
            _cases.Add(c);
        }

        private async Task RunAll()
        {
            try
            {
                Directory.CreateDirectory(_outDir);
                foreach (ShotCase c in _cases)
                {
                    await Capture(c);
                }
                Console.WriteLine("shots done: " + _cases.Count);
            }
            catch (Exception ex)
            {
                Console.WriteLine("shot failed: " + ex);
            }
            finally
            {
                ExitThread();
            }
        }

        private async Task Capture(ShotCase c)
        {
            _app.Config.Load();
            if (c.SettingsJson != null)
            {
                var over = Store.Json.Deserialize<Dictionary<string, object>>(c.SettingsJson);
                if (over != null)
                {
                    foreach (KeyValuePair<string, object> kv in over) _app.Config.Data[kv.Key] = kv.Value;
                }
            }
            _app.Config.Data["theme"] = c.Theme;

            Color bg = c.Theme == "light" ? Color.FromArgb(247, 248, 250) : Color.FromArgb(17, 20, 25);
            int zoom = c.Height * 2 <= 1000 ? 2 : 1;
            var form = new ShotForm(_app, c.Width * zoom, c.Height * zoom, bg);
            form.Zoom = zoom;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-6000, -6000);
            form.Show();

            var query = new Dictionary<string, string>();
            query["now"] = c.Now;
            query["theme"] = c.Theme;

            await form.Open(c.Page, query);
            await form.WaitReady(8000);
            await Task.Delay(1200);

            string file = Path.Combine(_outDir, c.Name + ".png");
            await form.CapturePngAsync(file);
            form.Close();
            Console.WriteLine("  " + c.Name + ".png");
        }

        private sealed class ShotCase
        {
            public string Name;
            public string Page;
            public string Now;
            public string Theme;
            public string SettingsJson;
            public int Width;
            public int Height;
        }
    }

    internal sealed class ShotForm : WebHostForm
    {
        private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>();

        public ShotForm(App app, int widthDip, int heightDip, Color bg)
            : base(app, widthDip, heightDip, bg, false)
        {
        }

        public double Zoom
        {
            get { return ZoomFactor; }
            set { ZoomFactor = value; }
        }

        public Task Open(string page, Dictionary<string, string> query)
        {
            return Navigate(page, query);
        }

        public async Task WaitReady(int timeoutMs)
        {
            Task finished = await Task.WhenAny(_done.Task, Task.Delay(timeoutMs));
            if (finished != _done.Task) Console.WriteLine("  (timeout waiting for ready)");
        }

        protected override void OnReady()
        {
            base.OnReady();
            _done.TrySetResult(true);
        }
    }
}
