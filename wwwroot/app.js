// 校园浏览器服务端管理后台（原生 JS，无构建依赖）
const $ = (id) => document.getElementById(id);

const state = {
  token: localStorage.getItem('cb_token') || '',
  config: null,
  devices: [],
  autoRefreshTimer: null,
  currentTab: 'home',
  htmlPages: [],
  currentHtmlId: 0,
  logs: { page: 0, size: 100, total: 0, items: [], events: [] },
  logTimer: null,
  homeTimer: null,
  overview: null
};

// 设备面板自动刷新：默认 1 秒/次（v0.8.0），用户选择持久化到本地
const AUTO_REFRESH_KEY = 'cb_auto_refresh';
function savedAutoRefresh() {
  const v = parseInt(localStorage.getItem(AUTO_REFRESH_KEY), 10);
  return Number.isFinite(v) ? v : 1;
}

async function api(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  if (state.token) headers['Authorization'] = 'Bearer ' + state.token;
  const resp = await fetch(path, { ...options, headers });
  const text = await resp.text();
  const data = text ? JSON.parse(text) : {};
  if (!resp.ok) throw new Error(data.error || `请求失败（${resp.status}）`);
  return data;
}

function show(screen) {
  ['setup', 'login', 'dashboard'].forEach(s =>
    $('screen-' + s).classList.toggle('hidden', s !== screen));
}

function setMsg(id, text, ok = false) {
  const el = $(id);
  el.textContent = text;
  el.className = 'msg ' + (ok ? 'ok' : 'err');
}

// ---------- 快捷书签 ----------

