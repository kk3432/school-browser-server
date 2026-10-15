// 内置 HTML 页（v0.8.0）：服务端托管的静态页，平板以书签/快捷方式打开

function fmtSize(bytes) {
  const n = Number(bytes) || 0;
  return n >= 1024 ? (n / 1024).toFixed(1) + ' KB' : n + ' B';
}

/** 拉取 HTML 页列表并渲染左侧列表 + 配置表单勾选项。 */
async function refreshHtmlPages() {
  state.htmlPages = await api('/api/admin/html-pages');
  renderHtmlList();
  // 配置管理里的勾选列表依赖同一份数据，同步重画
  pageOf('config').refreshPicker?.();
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
    div.innerHTML = `<div class="t">${escHtml(p.title || p.slug)}</div><div class="s">/h/${escHtml(p.slug)} · ${fmtSize(p.size)}</div>`;
    div.addEventListener('click', () => selectHtmlPage(p.id));
    box.appendChild(div);
  });
}

async function selectHtmlPage(id) {
  const p = state.htmlPages.find(x => x.id === id);
  if (!p) return;
  state.currentHtmlId = id;
  $('html-id').value = String(p.id);
  $('html-title').value = p.title || '';
  $('html-slug').value = p.slug || '';
  $('html-url-line').textContent = '访问地址：' + (p.url || '/h/' + p.slug);
  setMsg('html-msg', '');
  renderHtmlList();
  // v0.8.2：列表接口不再带正文（10 页最多 5 MB），选中时按需取单页正文
  $('html-body').value = '加载中…';
  try {
    const detail = await api('/api/admin/html-pages/' + id);
    if (state.currentHtmlId === id) $('html-body').value = detail.html || '';
  } catch (e) {
    if (state.currentHtmlId === id) {
      $('html-body').value = '';
      setMsg('html-msg', '正文加载失败：' + e.message);
    }
  }
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

definePage('html', {
  file: 'pages/html.html',
  init() {
    $('btn-html-new').addEventListener('click', newHtmlPage);
    $('btn-html-save').addEventListener('click', saveHtmlPage);
    $('btn-html-delete').addEventListener('click', deleteHtmlPage);
    $('btn-html-preview').addEventListener('click', previewHtmlPage);
    $('btn-html-copy').addEventListener('click', copyHtmlUrl);
  },
  load() { renderHtmlList(); }
});
