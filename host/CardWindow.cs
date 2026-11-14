// 卡片窗口：分层窗口逐像素 alpha + GDI+ 自绘。
// 背景可以半透明透出桌面，文字与数字始终完全不透明。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WageClock
{
    internal sealed class CardWindow : Form
    {
        // ---- 逻辑尺寸（与旧卡片一致）----
        const float W = 480f, H = 240f;
        const float CardPad = 8f;
        const float Radius = 18f;

        // ---- Win32 ----
        const int WS_EX_LAYERED = 0x00080000;
        const int ULW_ALPHA = 0x00000002;
        const byte AC_SRC_OVER = 0x00;
        const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

        // ---- 状态 ----
        readonly App _app;
        readonly Timer _timer;
        readonly Timer _posTimer = new Timer();
        Palette _pal;
        float _scale = 1f;
        Bitmap _base;                 // 静态层：面板、标题、图标、底栏标签
        Bitmap _frame;                // 每帧合成结果
        WageView _view;

        double _moneyPrev, _moneyCur;
        float _rollT = 1f;            // 0→1 的滚动进度
        float _moneySize;             // 金额字号，按可用宽度算一次

        sealed class Spark
        {
            public float X, Y, VX, VY, Life, Max, Size;
            public Color C;
        }
        readonly List<Spark> _sparks = new List<Spark>();
        readonly Random _rnd = new Random(20261114);
        float _emberPhase;

        // 命中区（逻辑坐标）
        RectangleF _zPin, _zGear, _zClose;

        public CardWindow(App app, bool light)
        {
            _app = app;
            _pal = light ? Palette.Light() : Palette.Dark();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Text = "Wage Clock";
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;   // 否则 WinForms 会按字体把 400×272 缩成 394×243
            try { Icon = new Icon(Paths.IconPath); } catch (Exception) { }

            Point? saved = app.Config.WindowPos;
            Location = saved.HasValue ? saved.Value : DefaultLocation();

            // 不能在这里 CreateGraphics()：那会提前建出窗口句柄，OnHandleCreated 时状态还没准备好
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) _scale = g.DpiX / 96f;
            Size = new Size((int)Math.Round(W * _scale), (int)Math.Round(H * _scale));

            _view = Schedule.Evaluate(_app.Now, _app.Settings);
            _moneyCur = _view.Earned;
            _moneyPrev = _moneyCur;

            _timer = new Timer { Interval = 33 };
            _timer.Tick += delegate { Tick(); };

            // 拖动后把位置写回配置
            _posTimer.Interval = 700;
            _posTimer.Tick += delegate
            {
                _posTimer.Stop();
                if (WindowState != FormWindowState.Normal) return;
                _app.Config.WindowPos = Location;
                _app.Config.Save();
            };
            LocationChanged += delegate
            {
                _posTimer.Stop();
                _posTimer.Start();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return false; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RECT wr, cr; GetWindowRect(Handle, out wr); GetClientRect(Handle, out cr);
            Paths.Log("win32 window=" + (wr.R - wr.L) + "x" + (wr.B - wr.T) + " client=" + (cr.R - cr.L) + "x" + (cr.B - cr.T) + " dpi=" + DeviceDpi);
            Paths.Log("card created: client=" + ClientSize.Width + "x" + ClientSize.Height
                + " size=" + Size.Width + "x" + Size.Height + " scale=" + _scale.ToString("0.###") + " border=" + FormBorderStyle);
            RebuildBase();
            Render();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Paths.Log("card shown, timer start");
            _timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _timer.Dispose();
            if (_base != null) _base.Dispose();
            if (_frame != null) _frame.Dispose();
            base.OnFormClosed(e);
        }

        public void SetTheme(bool light)
        {
            _pal = light ? Palette.Light() : Palette.Dark();
            RebuildBase();
            Render();
        }

        static Point DefaultLocation()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            return new Point(area.Right - 424, area.Bottom - 312);
        }

        int _ticks;

        void Tick()
        {
            if (++_ticks <= 3) Paths.Log("tick " + _ticks);
            _view = Schedule.Evaluate(_app.Now, _app.Settings);

            if (Math.Abs(_view.Earned - _moneyCur) > 1e-9)
            {
                _moneyPrev = _moneyCur;
                _moneyCur = _view.Earned;
                _rollT = 0f;
            }
            if (_rollT < 1f) _rollT = Math.Min(1f, _rollT + 0.055f);

            StepSparks();
            Render();
        }

        // ================= 绘制 =================

        void RebuildBase()
        {
            if (_view == null) return;
            if (_base != null) _base.Dispose();
            // 用窗口矩形而不是客户区：分层窗口的位图是按整个窗口贴的
            _base = new Bitmap(Size.Width, Size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(_base))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(_scale, _scale);

                DrawCard(g);
                DrawHeader(g);
                DrawTools(g);
                DrawTrack(g);
                DrawMetaLabels(g);
            }
        }

        void Render()
        {
            if (_base == null) return;
            if (_frame == null || _frame.Width != _base.Width || _frame.Height != _base.Height)
            {
                if (_frame != null) _frame.Dispose();
                _frame = new Bitmap(_base.Width, _base.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }

            using (Graphics g = Graphics.FromImage(_frame))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.CompositingMode = CompositingMode.SourceOver;
                g.Clear(Color.Transparent);
                g.DrawImageUnscaled(_base, 0, 0);
                g.ScaleTransform(_scale, _scale);

                DrawStatePill(g);
                DrawMoney(g);
                DrawProgress(g);
                DrawSparks(g);
                DrawMetaValues(g);
            }
            Push(_frame);
        }

        void DrawCard(Graphics g)
        {
            var rect = new RectangleF(CardPad, CardPad, W - CardPad * 2, H - CardPad * 2);
            using (GraphicsPath path = Round(rect, Radius))
            {
                // 底色只在这里给 alpha：0.85 半透明，透出桌面
                var mix = Blend(_pal.Bg2, _pal.Bg1, 0.5f);
                using (var brush = new LinearGradientBrush(
                    new PointF(rect.X, rect.Y), new PointF(rect.X + rect.Width, rect.Bottom),
                    Color.FromArgb(217, mix), Color.FromArgb(217, _pal.Bg1)))
                {
                    g.FillPath(brush, path);
                }
                using (var pen = new Pen(_pal.Line, 1f)) g.DrawPath(pen, path);
            }
        }

        void DrawHeader(Graphics g)
        {
            using (var f = UiFont(13f, FontStyle.Regular))
            using (var b = new SolidBrush(_pal.Dim))
            {
                DrawTextAt(g, _view.DateLabel, f, b, 24f, 22f);
            }
        }

        void DrawTools(Graphics g)
        {
            _zPin = new RectangleF(W - 93f, 16, 20, 20);
            _zGear = new RectangleF(W - 67f, 16, 20, 20);
            _zClose = new RectangleF(W - 41f, 16, 20, 20);

            using (var pen = new Pen(_pal.Faint, 1.3f))
            {
                // 置顶：图钉 —— 三角头 + 针杆
                var c = Center(_zPin);
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new PointF[]
                    {
                        new PointF(c.X - 4.6f, c.Y - 5.2f),
                        new PointF(c.X + 4.6f, c.Y - 5.2f),
                        new PointF(c.X + 1.5f, c.Y + 1.0f),
                        new PointF(c.X - 1.5f, c.Y + 1.0f),
                    });
                    g.DrawPath(pen, path);
                }
                g.DrawLine(pen, c.X, c.Y + 1.0f, c.X, c.Y + 6.0f);

                // 设置：齿轮 —— 八个齿 + 内外圆
                c = Center(_zGear);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4.0;
                    g.DrawLine(pen,
                        c.X + (float)(3.7 * Math.Cos(a)), c.Y + (float)(3.7 * Math.Sin(a)),
                        c.X + (float)(5.4 * Math.Cos(a)), c.Y + (float)(5.4 * Math.Sin(a)));
                }
                g.DrawEllipse(pen, c.X - 3.8f, c.Y - 3.8f, 7.6f, 7.6f);
                g.DrawEllipse(pen, c.X - 1.3f, c.Y - 1.3f, 2.6f, 2.6f);

                // 隐藏：叉
                c = Center(_zClose);
                g.DrawLine(pen, c.X - 3.8f, c.Y - 3.8f, c.X + 3.8f, c.Y + 3.8f);
                g.DrawLine(pen, c.X + 3.8f, c.Y - 3.8f, c.X - 3.8f, c.Y + 3.8f);
            }
        }

        void DrawTrack(Graphics g)
        {
            var track = TrackRect();
            using (var path = Round(track, track.Height / 2))
            using (var pen = new Pen(_pal.Track, track.Height))
            {
                g.DrawPath(pen, path);
            }
        }

        void DrawMetaLabels(Graphics g)
        {
            using (var f = UiFont(11f, FontStyle.Regular))
            using (var b = new SolidBrush(_pal.Faint))
            {
                DrawTextAt(g, "距周末", f, b, 24f, 178f);
                DrawTextAt(g, "距下班", f, b, W / 2f - 40f, 178f);
                RightText(g, "本月", f, b, W - 24f, 178f);
            }
        }

        void DrawStatePill(Graphics g)
        {
            bool active = _view.State == "working";
            using (var f = UiFont(12f, FontStyle.Regular))
            using (var b = new SolidBrush(active ? _pal.Accent : _pal.Dim))
            {
                SizeF size = g.MeasureString(_view.StateLabel, f);
                g.DrawString(_view.StateLabel, f, b, W - 158f, 23f);
            }
        }
        void DrawMoney(Graphics g)
        {
            const int IntDigits = 4, DecDigits = 4;
            string prev = FormatDigits(_moneyPrev, IntDigits, DecDigits);
            string cur = FormatDigits(_moneyCur, IntDigits, DecDigits);

            float barY = H - 54f;
            float areaTop = 46f;
            float areaBottom = barY - 40f;   // 金额与进度条之间留出呼吸空间
            float avail = W - 40f;

            // 字号：宽度与高度双重约束，上限 120px
            float size = 120f;
            for (; size >= 32f; size -= 2f)
            {
                using (var fi = MoneyFont(size))
                using (var fd = MoneyFont(size * 0.5f))
                {
                    float iw = g.MeasureString("0000", fi).Width * 0.92f;
                    float dw = g.MeasureString("0000", fd).Width * 0.92f;
                    float dotw = g.MeasureString(".", fd).Width * 0.55f;
                    float h = fi.GetHeight(g);
                    if (iw + dw + dotw <= avail && h <= (areaBottom - areaTop)) break;
                }
            }

            using (var fi = MoneyFont(size))
            using (var fd = MoneyFont(size * 0.5f))
            using (var brush = new SolidBrush(_pal.Neon))
            using (var decBrush = new SolidBrush(Color.FromArgb(217, _pal.Neon)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            {
                float intW = g.MeasureString("0000", fi).Width * 0.92f / 4f;
                float decW = g.MeasureString("0000", fd).Width * 0.92f / 4f;
                float dotW = decW * 0.55f;
                float totalW = intW * IntDigits + dotW + decW * DecDigits;
                float x = (W - totalW) / 2f;

                // 垂直居中：整数部分的可见高度占满中间区域
                float baseline = (areaTop + areaBottom) / 2f + Ascent(fi) * 0.52f;
                float intTop = baseline - Ascent(fi);
                float decTop = baseline - Ascent(fd);
                float rowH = intTop + fi.GetHeight(g) * 0.02f + fi.GetHeight(g) * 0.98f - intTop;

                float t = Ease(_rollT);
                bool rolling = _rollT < 1f;

                for (int i = 0; i < cur.Length; i++)
                {
                    bool isDot = cur[i] == '.';
                    bool isInt = i < IntDigits;
                    Font f = isDot ? fd : (isInt ? fi : fd);
                    float cw = isDot ? dotW : (isInt ? intW : decW);
                    float top = isInt ? intTop : decTop;
                    var cell = new RectangleF(x, top, cw, f.GetHeight(g));

                    if (!rolling || prev[i] == cur[i])
                    {
                        g.DrawString(cur[i].ToString(), f, isInt ? brush : decBrush, cell, sf);
                    }
                    else
                    {
                        Region old = g.Clip;
                        g.SetClip(cell);
                        g.DrawString(prev[i].ToString(), f, isInt ? brush : decBrush,
                            new RectangleF(cell.X, cell.Y - rowH * t, cell.Width, cell.Height), sf);
                        g.DrawString(cur[i].ToString(), f, isInt ? brush : decBrush,
                            new RectangleF(cell.X, cell.Y + rowH * (1f - t), cell.Width, cell.Height), sf);
                        g.Clip = old;
                    }
                    x += cw;
                }
            }
        }

        /// <summary>字体基线高度，用于不同字号对齐同一条基线。</summary>
        static float Ascent(Font f)
        {
            FontFamily fam = f.FontFamily;
            return fam.GetCellAscent(f.Style) * f.Size / fam.GetEmHeight(f.Style);
        }
        /// <summary>挑一个能塞进可用宽度的字号。</summary>
        float FitMoneySize(Graphics g, float avail)
        {
            if (_moneySize > 0) return _moneySize;
            for (float size = 85f; size >= 26f; size -= 2f)
            {
                using (var f = MoneyFont(size))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                {
                    float digitW = WidestDigit(g, f, sf);
                    float decDigitW = digitW * 0.667f;
                    float numW = digitW * 4 + digitW * 0.367f + decDigitW * 4;
                    float yenW = size * 0.52f * 0.6f;
                    if (yenW + 4f + numW <= avail) { _moneySize = size; return size; }
                }
            }
            _moneySize = 26f;
            return _moneySize;
        }

        void DrawProgress(Graphics g)
        {
            var track = TrackRect();
            float pct = (float)Math.Max(0, Math.Min(1, _view.Progress));
            float burnX = track.X + track.Width * pct;

            // 已烧掉的部分：灰烬
            if (pct > 0.001f)
            {
                var ash = new RectangleF(track.X, track.Y, Math.Max(0.1f, burnX - track.X), track.Height);
                using (GraphicsPath path = Round(ash, ash.Height / 2))
                using (var brush = new LinearGradientBrush(ash, _pal.AshHi, _pal.AshLo, 0f))
                {
                    g.FillPath(brush, path);
                }
            }

            // 还没烧的部分：引线
            if (pct < 0.999f)
            {
                var fuse = new RectangleF(burnX, track.Y, Math.Max(0.1f, track.Right - burnX), track.Height);
                using (GraphicsPath path = Round(fuse, fuse.Height / 2))
                using (var brush = new LinearGradientBrush(fuse, _pal.FuseHi, _pal.FuseLo, 0f))
                {
                    g.FillPath(brush, path);
                }
            }

            // 燃烧点
            if (pct > 0.001f && pct < 0.999f)
            {
                using (var glow = new SolidBrush(Color.FromArgb(90, _pal.Warn)))
                {
                    g.FillEllipse(glow, burnX - 7, track.Y - 3.5f, 14, 14);
                }
                using (var hot = new SolidBrush(_pal.Warn))
                {
                    g.FillEllipse(hot, burnX - 1.8f, track.Y + track.Height / 2 - 1.8f, 3.6f, 3.6f);
                }
                SpawnSparks(burnX, track.Y + track.Height / 2, pct);
            }
        }

        void DrawSparks(Graphics g)
        {
            foreach (Spark s in _sparks)
            {
                float k = Math.Max(0f, s.Life / s.Max);
                int a = (int)(255 * k);
                using (var glow = new SolidBrush(Color.FromArgb(a / 4, s.C)))
                {
                    float d = s.Size * 3f;
                    g.FillEllipse(glow, s.X - d / 2, s.Y - d / 2, d, d);
                }
                using (var b = new SolidBrush(Color.FromArgb(a, s.C)))
                {
                    g.FillEllipse(b, s.X - s.Size / 2, s.Y - s.Size / 2, s.Size, s.Size);
                }
            }
        }

        void DrawMetaValues(Graphics g)
        {
            using (var f = UiFont(15f, FontStyle.Regular))
            using (var b = new SolidBrush(_pal.Txt))
            {
                string weekend = _view.WeekendReached ? "已到周末" : Schedule.FormatSpan(_view.WeekendSeconds);
                DrawTextAt(g, weekend, f, b, 24f, 194f);

                string off = _view.State == "off" ? "—" : Schedule.FormatClockCs(_view.OffWorkSeconds);
                DrawTextAt(g, off, f, b, W / 2f - 40f, 194f);

                RightText(g, _view.ElapsedWorkdays + "/" + _view.MonthWorkdays + " 天", f, b, W - 24f, 194f);
            }
        }

        // ================= 小工具 =================

        RectangleF TrackRect()
        {
            return new RectangleF(24f, 158f, W - 48f, 5f);
        }

        static Font UiFont(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Pixel);
        }

        static Font MoneyFont(float size)
        {
            try
            {
                return new Font("Segoe UI Variable Display", size, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            }
        }

        /// <summary>数字的实际前进宽度（用于收紧字距）。</summary>
        static float Advance(Graphics g, Font f)
        {
            return g.MeasureString("0000", f).Width / 4f;
        }
        static float WidestDigit(Graphics g, Font f, StringFormat sf)
        {
            float max = 0;
            for (char c = '0'; c <= '9'; c++)
            {
                float w = g.MeasureString(c.ToString(), f, PointF.Empty, sf).Width;
                if (w > max) max = w;
            }
            return max * 1.02f;
        }

        static string FormatDigits(double value, int intDigits, int decDigits)
        {
            if (value < 0) value = 0;
            double scale = Math.Pow(10, decDigits);
            long scaled = (long)Math.Floor(value * scale + 1e-6);
            string dec = (scaled % (long)scale).ToString(new string('0', decDigits), CultureInfo.InvariantCulture);
            long whole = scaled / (long)scale;
            string intPart = whole.ToString(CultureInfo.InvariantCulture).PadLeft(intDigits, '0');
            return intPart + "." + dec;
        }

        static void DrawTextAt(Graphics g, string text, Font font, Brush brush, float x, float y)
        {
            g.DrawString(text, font, brush, x, y);
        }

        static void RightText(Graphics g, string text, Font font, Brush brush, float right, float y)
        {
            SizeF size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, right - size.Width, y);
        }

        static PointF Center(RectangleF r)
        {
            return new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        }

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        static Color FromArgb(int alpha, Color c)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        static float Ease(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return 1f - (1f - t) * (1f - t) * (1f - t);
        }

        static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2f;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ================= 火花 =================

        void SpawnSparks(float x, float y, float pct)
        {
            int count = _rnd.Next(0, 3);
            for (int i = 0; i < count; i++)
            {
                float life = 0.35f + (float)_rnd.NextDouble() * 0.55f;
                _sparks.Add(new Spark
                {
                    X = x + (float)(_rnd.NextDouble() * 6 - 3),
                    Y = y + (float)(_rnd.NextDouble() * 6 - 3),
                    VX = (float)(_rnd.NextDouble() * 46 - 23) + (pct > 0.5f ? 12 : -12),
                    VY = -(12f + (float)_rnd.NextDouble() * 46f),
                    Life = life,
                    Max = life,
                    Size = 1.4f + (float)_rnd.NextDouble() * 1.8f,
                    C = _rnd.Next(3) == 0 ? _pal.Accent2 : _pal.Warn,
                });
            }
        }

        void StepSparks()
        {
            _emberPhase += 0.05f;
            float dt = 0.033f;
            for (int i = _sparks.Count - 1; i >= 0; i--)
            {
                Spark s = _sparks[i];
                s.Life -= dt;
                if (s.Life <= 0)
                {
                    _sparks.RemoveAt(i);
                    continue;
                }
                s.X += s.VX * dt;
                s.Y += s.VY * dt;
                s.VY += 62f * dt;      // 轻微重力
                s.VX *= 0.985f;
            }
            if (_sparks.Count > 320) _sparks.RemoveRange(0, _sparks.Count - 320);
        }

        // ================= 命中与拖动 =================

        PointF ToLogical(Point p)
        {
            return new PointF(p.X / _scale, p.Y / _scale);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            PointF p = ToLogical(e.Location);

            if (e.Button == MouseButtons.Right)
            {
                _app.ShowContextMenu(this, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (_zClose.Contains(p)) { _app.RequestClose(); return; }
            if (_zGear.Contains(p)) { _app.OpenSettings(); return; }
            if (_zPin.Contains(p)) { _app.SetPinned(!_app.AlwaysOnTop); return; }

            Native.DragWindow(Handle);
        }

        // ================= 推送到屏幕 =================

        void Push(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT { X = 0, Y = 0 };
                var dst = new POINT { X = Left, Y = Top };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA,
                };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>截图工具用：把当前卡片渲染成位图。</summary>
        public Bitmap Snapshot(DateTime now)
        {
            _view = Schedule.Evaluate(now, _app.Settings);
            _moneyCur = _view.Earned;
            _moneyPrev = _moneyCur;
            _rollT = 1f;
            RebuildBase();
            Render();
            return new Bitmap(_frame);
        }
    }
}