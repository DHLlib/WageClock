'use strict';

/* Bridge between the renderer and the WinForms + WebView2 host.

   The same pages also run in a plain browser (dev preview): when the WebView2
   bridge is absent, settings fall back to localStorage and every value is
   computed locally, so nothing breaks. */

(function () {
  const wv = window.chrome && window.chrome.webview ? window.chrome.webview : null;
  const S = window.WageSchedule;
  const LS_KEY = 'wage-clock.settings.v1';
  const HOLIDAYS_URL = '/lib/holidays.json';

  // surface renderer errors in the host log: WebView2 has no console here
  window.addEventListener('error', (ev) => {
    if (wv) wv.postMessage({ action: 'js-error', payload: { message: String(ev.message), source: ev.filename, line: ev.lineno } });
  });
  window.addEventListener('unhandledrejection', (ev) => {
    if (wv) wv.postMessage({ action: 'js-error', payload: { message: 'unhandled rejection: ' + String(ev.reason) } });
  });

  let holidays = {};
  let holidaysReady = null;
  let seq = 0;
  const pending = new Map();
  const handlers = { tick: [], settings: [], pinned: [] };

  /* ---------------- holidays ---------------- */

  function loadHolidays() {
    if (!holidaysReady) {
      holidaysReady = fetch(HOLIDAYS_URL)
        .then((r) => (r.ok ? r.json() : {}))
        .catch(() => ({}))
        .then((data) => { holidays = data; return data; });
    }
    return holidaysReady;
  }

  /* ---------------- local storage ---------------- */

  function readLocal() {
    try {
      const raw = localStorage.getItem(LS_KEY);
      return raw ? JSON.parse(raw) : {};
    } catch { return {}; }
  }

  function writeLocal(s) {
    try { localStorage.setItem(LS_KEY, JSON.stringify(s)); } catch { /* ignore */ }
  }

  /* ---------------- transport ---------------- */

  function rpc(action, payload) {
    if (!wv) {
      if (action === 'init') return Promise.resolve({ settings: readLocal(), pinned: true });
      if (action === 'save') {
        const merged = Object.assign({}, readLocal(), payload || {});
        writeLocal(merged);
        return Promise.resolve({ settings: merged });
      }
      return Promise.resolve({});
    }
    return new Promise((resolve, reject) => {
      const id = ++seq;
      const timer = setTimeout(() => { pending.delete(id); reject(new Error('timeout: ' + action)); }, 5000);
      pending.set(id, (data) => { clearTimeout(timer); resolve(data); });
      wv.postMessage({ id, action, payload });
    });
  }

  if (wv) {
    wv.addEventListener('message', (event) => {
      const msg = event.data;
      if (!msg || typeof msg !== 'object') return;
      if (msg.replyId != null) {
        const fn = pending.get(msg.replyId);
        if (fn) { pending.delete(msg.replyId); fn(msg.data); }
        return;
      }
      if (msg.type) (handlers[msg.type] || []).forEach((fn) => fn(msg.payload));
    });
  }

  function post(type, payload) {
    if (!wv) return;
    wv.postMessage({ action: 'event', payload: { type, payload } });
  }

  /* ---------------- public api ---------------- */

  window.api = {
    async init() {
      await loadHolidays();
      const fromHost = await rpc('init');
      const settings = Object.assign({}, S.DEFAULT_SETTINGS, fromHost.settings || {});
      return {
        settings,
        pinned: fromHost.pinned !== false,
        holidayInfo: fromHost.holidayInfo || { current: new Date().getFullYear(), covered: false, holidays: 0, workdays: 0 },
        autoLaunchError: fromHost.autoLaunchError || null,
        trayAvailable: fromHost.trayAvailable !== false,
      };
    },

    async save(patch) {
      const res = await rpc('save', patch || {});
      return res;
    },

    /* Preview is always local: no reason to round-trip for arithmetic. */
    async preview(patch) {
      await loadHolidays();
      const merged = Object.assign({}, S.DEFAULT_SETTINGS, patch || {});
      const rates = S.hourlyRate(merged, holidays, new Date());
      return {
        workdays: rates.workdays,
        daily: rates.daily,
        hourly: rates.hourly,
        hours: S.totalWorkSeconds(merged) / 3600,
        salary: merged.monthlySalary,
      };
    },

    evaluate(now, settings) {
      return S.evaluate(now || new Date(), settings, holidays);
    },

    setTheme(theme) { if (wv) wv.postMessage({ action: 'set-theme', payload: { theme } }); },
    cancelled() { if (wv) wv.postMessage({ action: 'cancelled' }); },
    closeWidget() { if (wv) wv.postMessage({ action: 'close-widget' }); },
    openSettings() { if (wv) wv.postMessage({ action: 'open-settings' }); },
    togglePin() { if (wv) wv.postMessage({ action: 'toggle-pin' }); },
    closeSettings() { if (wv) wv.postMessage({ action: 'close-settings' }); },
    quit() { if (wv) wv.postMessage({ action: 'quit' }); },
    drag() { if (wv) wv.postMessage({ action: 'drag' }); },
    contextMenu() { if (wv) wv.postMessage({ action: 'context-menu' }); },
    ready() { if (wv) wv.postMessage({ action: 'ready' }); },

    onTick(cb) { handlers.tick.push(cb); },
    onSettings(cb) { handlers.settings.push(cb); },
    onPinned(cb) { handlers.pinned.push(cb); },
  };
}());
