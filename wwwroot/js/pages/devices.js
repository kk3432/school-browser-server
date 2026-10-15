// 设备管理：列表 / 筛选 / 照片查看 / 自动刷新 / CSV 导出
let deviceTimer = null;

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

function stopAutoRefresh() {
  if (deviceTimer) { clearInterval(deviceTimer); deviceTimer = null; }
}

function setAutoRefresh(seconds) {
  stopAutoRefresh();
  if (seconds > 0 && state.currentTab === 'devices') {
    deviceTimer = setInterval(refreshDevicesOnly, seconds * 1000);
  }
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
      ? '<span class="dot-online-text">●</span> 在线'
      : '<span class="dot-offline-text">●</span> 离线';
    const photoCell = d.photoSkipReason
      ? '<span class="muted" title="' + escAttr(d.photoSkipReason) + '">跳过（' + escHtml(d.photoSkipReason) + '）</span>'
      : d.lastPhotoAt
        ? escHtml(d.lastPhotoAt)
        : '<span class="muted">—</span>';
    // 输错密码拍照：最近 10 分钟内有记录则整行标红告警
    const wrongPinRecent = d.lastWrongPinPhotoAt &&
      (now - new Date(d.lastWrongPinPhotoAt.replace(' ', 'T')).getTime()) < 10 * 60 * 1000;
    if (wrongPinRecent) tr.classList.add('row-warn');
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
      <td class="nowrap">${viewBtn} ${viewWrongBtn}</td>`;
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
    label.className = 'export-col';
    label.innerHTML = `<input type="checkbox" value="${c.key}" checked> ${escHtml(c.label)}`;
    box.appendChild(label);
  });
  $('export-from').value = '';
  $('export-to').value = '';
  $('export-all').checked = true;
  $('export-csv-modal').classList.add('open');
}

function closeExportModal() { $('export-csv-modal').classList.remove('open'); }

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

definePage('devices', {
  file: 'pages/devices.html',
  init() {
    $('device-filter-status').addEventListener('change', applyDeviceFilter);
    $('device-filter-keyword').addEventListener('input', applyDeviceFilter);
    $('device-auto-refresh').addEventListener('change', e => {
      const seconds = parseInt(e.target.value, 10) || 0;
      localStorage.setItem(AUTO_REFRESH_KEY, String(seconds));
      setAutoRefresh(seconds);
    });
    // 恢复上次选择（默认 5 秒/次）
    $('device-auto-refresh').value = String(savedAutoRefresh());

    $('btn-export-csv').addEventListener('click', openExportModal);
    $('export-cancel').addEventListener('click', closeExportModal);
    $('export-do').addEventListener('click', doExportCsv);
    $('export-select-all').addEventListener('click', () => { $('export-columns').querySelectorAll('input').forEach(i => i.checked = true); });
    $('export-select-none').addEventListener('click', () => { $('export-columns').querySelectorAll('input').forEach(i => i.checked = false); });
  },
  load() { applyDeviceFilter(); },
  show() { setAutoRefresh(savedAutoRefresh()); },
  hide() { stopAutoRefresh(); }
});
