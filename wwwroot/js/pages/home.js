// 首页概览（v0.8.0，默认 5 秒刷新）：在线数 / 今日在线曲线 / 在线占比
let homeTimer = null;

function stopHomeRefresh() {
  if (homeTimer) { clearInterval(homeTimer); homeTimer = null; }
}

function startHomeRefresh() {
  stopHomeRefresh();
  // 切换频率/重新进入首页时丢掉增量状态，先拉一整份
  state.overview = null;
  state.overviewLastTs = 0;
  state.chartStatic = null;
  const seconds = parseInt($('home-auto-refresh').value, 10);
  refreshOverview();
  if (seconds > 0) homeTimer = setInterval(refreshOverview, seconds * 1000);
}

/**
 * 拉取概览指标（在线数/总数/百分比 + 今日采样曲线）并重绘三块图。
 * v0.8.2：首次拉整份，之后带 ?since=<最后一点 ts> 走增量——服务端按 5 秒采样周期缓存整份结果，
 * 增量响应只有 0-2 个点（几百字节），不再每秒重传整天采样（日终约 760 KB）。
 * 同时加在途守卫：上一次没返回就不叠加新请求（响应 > 刷新间隔时原来的定时器会堆请求）。
 */
async function refreshOverview() {
  if (state.overviewInFlight) return;
  state.overviewInFlight = true;
  try {
    const path = state.overviewLastTs
      ? '/api/admin/stats/overview?since=' + state.overviewLastTs
      : '/api/admin/stats/overview';
    const data = await api(path);
    if (data.mode === 'delta' && state.overview) {
      const merged = Object.assign({}, state.overview, data);
      merged.samples = (state.overview.samples || []).concat(data.samples || []);
      state.overview = merged;
      renderOverview(merged);
    } else {
      state.overview = data;
      renderOverview(data);
    }
    const samples = state.overview.samples || [];
    if (samples.length) state.overviewLastTs = samples[samples.length - 1].ts;
  } catch (e) {
    // 自动刷新失败静默，不打断界面
  } finally {
    state.overviewInFlight = false;
  }
}

function renderOverview(d) {
  $('stat-online').textContent = d.online;
  $('stat-total').textContent = d.total;
  $('stat-offline').textContent = d.offline;
  $('stat-percent').textContent = (d.percent || 0) + '%';
  $('donut-online').textContent = d.online;
  $('donut-offline').textContent = d.offline;
  $('home-updated').textContent = '更新于 ' + new Date().toLocaleTimeString('zh-CN');
  renderLineChart(d);
  renderDonut(d);
}

/**
 * 今日在线折线图（纯 SVG，无第三方库）。
 * X 轴固定为 00:00 → 24:00，采样点按真实时间落位；右端补一个"现在"的实时点。
 */
