// 控制台外壳（v0.8.3 拆页后）：令牌守卫 + 片段注入 + 标签切换 + 控制台数据加载
// 每个标签页的 HTML 在 pages/*.html，脚本在 js/pages/*.js，均通过 definePage 登记。

const TAB_ORDER = ['home', 'config', 'devices', 'html', 'logs', 'versions', 'settings'];

/** 把各标签页片段注入 #tab-panels，然后绑事件（只在启动时执行一次）。 */
async function loadPanels() {
  const container = $('tab-panels');
  for (const name of TAB_ORDER) {
    const def = pageOf(name);
    const div = document.createElement('div');
    div.className = 'tab-panel';
    div.id = 'panel-' + name;
    if (def.file) {
      try {
        const resp = await fetch(def.file, { cache: 'no-store' });
        if (!resp.ok) throw new Error(String(resp.status));
        div.innerHTML = await resp.text();
      } catch (e) {
        div.innerHTML = '<div class="card"><p class="msg err">面板片段加载失败：'
          + escHtml(def.file) + '（' + escHtml(e.message) + '）</p></div>';
      }
    }
    container.appendChild(div);
  }
  TAB_ORDER.forEach(n => pageOf(n).init?.());
}

function switchTab(tab) {
  const prev = state.currentTab;
  if (prev && prev !== tab) pageOf(prev).hide?.();
  state.currentTab = tab;
  document.querySelectorAll('.tab').forEach(t =>
    t.classList.toggle('active', t.dataset.tab === tab));
  TAB_ORDER.forEach(n => {
    const panel = $('panel-' + n);
    if (!panel) return;
    const on = n === tab;
    panel.classList.toggle('active', on);
    panel.style.display = on ? 'block' : 'none';
  });
  pageOf(tab).show?.();
}

async function loadDashboard() {
  try {
    const [config, versions, devices, key, htmlPages, logs] = await Promise.all([
      api('/api/admin/config'),
      api('/api/admin/config/versions'),
      api('/api/admin/devices'),
      api('/api/admin/public-key'),
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
    const payload = { config, versions, devices, key, htmlPages, logs };
    TAB_ORDER.forEach(n => pageOf(n).load?.(payload));
    $('screen-dashboard').classList.remove('hidden');
    switchTab(state.currentTab || 'home');
  } catch (e) {
    // 令牌失效/被吊销：回登录页重新登录
    clearToken();
    gotoLogin();
  }
}

/** 供页面模块（如版本回滚）触发的整份重载。 */
function adminReload() { return loadDashboard(); }

async function logout() {
  try { await api('/api/logout', { method: 'POST' }); } catch (e) { /* 忽略 */ }
  clearToken();
  gotoLogin();
}

(async function boot() {
  if (!state.token) { gotoLogin(); return; }
  await loadPanels();
  $('btn-reload').addEventListener('click', loadDashboard);
  $('btn-logout').addEventListener('click', logout);
  document.querySelectorAll('.tab').forEach(t =>
    t.addEventListener('click', () => switchTab(t.dataset.tab)));
  await loadDashboard();
})();
