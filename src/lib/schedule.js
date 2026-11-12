'use strict';

/**
 * Pure scheduling / wage math. No Electron, no DOM — unit-testable.
 *
 * Coordinate system: "seconds since local midnight" for a single day.
 * A day has two paid segments (morning, afternoon); the gap between them is unpaid.
 */

const DEFAULT_SETTINGS = {
  monthlySalary: 8000,
  morning: { start: '09:00', end: '12:00' },
  afternoon: { start: '13:30', end: '18:00' },
  workdays: [1, 2, 3, 4, 5], // 0=Sunday … 6=Saturday
  
  overrides: {}, // 'YYYY-MM-DD' -> 'off' | 'on'
  theme: 'auto',
  closeAction: 'auto',
  alwaysOnTop: true,
  opacity: 1,
  autoLaunch: false,
};

const pad2 = (n) => String(n).padStart(2, '0');

function toHM(seconds) {
  const s = Math.max(0, Math.floor(seconds));
  return { h: Math.floor(s / 3600), m: Math.floor((s % 3600) / 60), s: s % 60 };
}

/** 3720 -> "01:02:00" */
function formatClock(seconds) {
  const { h, m, s } = toHM(seconds);
  return `${pad2(h)}:${pad2(m)}:${pad2(s)}`;
}

/** 03:42:15.78 — the hundredths make the countdown visibly flow */
function formatClockCs(seconds) {
  const t = Math.max(0, seconds);
  const whole = Math.floor(t);
  const cs = Math.min(99, Math.floor((t - whole) * 100));
  const { h, m, s } = toHM(whole);
  return `${pad2(h)}:${pad2(m)}:${pad2(s)}.${pad2(cs)}`;
}

/** 2d 05:12 for long spans, 05:12:33 under a day */
function formatCountdown(seconds) {
  const total = Math.max(0, Math.floor(seconds));
  if (total >= 86400) {
    const d = Math.floor(total / 86400);
    const h = Math.floor((total % 86400) / 3600);
    const m = Math.floor((total % 3600) / 60);
    return `${d}天 ${pad2(h)}:${pad2(m)}`;
  }
  return formatClock(total);
}

/** Seconds until the week's last shift ends (Friday 18:00 for a Mon-Fri week). */
function weekendCountdown(now, settings) {
  const dayKey = dateKey(now);
  if (_weekendMemo.settings === settings && _weekendMemo.dayKey === dayKey) {
    return _weekendMemo.result;
  }
  const dow = now.getDay();
  const monday = addDays(now, dow === 0 ? -6 : 1 - dow);
  let last = null;
  for (let i = 6; i >= 0; i--) {
    const d = addDays(monday, i);
    if (isWorkday(d, settings)) { last = d; break; }
  }
  if (!last) return { reached: true, seconds: 0 };
  const end = parseHM(settings.afternoon && settings.afternoon.end) || parseHM(settings.morning && settings.morning.end) || 0;
  const target = new Date(last.getTime() + end * 1000);
  const diff = (target.getTime() - now.getTime()) / 1000;
  const result = diff <= 0 ? { reached: true, seconds: 0 } : { reached: false, seconds: diff };
  _weekendMemo = { settings, dayKey, result };
  return result;
}

function formatSpan(seconds) {
  const total = Math.max(0, Math.floor(seconds));
  const d = Math.floor(total / 86400);
  const h = Math.floor((total % 86400) / 3600);
  const m = Math.floor((total % 3600) / 60);
  const parts = [];
  if (d) parts.push(`${d}天`);
  if (h) parts.push(`${h}小时`);
  if (!d && m) parts.push(`${m}分`);
  return parts.length ? parts.join('') : '不到1分';
}

/** "09:30" -> 34200 ; invalid -> null */
function parseHM(str) {
  const m = /^(\d{1,2}):(\d{2})$/.exec(String(str || '').trim());
  if (!m) return null;
  const h = Number(m[1]);
  const min = Number(m[2]);
  if (h > 23 || min > 59) return null;
  return h * 3600 + min * 60;
}

function dateKey(d) {
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
}

function startOfDay(d) {
  const x = new Date(d);
  x.setHours(0, 0, 0, 0);
  return x;
}

function secondsOfDay(d) {
  return d.getHours() * 3600 + d.getMinutes() * 60 + d.getSeconds();
}

function addDays(d, n) {
  const x = startOfDay(d);
  x.setDate(x.getDate() + n);
  return x;
}

/** Segment length in seconds; invalid/negative -> 0 */
function segSeconds(seg) {
  if (!seg) return 0;
  const a = parseHM(seg.start);
  const b = parseHM(seg.end);
  if (a == null || b == null || b <= a) return 0;
  return b - a;
}

function totalWorkSeconds(settings) {
  return segSeconds(settings.morning) + segSeconds(settings.afternoon);
}

