// 排班与薪资计算：src/lib/schedule.js 的 C# 移植版。
// 语义必须与 JS 版一致（tests/schedule.test.js 里的断言逐条对应到 SelfTest.cs）。
using System;
using System.Collections.Generic;
using System.Globalization;

namespace WageClock
{
    internal sealed class Segment
    {
        public string Start = "09:00";
        public string End = "18:00";

        public Segment() { }
        public Segment(string start, string end) { Start = start; End = end; }
    }

    internal sealed class WageSettings
    {
        public double MonthlySalary = 8000;
        public Segment Morning = new Segment("09:00", "12:00");
        public Segment Afternoon = new Segment("13:30", "18:00");
        public bool[] Workdays = new bool[] { false, true, true, true, true, true, false }; // 0=周日 … 6=周六
        public Dictionary<string, string> Overrides = new Dictionary<string, string>();

        /// <summary>从持久化的配置字典读出设置。</summary>
        public static WageSettings FromConfig(Store config)
        {
            var s = new WageSettings();
            double salary;
            if (double.TryParse(config.Str("monthlySalary", "8000"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out salary)) s.MonthlySalary = salary;

            s.Morning = ReadSegment(config, "morning", "09:00", "12:00");
            s.Afternoon = ReadSegment(config, "afternoon", "13:30", "18:00");

            object raw;
            if (config.Data.TryGetValue("workdays", out raw))
            {
                var list = raw as System.Collections.IList;
                if (list != null)
                {
                    var days = new bool[7];
                    foreach (object o in list)
                    {
                        try
                        {
                            int d = Convert.ToInt32(o);
                            if (d >= 0 && d <= 6) days[d] = true;
                        }
                        catch { }
                    }
                    s.Workdays = days;
                }
            }

            if (config.Data.TryGetValue("overrides", out raw))
            {
                var map = raw as Dictionary<string, object>;
                if (map != null)
                {
                    foreach (KeyValuePair<string, object> kv in map)
                    {
                        s.Overrides[kv.Key] = Convert.ToString(kv.Value);
                    }
                }
            }
            return s;
        }

        static Segment ReadSegment(Store config, string key, string defStart, string defEnd)
        {
            object raw;
            if (!config.Data.TryGetValue(key, out raw)) return new Segment(defStart, defEnd);
            var map = raw as Dictionary<string, object>;
            if (map == null) return new Segment(defStart, defEnd);
            object a, b;
            string start = map.TryGetValue("start", out a) ? Convert.ToString(a) : defStart;
            string end = map.TryGetValue("end", out b) ? Convert.ToString(b) : defEnd;
            return new Segment(start, end);
        }
        public static readonly string[] WeekdayCN = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };


    }

    internal sealed class WageRates
    {
        public int Workdays;
        public double Daily;
        public double Hourly;
    }

    internal sealed class WageView
    {
        public string State = "off";        // before | working | lunch | done | off
        public string StateLabel = "今日休息";
        public string DateLabel = "";
        public double Earned;
        public double DailyWage;
        public double HourlyWage;
        public int MonthWorkdays;
        public int ElapsedWorkdays;
        public double TotalSeconds;
        public double EarnedSeconds;
        public double RemainingSeconds;
        public double Progress;
        public double OffWorkSeconds;
        public bool WeekendReached;
        public double WeekendSeconds;
    }

