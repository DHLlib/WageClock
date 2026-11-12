'use strict';

const test = require('node:test');
const assert = require('node:assert');
const S = require('../src/lib/schedule.js');
const holidays = require('../src/lib/holidays.json');

const settings = Object.assign({}, S.DEFAULT_SETTINGS, { monthlySalary: 8000 });

const at = (y, m, d, hh = 0, mm = 0, ss = 0) => new Date(y, m - 1, d, hh, mm, ss);

test('parseHM / segSeconds / totalWorkSeconds', () => {
  assert.equal(S.parseHM('09:30'), 34200);
  assert.equal(S.parseHM('9:30'), 34200);
  assert.equal(S.parseHM('24:00'), null);
  assert.equal(S.parseHM('ab'), null);
  assert.equal(S.segSeconds({ start: '09:00', end: '12:00' }), 10800);
  assert.equal(S.segSeconds({ start: '12:00', end: '09:00' }), 0, 'inverted segment is invalid');
  assert.equal(S.totalWorkSeconds(settings), 7.5 * 3600);
});

test('workday detection honours 2026 statutory holidays and make-up days', () => {
  assert.equal(S.isWorkday(at(2026, 1, 1), settings, holidays), false, '元旦');
  assert.equal(S.isWorkday(at(2026, 1, 4), settings, holidays), true, '周日补班');
  assert.equal(S.isWorkday(at(2026, 2, 17), settings, holidays), false, '春节');
  assert.equal(S.isWorkday(at(2026, 2, 14), settings, holidays), true, '周六补班');
  assert.equal(S.isWorkday(at(2026, 11, 12), settings, holidays), true, '普通周四');
  assert.equal(S.isWorkday(at(2026, 11, 14), settings, holidays), false, '普通周六');
  // 关闭节假日表后只看星期
  const noHoliday = Object.assign({}, settings, { useHolidays: false });
  assert.equal(S.isWorkday(at(2026, 1, 1), noHoliday, holidays), true);
  // 手动例外优先级最高
  const withOverride = Object.assign({}, settings, { overrides: { '2026-11-12': 'off', '2026-11-14': 'on' } });
  assert.equal(S.isWorkday(at(2026, 11, 12), withOverride, holidays), false);
  assert.equal(S.isWorkday(at(2026, 11, 14), withOverride, holidays), true);
});

test('monthly workday counts', () => {
  assert.equal(S.countWorkdays(2026, 10, settings, holidays), 21, '2026-11');
  assert.equal(S.countWorkdays(2026, 9, settings, holidays), 18, '2026-10 含国庆 7 天 + 10 月 10 日补班');
  assert.equal(S.countWorkdays(2026, 0, settings, holidays), 21, '2026-01 元旦 + 1 月 4 日补班');
  assert.equal(S.countWorkdays(2026, 1, settings, holidays), 16, '2026-02 春节 9 天 + 两个补班周六');
});

test('rates: daily / hourly', () => {
  const r = S.hourlyRate(settings, holidays, at(2026, 11, 12, 10, 0));
  assert.equal(r.workdays, 21);
  assert.ok(Math.abs(r.daily - 8000 / 21) < 1e-9);
  assert.ok(Math.abs(r.hourly - 8000 / 21 / 7.5) < 1e-9);
});

test('evaluate: state machine across one working day', () => {
  const d = (hh, mm) => S.evaluate(at(2026, 11, 12, hh, mm), settings, holidays);
  const total = 27000;

  const before = d(8, 30);
  assert.equal(before.state, 'before');
  assert.equal(before.earned, 0);
  assert.equal(before.remainingSeconds, total);
  assert.equal(before.progress, 0);

  const morning = d(10, 30);
  assert.equal(morning.state, 'working');
  assert.equal(morning.earnedSeconds, 5400);
  assert.equal(morning.earned, 76.1904, 'floor 到万分之一');

  const lunch = d(12, 30);
  assert.equal(lunch.state, 'lunch');
  assert.equal(lunch.earnedSeconds, 10800);
  assert.equal(lunch.earned, 152.3809);
  assert.equal(lunch.primaryValue, S.formatClock(27000 - 10800), '午休时剩余工时冻结');

  const afternoon = d(15, 0);
  assert.equal(afternoon.state, 'working');
  assert.equal(afternoon.earnedSeconds, 16200);
  assert.equal(afternoon.earned, 228.5714);

  const done = d(19, 0);
  assert.equal(done.state, 'done');
  assert.equal(done.progress, 1);
  assert.equal(done.earned, Math.floor((8000 / 21) * 10000) / 10000);
  assert.match(done.primaryLabel, /下次上班/);
});

test('evaluate: non-working day and make-up Saturday', () => {
  const sat = S.evaluate(at(2026, 11, 14, 10, 0), settings, holidays);
  assert.equal(sat.state, 'off');
  assert.equal(sat.earned, 0);
  assert.equal(sat.primaryValue, '周一 09:00');

  const makeup = S.evaluate(at(2026, 2, 14, 10, 0), settings, holidays);
  assert.equal(makeup.state, 'working', '2 月 14 日周六补班照常算钱');
});

test('money is monotonic, never negative, never above daily wage', () => {
  let prev = -1;
  const daily = Math.floor((8000 / 21) * 10000) / 10000;
  for (let sec = 0; sec < 86400; sec += 60) {
    const now = at(2026, 11, 12, 0, 0, sec);
    const v = S.evaluate(now, settings, holidays);
    assert.ok(v.earned >= 0, 'non-negative');
    assert.ok(v.earned <= daily + 1e-9, 'never above daily wage');
    assert.ok(v.earned >= prev, `monotonic at ${sec}s`);
    prev = v.earned;
  }
});

test('broken schedule (zero length) does not divide by zero', () => {
  const broken = Object.assign({}, settings, {
    morning: { start: '09:00', end: '09:00' },
    afternoon: { start: '13:00', end: '13:00' },
  });
  const v = S.evaluate(at(2026, 11, 12, 10, 0), broken, holidays);
  assert.ok(Number.isFinite(v.earned));
  assert.equal(v.earned, 0);
});

test('nextWorkdayStart skips weekend', () => {
  const next = S.nextWorkdayStart(at(2026, 11, 13, 18, 30), settings, holidays);
  assert.equal(next.getFullYear(), 2026);
  assert.equal(next.getMonth(), 10);
  assert.equal(next.getDate(), 16);
  assert.equal(next.getHours(), 9);
});

test('formatters', () => {
  assert.equal(S.formatClock(3 * 3600 + 45 * 60), '03:45:00');
  assert.equal(S.formatMoney(1234.5), '¥1,234.50');
  assert.equal(S.formatSpan(86400 + 3600), '1天1小时');
  assert.equal(S.formatSpan(0), '不到1分');
});