function escAttr(s) {
  return String(s).replace(/&/g, '&amp;').replace(/"/g, '&quot;')
    .replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// HTML 文本转义：所有来自接口的动态数据在拼 innerHTML 前必须经过此函数，防止存储型 XSS。
function escHtml(s) {
  return String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function addBookmarkRow(containerId, title = '', url = '') {
  const row = document.createElement('div');
  row.className = 'bm-row';
  row.innerHTML =
    `<input class="bm-title" placeholder="名称，如：学校官网" value="${escAttr(title)}">` +
    `<input class="bm-url" placeholder="https://..." value="${escAttr(url)}">` +
    `<button type="button" class="bm-del" title="删除该行">×</button>`;
  row.querySelector('.bm-del').addEventListener('click', () => row.remove());
  $(containerId).appendChild(row);
}

function collectBookmarks(containerId) {
  return [...$(containerId).querySelectorAll('.bm-row')]
    .map(r => ({
      title: r.querySelector('.bm-title').value.trim(),
      url: r.querySelector('.bm-url').value.trim()
    }))
    .filter(b => b.url !== '');
}

function fillBookmarks(containerId, list) {
  $(containerId).innerHTML = '';
  (list && list.length ? list : [{}]).forEach(b =>
    addBookmarkRow(containerId, b.title || '', b.url || ''));
}

// ---------- 允许唤醒的应用 ----------

function addAppRow(containerId, label = '', pkg = '', scheme = '') {
  const row = document.createElement('div');
  row.className = 'app-row';
  row.innerHTML =
    `<input class="app-label" placeholder="显示名，如：学习通" value="${escAttr(label)}">` +
    `<input class="app-pkg" placeholder="包名，如：com.example.learning" value="${escAttr(pkg)}">` +
    `<input class="app-scheme" placeholder="scheme，如：learning（可空）" value="${escAttr(scheme)}">` +
    `<button type="button" class="app-del" title="删除该行">×</button>`;
  row.querySelector('.app-del').addEventListener('click', () => row.remove());
  $(containerId).appendChild(row);
}

function collectApps(containerId) {
  return [...$(containerId).querySelectorAll('.app-row')]
    .map(r => ({
      label: r.querySelector('.app-label').value.trim(),
      package: r.querySelector('.app-pkg').value.trim(),
      scheme: r.querySelector('.app-scheme').value.trim()
    }))
    .filter(a => a.package !== '');
}

function fillApps(containerId, list) {
  $(containerId).innerHTML = '';
  (list && list.length ? list : [{}]).forEach(a =>
    addAppRow(containerId, a.label || '', a.package || '', a.scheme || ''));
}

// ---------- 初始化向导 ----------

/** 初始化向导里的单个 HTML 页（留空则不创建）。 */
function setupHtmlPages() {
  const slug = $('setup-html-slug').value.trim().toLowerCase();
  const html = $('setup-html-body').value;
  if (!slug && !html) return [];
  return [{ slug, title: $('setup-html-title').value.trim(), html }];
}

async function submitSetup() {
  const body = {
    username: $('setup-username').value.trim(),
    password: $('setup-password').value,
    title: $('setup-title').value.trim(),
    homeUrl: $('setup-home').value.trim(),
    mode: $('setup-mode').value,
    rules: $('setup-rules').value,
    bookmarks: collectBookmarks('setup-bookmarks'),
    updateIntervalSeconds: parseInt($('setup-interval').value, 10) || 300,
    hiddenEntryEnabled: $('setup-hidden-entry').checked,
    blockScreenshot: $('setup-block-screenshot').checked,
    requireStartupPhoto: $('setup-require-photo').checked,
    allowedApps: collectApps('setup-apps'),
    htmlPages: setupHtmlPages(),
    adminPin: $('setup-pin').value.trim()
  };
  setMsg('setup-msg', '');
  try {
    await api('/api/setup', { method: 'POST', body: JSON.stringify(body) });
    const login = await api('/api/login', {
      method: 'POST',
      body: JSON.stringify({ username: body.username, password: body.password })
    });
    state.token = login.token;
    localStorage.setItem('cb_token', state.token);
    await loadDashboard();
  } catch (e) {
    setMsg('setup-msg', e.message);
  }
}

// ---------- 登录 ----------

async function submitLogin() {
  setMsg('login-msg', '');
  try {
    const data = await api('/api/login', {
      method: 'POST',
      body: JSON.stringify({
        username: $('login-username').value.trim(),
        password: $('login-password').value
      })
    });
    state.token = data.token;
    localStorage.setItem('cb_token', state.token);
    await loadDashboard();
  } catch (e) {
    setMsg('login-msg', '账号或密码错误');
  }
}

function logout() {
  api('/api/logout', { method: 'POST' }).catch(() => {});
  state.token = '';
  localStorage.removeItem('cb_token');
  show('login');
}

// ---------- 控制台 ----------

async function loadDashboard() {
  try {
    const [config, versions, devices, key, security, htmlPages, logs] = await Promise.all([
      api('/api/admin/config'),
      api('/api/admin/config/versions'),
      api('/api/admin/devices'),
      api('/api/admin/public-key'),
      api('/api/admin/security'),
      api('/api/admin/html-pages'),
      api('/api/admin/logs?limit=100')
    ]);
    state.config = config;
    state.devices = devices;
    state.htmlPages = htmlPages;
    state.logs.items = logs.items || [];
    state.logs.total = logs.total || 0;
    state.logs.page = 0;
    state.logs.events = logs.events || [];
    fillForm(config);
    renderVersions(versions);
    applyDeviceFilter();
    renderHtmlList();
    renderLogEventFilter();
    renderLogs();
    $('ta-pubkey').value = key.publicKey || '';
    $('set-ua-check').checked = !!security.uaCheckEnabled;
    show('dashboard');
    switchTab(state.currentTab || 'home');
  } catch (e) {
    state.token = '';
    localStorage.removeItem('cb_token');
    show('login');
  }
}

/** 仅刷新设备列表（自动刷新调用，不重新拉配置/版本）。 */
async function refreshDevicesOnly() {
  try {
    const devices = await api('/api/admin/devices');
    state.devices = devices;
    applyDeviceFilter();
  } catch (e) {
    // 自动刷新失败静默，不打断界面
  }
}

function fillForm(c) {
  $('f-title').value = c.title || '校园门户';
  $('f-home').value = c.home_url || '';
  $('f-fallback').value = c.fallback_url || '';
  $('f-mode').value = c.mode || 'whitelist';
  $('f-interval').value = c.update_interval_seconds || 300;
  $('f-kiosk').checked = !!c.kiosk;
  $('f-hidden-entry').checked = c.hidden_entry_enabled !== false;
  $('f-block-screenshot').checked = c.block_screenshot !== false;
  $('f-require-photo').checked = !!c.require_startup_photo;
  $('f-rules').value = (c.rules || []).join('\n');
  fillBookmarks('f-bookmarks', c.bookmarks);
  fillApps('f-apps', c.allowed_apps);
  renderHtmlPicker(c.html_pages);
  $('f-pin').value = '';
  $('f-note').value = '';
}

// ---------- 内置 HTML 页（v0.8.0） ----------

/** 配置表单里的 HTML 页勾选列表（勾选后写入配置下发）。 */
function renderHtmlPicker(selected) {
  const box = $('f-html-pages');
  const chosen = new Set((selected || []).map(p => p.slug));
  box.innerHTML = '';
  if (!state.htmlPages.length) {
    $('f-html-hint').textContent = '还没有 HTML 页，去「HTML 页」标签新建，或直接用它给出的 URL 加进上面的书签列表。';
    return;
  }
  state.htmlPages.forEach(p => {
    const label = document.createElement('label');
    label.innerHTML =
      `<input type="checkbox" value="${escAttr(p.slug)}"${chosen.has(p.slug) ? ' checked' : ''}>` +
      `<span>${escHtml(p.title || p.slug)}</span> <code>/h/${escHtml(p.slug)}</code>`;
    box.appendChild(label);
  });
  $('f-html-hint').textContent = '勾选后会写入配置下发（配置里只带名称与地址，正文仍由服务端托管）。';
}

function collectHtmlPages() {
  return [...$('f-html-pages').querySelectorAll('input[type=checkbox]:checked')].map(i => {
    const p = state.htmlPages.find(x => x.slug === i.value) || {};
    return { slug: i.value, title: p.title || i.value, html: '' };
  });
}

/** 拉取 HTML 页列表并渲染左侧列表 + 配置表单勾选项。 */
async function refreshHtmlPages() {
  state.htmlPages = await api('/api/admin/html-pages');
  renderHtmlList();
  const current = (state.config && state.config.html_pages) || [];
  renderHtmlPicker(current);
}

function renderHtmlList() {
  const box = $('html-list');
  box.innerHTML = '';
  if (!state.htmlPages.length) {
    box.innerHTML = '<p class="muted">暂无 HTML 页，点「+ 新建」创建。</p>';
    return;
  }
  state.htmlPages.forEach(p => {
    const div = document.createElement('div');
    div.className = 'html-item' + (p.id === state.currentHtmlId ? ' active' : '');
    div.innerHTML = `<div class="t">${escHtml(p.title || p.slug)}</div><div class="s">/h/${escHtml(p.slug)}</div>`;
    div.addEventListener('click', () => selectHtmlPage(p.id));
    box.appendChild(div);
  });
}

function selectHtmlPage(id) {
  const p = state.htmlPages.find(x => x.id === id);
  if (!p) return;
  state.currentHtmlId = id;
  $('html-id').value = String(p.id);
  $('html-title').value = p.title || '';
  $('html-slug').value = p.slug || '';
  $('html-body').value = p.html || '';
  $('html-url-line').textContent = '访问地址：' + (p.url || '/h/' + p.slug);
  setMsg('html-msg', '');
  renderHtmlList();
}

function newHtmlPage() {
  state.currentHtmlId = 0;
  $('html-id').value = '0';
  $('html-title').value = '';
  $('html-slug').value = '';
  $('html-body').value = '<!DOCTYPE html>\n<html lang="zh-CN">\n<head>\n<meta charset="UTF-8">\n'
    + '<meta name="viewport" content="width=device-width, initial-scale=1.0">\n<title></title>\n</head>\n<body>\n\n</body>\n</html>\n';
  $('html-url-line').textContent = '访问地址：保存后生成';
  setMsg('html-msg', '');
  renderHtmlList();
}

async function saveHtmlPage() {
  const id = parseInt($('html-id').value, 10) || 0;
  const body = {
    id,
    slug: $('html-slug').value.trim().toLowerCase(),
    title: $('html-title').value.trim(),
    html: $('html-body').value
  };
  if (!body.slug) { setMsg('html-msg', '请填写地址片段'); return; }
  try {
    const data = await api('/api/admin/html-pages', { method: 'POST', body: JSON.stringify(body) });
    setMsg('html-msg', `已保存：${data.url}`, true);
    await refreshHtmlPages();
    if (data.id) selectHtmlPage(data.id);
  } catch (e) {
    setMsg('html-msg', e.message);
  }
}

async function deleteHtmlPage() {
  const id = parseInt($('html-id').value, 10) || 0;
  if (!id) { setMsg('html-msg', '当前是新建状态，没有可删除的页面'); return; }
  if (!confirm('确认删除该 HTML 页？已发布配置中的书签将无法打开。')) return;
  try {
    await api('/api/admin/html-pages/' + id, { method: 'DELETE' });
    state.currentHtmlId = 0;
    setMsg('html-msg', '已删除', true);
    newHtmlPage();
    await refreshHtmlPages();
  } catch (e) {
    setMsg('html-msg', e.message);
  }
}

function previewHtmlPage() {
  const html = $('html-body').value;
  const url = URL.createObjectURL(new Blob([html], { type: 'text/html;charset=utf-8' }));
  window.open(url, '_blank');
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}

function copyHtmlUrl() {
  const p = state.htmlPages.find(x => x.id === state.currentHtmlId);
  if (!p) { setMsg('html-msg', '先保存页面'); return; }
  const text = p.url || location.origin + '/h/' + p.slug;
  navigator.clipboard?.writeText(text).then(
    () => setMsg('html-msg', '已复制的访问地址：' + text, true),
    () => setMsg('html-msg', '复制失败，请手动复制：' + text)
  );
}

async function publish() {
  setMsg('pub-msg', '');
  const body = {
    title: $('f-title').value,
    homeUrl: $('f-home').value,
    fallbackUrl: $('f-fallback').value,
    mode: $('f-mode').value,
    rules: $('f-rules').value,
    bookmarks: collectBookmarks('f-bookmarks'),
    updateIntervalSeconds: parseInt($('f-interval').value, 10) || 300,
    kiosk: $('f-kiosk').checked,
    hiddenEntryEnabled: $('f-hidden-entry').checked,
    blockScreenshot: $('f-block-screenshot').checked,
    requireStartupPhoto: $('f-require-photo').checked,
    allowedApps: collectApps('f-apps'),
    htmlPages: collectHtmlPages(),
    adminPin: $('f-pin').value.trim(),
    note: $('f-note').value
  };
  try {
    const data = await api('/api/admin/config/publish', {
      method: 'POST', body: JSON.stringify(body)
    });
    setMsg('pub-msg', `已发布版本 ${data.version}，平板将在下次轮询时生效`, true);
    $('f-pin').value = ''; $('f-note').value = '';
    const versions = await api('/api/admin/config/versions');
    renderVersions(versions);
  } catch (e) {
    setMsg('pub-msg', e.message);
  }
}

async function rollback(version) {
  if (!confirm(`确认回滚到 ${version}？将以该内容生成一个新版本下发。`)) return;
  try {
    const data = await api(`/api/admin/config/rollback?version=${encodeURIComponent(version)}`, {
      method: 'POST'
    });
    alert(`已回滚并发布为 ${data.version}`);
    await loadDashboard();
  } catch (e) {
    alert(e.message);
  }
}

function renderVersions(list) {
  const tbody = $('tbl-versions').querySelector('tbody');
  tbody.innerHTML = '';
  list.forEach(v => {
    const tr = document.createElement('tr');
    tr.innerHTML = `<td><code>${escHtml(v.version)}</code>${v.current ? ' <b>（当前）</b>' : ''}</td>
      <td>${escHtml(v.publishedAt)}</td><td>${escHtml(v.note || '')}</td>
      <td>${v.current ? '—' : `<button class="mini" data-v="${escAttr(v.version)}">回滚</button>`}</td>`;
    tr.querySelector('button')?.addEventListener('click', () => rollback(v.version));
    tbody.appendChild(tr);
  });
}

/** 按状态+关键词过滤设备后渲染（纯前端过滤）。 */
function applyDeviceFilter() {
  const status = $('device-filter-status').value;
  const kw = $('device-filter-keyword').value.trim().toLowerCase();
  const filtered = state.devices.filter(d => {
    if (status === 'online' && !d.online) return false;
    if (status === 'offline' && d.online) return false;
    if (kw) {
      const hay = `${d.deviceId} ${d.name || ''} ${d.ipAddress || ''}`.toLowerCase();
      if (!hay.includes(kw)) return false;
    }
    return true;
  });
  renderDevices(filtered);
}

function renderDevices(list) {
  const tbody = $('tbl-devices').querySelector('tbody');
  tbody.innerHTML = list.length
    ? ''
    : '<tr><td colspan="11" class="muted">暂无设备注册</td></tr>';
  const now = Date.now();
  list.forEach(d => {
    const tr = document.createElement('tr');
    const status = d.online
      ? '<span style="color:#2E9E5B;font-weight:700;">●</span> 在线'
      : '<span style="color:#9AA9BA;">●</span> 离线';
    const photoCell = d.photoSkipReason
      ? '<span class="muted" title="' + escAttr(d.photoSkipReason) + '">跳过（' + escHtml(d.photoSkipReason) + '）</span>'
      : d.lastPhotoAt
        ? escHtml(d.lastPhotoAt)
        : '<span class="muted">—</span>';
    // 输错密码拍照：最近 10 分钟内有记录则整行标红告警
    const wrongPinRecent = d.lastWrongPinPhotoAt &&
      (now - new Date(d.lastWrongPinPhotoAt.replace(' ', 'T')).getTime()) < 10 * 60 * 1000;
    if (wrongPinRecent) tr.style.background = '#FDECEC';
    const wrongPinCell = d.lastWrongPinPhotoAt
      ? escHtml(d.lastWrongPinPhotoAt)
      : '<span class="muted">—</span>';
    const viewBtn = d.lastPhotoAt
      ? `<button class="mini" data-dev="${escAttr(d.deviceId)}" data-type="startup">启动</button>`
      : '<span class="muted">—</span>';
    const viewWrongBtn = d.lastWrongPinPhotoAt
      ? `<button class="mini" data-dev="${escAttr(d.deviceId)}" data-type="wrong_pin">输错</button>`
      : '<span class="muted">—</span>';
    tr.innerHTML = `<td>${status}</td><td><code>${escHtml(d.deviceId)}</code></td><td>${escHtml(d.name || '')}</td>
      <td>${escHtml(d.appVersion || '')}</td><td>${escHtml(d.configVersion || '')}</td>
      <td>${d.ipAddress ? '<code>' + escHtml(d.ipAddress) + '</code>' : '<span class="muted">—</span>'}</td>
      <td>${escHtml(d.registeredAt)}</td><td>${escHtml(d.lastSeen)}</td>
      <td>${photoCell}</td><td>${wrongPinCell}</td>
      <td style="white-space:nowrap;">${viewBtn} ${viewWrongBtn}</td>`;
    tr.querySelectorAll('button').forEach(b => b.addEventListener('click', () => viewPhoto(d.deviceId, b.dataset.type)));
    tbody.appendChild(tr);
  });
}

/** 带鉴权拉取设备最新照片并在新窗口打开。type=startup|wrong_pin */
async function viewPhoto(deviceId, type) {
  try {
    const resp = await fetch(`/api/admin/photo?deviceId=${encodeURIComponent(deviceId)}&file=latest&type=${encodeURIComponent(type || 'startup')}`, {
      headers: { 'Authorization': 'Bearer ' + state.token }
    });
    if (!resp.ok) throw new Error(`照片加载失败（${resp.status}）`);
    const blob = await resp.blob();
    const url = URL.createObjectURL(blob);
    window.open(url, '_blank');
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  } catch (e) {
    alert(e.message);
  }
}

// ---------- CSV 导出 ----------
const EXPORT_COLUMNS = [
  { key: 'online', label: '在线状态' },
  { key: 'deviceId', label: '设备识别码' },
  { key: 'name', label: '名称' },
  { key: 'appVersion', label: 'APP版本' },
  { key: 'configVersion', label: '配置版本' },
  { key: 'ipAddress', label: 'IP地址' },
  { key: 'registeredAt', label: '注册时间' },
  { key: 'lastSeen', label: '最后上线时间' },
  { key: 'lastPhotoAt', label: '最近启动拍照' },
  { key: 'photoSkipReason', label: '拍照跳过原因' },
  { key: 'lastWrongPinPhotoAt', label: '最近输错拍照' },
];

function openExportModal() {
  const box = $('export-columns');
  box.innerHTML = '';
  EXPORT_COLUMNS.forEach(c => {
    const label = document.createElement('label');
    label.style.cssText = 'display:flex;align-items:center;gap:6px;cursor:pointer;';
    label.innerHTML = `<input type="checkbox" value="${c.key}" checked> ${escHtml(c.label)}`;
    box.appendChild(label);
  });
  $('export-from').value = '';
  $('export-to').value = '';
  $('export-all').checked = true;
  $('export-csv-modal').style.display = 'flex';
}
function closeExportModal() { $('export-csv-modal').style.display = 'none'; }

async function doExportCsv() {
  const cols = Array.from($('export-columns').querySelectorAll('input:checked')).map(i => i.value);
  if (cols.length === 0) { alert('请至少选择一列'); return; }
  const params = new URLSearchParams({ columns: cols.join(',') });
  if (!$('export-all').checked) {
    if ($('export-from').value) params.set('from', $('export-from').value);
    if ($('export-to').value) params.set('to', $('export-to').value);
  }
  try {
    const resp = await fetch('/api/admin/devices/export.csv?' + params.toString(), {
      headers: { 'Authorization': 'Bearer ' + state.token }
    });
    if (!resp.ok) throw new Error(`导出失败（${resp.status}）`);
    const blob = await resp.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `devices_${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '')}.csv`;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 5000);
    closeExportModal();
  } catch (e) { alert(e.message); }
}

// ---------- 标签分级 ----------

function switchTab(tab) {
  state.currentTab = tab;
  document.querySelectorAll('.tab').forEach(t =>
    t.classList.toggle('active', t.dataset.tab === tab));
  document.querySelectorAll('.tab-panel').forEach(p => {
    const showPanel = p.id === 'panel-' + tab;
    p.classList.toggle('active', showPanel);
    p.style.display = showPanel ? 'block' : 'none';
  });
  // 每个标签的定时器只在自身可见时运行（切走即暂停，避免后台空转）
  stopAutoRefresh();
  stopHomeRefresh();
  stopLogRefresh();
  if (tab === 'home') {
    startHomeRefresh();
  } else if (tab === 'devices') {
    setAutoRefresh(savedAutoRefresh());
  } else if (tab === 'logs') {
    setLogAutoRefresh(parseInt($('log-auto-refresh').value, 10) || 0);
  }
}

// ---------- 自动刷新（设备面板，默认 1 秒） ----------

function stopAutoRefresh() {
  if (state.autoRefreshTimer) {
    clearInterval(state.autoRefreshTimer);
    state.autoRefreshTimer = null;
  }
}

function setAutoRefresh(seconds) {
  stopAutoRefresh();
  state.autoRefresh = seconds;
  if (seconds > 0 && state.currentTab === 'devices') {
    state.autoRefreshTimer = setInterval(refreshDevicesOnly, seconds * 1000);
  }
}

// ---------- 首页概览（v0.8.0，默认 1 秒刷新） ----------

function stopHomeRefresh() {
  if (state.homeTimer) {
    clearInterval(state.homeTimer);
    state.homeTimer = null;
  }
}

function startHomeRefresh() {
  stopHomeRefresh();
  const seconds = parseInt($('home-auto-refresh').value, 10);
  refreshOverview();
  if (seconds > 0) state.homeTimer = setInterval(refreshOverview, seconds * 1000);
}

/** 拉取概览指标（在线数/总数/百分比 + 今日采样曲线）并重绘三块图。 */
async function refreshOverview() {
  try {
    const data = await api('/api/admin/stats/overview');
    state.overview = data;
    renderOverview(data);
  } catch (e) {
    // 自动刷新失败静默，不打断界面
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
  const maxVal = Math.max(1, d.total, ...(d.samples || []).map(s => s.total), ...(d.samples || []).map(s => s.online));
  const xOf = ts => padL + Math.min(1, Math.max(0, (ts - dayStart) / 86400)) * plotW;
  const yOf = v => padT + plotH - (Math.min(v, maxVal) / maxVal) * plotH;

  // 采样点 + 实时点（当前在线数）
  const pts = (d.samples || []).map(s => ({ ts: s.ts, v: s.online }));
  pts.push({ ts: Math.min(dayEnd, d.now), v: d.online });
  const line = pts.map(p => `${xOf(p.ts).toFixed(1)},${yOf(p.v).toFixed(1)}`).join(' ');
  const area = pts.length
    ? `${padL},${padT + plotH} ${line} ${xOf(pts[pts.length - 1].ts).toFixed(1)},${padT + plotH}`
    : '';

  // Y 轴 5 条网格线（整数刻度）
  let grid = '';
  for (let i = 0; i <= 4; i++) {
    const v = Math.round(maxVal * i / 4);
    const y = yOf(v);
    grid += `<line x1="${padL}" y1="${y.toFixed(1)}" x2="${W - padR}" y2="${y.toFixed(1)}" stroke="#eef2f7" stroke-width="1"/>`
      + `<text x="${padL - 8}" y="${(y + 4).toFixed(1)}" text-anchor="end" font-size="11" fill="#94a3b8">${v}</text>`;
  }
  // X 轴每 4 小时一个刻度
  let xAxis = '';
  for (let h = 0; h <= 24; h += 4) {
    const x = padL + (h / 24) * plotW;
    xAxis += `<line x1="${x.toFixed(1)}" y1="${padT}" x2="${x.toFixed(1)}" y2="${padT + plotH}" stroke="#f6f8fb" stroke-width="1"/>`
      + `<text x="${x.toFixed(1)}" y="${H - 8}" text-anchor="middle" font-size="11" fill="#94a3b8">${String(h).padStart(2, '0')}:00</text>`;
  }
  const last = pts[pts.length - 1];
  const dot = last ? `<circle cx="${xOf(last.ts).toFixed(1)}" cy="${yOf(last.v).toFixed(1)}" r="3.5" fill="#1b7ae0"/>` : '';

  $('chart-line').innerHTML = grid + xAxis
    + (area ? `<polygon points="${area}" fill="rgba(27,122,224,.12)"/>` : '')
    + (pts.length > 1 ? `<polyline points="${line}" fill="none" stroke="#1b7ae0" stroke-width="2.2" stroke-linejoin="round"/>` : '')
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
    `<circle cx="${cx}" cy="${cy}" r="${(R + r) / 2}" fill="none" stroke="#eef2f7" stroke-width="${R - r}"/>`
    + seg(0, ratio, '#1b7ae0')
    + seg(ratio, 1, '#cbd5e1')
    + `<text x="${cx}" y="${cy + 2}" text-anchor="middle" font-size="34" font-weight="700" fill="#1b7ae0">${(d.percent || 0)}%</text>`
    + `<text x="${cx}" y="${cy + 26}" text-anchor="middle" font-size="12" fill="#94a3b8">在线占比</text>`;
}

// ---------- 操作日志（v0.8.0） ----------

function renderLogEventFilter() {
  const sel = $('log-filter-event');
  const events = state.logs.events.length
    ? state.logs.events
    : [{ value: 'all', label: '全部事件' }];
  sel.innerHTML = events.map(e => `<option value="${escAttr(e.value)}">${escHtml(e.label)}</option>`).join('');
}

function logQuery() {
  const from = $('log-filter-from').value;
  const to = $('log-filter-to').value;
  return {
    event: $('log-filter-event').value || 'all',
    q: $('log-filter-keyword').value.trim(),
    from: from ? String(Math.floor(new Date(from).getTime() / 1000)) : '',
    to: to ? String(Math.floor(new Date(to).getTime() / 1000)) : ''
  };
}

async function refreshLogs(resetPage = false) {
  if (resetPage) state.logs.page = 0;
  const q = logQuery();
  const params = new URLSearchParams({ limit: String(state.logs.size), offset: String(state.logs.page * state.logs.size) });
  if (q.event && q.event !== 'all') params.set('event', q.event);
  if (q.q) params.set('q', q.q);
  if (q.from) params.set('from', q.from);
  if (q.to) params.set('to', q.to);
  try {
    const data = await api('/api/admin/logs?' + params.toString());
    state.logs.items = data.items || [];
    state.logs.total = data.total || 0;
    if (data.events && data.events.length && !state.logs.events.length) {
      state.logs.events = data.events;
      renderLogEventFilter();
    }
    renderLogs();
  } catch (e) {
    // 自动刷新失败静默
  }
}

function renderLogs() {
  const tbody = $('tbl-logs').querySelector('tbody');
  const items = state.logs.items;
  tbody.innerHTML = items.length ? '' : '<tr><td colspan="9" class="muted">暂无日志</td></tr>';
  items.forEach(r => {
    const tr = document.createElement('tr');
    const bad = r.event === 'login_fail' || r.event === 'login_blocked' || (r.status && r.status >= 400);
    if (bad) tr.style.background = '#FDECEC';
    tr.innerHTML = `<td>${escHtml(r.ts)}</td><td>${escHtml(r.eventLabel || r.event)}</td>
      <td><code>${escHtml(r.ip || '')}</code></td><td>${escHtml(r.actor || '')}</td>
      <td>${r.deviceId ? '<code>' + escHtml(r.deviceId) + '</code>' : '<span class="muted">—</span>'}</td>
      <td>${escHtml(r.method || '')}</td><td>${escHtml(r.path || '')}</td>
      <td>${r.status || ''}</td><td class="detail">${escHtml(r.detail || '')}</td>`;
    tbody.appendChild(tr);
  });
  const pages = Math.max(1, Math.ceil(state.logs.total / state.logs.size));
  $('log-summary').textContent = `共 ${state.logs.total} 条（每页 ${state.logs.size} 条，保留 90 天）`;
  $('log-page').textContent = `第 ${state.logs.page + 1} / ${pages} 页`;
  $('btn-log-prev').disabled = state.logs.page <= 0;
  $('btn-log-next').disabled = state.logs.page + 1 >= pages;
}

function stopLogRefresh() {
  if (state.logTimer) {
    clearInterval(state.logTimer);
    state.logTimer = null;
  }
}

function setLogAutoRefresh(seconds) {
  stopLogRefresh();
  if (seconds > 0 && state.currentTab === 'logs') {
    state.logTimer = setInterval(() => refreshLogs(false), seconds * 1000);
  }
}

async function exportLogsCsv() {
  const q = logQuery();
  const params = new URLSearchParams();
  if (q.event && q.event !== 'all') params.set('event', q.event);
  if (q.q) params.set('q', q.q);
  if (q.from) params.set('from', q.from);
  if (q.to) params.set('to', q.to);
  try {
    const resp = await fetch('/api/admin/logs/export.csv?' + params.toString(), {
      headers: { 'Authorization': 'Bearer ' + state.token }
    });
    if (!resp.ok) throw new Error(`导出失败（${resp.status}）`);
    const blob = await resp.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `logs_${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '')}.csv`;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 5000);
  } catch (e) { alert(e.message); }
}

// ---------- 安全设置 ----------

async function saveSecurity() {
  setMsg('security-msg', '');
  try {
    const data = await api('/api/admin/security', {
      method: 'POST',
      body: JSON.stringify({ uaCheckEnabled: $('set-ua-check').checked })
    });
    setMsg('security-msg', data.uaCheckEnabled ? '已开启 UA 强制校验' : '已关闭 UA 校验', true);
  } catch (e) {
    setMsg('security-msg', e.message);
  }
}

// ---------- 启动 ----------

$('btn-setup').addEventListener('click', submitSetup);
$('btn-login').addEventListener('click', submitLogin);
$('btn-publish').addEventListener('click', publish);
$('btn-reload').addEventListener('click', loadDashboard);
$('btn-logout').addEventListener('click', logout);
$('btn-setup-add-bm').addEventListener('click', () => addBookmarkRow('setup-bookmarks'));
$('btn-f-add-bm').addEventListener('click', () => addBookmarkRow('f-bookmarks'));
$('btn-setup-add-app').addEventListener('click', () => addAppRow('setup-apps'));
$('btn-f-add-app').addEventListener('click', () => addAppRow('f-apps'));
$('btn-export-csv').addEventListener('click', openExportModal);
$('btn-save-security').addEventListener('click', saveSecurity);

// 标签切换
document.querySelectorAll('.tab').forEach(t =>
  t.addEventListener('click', () => switchTab(t.dataset.tab)));

// 设备筛选
$('device-filter-status').addEventListener('change', applyDeviceFilter);
$('device-filter-keyword').addEventListener('input', applyDeviceFilter);

// 自动刷新
$('device-auto-refresh').addEventListener('change', e => {
  const seconds = parseInt(e.target.value, 10) || 0;
  localStorage.setItem(AUTO_REFRESH_KEY, String(seconds));
  setAutoRefresh(seconds);
});
$('home-auto-refresh').addEventListener('change', () => startHomeRefresh());
$('log-auto-refresh').addEventListener('change', e =>
  setLogAutoRefresh(parseInt(e.target.value, 10) || 0));

// HTML 页编辑器
$('btn-html-new').addEventListener('click', newHtmlPage);
$('btn-html-save').addEventListener('click', saveHtmlPage);
$('btn-html-delete').addEventListener('click', deleteHtmlPage);
$('btn-html-preview').addEventListener('click', previewHtmlPage);
$('btn-html-copy').addEventListener('click', copyHtmlUrl);

// 操作日志
$('btn-log-query').addEventListener('click', () => refreshLogs(true));
$('btn-log-export').addEventListener('click', exportLogsCsv);
$('btn-log-prev').addEventListener('click', () => {
  if (state.logs.page > 0) { state.logs.page--; refreshLogs(false); }
});
$('btn-log-next').addEventListener('click', () => {
  const pages = Math.max(1, Math.ceil(state.logs.total / state.logs.size));
  if (state.logs.page + 1 < pages) { state.logs.page++; refreshLogs(false); }
});
$('btn-log-reset').addEventListener('click', () => {
  $('log-filter-event').value = 'all';
  $('log-filter-keyword').value = '';
  $('log-filter-from').value = '';
  $('log-filter-to').value = '';
  refreshLogs(true);
});
$('log-filter-keyword').addEventListener('keydown', e => {
  if (e.key === 'Enter') refreshLogs(true);
});

$('export-cancel').addEventListener('click', closeExportModal);
$('export-do').addEventListener('click', doExportCsv);
$('export-select-all').addEventListener('click', () => { $('export-columns').querySelectorAll('input').forEach(i => i.checked = true); });
$('export-select-none').addEventListener('click', () => { $('export-columns').querySelectorAll('input').forEach(i => i.checked = false); });

// 初始化向导默认给一行空书签
addBookmarkRow('setup-bookmarks');

// 设备面板自动刷新恢复上次选择（默认 1 秒/次，v0.8.0）
$('device-auto-refresh').value = String(savedAutoRefresh());

(async function init() {
  try {
    const status = await api('/api/setup/status');
    if (status.needsSetup) { show('setup'); return; }
    if (state.token) { await loadDashboard(); return; }
    show('login');
  } catch (e) {
    show('login');
  }
})();
