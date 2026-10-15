// 校园浏览器服务端管理后台 · 公共核心（原生 JS，无构建依赖）
// 约定：任何来自接口的数据在拼进 innerHTML 前必须经过 escHtml()/escAttr()，防止存储型 XSS。

const $ = (id) => document.getElementById(id);

const state = {
  token: sessionStorage.getItem('cb_token') || '',
  config: null,
  devices: [],
  currentTab: 'home',
  htmlPages: [],
  currentHtmlId: 0,
  logs: { page: 0, size: 100, total: 0, items: [], events: [] },
  overview: null,
  // 概览增量刷新：最后一点的 ts、在途标记、折线图静态层缓存
  overviewLastTs: 0,
  overviewInFlight: false,
  chartStatic: null
};

// v0.8.3：令牌改存 sessionStorage（关标签页即失效），顺手清掉旧版留在 localStorage 的令牌
localStorage.removeItem('cb_token');
function setToken(t) { state.token = t; sessionStorage.setItem('cb_token', t); }
function clearToken() { state.token = ''; sessionStorage.removeItem('cb_token'); }

// 设备面板自动刷新频率偏好（非敏感，仍存 localStorage）
const AUTO_REFRESH_KEY = 'cb_auto_refresh';
function savedAutoRefresh() {
  const v = parseInt(localStorage.getItem(AUTO_REFRESH_KEY), 10);
  return Number.isFinite(v) ? v : 5;
}

async function api(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  if (state.token) headers['Authorization'] = 'Bearer ' + state.token;
  const resp = await fetch(path, { ...options, headers, cache: 'no-store' });
  const text = await resp.text();
  let data = {};
  try { data = text ? JSON.parse(text) : {}; } catch (e) { data = {}; }
  if (!resp.ok) throw new Error(data.error || `请求失败（${resp.status}）`);
  return data;
}

function setMsg(id, text, ok = false) {
  const el = $(id);
  if (!el) return;
  el.textContent = text;
  el.className = 'msg ' + (ok ? 'ok' : 'err');
}

function escAttr(s) {
  return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/"/g, '&quot;')
    .replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// HTML 文本转义：所有来自接口的动态数据在拼 innerHTML 前必须经过此函数，防止存储型 XSS。
function escHtml(s) {
  return String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

// ---------- 快捷书签 ----------

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

// ---------- 页面模块注册表 ----------

/**
 * 每个标签页一个模块：HTML 片段由 admin.js 注入 #tab-panels，脚本在这里登记生命周期钩子。
 *  - file：片段路径（相对 wwwroot）
 *  - init()：片段注入后绑定事件，只执行一次
 *  - load(payload)：控制台数据加载完成后填充表单/表格
 *  - show()/hide()：标签切入/切走（定时器只在可见时运行）
 */
const CB_PAGES = {};
function definePage(name, def) { CB_PAGES[name] = def; }
function pageOf(name) { return CB_PAGES[name] || {}; }

/** 进入登录页（令牌失效/未登录）。 */
function gotoLogin() { location.replace('index.html'); }
