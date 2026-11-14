// 卡片配色：对应 src/styles/widget.css 里 dark / light 两套 CSS 变量。
using System.Drawing;

namespace WageClock
{
    internal sealed class Palette
    {
        public Color Bg1, Bg2, Line, LineStrong, Txt, Dim, Faint;
        public Color Accent, Accent2, Neon, Warn;
        public Color Track, Pill;
        public Color FuseHi, FuseLo;
        public Color AshHi, AshMid, AshLo;

        public static Palette Dark()
        {
            return new Palette
            {
                Bg1 = Hex(0x111419),
                Bg2 = Hex(0x191E26),
                Line = Alpha(255, 255, 255, 19),
                LineStrong = Alpha(255, 255, 255, 41),
                Txt = Hex(0xF2F4F8),
                Dim = Alpha(242, 244, 248, 118),
                Faint = Alpha(242, 244, 248, 69),
                Accent = Hex(0x55E3A6),
                Accent2 = Hex(0x9FF7D2),
                Neon = Hex(0x4DFF9F),
                Warn = Hex(0xF3B45E),
                Track = Alpha(255, 255, 255, 19),
                Pill = Alpha(255, 255, 255, 14),
                FuseHi = Alpha(255, 255, 255, 87),
                FuseLo = Alpha(255, 255, 255, 43),
                AshHi = Alpha(56, 52, 46, 235),
                AshMid = Alpha(32, 29, 26, 240),
                AshLo = Alpha(14, 12, 10, 245),
            };
        }

        public static Palette Light()
        {
            return new Palette
            {
                Bg1 = Hex(0xFFFFFF),
                Bg2 = Hex(0xF5F6F8),
                Line = Alpha(17, 20, 26, 20),
                LineStrong = Alpha(17, 20, 26, 41),
                Txt = Hex(0x12151A),
                Dim = Alpha(18, 21, 26, 133),
                Faint = Alpha(18, 21, 26, 84),
                Accent = Hex(0x0FA86E),
                Accent2 = Hex(0x3FD69C),
                Neon = Hex(0x04895A),
                Warn = Hex(0xC77A14),
                Track = Alpha(17, 20, 26, 19),
                Pill = Alpha(17, 20, 26, 12),
                FuseHi = Alpha(17, 20, 26, 61),
                FuseLo = Alpha(17, 20, 26, 33),
                AshHi = Alpha(150, 146, 140, 235),
                AshMid = Alpha(120, 116, 110, 240),
                AshLo = Alpha(96, 92, 86, 245),
            };
        }

        static Color Hex(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        static Color Alpha(int r, int g, int b, int a)
        {
            return Color.FromArgb(a, r, g, b);
        }
    }
}
