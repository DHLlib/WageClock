'use strict';

/* Wage Clock · settings renderer */

const $ = (id) => document.getElementById(id);
const media = window.matchMedia('(prefers-color-scheme: light)');

let themeMode = 'auto';
let autoLaunchError = null;
let closeAction = 'auto';
let trayAvailable = true;
let themeAtOpen = 'auto';
let firstHydrate = true;
let preferredDays = [1, 2, 3, 4, 5];
let previewTimer = null;
let isValid = true;

/* ------------------------------------------------------------------ */
/* helpers                                                             */
/* ------------------------------------------------------------------ */

function applyTheme() {
  const resolved = themeMode === 'auto' ? (media.matches ? 'light' : 'dark') : themeMode;
  document.body.className = resolved;
  document.querySelectorAll('#theme button').forEach((b) => {
    b.classList.toggle('on', b.dataset.theme === themeMode);
  });
}

function parseHM(v) {
  const m = /^(\d{1,2}):(\d{2})$/.exec(String(v || '').trim());
  if (!m) return null;
  const h = Number(m[1]);
  const min = Number(m[2]);
  if (h > 23 || min > 59) return null;
  return h * 3600 + min * 60;
}

function durLabel(a, b) {
  const sec = (parseHM(b) ?? 0) - (parseHM(a) ?? 0);
  if (!(sec > 0)) return '0 h';
  return `${(sec / 3600).toFixed(1)} h`;
}