function renderLineChart(d) {
  const W = 1000, H = 260, padL = 42, padR = 14, padT = 18, padB = 30;
  const plotW = W - padL - padR, plotH = H - padT - padB;
  const dayStart = d.todayStart;
  const dayEnd = dayStart + 86400;
  // v0.8.2：不再用两个展开运算符（日终 17,280 点 = 34,560 个实参，逼近引擎参数上限，且每秒重算）
  let maxVal = Math.max(1, d.total || 0);
  for (const s of (d.samples || [])) {
    if (s.total > maxVal) maxVal = s.total;
    if (s.online > maxVal) maxVal = s.online;
  }
  const xOf = ts => padL + Math.min(1, Math.max(0, (ts - dayStart) / 86400)) * plotW;
  const yOf = v => padT + plotH - (Math.min(v, maxVal) / maxVal) * plotH;

  // 采样点 + 实时点（当前在线数）
  const pts = (d.samples || []).map(s => ({ ts: s.ts, v: s.online }));
  pts.push({ ts: Math.min(dayEnd, d.now), v: d.online });
  const line = pts.map(p => `${xOf(p.ts).toFixed(1)},${yOf(p.v).toFixed(1)}`).join(' ');
  const area = pts.length
    ? `${padL},${padT + plotH} ${line} ${xOf(pts[pts.length - 1].ts).toFixed(1)},${padT + plotH}`
    : '';

  // 网格/坐标轴静态层：只跟 maxVal 与当天起点有关，刷新时复用字符串（原来每次重建整块 SVG innerHTML 的这部分）
  let grid, xAxis;
  const cache = state.chartStatic;
  if (cache && cache.maxVal === maxVal && cache.dayStart === dayStart) {
    grid = cache.grid;
    xAxis = cache.xAxis;
  } else {
    grid = '';
    for (let i = 0; i <= 4; i++) {
      const v = Math.round(maxVal * i / 4);
      const y = yOf(v);
      grid += `<line x1="${padL}" y1="${y.toFixed(1)}" x2="${W - padR}" y2="${y.toFixed(1)}" stroke="#e8eef7" stroke-width="1"/>`
        + `<text x="${padL - 8}" y="${(y + 4).toFixed(1)}" text-anchor="end" font-size="11" fill="#94a3b8">${v}</text>`;
    }
    xAxis = '';
    for (let h = 0; h <= 24; h += 4) {
      const x = padL + (h / 24) * plotW;
      xAxis += `<line x1="${x.toFixed(1)}" y1="${padT}" x2="${x.toFixed(1)}" y2="${padT + plotH}" stroke="#f4f7fb" stroke-width="1"/>`
        + `<text x="${x.toFixed(1)}" y="${H - 8}" text-anchor="middle" font-size="11" fill="#94a3b8">${String(h).padStart(2, '0')}:00</text>`;
    }
    state.chartStatic = { maxVal, dayStart, grid, xAxis };
  }
  const last = pts[pts.length - 1];
  // 末端实时点：白色描边 + 实心圆点，突出"现在"的位置
  const dot = last
    ? `<circle cx="${xOf(last.ts).toFixed(1)}" cy="${yOf(last.v).toFixed(1)}" r="5.5" fill="#fff"/>`
      + `<circle cx="${xOf(last.ts).toFixed(1)}" cy="${yOf(last.v).toFixed(1)}" r="3.5" fill="#1d4ed8"/>`
    : '';

  $('chart-line').innerHTML =
    '<defs><linearGradient id="lineAreaGrad" x1="0" y1="0" x2="0" y2="1">'
    + '<stop offset="0%" stop-color="#2563eb" stop-opacity=".22"/>'
    + '<stop offset="100%" stop-color="#2563eb" stop-opacity="0"/>'
    + '</linearGradient></defs>'
    + grid + xAxis
    + (area ? `<polygon points="${area}" fill="url(#lineAreaGrad)"/>` : '')
    + (pts.length > 1
      ? `<polyline points="${line}" fill="none" stroke="#2563eb" stroke-width="2.2" stroke-linejoin="round" stroke-linecap="round"/>`
      : '')
    + dot;
  $('line-empty').style.display = pts.length > 1 ? 'none' : 'block';
}

/** 在线占比环形图（纯 SVG）：在线 vs 离线，中心显示百分比。 */
function renderDonut(d) {
  const R = 88, r = 60, cx = 120, cy = 120;
  const total = Math.max(1, d.total);
  const ratio = d.total === 0 ? 0 : d.online / total;
  const seg = (from, to, color) => {
    if (to - from <= 0.0001) return '';
    const a0 = -Math.PI / 2 + from * 2 * Math.PI;
    const a1 = -Math.PI / 2 + to * 2 * Math.PI;
    const x0 = cx + R * Math.cos(a0), y0 = cy + R * Math.sin(a0);
    const x1 = cx + R * Math.cos(a1), y1 = cy + R * Math.sin(a1);
    const xi1 = cx + r * Math.cos(a1), yi1 = cy + r * Math.sin(a1);
    const xi0 = cx + r * Math.cos(a0), yi0 = cy + r * Math.sin(a0);
    const large = (to - from) > 0.5 ? 1 : 0;
    return `<path d="M${x0.toFixed(2)},${y0.toFixed(2)} A${R},${R} 0 ${large} 1 ${x1.toFixed(2)},${y1.toFixed(2)} `
      + `L${xi1.toFixed(2)},${yi1.toFixed(2)} A${r},${r} 0 ${large} 0 ${xi0.toFixed(2)},${yi0.toFixed(2)} Z" fill="${color}"/>`;
  };
  $('chart-donut').innerHTML =
    `<circle cx="${cx}" cy="${cy}" r="${(R + r) / 2}" fill="none" stroke="#eef2f8" stroke-width="${R - r}"/>`
    + seg(0, ratio, '#059669')
    + seg(ratio, 1, '#cbd5e1')
    + `<text x="${cx}" y="${cy + 2}" text-anchor="middle" font-size="34" font-weight="700" fill="#0f172a">${(d.percent || 0)}%</text>`
    + `<text x="${cx}" y="${cy + 26}" text-anchor="middle" font-size="12" fill="#64748b">在线占比</text>`;
}

definePage('home', {
  file: 'pages/home.html',
  init() {
    $('home-auto-refresh').addEventListener('change', () => startHomeRefresh());
  },
  load() {
    // 概览数据由标签切入时自行拉取，这里无需预填
  },
  show() { startHomeRefresh(); },
  hide() { stopHomeRefresh(); }
});