function segments(settings) {
  const out = [];
  const push = (seg, kind) => {
    const a = parseHM(seg && seg.start);
    const b = parseHM(seg && seg.end);
    if (a == null || b == null || b <= a) return;
    out.push({ kind, start: a, end: b });
  };
  push(settings.morning, 'work');
  push(settings.afternoon, 'work');
  return out;
}

/** Is this calendar day a working day? 法定假不带薪口径，只按星期与手动覆盖。 */
function isWorkday(d, settings) {
  const key = dateKey(d);
  if (settings.overrides && settings.overrides[key]) {
    return settings.overrides[key] === 'on';
  }
  return (settings.workdays || []).includes(d.getDay());
}

function monthDays(year, month /* 0-based */) {
  return new Date(year, month + 1, 0).getDate();
}

function countWorkdays(year, month, settings) {
  const n = monthDays(year, month);
  let c = 0;
  for (let day = 1; day <= n; day++) {
    if (isWorkday(new Date(year, month, day), settings)) c++;
  }
  return c;
}

// evaluate() runs every animation frame, so the per-month and per-day lookups are memoised
// on the settings object identity (a save replaces the object) plus the month/day key.
let _rateMemo = { settings: null, monthKey: '', result: null };
let _weekendMemo = { settings: null, dayKey: '', result: null };

function hourlyRate(settings, when) {
  const d = when || new Date();
  const monthKey = d.getFullYear() + '-' + d.getMonth();
  if (_rateMemo.settings === settings && _rateMemo.monthKey === monthKey) return _rateMemo.result;

  const n = countWorkdays(d.getFullYear(), d.getMonth(), settings);
  const hours = totalWorkSeconds(settings) / 3600;
  let result;
  if (n <= 0 || hours <= 0) {
    result = { workdays: n, daily: 0, hourly: 0 };
  } else {
    const daily = Number(settings.monthlySalary || 0) / n;
    result = { workdays: n, daily, hourly: daily / hours };
  }
  _rateMemo = { settings, monthKey, result };
  return result;
}

const WEEKDAY_CN = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];

/** Next working day whose morning start is after `from` (strictly next day when today is already done). */
function nextWorkdayStart(from, settings) {
  for (let i = 0; i <= 400; i++) {
    const d = addDays(from, i);
    if (!isWorkday(d, settings)) continue;
    const start = parseHM(settings.morning.start);
    if (start == null) continue;
    const at = new Date(d);
    at.setHours(0, 0, 0, 0);
    const candidate = new Date(at.getTime() + start * 1000);
    if (candidate.getTime() > from.getTime()) return candidate;
  }
  return null;
}

/**
 * Full view model for one instant.
 * @returns {{
 *   state:'before'|'working'|'lunch'|'done'|'off',
 *   stateLabel:string, dateLabel:string, dayOfMonth:number,
 *   earned:number, dailyWage:number, hourlyWage:number, monthWorkdays:number, elapsedWorkdays:number,
 *   totalSeconds:number, earnedSeconds:number, remainingSeconds:number, progress:number,
 *   primaryLabel:string, primaryValue:string, secondaryLabel:string, secondaryValue:string,
 *   segmentList:{kind:string,ratio:number}[], nowRatio:number
 * }}
 */
