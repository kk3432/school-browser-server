// 校园浏览器服务端管理后台（原生 JS，无构建依赖）
const $ = (id) => document.getElementById(id);

const state = {
  token: localStorage.getItem('cb_token') || '',
  config: null
};

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
    allowedApps: collectApps('setup-apps'),
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
    const [config, versions, devices, key] = await Promise.all([
      api('/api/admin/config'),
      api('/api/admin/config/versions'),
      api('/api/admin/devices'),
      api('/api/admin/public-key')
    ]);
    state.config = config;
    fillForm(config);
    renderVersions(versions);
    renderDevices(devices);
    $('ta-pubkey').value = key.publicKey || '';
    show('dashboard');
  } catch (e) {
    state.token = '';
    localStorage.removeItem('cb_token');
    show('login');
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
  $('f-rules').value = (c.rules || []).join('\n');
  fillBookmarks('f-bookmarks', c.bookmarks);
  fillApps('f-apps', c.allowed_apps);
  $('f-pin').value = '';
  $('f-note').value = '';
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
    allowedApps: collectApps('f-apps'),
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

function renderDevices(list) {
  const tbody = $('tbl-devices').querySelector('tbody');
  tbody.innerHTML = list.length
    ? ''
    : '<tr><td colspan="8" class="muted">暂无设备注册</td></tr>';
  list.forEach(d => {
    const tr = document.createElement('tr');
    const status = d.online
      ? '<span style="color:#2E9E5B;font-weight:700;">●</span> 在线'
      : '<span style="color:#9AA9BA;">●</span> 离线';
    tr.innerHTML = `<td>${status}</td><td><code>${escHtml(d.deviceId)}</code></td><td>${escHtml(d.name || '')}</td>
      <td>${escHtml(d.appVersion || '')}</td><td>${escHtml(d.configVersion || '')}</td>
      <td>${d.ipAddress ? '<code>' + escHtml(d.ipAddress) + '</code>' : '<span class="muted">—</span>'}</td>
      <td>${escHtml(d.registeredAt)}</td><td>${escHtml(d.lastSeen)}</td>`;
    tbody.appendChild(tr);
  });
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

// 初始化向导默认给一行空书签
addBookmarkRow('setup-bookmarks');

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
