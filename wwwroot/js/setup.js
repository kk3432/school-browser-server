// 首次启动 · 初始化向导（v0.8.3 拆页后为独立页面）

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
    setToken(login.token);
    location.replace('admin.html');
  } catch (e) {
    setMsg('setup-msg', e.message);
  }
}

$('btn-setup').addEventListener('click', submitSetup);
$('btn-setup-add-bm').addEventListener('click', () => addBookmarkRow('setup-bookmarks'));
$('btn-setup-add-app').addEventListener('click', () => addAppRow('setup-apps'));

// 默认给一行空书签
addBookmarkRow('setup-bookmarks');
