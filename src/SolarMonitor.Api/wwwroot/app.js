'use strict';

const LIVE_MS = 10_000;
const HISTORY_MS = 60_000;
const STALE_S = 60;
const STATES = ['standby', 'self-check', 'normal', 'alarm', 'fault'];

const $ = (id) => document.getElementById(id);

function kw(w) {
  if (w == null) return '–';
  return Math.abs(w) < 1000 ? `${Math.round(w)} W` : `${(w / 1000).toFixed(2)} kW`;
}

const kwh = (v) => `${v.toFixed(1)} kWh`;
const cssVar = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

// ─── Live tiles and table ─────────────────────────────────────────────────────

let lastTime = null;

async function refreshLive() {
  try {
    const res = await fetch('api/live', { cache: 'no-store' });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const { phases, total } = await res.json();
    if (!total) {
      setStatus('no readings in the last 15 minutes', true);
      return;
    }
    lastTime = new Date(total.time);
    renderTiles(total);
    renderPhases(phases);
    updateStatus();
  } catch (e) {
    setStatus(`API error: ${e.message}`, true);
  }
}

function renderTiles(t) {
  $('pv').textContent = kw(t.pvW);
  $('pv-sub').textContent = `today ${kwh(t.today.pv)}`;

  $('load').textContent = kw(t.loadW);
  $('load-sub').textContent = `today ${kwh(t.today.load)}`;

  $('grid-label').textContent = t.gridW >= 0 ? 'Grid · importing' : 'Grid · exporting';
  $('grid').textContent = kw(Math.abs(t.gridW));
  $('grid-sub').textContent = `today bought ${kwh(t.today.gridBuy)} · sold ${kwh(t.today.gridSell)}`;

  $('soc').textContent = `${t.batterySoc}%`;
  const flow = t.batteryW < 0 ? `charging ${kw(-t.batteryW)}`
    : t.batteryW > 0 ? `discharging ${kw(t.batteryW)}` : 'idle';
  const temp = t.batteryTempC != null ? ` · ${t.batteryTempC.toFixed(1)} °C` : '';
  $('batt-sub').textContent = `${flow} · ${t.batteryV.toFixed(2)} V${temp}`;
}

function renderPhases(phases) {
  const rows = phases.map((p) => {
    const state = STATES[p.runningState] ?? `state ${p.runningState}`;
    const bad = p.runningState !== 2 ? ' class="bad"' : '';
    return `<tr>
      <td>${escapeHtml(p.name)}</td>
      <td>${kw(p.pvW)}</td>
      <td>${kw(p.loadW)}</td>
      <td>${kw(p.gridW)}</td>
      <td>${kw(p.batteryW)}</td>
      <td>${kwh(p.today.pv)}</td>
      <td>${p.dcTempC.toFixed(0)} °C</td>
      <td${bad}>${state}</td>
    </tr>`;
  });
  $('phases').innerHTML = rows.join('');
}

function escapeHtml(s) {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

function updateStatus() {
  if (!lastTime) return;
  const age = Math.round((Date.now() - lastTime.getTime()) / 1000);
  setStatus(`updated ${lastTime.toLocaleTimeString()} (${age}s ago)`, age > STALE_S);
}

function setStatus(text, stale) {
  const el = $('status');
  el.textContent = text;
  el.classList.toggle('stale', !!stale);
}

// ─── Chart ────────────────────────────────────────────────────────────────────

let chart = null;
let chartData = [[], [], [], [], [], []];

function chartOptions(width) {
  const fmtW = (u, v) => (v == null ? '–' : kw(v));
  const series = (label, color, extra = {}) => ({ label, stroke: color, width: 1.5, value: fmtW, ...extra });
  const axis = { stroke: cssVar('--muted'), grid: { stroke: cssVar('--border'), width: 1 }, ticks: { stroke: cssVar('--border') } };

  return {
    width,
    height: 320,
    cursor: { sync: { key: 'solar' } },
    scales: { x: { time: true }, w: {}, pct: { range: [0, 100] } },
    series: [
      {},
      series('Solar', cssVar('--pv'), { scale: 'w', fill: cssVar('--pv') + '22' }),
      series('House', cssVar('--load'), { scale: 'w' }),
      series('Grid (+ import)', cssVar('--grid'), { scale: 'w' }),
      series('Battery (+ discharge)', cssVar('--batt'), { scale: 'w' }),
      { label: 'Battery %', stroke: cssVar('--soc'), width: 1.5, dash: [4, 4], scale: 'pct', value: (u, v) => (v == null ? '–' : `${v.toFixed(0)}%`) },
    ],
    axes: [
      { ...axis },
      { ...axis, scale: 'w', values: (u, vals) => vals.map((v) => `${(v / 1000).toFixed(1)}k`), size: 50 },
      { ...axis, scale: 'pct', side: 1, values: (u, vals) => vals.map((v) => `${v}%`), grid: { show: false }, size: 45 },
    ],
  };
}

function buildChart() {
  const el = $('chart');
  if (chart) chart.destroy();
  chart = new uPlot(chartOptions(el.clientWidth), chartData, el);
}

async function refreshHistory() {
  try {
    const res = await fetch('api/history?hours=24&bucketSeconds=120', { cache: 'no-store' });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const h = await res.json();
    chartData = [h.t, h.pv, h.load, h.grid, h.battery, h.soc];
    if (chart) chart.setData(chartData);
    else buildChart();
  } catch (e) {
    console.warn('history', e);
  }
}

new ResizeObserver(() => chart && chart.setSize({ width: $('chart').clientWidth, height: 320 })).observe($('chart'));
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', buildChart);

// ─── Start ────────────────────────────────────────────────────────────────────

refreshLive();
refreshHistory();
setInterval(refreshLive, LIVE_MS);
setInterval(refreshHistory, HISTORY_MS);
setInterval(updateStatus, 1000);