function evaluate(now, settings) {
  const s = Object.assign({}, DEFAULT_SETTINGS, settings || {});
  const total = totalWorkSeconds(s);
  const rates = hourlyRate(s, now);
  const workday = isWorkday(now, s);
  const nowSec = secondsOfDay(now) + now.getMilliseconds() / 1000;
  const segs = segments(s);

  const base = {
    dateLabel: `${WEEKDAY_CN[now.getDay()]} · ${now.getMonth() + 1}月${now.getDate()}日`,
    dayOfMonth: now.getDate(),
    dailyWage: rates.daily,
    hourlyWage: rates.hourly,
    monthWorkdays: rates.workdays,
    totalSeconds: total,
    earnedSeconds: 0,
    remainingSeconds: total,
    progress: 0,
    nowRatio: 0,
  };

  // How many workdays of this month are already "attended" (fully elapsed or started).
  const elapsed = (() => {
    let c = 0;
    for (let day = 1; day < now.getDate(); day++) {
      if (isWorkday(new Date(now.getFullYear(), now.getMonth(), day), s)) c++;
    }
    if (workday && nowSec >= (parseHM(s.morning.start) || 0)) c++;
    return c;
  })();

  if (!workday || segs.length === 0) {
    const next = workday ? null : nextWorkdayStart(addDays(now, 1), s);
    const gap = next ? Math.max(0, (next.getTime() - now.getTime()) / 1000) : 0;
    return Object.assign(base, {
      state: 'off',
      stateLabel: workday ? '未排班' : '今日休息',
      earned: 0,
      earnedSeconds: 0,
      progress: 0,
      nowRatio: 0,
      elapsedWorkdays: elapsed,
      primaryLabel: '下次上班',
      primaryValue: next
        ? `${WEEKDAY_CN[next.getDay()]} ${pad2(next.getHours())}:${pad2(next.getMinutes())}`
        : '—',
      secondaryLabel: '距下次上班',
      secondaryValue: next ? formatSpan(gap) : '—',
      segmentList: [],
      offWorkSeconds: null,
      weekendReached: weekendCountdown(now, s).reached,
      weekendSeconds: weekendCountdown(now, s).seconds,
    });
  }

  const [m] = segs;
  const a = segs[1];
  let state;
  let earned;
  let next;

  if (nowSec < m.start) {
    state = 'before';
    earned = 0;
    next = { at: new Date(startOfDay(now).getTime() + m.start * 1000), label: '距上班' };
  } else if (nowSec < m.end) {
    state = 'working';
    earned = nowSec - m.start;
    next = { at: new Date(startOfDay(now).getTime() + m.end * 1000), label: '距午休' };
  } else if (a && nowSec < a.start) {
    state = 'lunch';
    earned = m.end - m.start;
    next = { at: new Date(startOfDay(now).getTime() + a.start * 1000), label: '距下午上班' };
  } else if (a && nowSec < a.end) {
    state = 'working';
    earned = m.end - m.start + (nowSec - a.start);
    next = { at: new Date(startOfDay(now).getTime() + a.end * 1000), label: '距下班' };
  } else {
    state = 'done';
    earned = total;
    next = { at: new Date(startOfDay(now).getTime() + (a ? a.end : m.end) * 1000), label: '已下班' };
  }

  // Never let a broken config produce negative values.
  earned = Math.max(0, Math.min(total, earned));
  const progress = total > 0 ? earned / total : 0;
  // floor to cents: never show money that has not been earned yet
  const money = Math.floor(rates.daily * progress * 10000 + 1e-6) / 10000;
  const remaining = Math.max(0, total - earned);
  const untilNext = Math.max(0, (next.at.getTime() - now.getTime()) / 1000);
  const hhmm = `${pad2(next.at.getHours())}:${pad2(next.at.getMinutes())}`;

  const stateLabel = { before: '待开工', working: '工作中', lunch: '午休', done: '已下班' }[state];
  const endOfDay = a ? a.end : m.end;
  const secToOffWork = Math.max(0, endOfDay - nowSec);
  const weekend = weekendCountdown(now, s);

  const view = Object.assign(base, {
    state,
    stateLabel,
    earnedSeconds: earned,
    remainingSeconds: remaining,
    progress,
    earned: money,
    elapsedWorkdays: elapsed,
    secondaryLabel: state === 'working' ? '已工作' : next.label,
    secondaryValue: state === 'working' ? formatClock(earned) : formatClock(untilNext),
    segmentList: segs.map((x) => ({ kind: x.kind, ratio: total > 0 ? (x.end - x.start) / total : 0 })),
    nowRatio: progress,
    offWorkSeconds: secToOffWork,
    weekendReached: weekend.reached,
    weekendSeconds: weekend.seconds,
  });

  if (state === 'done') {
    const after = nextWorkdayStart(addDays(now, 1), s);
    const gap = after ? Math.max(0, (after.getTime() - now.getTime()) / 1000) : 0;
    view.primaryLabel = '下次上班';
    view.primaryValue = after
      ? `${WEEKDAY_CN[after.getDay()]} ${pad2(after.getHours())}:${pad2(after.getMinutes())}`
      : '—';
    view.secondaryLabel = '距下次上班';
    view.secondaryValue = formatSpan(gap);
  } else {
    view.primaryLabel = '剩余工时';
    view.primaryValue = formatClock(remaining);
  }

  return view;
}

/** Money for labels: 1234.5 -> "¥1,234.50" */
function formatMoney(n) {
  const v = Number.isFinite(n) ? n : 0;
  return '¥' + v.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

/** Plain money without symbol: 1234.5 -> "1,234.50" */
function formatAmount(n) {
  const v = Number.isFinite(n) ? n : 0;
  return v.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

const API = {
  DEFAULT_SETTINGS,
  parseHM,
  segSeconds,
  totalWorkSeconds,
  segments,
  isWorkday,
  countWorkdays,
  hourlyRate,
  monthDays,
  nextWorkdayStart,
  evaluate,
  formatClock,
  formatClockCs,
  formatCountdown,
  weekendCountdown,
  formatSpan,
  formatMoney,
  formatAmount,
  dateKey,
  pad2,
  WEEKDAY_CN,
};

if (typeof module !== 'undefined' && module.exports) module.exports = API;
if (typeof window !== 'undefined') window.WageSchedule = API;