function money(n) {
  const v = Number.isFinite(n) ? n : 0;
  return '¥' + v.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

/* ------------------------------------------------------------------ */
/* collect / validate                                                  */
/* ------------------------------------------------------------------ */

function collect() {
  return {
    monthlySalary: Number($('salary').value),
    morning: { start: $('amStart').value, end: $('amEnd').value },
    afternoon: { start: $('pmStart').value, end: $('pmEnd').value },
    workdays: preferredDays.slice().sort(),
    theme: themeMode,
    closeAction,
    alwaysOnTop: $('alwaysOnTop').checked,
    autoLaunch: $('autoLaunch').checked,
  };
}

function validate(c) {
  const err = $('err');
  err.textContent = '';
  isValid = true;

  if (!Number.isFinite(c.monthlySalary) || c.monthlySalary <= 0) {
    err.textContent = '月薪需要大于 0';
    return invalid();
  }
  const am = [parseHM(c.morning.start), parseHM(c.morning.end)];
  const pm = [parseHM(c.afternoon.start), parseHM(c.afternoon.end)];
  if (am[0] == null || am[1] == null) { err.textContent = '上午时间不完整'; return invalid(); }
  if (am[1] <= am[0]) { err.textContent = '上午结束时间需晚于开始时间'; return invalid(); }
  if (pm[0] == null || pm[1] == null) { err.textContent = '下午时间不完整'; return invalid(); }
  if (pm[1] <= pm[0]) { err.textContent = '下午结束时间需晚于开始时间'; return invalid(); }
  if (pm[0] < am[1]) { err.textContent = '下午开始时间不能早于上午结束时间'; return invalid(); }
  if (!c.workdays.length) { err.textContent = '至少选择一个工作日'; return invalid(); }

  function invalid() {
    isValid = false;
    $('save').disabled = true;
    return false;
  }
  $('save').disabled = false;
  return true;
}

/* ------------------------------------------------------------------ */
/* live preview                                                        */
/* ------------------------------------------------------------------ */

function refreshDurations() {
  $('amDur').textContent = durLabel($('amStart').value, $('amEnd').value);
  $('pmDur').textContent = durLabel($('pmStart').value, $('pmEnd').value);
  const total = (parseHM($('amEnd').value) ?? 0) - (parseHM($('amStart').value) ?? 0)
              + (parseHM($('pmEnd').value) ?? 0) - (parseHM($('pmStart').value) ?? 0);
  $('totalDur').textContent = total > 0 ? (total / 3600).toFixed(1) : '0';
}

function schedulePreview() {
  refreshDurations();
  clearTimeout(previewTimer);
  previewTimer = setTimeout(runPreview, 120);
}

async function runPreview() {
  const c = collect();
  if (!validate(c)) return;
  try {
    const p = await window.api.preview(c);
    $('pvDays').textContent = `${p.workdays} 天`;
    $('pvDaily').textContent = money(p.daily);
    $('pvHourly').textContent = money(p.hourly);
  } catch (e) {
    /* preview is best-effort */
  }
}

/* ------------------------------------------------------------------ */
/* hydrate                                                             */
/* ------------------------------------------------------------------ */

function paintDays() {
  document.querySelectorAll('#days button').forEach((b) => {
    b.classList.toggle('on', preferredDays.includes(Number(b.dataset.day)));
  });
}

function hydrate(settings) {
  $('salary').value = settings.monthlySalary;
  $('amStart').value = settings.morning.start;
  $('amEnd').value = settings.morning.end;
  $('pmStart').value = settings.afternoon.start;
  $('pmEnd').value = settings.afternoon.end;
  preferredDays = settings.workdays.slice();
  paintDays();
  $('alwaysOnTop').checked = !!settings.alwaysOnTop;
  $('autoLaunch').checked = !!settings.autoLaunch;
  themeMode = settings.theme || 'auto';
  if (firstHydrate) { themeAtOpen = themeMode; firstHydrate = false; }
  closeAction = settings.closeAction || 'auto';
  applyTheme();
  paintCloseHint();
  schedulePreview();
}

function paintCloseHint() {
  const el = closeHint;
  if (el) {
    const autoOn = autoLaunch.checked;
    if (closeAction === 'tray') {
      el.textContent = '点 × 收进托盘，进程继续跑；退出请用托盘右键菜单。';
    } else if (closeAction === 'quit') {
      el.textContent = '点 × 直接退出程序，托盘图标也会一起消失。';
    } else if (autoOn) {
      el.textContent = '跟随开机自启：已开启 → 点 × 收进托盘。';
    } else {
      el.textContent = '跟随开机自启：未开启 → 点 × 直接退出。';
    }
  }
  document.querySelectorAll('#closeAction button').forEach((b) => {
    b.classList.toggle('on', b.dataset.close === closeAction);
  });
}

function paintTrayNote() {
  const el = $('trayNote');
  if (!el) return;
  if (trayAvailable) {
    el.style.display = 'none';
    return;
  }
  el.textContent = '当前进程以低完整性运行（沙箱或受限启动），Windows 不允许注册托盘图标：本实例不会收进托盘，点 × 会直接退出。想正常使用托盘请直接双击运行程序。';
  el.style.display = '';
}

function paintAutoLaunchError() {
  const el = $('autoLaunchNote');
  if (!el) return;
  if (autoLaunchError) {
    el.textContent = autoLaunchError;
    el.style.display = '';
  } else {
    el.style.display = 'none';
  }
}


/* ------------------------------------------------------------------ */
/* wire-up                                                             */
/* ------------------------------------------------------------------ */

['salary', 'amStart', 'amEnd', 'pmStart', 'pmEnd'].forEach((id) => {
  $(id).addEventListener('input', schedulePreview);
  $(id).addEventListener('change', schedulePreview);
});

document.querySelectorAll('#days button').forEach((b) => {
  b.addEventListener('click', () => {
    const day = Number(b.dataset.day);
    if (preferredDays.includes(day)) preferredDays = preferredDays.filter((d) => d !== day);
    else preferredDays.push(day);
    paintDays();
    schedulePreview();
  });
});

document.querySelectorAll('#theme button').forEach((b) => {
  b.addEventListener('click', () => {
    themeMode = b.dataset.theme;
    applyTheme();
    window.api.setTheme(themeMode); // theme is a display preference: apply it immediately
    schedulePreview();
  });
});

['alwaysOnTop', 'autoLaunch'].forEach((id) => {
  $(id).addEventListener('change', schedulePreview);
});


$('autoLaunch').addEventListener('change', paintCloseHint);

document.querySelectorAll('#closeAction button').forEach((b) => {
  b.addEventListener('click', () => {
    closeAction = b.dataset.close;
    paintCloseHint();
  });
});

$('save').addEventListener('click', async () => {
  const c = collect();
  if (!validate(c)) return;
  await window.api.save(c);
  window.api.closeSettings();
});

$('cancel').addEventListener('click', async () => {
  if (themeMode !== themeAtOpen) window.api.setTheme(themeAtOpen); // theme previews live, so undo it
  window.api.cancelled();
  window.api.closeSettings();
});

$('close').addEventListener('click', async () => {
  if (themeMode !== themeAtOpen) window.api.setTheme(themeAtOpen);
  window.api.cancelled();
  window.api.closeSettings();
});

window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    if (themeMode !== themeAtOpen) window.api.setTheme(themeAtOpen);
    window.api.cancelled();
    window.api.closeSettings();
  }
  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
    const btn = $('save');
    if (!btn.disabled) btn.click();
  }
});

media.addEventListener('change', () => { if (themeMode === 'auto') applyTheme(); });

// re-hydrate whenever the host broadcasts settings (save, or cancel restoring the stored values)
window.api.onSettings(({ settings: fresh, autoLaunchError: err }) => {
  if (!fresh) return;
  hydrate(fresh);
  autoLaunchError = err || null;
  paintAutoLaunchError();
  schedulePreview();
});

window.api.init().then(({ settings, autoLaunchError: err, trayAvailable: trayOk }) => {
  autoLaunchError = err || null;
  trayAvailable = trayOk !== false;
  paintTrayNote();
  hydrate(settings);
  paintAutoLaunchError();
  window.api.ready();
});