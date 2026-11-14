// Schedule.cs 的对照测试：逐条复刻 tests/schedule.test.js，用来验收移植是否等价。
// 用 `WageClock.exe --selftest` 运行，结果写进日志并返回退出码。
using System;
using System.Collections.Generic;
using System.Globalization;

namespace WageClock
{
    internal static class SelfTest
    {
        static int _pass;
        static readonly List<string> _failures = new List<string>();

        static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++;
            else _failures.Add(name + "  " + detail);
        }

        static void Eq(string name, string expected, string actual)
        {
            Check(name, expected == actual, "期望 [" + expected + "] 实际 [" + actual + "]");
        }

        static void Eq(string name, double expected, double actual)
        {
            Check(name, Math.Abs(expected - actual) < 1e-9, "期望 " + expected.ToString("R", CultureInfo.InvariantCulture)
                + " 实际 " + actual.ToString("R", CultureInfo.InvariantCulture));
        }

        static void Eq(string name, int expected, int actual)
        {
            Check(name, expected == actual, "期望 " + expected + " 实际 " + actual);
        }

        static void Eq(string name, bool expected, bool actual)
        {
            Check(name, expected == actual, "期望 " + expected + " 实际 " + actual);
        }

        static WageSettings Defaults()
        {
            var s = new WageSettings();
            s.MonthlySalary = 8000;
            s.Morning = new Segment("09:00", "12:00");
            s.Afternoon = new Segment("13:30", "18:00");
            return s;
        }

        static DateTime At(int y, int mo, int d, int hh = 0, int mm = 0, int ss = 0)
        {
            return new DateTime(y, mo, d, hh, mm, ss);
        }

