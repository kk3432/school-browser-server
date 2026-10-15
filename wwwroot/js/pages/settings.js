// 系统设置：平板管理密码（与配置版本解耦）+ 配置签名公钥

/** 刷新平板密码状态（只回「是否已设置」，服务端从不回哈希）。 */
async function refreshPinStatus() {
  try {
    const data = await api('/api/admin/pin');
    $('set-pin').value = '';
    setMsg('pin-msg', '');
    $('pin-status').textContent = data.pinSet
      ? '当前已设置平板管理密码（6 位数字）。'
      : '当前尚未设置平板管理密码，平板上无法进入隐藏管理入口。';
  } catch (e) {
    $('pin-status').textContent = '状态读取失败：' + e.message;
  }
}

async function savePin() {
  setMsg('pin-msg', '');
  const pin = $('set-pin').value.trim();
  try {
    const data = await api('/api/admin/pin', {
      method: 'POST',
      body: JSON.stringify({ pin })
    });
    $('set-pin').value = '';
    setMsg('pin-msg', data.changed ? '已更新平板管理密码' : '留空未修改，密码保持不变', true);
    await refreshPinStatus();
  } catch (e) {
    setMsg('pin-msg', e.message);
  }
}

definePage('settings', {
  file: 'pages/settings.html',
  init() {
    $('btn-save-pin').addEventListener('click', savePin);
    $('set-pin').addEventListener('keydown', e => { if (e.key === 'Enter') savePin(); });
  },
  load(payload) {
    $('ta-pubkey').value = (payload.key && payload.key.publicKey) || '';
  },
  show() { refreshPinStatus(); }
});
