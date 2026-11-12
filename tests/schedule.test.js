'use strict';

const test = require('node:test');
const assert = require('node:assert');
const S = require('../src/lib/schedule.js');

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

test('workday detection follows the weekday set and manual overrides', () => {
  assert.equal(S.isWorkday(at(2026, 11, 12), settings), true, '普通周四');
  assert.equal(S.isWorkday(at(2026, 11, 14), settings), false, '普通周六');
  assert.equal(S.isWorkday(at(2026, 11, 15), settings), false, '普通周日');
  assert.equal(S.isWorkday(at(2026, 1, 1), settings), true, '元旦也是周四，按带薪假口径照常上班');

  const withOverride = Object.assign({}, settings, { overrides: { '2026-11-12': 'off', '2026-11-14': 'on' } });
  assert.equal(S.isWorkday(at(2026, 11, 12), withOverride), false, '手动标记为休息');
  assert.equal(S.isWorkday(at(2026, 11, 14), withOverride), true, '手动标记为上班');
});
test('monthly workday counts', () => {
  assert.equal(S.countWorkdays(2026, 10, settings), 21, '2026-11');
  assert.equal(S.countWorkdays(2026, 9, settings), 22, '2026-10 有 22 个工作日');
  assert.equal(S.countWorkdays(2026, 0, settings), 22, '2026-01 有 22 个工作日');
  assert.equal(S.countWorkdays(2026, 1, settings), 20, '2026-02 有 20 个工作日');
  assert.equal(S.countWorkdays(2026, 1, { workdays: [] }), 0, '一个工作日都不选时返回 0');
});

test('rates: daily / hourly', () => {
  const r = S.hourlyRate(settings, at(2026, 11, 12, 10, 0));
  assert.equal(r.workdays, 21);
  assert.ok(Math.abs(r.daily - 8000 / 21) < 1e-9);
  assert.ok(Math.abs(r.hourly - 8000 / 21 / 7.5) < 1e-9);
});

test('evaluate: state machine across one working day', () => {
  const d = (hh, mm) => S.evaluate(at(2026, 11, 12, hh, mm), settings);
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

test('evaluate: weekend stays off', () => {
  const sat = S.evaluate(at(2026, 11, 14, 10, 0), settings);
  assert.equal(sat.state, 'off');
  assert.equal(sat.earned, 0);
  assert.equal(sat.primaryValue, '周一 09:00');

  const sun = S.evaluate(at(2026, 11, 15, 10, 0), settings);
  assert.equal(sun.state, 'off', '周日同样是休息日');
  assert.equal(sun.earned, 0);
});

test('money is monotonic, never negative, never above daily wage', () => {
  let prev = -1;
  const daily = Math.floor((8000 / 21) * 10000) / 10000;
  for (let sec = 0; sec < 86400; sec += 60) {
    const now = at(2026, 11, 12, 0, 0, sec);
    const v = S.evaluate(now, settings);
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
  const v = S.evaluate(at(2026, 11, 12, 10, 0), broken);
  assert.ok(Number.isFinite(v.earned));
  assert.equal(v.earned, 0);
});

test('nextWorkdayStart skips weekend', () => {
  const next = S.nextWorkdayStart(at(2026, 11, 13, 18, 30), settings);
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
