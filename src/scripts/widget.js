'use strict';

/* Wage Clock · widget renderer

   The card is a continuous counter, not a 1 Hz refresher: a rAF loop re-evaluates the
   schedule ~30 times a second so the last decimals and the hundredths of the countdown
   visibly stream. Integer digits snap, decimals roll continuously (mechanical odometer). */

const S = window.WageSchedule;
const INT_COLS = 4;
const DEC_COLS = 4;
const CELLS = 11; // 0-9 plus one repeated 0 so a rolling column never runs out of digit
const FRAME_MS = 50; // 20 fps: fast enough to read as flow, cheap enough for an always-on widget

const $ = (id) => document.getElementById(id);
const params = new URLSearchParams(location.search);
const forcedNow = params.get('now') ? new Date(params.get('now')) : null;
const forcedTheme = params.get('theme');

/* ------------------------------------------------------------------ */
/* odometer                                                            */
/* ------------------------------------------------------------------ */

class DigitColumn {
  constructor() {
    this.el = document.createElement('div');
    this.el.className = 'col';
    this.strip = document.createElement('div');
    this.strip.className = 'strip';
    for (let i = 0; i < CELLS; i++) {
      const cell = document.createElement('div');
      cell.className = 'cell';
      cell.textContent = String(i % 10);
      this.strip.appendChild(cell);
    }
    this.el.appendChild(this.strip);
    this.phase = 0;
  }

  /** phase is a continuous position in [0,10): 3.5 shows the 3 sliding halfway to 4. */
  setPhase(phase) {
    const p = ((phase % 10) + 10) % 10;
    if (p === this.phase) return;
    this.phase = p;
    this.strip.style.transform = `translateY(${(-p * 100) / CELLS}%)`;
  }
}

class Odometer {
  constructor(root) {
    this.int = [];
    this.dec = [];
    for (let i = 0; i < INT_COLS; i++) {
      const col = new DigitColumn();
      this.int.push(col);
      root.appendChild(col.el);
    }
    const dotCol = document.createElement('div');
    dotCol.className = 'col dot-col';
    const dotCell = document.createElement('div');
    dotCell.className = 'cell';
    dotCell.textContent = '.';
    dotCol.appendChild(dotCell);
    root.appendChild(dotCol);

    for (let i = 0; i < DEC_COLS; i++) {
      const col = new DigitColumn();
      col.el.classList.add('dec');
      this.dec.push(col);
      root.appendChild(col.el);
    }
  }

  render(amount) {
    const v = Math.max(0, Number(amount) || 0);
    const intVal = Math.floor(v);
    const intRaw = String(intVal);
    const intStr = intRaw.padStart(INT_COLS, '0'); // padded, so its length is always INT_COLS
    const ghostFrom = INT_COLS - intRaw.length; // compare against the raw length instead

    for (let i = 0; i < INT_COLS; i++) {
      this.int[i].setPhase(Number(intStr[i])); // integers snap, never creep
      this.int[i].el.classList.toggle('ghost', i < ghostFrom);
    }
    for (let j = 0; j < DEC_COLS; j++) {
      this.dec[j].setPhase(Math.floor((v * Math.pow(10, j + 1)) % 10)); // decimals snap too — a rolling wheel is unreadable at 140 digits/s
    }
  }
}

/* ------------------------------------------------------------------ */
/* sparkler                                                            */
/* ------------------------------------------------------------------ */

/* A sparkler burns with a dense spray of tiny embers, not one flame.
   Particles live on a canvas that sits above the track; only transform-free
   fills, so it stays cheap at 60 fps. */