    internal static class Schedule
    {
        public static string Pad2(int n)
        {
            return n < 10 ? "0" + n.ToString(CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"09:30" → 34200；非法返回 null。</summary>
        public static int? ParseHM(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string s = text.Trim();
            if (s.Length < 4 || s.Length > 5) return null;
            int colon = s.IndexOf(':');
            if (colon < 1 || colon != s.Length - 3) return null;
            int h, m;
            if (!int.TryParse(s.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return null;
            if (!int.TryParse(s.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return null;
            if (h > 23 || m > 59) return null;
            return h * 3600 + m * 60;
        }

        /// <summary>时段秒数；非法或倒置返回 0。</summary>
        public static int SegSeconds(Segment seg)
        {
            if (seg == null) return 0;
            int? a = ParseHM(seg.Start);
            int? b = ParseHM(seg.End);
            if (a == null || b == null || b.Value <= a.Value) return 0;
            return b.Value - a.Value;
        }

        public static int TotalWorkSeconds(WageSettings s)
        {
            return SegSeconds(s.Morning) + SegSeconds(s.Afternoon);
        }

        public static string FormatClock(double seconds)
        {
            int total = Math.Max(0, (int)Math.Floor(seconds));
            int h = total / 3600, m = (total % 3600) / 60, sec = total % 60;
            return Pad2(h) + ":" + Pad2(m) + ":" + Pad2(sec);
        }

        /// <summary>03:42:15.78 —— 百分秒让倒计时肉眼可见地流动。</summary>
        public static string FormatClockCs(double seconds)
        {
            double t = Math.Max(0, seconds);
            int whole = (int)Math.Floor(t);
            int cs = Math.Min(99, (int)Math.Floor((t - whole) * 100));
            return FormatClock(whole) + "." + Pad2(cs);
        }

        /// <summary>两天以上用「2天 05:12」，一天内用完整时钟。</summary>
        public static string FormatCountdown(double seconds)
        {
            int total = Math.Max(0, (int)Math.Floor(seconds));
            if (total >= 86400)
            {
                int d = total / 86400;
                int h = (total % 86400) / 3600;
                int m = (total % 3600) / 60;
                return d + "天 " + Pad2(h) + ":" + Pad2(m);
            }
            return FormatClock(total);
        }

        public static string FormatSpan(double seconds)
        {
            int total = Math.Max(0, (int)Math.Floor(seconds));
            int d = total / 86400;
            int h = (total % 86400) / 3600;
            int m = (total % 3600) / 60;
            var parts = new List<string>();
            if (d > 0) parts.Add(d + "天");
            if (h > 0) parts.Add(h + "小时");
            if (d == 0 && m > 0) parts.Add(m + "分");
            return parts.Count > 0 ? string.Join("", parts.ToArray()) : "不到1分";
        }

        public static string DateKey(DateTime d)
        {
            return d.Year.ToString(CultureInfo.InvariantCulture) + "-" + Pad2(d.Month) + "-" + Pad2(d.Day);
        }

        public static DateTime StartOfDay(DateTime d)
        {
            return new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, d.Kind);
        }

        public static double SecondsOfDay(DateTime d)
        {
            return d.Hour * 3600 + d.Minute * 60 + d.Second + d.Millisecond / 1000.0;
        }

        public static bool IsWorkday(DateTime d, WageSettings s)
        {
            string key = DateKey(d);
            string value;
            if (s.Overrides.TryGetValue(key, out value))
            {
                return value == "on";
            }
            int dow = (int)d.DayOfWeek;             // 0=周日
            return s.Workdays != null && s.Workdays[dow];
        }

        public static int MonthDays(int year, int month0)
        {
            return DateTime.DaysInMonth(year, month0 + 1);
        }

        public static int CountWorkdays(int year, int month0, WageSettings s)
        {
            int days = MonthDays(year, month0);
            int count = 0;
            for (int day = 1; day <= days; day++)
            {
                if (IsWorkday(new DateTime(year, month0 + 1, day), s)) count++;
            }
            return count;
        }

        public static WageRates HourlyRate(WageSettings s, DateTime when)
        {
            int n = CountWorkdays(when.Year, when.Month - 1, s);
            double hours = TotalWorkSeconds(s) / 3600.0;
            var r = new WageRates { Workdays = n };
            if (n <= 0 || hours <= 0) return r;
            r.Daily = s.MonthlySalary / n;
            r.Hourly = r.Daily / hours;
            return r;
        }

        public static DateTime? NextWorkdayStart(DateTime from, WageSettings s)
        {
            for (int i = 0; i <= 400; i++)
            {
                DateTime d = StartOfDay(from).AddDays(i);
                if (!IsWorkday(d, s)) continue;
                int? start = ParseHM(s.Morning != null ? s.Morning.Start : null);
                if (start == null) continue;
                DateTime candidate = d.AddSeconds(start.Value);
                if (candidate > from) return candidate;
            }
            return null;
        }

        /// <summary>到本周最后一个班次结束的秒数（周一为周首）。</summary>
        public static void WeekendCountdown(DateTime now, WageSettings s, out bool reached, out double seconds)
        {
            int dow = (int)now.DayOfWeek;
            DateTime monday = StartOfDay(now).AddDays(dow == 0 ? -6 : 1 - dow);
            DateTime? last = null;
            for (int i = 6; i >= 0; i--)
            {
                DateTime d = monday.AddDays(i);
                if (IsWorkday(d, s)) { last = d; break; }
            }
            if (last == null) { reached = true; seconds = 0; return; }

            int end = ParseHM(s.Afternoon != null ? s.Afternoon.End : null)
                   ?? ParseHM(s.Morning != null ? s.Morning.End : null)
                   ?? 0;
            DateTime target = last.Value.AddSeconds(end);
            double diff = (target - now).TotalSeconds;
            reached = diff <= 0;
            seconds = diff <= 0 ? 0 : diff;
        }

        public static WageView Evaluate(DateTime now, WageSettings s)
        {
            var view = new WageView();
            double total = TotalWorkSeconds(s);
            WageRates rates = HourlyRate(s, now);
            bool workday = IsWorkday(now, s);
            double nowSec = SecondsOfDay(now);

            view.DateLabel = WageSettings.WeekdayCN[(int)now.DayOfWeek] + " · " + now.Month + "月" + now.Day + "日";
            view.DailyWage = rates.Daily;
            view.HourlyWage = rates.Hourly;
            view.MonthWorkdays = rates.Workdays;
            view.TotalSeconds = total;
            view.RemainingSeconds = total;

            // 本月已经上过的天数（已完整过去或已开始）
            int elapsed = 0;
            for (int day = 1; day < now.Day; day++)
            {
                if (IsWorkday(new DateTime(now.Year, now.Month, day), s)) elapsed++;
            }
            int? morningStart = ParseHM(s.Morning != null ? s.Morning.Start : null);
            if (workday && nowSec >= (morningStart ?? 0)) elapsed++;
            view.ElapsedWorkdays = elapsed;

            var segs = new List<Segment>();
            if (SegSeconds(s.Morning) > 0) segs.Add(s.Morning);
            if (SegSeconds(s.Afternoon) > 0) segs.Add(s.Afternoon);

            bool weekendReached;
            double weekendSeconds;
            WeekendCountdown(now, s, out weekendReached, out weekendSeconds);
            view.WeekendReached = weekendReached;
            view.WeekendSeconds = weekendSeconds;

            if (!workday || segs.Count == 0)
            {
                view.State = "off";
                view.StateLabel = workday ? "未排班" : "今日休息";
                view.Earned = 0;
                view.EarnedSeconds = 0;
                view.Progress = 0;
                view.RemainingSeconds = total;
                view.OffWorkSeconds = 0;
                return view;
            }

            int mStart = ParseHM(segs[0].Start).Value;
            int mEnd = ParseHM(segs[0].End).Value;
            int? aStart = segs.Count > 1 ? ParseHM(segs[1].Start) : (int?)null;
            int? aEnd = segs.Count > 1 ? ParseHM(segs[1].End) : (int?)null;

            string state;
            double earned;

            if (nowSec < mStart)
            {
                state = "before";
                earned = 0;
            }
            else if (nowSec < mEnd)
            {
                state = "working";
                earned = nowSec - mStart;
            }
            else if (aStart != null && nowSec < aStart.Value)
            {
                state = "lunch";
                earned = mEnd - mStart;
            }
            else if (aStart != null && aEnd != null && nowSec < aEnd.Value)
            {
                state = "working";
                earned = (mEnd - mStart) + (nowSec - aStart.Value);
            }
            else
            {
                state = "done";
                earned = total;
            }

            earned = Math.Max(0, Math.Min(total, earned));
            double progress = total > 0 ? earned / total : 0;
            view.State = state;
            view.StateLabel = state == "before" ? "待开工"
                            : state == "working" ? "工作中"
                            : state == "lunch" ? "午休" : "已下班";
            view.EarnedSeconds = earned;
            view.Progress = progress;
            // 向下取整到万分之一：不显示还没赚到的钱
            view.Earned = Math.Floor(rates.Daily * progress * 10000 + 1e-6) / 10000;
            view.RemainingSeconds = Math.Max(0, total - earned);

            int endOfDay = (aEnd != null) ? aEnd.Value : mEnd;
            view.OffWorkSeconds = Math.Max(0, endOfDay - nowSec);
            return view;
        }

        /// <summary>1234.5 → "¥1,234.50"</summary>
        public static string FormatMoney(double n)
        {
            double v = double.IsNaN(n) || double.IsInfinity(n) ? 0 : n;
            return "¥" + v.ToString("N2", CultureInfo.GetCultureInfo("zh-CN"));
        }

        public static string FormatAmount(double n)
        {
            double v = double.IsNaN(n) || double.IsInfinity(n) ? 0 : n;
            return v.ToString("N2", CultureInfo.GetCultureInfo("zh-CN"));
        }
    }
}
