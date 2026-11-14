// 设置窗口：纯 WinForms 控件，替换原来的 WebView2 页面。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace WageClock
{
    internal sealed class NativeSettingsForm : Form
    {
        readonly App _app;

        NumericUpDown _salary;
        TextBox _amStart, _amEnd, _pmStart, _pmEnd;
        readonly CheckBox[] _days = new CheckBox[7];
        ComboBox _theme, _closeAction;
        CheckBox _alwaysOnTop, _autoLaunch;
        Label _preview;

        public NativeSettingsForm(App app)
        {
            _app = app;
            Text = "设置 · Wage Clock";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 620);
            BackColor = Color.FromArgb(247, 248, 250);
            Font = new Font("Microsoft YaHei UI", 9f);
            try { Icon = new Icon(Paths.IconPath); } catch (Exception) { }

            BuildUi();
            LoadFromConfig();
            ApplyPalette();
        }

        // ---------- 布局 ----------

        int _y = 18;

        void BuildUi()
        {
            Section("薪资");
            _salary = new NumericUpDown
            {
                Maximum = 1000000,
                Minimum = 0,
                DecimalPlaces = 0,
                Increment = 500,
                ThousandsSeparator = true,
                Width = 180,
                Location = new Point(96, _y),
            };
            Row("月薪", _salary);
            _y += 40;

            Section("上下班时间");
            _amStart = TimeBox(96, _y); _amEnd = TimeBox(200, _y);
            Hint("－", 178, _y + 4);
            _y += 34;
            _pmStart = TimeBox(96, _y); _pmEnd = TimeBox(200, _y);
            Hint("－", 178, _y + 4);
            _y += 34;
            Hint("上午", 24, _y - 56 + 4);
            Hint("下午", 24, _y - 22 + 4);
            _y += 8;

            Section("工作日");
            string[] names = { "日", "一", "二", "三", "四", "五", "六" };
            for (int i = 0; i < 7; i++)
            {
                var box = new CheckBox
                {
                    Text = names[i],
                    Appearance = Appearance.Button,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Size = new Size(44, 34),
                    Location = new Point(24 + i * 54, _y),
                    FlatStyle = FlatStyle.Flat,
                    FlatAppearance = { BorderColor = Color.FromArgb(214, 218, 224) },
                };
                _days[i] = box;
                Controls.Add(box);
            }
            _y += 44;
            Hint("只按星期判断工作日。法定节假日是带薪的，不参与换算。", 24, _y);
            _y += 30;

            Section("外观与启动");
            _theme = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 140,
                Location = new Point(96, _y),
            };
            _theme.Items.AddRange(new object[] { "跟随系统", "深色", "浅色" });
            Row("主题", _theme);
            _y += 36;

            _closeAction = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 140,
                Location = new Point(96, _y),
            };
            _closeAction.Items.AddRange(new object[] { "跟随设置", "收进托盘", "退出程序" });
            Row("关闭", _closeAction);
            _y += 44;

            _alwaysOnTop = new CheckBox
            {
                Text = "窗口置顶",
                Location = new Point(24, _y),
                AutoSize = true,
            };
            Controls.Add(_alwaysOnTop);
            _y += 30;

            _autoLaunch = new CheckBox
            {
                Text = "开机自动启动（从托盘启动）",
                Location = new Point(24, _y),
                AutoSize = true,
            };
            Controls.Add(_autoLaunch);
            _y += 40;

            var previewBox = new Panel
            {
                Location = new Point(24, _y),
                Size = new Size(392, 60),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _preview = new Label
            {
                Location = new Point(12, 10),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = Color.FromArgb(60, 66, 74),
            };
            previewBox.Controls.Add(_preview);
            Controls.Add(previewBox);
            _y += 72;

            var cancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Size = new Size(88, 32),
                Location = new Point(232, _y + 6),
                FlatStyle = FlatStyle.Flat,
            };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 220);
            Controls.Add(cancel);

            var save = new Button
            {
                Text = "保存",
                Size = new Size(88, 32),
                Location = new Point(328, _y + 6),
                BackColor = Color.FromArgb(15, 168, 110),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            save.FlatAppearance.BorderSize = 0;
            save.Click += delegate { SaveAndClose(); };
            Controls.Add(save);

            CancelButton = cancel;
        }

        void Section(string title)
        {
            var label = new Label
            {
                Text = title,
                Location = new Point(24, _y),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(18, 21, 26),
            };
            Controls.Add(label);
            _y += 30;
        }

        void Row(string label, Control field)
        {
            var text = new Label
            {
                Text = label,
                Location = new Point(24, field.Top + 4),
                AutoSize = true,
                ForeColor = Color.FromArgb(70, 76, 84),
                Width = 60,
            };
            Controls.Add(text);
            Controls.Add(field);
        }

        void Hint(string text, int x, int y)
        {
            var label = new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(120, 126, 134),
            };
            Controls.Add(label);
        }

        TextBox TimeBox(int x, int y)
        {
            var box = new TextBox
            {
                Location = new Point(x, y),
                Width = 74,
                TextAlign = HorizontalAlignment.Center,
                MaxLength = 5,
            };
            Controls.Add(box);
            return box;
        }

        // ---------- 读写配置 ----------

        void LoadFromConfig()
        {
            Store cfg = _app.Config;
            _salary.Value = (decimal)Math.Min(1000000, Math.Max(0, ParseDouble(cfg.Str("monthlySalary", "8000"), 8000)));

            Segment morning = ReadSegment(cfg, "morning", "09:00", "12:00");
            Segment afternoon = ReadSegment(cfg, "afternoon", "13:30", "18:00");
            _amStart.Text = morning.Start;
            _amEnd.Text = morning.End;
            _pmStart.Text = afternoon.Start;
            _pmEnd.Text = afternoon.End;

            WageSettings s = _app.Settings;
            for (int i = 0; i < 7; i++) _days[i].Checked = s.Workdays[i];

            string theme = cfg.Str("theme", "auto");
            _theme.SelectedIndex = theme == "dark" ? 1 : theme == "light" ? 2 : 0;

            string close = cfg.Str("closeAction", "auto");
            _closeAction.SelectedIndex = close == "tray" ? 1 : close == "quit" ? 2 : 0;

            _alwaysOnTop.Checked = _app.AlwaysOnTop;
            _autoLaunch.Checked = cfg.Flag("autoLaunch", false);

            UpdatePreview();
            _salary.ValueChanged += delegate { UpdatePreview(); };
            _amStart.TextChanged += delegate { UpdatePreview(); };
            _amEnd.TextChanged += delegate { UpdatePreview(); };
            _pmStart.TextChanged += delegate { UpdatePreview(); };
            _pmEnd.TextChanged += delegate { UpdatePreview(); };
        }

        void UpdatePreview()
        {
            var s = new WageSettings();
            s.MonthlySalary = (double)_salary.Value;
            s.Morning = new Segment(_amStart.Text, _amEnd.Text);
            s.Afternoon = new Segment(_pmStart.Text, _pmEnd.Text);
            var days = new bool[7];
            for (int i = 0; i < 7; i++) days[i] = _days[i].Checked;
            s.Workdays = days;

            int workdays = Schedule.CountWorkdays(DateTime.Now.Year, DateTime.Now.Month - 1, s);
            double hours = Schedule.TotalWorkSeconds(s) / 3600.0;
            double daily = workdays > 0 ? s.MonthlySalary / workdays : 0;
            double hourly = hours > 0 ? daily / hours : 0;

            _preview.Text = "本月工作日 " + workdays + " 天　日薪 " + Schedule.FormatMoney(daily)
                + "　时薪 " + Schedule.FormatMoney(hourly);
        }

        void SaveAndClose()
        {
            Dictionary<string, object> data = _app.Config.Data;

            data["monthlySalary"] = (double)_salary.Value;
            data["morning"] = new Dictionary<string, object>
            {
                { "start", NormalizeTime(_amStart.Text, "09:00") },
                { "end", NormalizeTime(_amEnd.Text, "12:00") },
            };
            data["afternoon"] = new Dictionary<string, object>
            {
                { "start", NormalizeTime(_pmStart.Text, "13:30") },
                { "end", NormalizeTime(_pmEnd.Text, "18:00") },
            };

            var list = new List<object>();
            for (int i = 0; i < 7; i++) if (_days[i].Checked) list.Add(i);
            data["workdays"] = list;

            data["theme"] = _theme.SelectedIndex == 1 ? "dark" : _theme.SelectedIndex == 2 ? "light" : "auto";
            data["closeAction"] = _closeAction.SelectedIndex == 1 ? "tray" : _closeAction.SelectedIndex == 2 ? "quit" : "auto";
            data["alwaysOnTop"] = _alwaysOnTop.Checked;
            data["autoLaunch"] = _autoLaunch.Checked;

            _app.Config.Save();
            _app.InvalidateSettings();
            _app.ApplySettingsChange();
            ApplyPalette();      // 设置页自己也跟着换肤
            Close();
        }

        /// <summary>设置窗口自己也要跟随主题，否则保存后只有卡片变了。</summary>
        void ApplyPalette()
        {
            bool light = _app.IsLightTheme();
            Color back = light ? Color.FromArgb(247, 248, 250) : Color.FromArgb(24, 27, 33);
            Color fore = light ? Color.FromArgb(18, 21, 26) : Color.FromArgb(238, 241, 246);
            Color dim = light ? Color.FromArgb(70, 76, 84) : Color.FromArgb(176, 184, 194);
            Color faint = light ? Color.FromArgb(120, 126, 134) : Color.FromArgb(140, 148, 158);
            Color field = light ? Color.White : Color.FromArgb(34, 38, 46);

            BackColor = back;
            foreach (Control c in Controls)
            {
                if (c is Label)
                {
                    c.ForeColor = ((Label)c).Font.Bold ? fore : dim;
                }
                else if (c is NumericUpDown)
                {
                    c.BackColor = field;
                    c.ForeColor = fore;
                    foreach (Control inner in c.Controls)   // 数字框内部的编辑区
                    {
                        inner.BackColor = field;
                        inner.ForeColor = fore;
                    }
                }
                else if (c is CheckBox || c is ComboBox)
                {
                    c.ForeColor = fore;
                }
                else if (c is TextBox)
                {
                    c.BackColor = field;
                    c.ForeColor = fore;
                }
                else if (c is Panel)
                {
                    c.BackColor = field;
                    foreach (Control inner in c.Controls)
                    {
                        inner.ForeColor = fore;
                    }
                }
            }
            _preview.ForeColor = fore;
            _preview.Parent.BackColor = field;
        }
        static Segment ReadSegment(Store cfg, string key, string defStart, string defEnd)
        {
            object raw;
            if (!cfg.Data.TryGetValue(key, out raw)) return new Segment(defStart, defEnd);
            var map = raw as Dictionary<string, object>;
            if (map == null) return new Segment(defStart, defEnd);
            object a, b;
            string start = map.TryGetValue("start", out a) ? Convert.ToString(a) : defStart;
            string end = map.TryGetValue("end", out b) ? Convert.ToString(b) : defEnd;
            return new Segment(start, end);
        }

        static double ParseDouble(string text, double fallback)
        {
            double v;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static string NormalizeTime(string text, string fallback)
        {
            int? seconds = Schedule.ParseHM(text);
            if (seconds == null) return fallback;
            int total = seconds.Value;
            return Schedule.Pad2(total / 3600) + ":" + Schedule.Pad2((total % 3600) / 60);
        }
    }
}
