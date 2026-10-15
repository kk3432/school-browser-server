// 配置管理：填表 → 发布新版本（平板密码已移到「系统设置」，与配置版本完全解耦）

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
  $('f-note').value = '';
}

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
    note: $('f-note').value
  };
  try {
    const data = await api('/api/admin/config/publish', {
      method: 'POST', body: JSON.stringify(body)
    });
    setMsg('pub-msg', `已发布版本 ${data.version}，平板将在下次轮询时生效`, true);
    $('f-note').value = '';
    const versions = await api('/api/admin/config/versions');
    pageOf('versions').load?.({ versions });
  } catch (e) {
    setMsg('pub-msg', e.message);
  }
}

definePage('config', {
  file: 'pages/config.html',
  init() {
    $('btn-publish').addEventListener('click', publish);
    $('btn-f-add-bm').addEventListener('click', () => addBookmarkRow('f-bookmarks'));
    $('btn-f-add-app').addEventListener('click', () => addAppRow('f-apps'));
  },
  load(payload) {
    fillForm(payload.config);
  },
  // HTML 页增删后由 html 页模块调用，重画勾选列表
  refreshPicker() {
    renderHtmlPicker((state.config && state.config.html_pages) || []);
  }
});