class Sparkler {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: true });
    this.parts = [];
    this.acc = 0;
    this.w = 0;
    this.h = 0;
    this.tipY = 46.5;
    this.resize();
  }

  resize() {
    const dpr = Math.min(1.5, window.devicePixelRatio || 1); // sparks are 1-2px dots: 1.5x is plenty
    const rect = this.canvas.getBoundingClientRect();
    this.w = rect.width || 360;
    this.h = rect.height || 58;
    this.canvas.width = Math.max(1, Math.round(this.w * dpr));
    this.canvas.height = Math.max(1, Math.round(this.h * dpr));
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  spawn(x) {
    // wide upward fan, slight forward bias so it looks like it is being pushed along
    const angle = -Math.PI / 2 + (Math.random() - 0.5) * 2.6 + 0.15;
    const speed = 26 + Math.random() * 105;
    this.parts.push({
      x: x + (Math.random() - 0.5) * 3,
      y: this.tipY + (Math.random() - 0.5) * 2,
      vx: Math.cos(angle) * speed,
      vy: Math.sin(angle) * speed,
      age: 0,
      life: 0.45 + Math.random() * 0.95,
      size: 0.8 + Math.random() * 1.5,
    });
  }

  frame(dt, x, burning) {
    const ctx = this.ctx;
    ctx.clearRect(0, 0, this.w, this.h);
    const step = Math.min(0.05, Math.max(0.008, dt));

    if (burning) {
      this.acc += 95 * step; // sparks per second
      let n = Math.floor(this.acc);
      this.acc -= n;
      while (n-- > 0 && this.parts.length < 95) this.spawn(x);
    }

    ctx.globalCompositeOperation = 'lighter';
    const alive = [];
    for (const p of this.parts) {
      p.age += step;
      if (p.age >= p.life) continue;
      p.vy += 155 * step; // gravity
      const drag = 1 - 1.4 * step;
      p.vx *= drag;
      p.vy *= drag;
      p.x += p.vx * step;
      p.y += p.vy * step;
      if (p.y > this.h + 6 || p.x < -12 || p.x > this.w + 12) continue;

      const t = p.age / p.life;
      const alpha = t < 0.15 ? t / 0.15 : 1 - (t - 0.15) / 0.85;
      const hue = 46 - 34 * t; // white-gold -> orange -> deep red
      const light = 92 - 40 * t;

      // short streak behind the ember reads as motion at 60 fps
      ctx.strokeStyle = `hsla(${hue.toFixed(0)}, 100%, ${light.toFixed(0)}%, ${(alpha * 0.55).toFixed(3)})`;
      ctx.lineWidth = p.size;
      ctx.beginPath();
      ctx.moveTo(p.x - p.vx * 0.05, p.y - p.vy * 0.05);
      ctx.lineTo(p.x, p.y);
      ctx.stroke();

      ctx.fillStyle = `hsla(${hue.toFixed(0)}, 100%, ${Math.min(98, light + 8).toFixed(0)}%, ${alpha.toFixed(3)})`;
      ctx.beginPath();
      ctx.arc(p.x, p.y, p.size * 0.75, 0, Math.PI * 2);
      ctx.fill();

      alive.push(p);
    }
    ctx.globalCompositeOperation = 'source-over';
    this.parts = alive;
  }
}

let sparkler = null;
/* ------------------------------------------------------------------ */
/* theme                                                               */
/* ------------------------------------------------------------------ */

const media = window.matchMedia('(prefers-color-scheme: light)');
let themeMode = 'auto';

function applyTheme() {
  const resolved = themeMode === 'auto' ? (media.matches ? 'light' : 'dark') : themeMode;
  document.body.className = resolved;
}
media.addEventListener('change', () => { if (themeMode === 'auto') applyTheme(); });

/* ------------------------------------------------------------------ */
/* render                                                              */
/* ------------------------------------------------------------------ */

const odo = new Odometer($('odo'));
const card = $('card');
let settings = null;
let trayAvailable = true;
const cache = { state: '', date: '', weekend: '', month: '', pct: -1, intLen: -1 };
const burn = { pct: 0, active: false };

function setText(el, value) {
  if (el.textContent !== value) el.textContent = value;
}

function render(view) {
  if (cache.state !== view.state) {
    cache.state = view.state;
    card.dataset.state = view.state;
    setText($('stateLabel'), view.stateLabel);
  }
  if (cache.date !== view.dateLabel) {
    cache.date = view.dateLabel;
    setText($('date'), view.dateLabel);
  }

  setText($('vOff'), view.offWorkSeconds == null ? '—' : S.formatClockCs(view.offWorkSeconds));

  const weekend = view.weekendReached ? '已到周末' : S.formatCountdown(view.weekendSeconds);
  if (cache.weekend !== weekend) {
    cache.weekend = weekend;
    setText($('vWeekend'), weekend);
  }

  const month = `${view.elapsedWorkdays}/${view.monthWorkdays} 天`;
  if (cache.month !== month) {
    cache.month = month;
    setText($('fMonth'), month);
  }

  const pct = Math.max(0, Math.min(1, view.progress || 0));
  if (Math.abs(pct - cache.pct) > 0.0003) {
    cache.pct = pct;
    $('fill').style.width = `${(pct * 100).toFixed(2)}%`;
  }

  const seg = (view.segmentList || [])[0];
  if (seg) $('mark').style.left = `${(seg.ratio * 100).toFixed(2)}%`;

  const intLen = String(Math.floor(view.earned)).length;
  if (intLen !== cache.intLen) {
    cache.intLen = intLen;
    fitMoney(); // a longer integer part may need a smaller glyph
  }

  odo.render(view.earned);
  paintProgressColor(pct);

  burn.pct = pct;
  burn.active = view.state === 'working';
}

