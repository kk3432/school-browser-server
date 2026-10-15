// 版本历史：发布记录 + 回滚（回滚只改配置内容，不会改动平板管理密码）

async function rollback(version) {
  if (!confirm(`确认回滚到 ${version}？将以该内容生成一个新版本下发。`)) return;
  try {
    const data = await api(`/api/admin/config/rollback?version=${encodeURIComponent(version)}`, {
      method: 'POST'
    });
    alert(`已回滚并发布为 ${data.version}`);
    await adminReload();
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

definePage('versions', {
  file: 'pages/versions.html',
  load(payload) { renderVersions(payload.versions || []); }
});
