import { post, get, toast, fmtNum } from '../api.js?v=13';
import { escapeHtml } from './indices.js?v=13';
import { showModal, closeAllModals } from './browse.js?v=13';

export function openExportDialog({ index, columns, getQuery }) {
  const selected = new Set(columns.map(c => c.path));

  showModal(`导出索引 ${index} → Excel`, `
    <div class="hint" id="exp-query" style="margin-bottom:12px"></div>
    <div class="field">
      <label>导出列（已选 <span id="exp-count">${columns.length}</span>/${columns.length}）</label>
      <input type="text" id="exp-search" placeholder="搜索字段名…">
      <div class="export-cols" id="exp-cols">
        ${columns.map(c => `
          <label><input type="checkbox" data-path="${escapeHtml(c.path)}" checked>
            ${escapeHtml(c.path)} <span style="color:var(--muted)">(${c.type})</span></label>`).join('')}
      </div>
    </div>
    <div style="display:flex; gap:16px">
      <label class="checkbox-row"><input type="checkbox" id="exp-id" checked> 包含 _id 列</label>
      <div class="field" style="flex:1">
        <label>行数上限</label>
        <input type="text" id="exp-max" value="100000" inputmode="numeric">
      </div>
    </div>
    <div class="hint">xlsx 单工作表上限 1,048,575 行；导出在后台执行，可随时取消</div>
    <div class="query-actions">
      <button class="btn btn-primary" id="exp-start" style="flex:1">开始导出</button>
      <button class="btn" id="exp-cancel-btn">关闭</button>
    </div>
    <div id="exp-progress" class="hidden">
      <div class="progress-bar"><div class="fill" id="exp-fill"></div></div>
      <div class="progress-text" id="exp-text"></div>
      <div class="query-actions">
        <button class="btn btn-danger" id="exp-stop">取消导出</button>
      </div>
    </div>`);

  const modalBody = document.querySelector('#modal-root .modal-body');
  updateQuerySummary();

  function updateQuerySummary() {
    let q;
    const box = modalBody.querySelector('#exp-query');
    try { q = getQuery(); } catch (e) {
      box.innerHTML = `<span style="color:#d9534f">当前查询语句有误：${escapeHtml(e.message)}</span>`;
      return;
    }
    if (!q) { box.innerHTML = '导出范围：<b>全部数据</b>'; return; }
    if (q.dsl) {
      box.innerHTML = '导出范围（DSL 查询）：<code>' +
        escapeHtml(JSON.stringify(q.dsl).slice(0, 120)) + (JSON.stringify(q.dsl).length > 120 ? '…' : '') + '</code>';
    } else if (q.queryString) {
      box.innerHTML = `导出范围（查询语句）：<code>${escapeHtml(q.queryString)}</code>`;
    } else {
      box.innerHTML = `导出范围（${q.conditions.length} 个条件）：` +
        q.conditions.map(c =>
          `<code>${escapeHtml(c.field)} ${escapeHtml(c.op)} ${escapeHtml(c.value ?? '')}${c.value2 ? '~' + escapeHtml(c.value2) : ''}</code>`
        ).join(' ');
    }
  }

  const search = modalBody.querySelector('#exp-search');
  search.addEventListener('input', () => {
    const kw = search.value.toLowerCase();
    modalBody.querySelectorAll('#exp-cols label').forEach(l => {
      const path = l.querySelector('input').dataset.path.toLowerCase();
      l.style.display = (!kw || path.includes(kw)) ? '' : 'none';
    });
  });
  modalBody.querySelectorAll('#exp-cols input').forEach(cb => cb.addEventListener('change', () => {
    const p = cb.dataset.path;
    cb.checked ? selected.add(p) : selected.delete(p);
    modalBody.querySelector('#exp-count').textContent = selected.size;
  }));

  modalBody.querySelector('#exp-cancel-btn').addEventListener('click', closeAllModals);

  modalBody.querySelector('#exp-start').addEventListener('click', async () => {
    const maxRows = parseInt(modalBody.querySelector('#exp-max').value, 10);
    if (Number.isNaN(maxRows) || maxRows <= 0) { toast('行数上限无效', true); return; }
    if (maxRows > 1048575) { toast('行数上限不得超过 1,048,575', true); return; }
    let q;
    try { q = getQuery(); } catch (e) { toast(e.message, true); return; }
    const cols = columns.filter(c => selected.has(c.path)).map(c => c.path);
    if (cols.length === 0) { toast('请至少选择一列', true); return; }
    try {
      const { taskId } = await post('/api/export', {
        index,
        query: q,
        columns: cols.length === columns.length ? null : cols,
        includeId: modalBody.querySelector('#exp-id').checked,
        maxRows
      });
      pollProgress(taskId);
    } catch (e) {
      toast(e.message, true);
    }
  });

  let currentTaskId = null;
  modalBody.querySelector('#exp-stop').addEventListener('click', async () => {
    if (!currentTaskId) return;
    try {
      await post(`/api/export/${currentTaskId}/cancel`);
      toast('已请求取消，等待任务退出…');
    } catch (e) {
      toast(e.message, true);
    }
  });

  function pollProgress(taskId) {
    currentTaskId = taskId;
    modalBody.querySelector('#exp-start').disabled = true;
    modalBody.querySelector('#exp-progress').classList.remove('hidden');
    const fill = modalBody.querySelector('#exp-fill');
    const text = modalBody.querySelector('#exp-text');

    const timer = setInterval(async () => {
      let t;
      try {
        t = await get(`/api/export/${taskId}`);
      } catch (e) {
        clearInterval(timer);
        toast(e.message, true);
        return;
      }
      const pct = t.percent != null ? t.percent.toFixed(1) : '';
      fill.style.width = (t.percent ?? (t.status === 'done' ? 100 : 5)) + '%';
      text.textContent = t.status === 'running' || t.status === 'queued'
        ? `${t.status === 'queued' ? '排队中' : '导出中'}：${fmtNum(t.rowsExported)} 行${pct ? `（${pct}%）` : ''}${t.total ? ` / ${fmtNum(t.total)}` : ''}`
        : '';
      if (t.status === 'done') {
        clearInterval(timer);
        text.textContent = `完成：${fmtNum(t.rowsExported)} 行 · ${t.fileName}，开始下载…`;
        location.href = `/api/export/${taskId}/download`;
        setTimeout(closeAllModals, 1500);
      } else if (t.status === 'canceled') {
        clearInterval(timer);
        text.textContent = '已取消';
        modalBody.querySelector('#exp-start').disabled = false;
      } else if (t.status === 'error') {
        clearInterval(timer);
        text.textContent = `失败：${t.error || '未知错误'}`;
        toast(t.error || '导出失败', true);
      }
    }, 1000);
  }
}