/** Shrink the counter until it fits the card, so a 4-digit daily wage never clips. */
function fitMoney() {
  const el = $('money');
  const avail = card.clientWidth - 40; // card padding, 20px each side
  el.style.setProperty('--m', '75px');
  const natural = el.scrollWidth;
  if (natural > avail) {
    const scaled = Math.max(40, Math.floor((75 * avail) / natural));
    el.style.setProperty('--m', scaled + 'px');
  }
}

let lastHue = -1;

/** Red at 0% of the shift, green at 100% — the same percentage the money counter uses. */
function paintProgressColor(pct) {
  const hue = 140 * pct;
  if (Math.abs(hue - lastHue) < 0.2) return;
  lastHue = hue;
  const light = document.body.classList.contains('light');
  const color = `hsl(${hue.toFixed(1)} 82% ${light ? 36 : 62}%)`;
  $('money').style.color = color;
  $('vOff').style.color = color;
}

function currentView() {
  const now = forcedNow ? new Date(forcedNow) : new Date();
  return window.api.evaluate(now, settings);
}

function tick() {
  if (!settings) return;
  render(currentView());
}

/* ------------------------------------------------------------------ */
/* loop                                                                */
/* ------------------------------------------------------------------ */

let lastFrame = 0;
let lastTs = 0;
let lastSpark = 0;
let sparkDt = 0;

function frame(ts) {
  const dt = lastTs ? (ts - lastTs) / 1000 : 0.016;
  lastTs = ts;

  if (settings && ts - lastFrame >= FRAME_MS) {
    lastFrame = ts;
    try { tick(); } catch (err) { console.error(err); }
  }
  if (sparkler && ts - lastSpark >= 32) { // particles at ~30 fps, compositor work stays low
    lastSpark = ts;
    try { sparkler.frame(sparkDt + dt, burn.pct * sparkler.w, burn.active); } catch (err) { console.error(err); }
    sparkDt = 0;
  } else {
    sparkDt += dt;
  }
  requestAnimationFrame(frame);
}

/* ------------------------------------------------------------------ */
/* wire-up                                                             */
/* ------------------------------------------------------------------ */

function setPinned(v) {
  $('pin').classList.toggle('on', !!v);
}

function isTrayResident(s) {
  if (!s || !trayAvailable) return false;
  if (s.closeAction === 'tray') return true;
  if (s.closeAction === 'quit') return false;
  return !!s.autoLaunch;
}

function updateCloseHint() {
  $('close').title = isTrayResident(settings)
    ? '收进托盘（右键托盘图标 → 退出）'
    : '退出程序';
}

$('gear').addEventListener('click', () => window.api.openSettings());
$('close').addEventListener('click', () => window.api.closeWidget());
$('pin').addEventListener('click', () => window.api.togglePin());

// Drag: the whole upper band is a handle, buttons excluded. Started from mousedown so a
// long press followed by a move still grabs the window (the host starts the move loop).
card.addEventListener('mousedown', (e) => {
  if (e.button !== 0) return;
  if (e.target.closest('.tools')) return; // never drag from the toolbar, only from empty space
  const rect = card.getBoundingClientRect();
  if (e.clientY - rect.top > 104) return;
  window.api.drag();
});

window.addEventListener('keydown', (e) => {
  if (e.altKey && e.key.toLowerCase() === 'p') { e.preventDefault(); window.api.togglePin(); }
});

window.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  window.api.contextMenu();
});

window.api.onPinned(setPinned);
window.api.onSettings(({ settings: next, theme }) => {
  if (!next) return;
  settings = next;
  updateCloseHint();
  const nextTheme = forcedTheme || theme || next.theme || 'auto';
  if (nextTheme !== themeMode) { themeMode = nextTheme; applyTheme(); }
  setPinned(next.alwaysOnTop !== false);
  tick();
});

window.api.init().then(({ settings: loaded, pinned, trayAvailable: trayOk }) => {
  settings = loaded;
  sparkler = new Sparkler($('sparkler'));
  window.addEventListener('resize', () => sparkler.resize());
  trayAvailable = trayOk !== false;
  updateCloseHint();
  themeMode = forcedTheme || loaded.theme || 'auto';
  applyTheme();
  setPinned(pinned);
  tick();
  window.api.ready();
  if (!forcedNow) requestAnimationFrame(frame);
});
