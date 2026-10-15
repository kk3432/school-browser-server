// 操作日志：分页 / 筛选 / 自动刷新 / CSV 导出
let logTimer = null;

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
    if (bad) tr.classList.add('row-warn');
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
  if (logTimer) { clearInterval(logTimer); logTimer = null; }
}

function setLogAutoRefresh(seconds) {
  stopLogRefresh();
  if (seconds > 0 && state.currentTab === 'logs') {
    logTimer = setInterval(() => refreshLogs(false), seconds * 1000);
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

definePage('logs', {
  file: 'pages/logs.html',
  init() {
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
    $('log-auto-refresh').addEventListener('change', e =>
      setLogAutoRefresh(parseInt(e.target.value, 10) || 0));
  },
  load() {
    renderLogEventFilter();
    renderLogs();
  },
  show() { setLogAutoRefresh(parseInt($('log-auto-refresh').value, 10) || 0); },
  hide() { stopLogRefresh(); }
});
