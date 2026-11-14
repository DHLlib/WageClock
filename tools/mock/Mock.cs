// 设计稿生成器：用 GDI+ 画三套金额主导的卡片方案，输出 PNG 供评审。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

static class Mock
{
    static void Main()
    {
        Draw("A", 480, 240, true, false, true);    // 极简：无标签、无 ¥、金额居中
        Draw("B", 480, 240, true, true, true);     // 折中：保留小标签
        Draw("C", 480, 240, false, true, false);   // 现状对照：标签 + ¥ + 顶部固定
    }

    static void Draw(string tag, int W, int H, bool minimal, bool keepLabel, bool center)
    {
        using (var bmp = new Bitmap(W, H))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            Color bg1 = Color.FromArgb(217, 25, 30, 38);
            Color bg2 = Color.FromArgb(217, 17, 20, 25);
            Color neon = Color.FromArgb(255, 77, 255, 159);
            Color txt = Color.FromArgb(238, 241, 246);
            Color dim = Color.FromArgb(120, 242, 244, 248);
            Color faint = Color.FromArgb(70, 242, 244, 248);
            Color track = Color.FromArgb(30, 255, 255, 255);

            var card = new RectangleF(8, 8, W - 16, H - 16);
            using (var path = Round(card, 18))
            using (var brush = new LinearGradientBrush(card.Location, new PointF(card.Right, card.Bottom), bg2, bg1))
                g.FillPath(brush, path);
            using (var pen = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
            using (var path = Round(card, 18))
                g.DrawPath(pen, path);

            using (var f = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(dim))
                g.DrawString("周四 · 11月12日", f, b, 24, 22);

            using (var f = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(neon))
                g.DrawString("工作中", f, b, W - 118, 23);

            // 金额
            float labelH0 = keepLabel ? 18f : 0f;
            float barY = H - 54;
            float areaTop = 44, areaBottom = barY - 14;
            string intPart = "0358", decPart = "2917";
            float avail = W - 40;
            float intSize = 120f;
            for (; intSize >= 40f; intSize -= 2f)
            {
                using (var fi = new Font("Segoe UI", intSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var fd = new Font("Segoe UI", intSize * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    float iw = g.MeasureString(intPart, fi).Width * 0.92f;
                    float dw = g.MeasureString(decPart, fd).Width * 0.92f;
                    float dotw = g.MeasureString(".", fd).Width * 0.55f;
                    if (iw + dw + dotw <= avail) break;
                }
            }

            float labelH = keepLabel ? 18f : 0f;
            using (var fi = new Font("Segoe UI", intSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fd = new Font("Segoe UI", intSize * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                float iw = g.MeasureString(intPart, fi).Width * 0.92f;
                float dw = g.MeasureString(decPart, fd).Width * 0.92f;
                float dotw = g.MeasureString(".", fd).Width * 0.55f;
                float total = iw + dw + dotw;
                float x = (W - total) / 2f;

                float baseline = center
                    ? (areaTop + labelH + areaBottom) / 2f + intSize * 0.36f
                    : areaTop + labelH + Ascent(fi);

                if (keepLabel)
                {
                    using (var lf = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var lb = new SolidBrush(faint))
                    {
                        string label = "窝囊费已赚";
                        float lw = g.MeasureString(label, lf).Width;
                        g.DrawString(label, lf, lb, minimal ? x : (W - lw) / 2f, baseline - Ascent(fi) - 16);
                    }
                }

                if (!minimal)
                {
                    using (var yf = new Font("Segoe UI", intSize * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var yb = new SolidBrush(Color.FromArgb(150, neon)))
                        g.DrawString("¥", yf, yb, x - 26, baseline - Ascent(yf));
                }

                using (var b = new SolidBrush(neon))
                {
                    g.DrawString(intPart, fi, b, x, baseline - Ascent(fi), StringFormat.GenericTypographic);
                    x += iw;
                    using (var bd = new SolidBrush(Color.FromArgb(217, neon)))
                    {
                        g.DrawString(".", fd, bd, x, baseline - Ascent(fd), StringFormat.GenericTypographic);
                        x += dotw;
                        g.DrawString(decPart, fd, bd, x, baseline - Ascent(fd), StringFormat.GenericTypographic);
                    }
                }
            }

            // 进度条
            var bar = new RectangleF(20, barY, W - 40, 6);
            using (var p = Round(bar, 3))
            using (var b = new SolidBrush(track))
                g.FillPath(b, p);
            float burned = 0.62f;
            var ash = new RectangleF(bar.X, bar.Y, bar.Width * burned, bar.Height);
            using (var p = Round(ash, 3))
            using (var b = new LinearGradientBrush(ash, Color.FromArgb(235, 56, 52, 46), Color.FromArgb(245, 14, 12, 10), 0f))
                g.FillPath(b, p);
            using (var b = new SolidBrush(Color.FromArgb(90, 243, 180, 94)))
                g.FillEllipse(b, bar.X + bar.Width * burned - 7, bar.Y - 3, 14, 14);

            // 底栏
            using (var lf = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var vf = new Font("Microsoft YaHei UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var lb = new SolidBrush(faint))
            using (var vb = new SolidBrush(txt))
            {
                float y = H - 40;
                g.DrawString("距周末", lf, lb, 20, y);
                g.DrawString("1天3小时", vf, vb, 20, y + 15);
                g.DrawString("距下班", lf, lb, W / 2f - 50, y);
                g.DrawString("03:09:54", vf, vb, W / 2f - 50, y + 15);
                string month = "本月 9/21";
                float mw = g.MeasureString(month, lf).Width;
                g.DrawString("本月", lf, lb, W - 20 - mw, y);
                string mv = "9/21 天";
                float vw = g.MeasureString(mv, vf).Width;
                g.DrawString(mv, vf, vb, W - 20 - vw, y + 15);
            }

            bmp.Save("shots/mock-" + tag + ".png", System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("shots/mock-" + tag + ".png  " + W + "x" + H + "  金额 " + (int)intSize + "px");
        }
    }

    static float Ascent(Font f)
    {
        FontFamily fam = f.FontFamily;
        return fam.GetCellAscent(f.Style) * f.Size / fam.GetEmHeight(f.Style);
    }

    static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