        public static int Run()
        {
            WageSettings settings = Defaults();

            // parseHM / segSeconds / totalWorkSeconds
            Eq("parseHM 09:30", 34200, Schedule.ParseHM("09:30").Value);
            Eq("parseHM 9:30", 34200, Schedule.ParseHM("9:30").Value);
            Check("parseHM 24:00 非法", Schedule.ParseHM("24:00") == null, "");
            Check("parseHM ab 非法", Schedule.ParseHM("ab") == null, "");
            Eq("segSeconds", 10800, Schedule.SegSeconds(new Segment("09:00", "12:00")));
            Eq("segSeconds 倒置", 0, Schedule.SegSeconds(new Segment("12:00", "09:00")));
            Eq("totalWorkSeconds", 7.5 * 3600, Schedule.TotalWorkSeconds(settings));

            // 工作日判定
            Eq("周四上班", true, Schedule.IsWorkday(At(2026, 11, 12), settings));
            Eq("周六休息", false, Schedule.IsWorkday(At(2026, 11, 14), settings));
            Eq("周日休息", false, Schedule.IsWorkday(At(2026, 11, 15), settings));
            Eq("元旦照常算班", true, Schedule.IsWorkday(At(2026, 1, 1), settings));

            var withOverride = Defaults();
            withOverride.Overrides["2026-11-12"] = "off";
            withOverride.Overrides["2026-11-14"] = "on";
            Eq("手动标记休息", false, Schedule.IsWorkday(At(2026, 11, 12), withOverride));
            Eq("手动标记上班", true, Schedule.IsWorkday(At(2026, 11, 14), withOverride));

            // 月度工作日数
            Eq("2026-11 工作日", 21, Schedule.CountWorkdays(2026, 10, settings));
            Eq("2026-10 工作日", 22, Schedule.CountWorkdays(2026, 9, settings));
            Eq("2026-01 工作日", 22, Schedule.CountWorkdays(2026, 0, settings));
            Eq("2026-02 工作日", 20, Schedule.CountWorkdays(2026, 1, settings));
            var noDays = Defaults();
            noDays.Workdays = new bool[7];
            Eq("一个都不选", 0, Schedule.CountWorkdays(2026, 1, noDays));

            // 费率
            WageRates r = Schedule.HourlyRate(settings, At(2026, 11, 12, 10, 0));
            Eq("费率 workdays", 21, r.Workdays);
            Eq("日薪", 8000.0 / 21, r.Daily);
            Eq("时薪", 8000.0 / 21 / 7.5, r.Hourly);

            // 状态机
            double total = 27000;
            WageView before = Schedule.Evaluate(At(2026, 11, 12, 8, 30), settings);
            Eq("before state", "before", before.State);
            Eq("before earned", 0.0, before.Earned);
            Eq("before remaining", total, before.RemainingSeconds);
            Eq("before progress", 0.0, before.Progress);

            WageView morning = Schedule.Evaluate(At(2026, 11, 12, 10, 30), settings);
            Eq("morning state", "working", morning.State);
            Eq("morning earnedSeconds", 5400.0, morning.EarnedSeconds);
            Eq("morning earned", 76.1904, morning.Earned);

            WageView lunch = Schedule.Evaluate(At(2026, 11, 12, 12, 30), settings);
            Eq("lunch state", "lunch", lunch.State);
            Eq("lunch earnedSeconds", 10800.0, lunch.EarnedSeconds);
            Eq("lunch earned", 152.3809, lunch.Earned);

            WageView afternoon = Schedule.Evaluate(At(2026, 11, 12, 15, 0), settings);
            Eq("afternoon state", "working", afternoon.State);
            Eq("afternoon earnedSeconds", 16200.0, afternoon.EarnedSeconds);
            Eq("afternoon earned", 228.5714, afternoon.Earned);

            WageView done = Schedule.Evaluate(At(2026, 11, 12, 19, 0), settings);
            Eq("done state", "done", done.State);
            Eq("done progress", 1.0, done.Progress);
            Eq("done earned", Math.Floor((8000.0 / 21) * 10000) / 10000, done.Earned);

            // 周末
            WageView sat = Schedule.Evaluate(At(2026, 11, 14, 10, 0), settings);
            Eq("周六 off", "off", sat.State);
            Eq("周六 earned", 0.0, sat.Earned);
            WageView sun = Schedule.Evaluate(At(2026, 11, 15, 10, 0), settings);
            Eq("周日 off", "off", sun.State);

            // 金额单调、不为负、不超过日薪
            double prev = -1;
            double daily = Math.Floor((8000.0 / 21) * 10000) / 10000;
            bool monotonic = true, bounded = true;
            for (int sec = 0; sec < 86400; sec += 60)
            {
                WageView v = Schedule.Evaluate(At(2026, 11, 12).AddSeconds(sec), settings);
                if (v.Earned < prev - 1e-9) monotonic = false;
                if (v.Earned < 0 || v.Earned > daily + 1e-9) bounded = false;
                prev = v.Earned;
            }
            Check("金额单调不减", monotonic, "");
            Check("金额在 [0, 日薪] 内", bounded, "");

            // 零长度排班不除零
            var broken = Defaults();
            broken.Morning = new Segment("09:00", "09:00");
            broken.Afternoon = new Segment("13:00", "13:00");
            WageView b = Schedule.Evaluate(At(2026, 11, 12, 10, 0), broken);
            Check("零长度不产生 NaN", !double.IsNaN(b.Earned) && !double.IsInfinity(b.Earned), "");
            Eq("零长度 earned", 0.0, b.Earned);

            // nextWorkdayStart 跳过周末
            DateTime? next = Schedule.NextWorkdayStart(At(2026, 11, 13, 18, 30), settings);
            Check("nextWorkdayStart 有值", next != null, "");
            if (next != null)
            {
                Eq("next 年", 2026, next.Value.Year);
                Eq("next 月", 11, next.Value.Month);
                Eq("next 日", 16, next.Value.Day);
                Eq("next 时", 9, next.Value.Hour);
            }

            // 格式化
            Eq("formatClock", "03:45:00", Schedule.FormatClock(3 * 3600 + 45 * 60));
            Eq("formatMoney", "¥1,234.50", Schedule.FormatMoney(1234.5));
            Eq("formatSpan", "1天1小时", Schedule.FormatSpan(86400 + 3600));
            Eq("formatSpan 0", "不到1分", Schedule.FormatSpan(0));

            return _failures.Count;
        }

        public static string Report()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("selftest: " + _pass + " passed, " + _failures.Count + " failed");
            foreach (string f in _failures) sb.AppendLine("  FAIL  " + f);
            return sb.ToString();
        }
    }
}
