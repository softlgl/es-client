import { get, post, put, del, toast, fmtNum } from '../api.js?v=13';
import { escapeHtml } from './indices.js?v=13';
import { openExportDialog } from './export-dialog.js?v=13';
import { openAiDialog } from './ai-dialog.js?v=13';

const OPS = [
  { v: 'eq', label: '等于' }, { v: 'ne', label: '不等于' },
  { v: 'contains', label: '包含', warn: true }, { v: 'prefix', label: '前缀' },
  { v: 'between', label: '介于' }, { v: 'gt', label: '大于' }, { v: 'gte', label: '≥' },
  { v: 'lt', label: '小于' }, { v: 'lte', label: '≤' },
  { v: 'exists', label: '存在' }, { v: 'missing', label: '不存在' }
];
const RANGE_OPS = new Set(['between', 'gt', 'gte', 'lt', 'lte']);
const NO_VALUE_OPS = new Set(['exists', 'missing']);

// 每个索引的浏览状态（条件/翻页/列选择/排序），切换索引后切回可恢复
const stateCache = new Map();

// 断开/切换连接时调用：不同集群同名索引结构可能不同
export function clearBrowseCache() { stateCache.clear(); }

export async function renderBrowse(view, index) {
  view.innerHTML = '<div class="panel browse-toolbar">加载中…</div>';
  let meta;
  try {
    meta = await get(`/api/indices/${encodeURIComponent(index)}/meta`);
  } catch (e) {
    toast(e.message, true);
    location.hash = '#/indices';
    return;
  }
  if (meta.status !== 'open') {
    view.innerHTML = `<div class="panel empty-hint">索引 ${escapeHtml(index)} 处于关闭状态</div>`;
    return;
  }

  const isNew = !stateCache.has(index);
  const state = isNew ? {
    index,
    meta,
    pageSize: 50,
    conditions: [],
    queryString: '',
    qsMode: 'qs',             // 查询语句模式：query_string | DSL JSON
    dslText: '',
    tab: 'simple',
    sort: null,               // {field, dir}
    visible: new Set(meta.columns.slice(0, 30).map(c => c.path)),
    history: [{ mode: 'from', from: 0 }], // 页起点栈（栈顶=当前页）
    current: null,            // 当前页数据
    sampled: false
  } : stateCache.get(index);
  state.meta = meta; // meta 每次重新拉取（服务端有缓存）
  if (isNew) stateCache.set(index, state);

  // 无 mapping 时用首页文档采样兜底
  if (meta.columns.length === 0) state.sampled = true;

  view.innerHTML = `
    <div class="browse-layout">
      <aside class="query-panel">
        <div class="panel">
          <div class="query-tabs">
            <div class="query-tab${state.tab === 'simple' ? ' active' : ''}" data-tab="simple">简单条件</div>
            <div class="query-tab${state.tab === 'query' ? ' active' : ''}" data-tab="query">查询语句</div>
          </div>
          <div id="cond-area"${state.tab === 'query' ? ' class="hidden"' : ''}></div>
          <div id="qs-area" class="${state.tab === 'query' ? '' : 'hidden'}">
            <div class="qs-mode">
              <label><input type="radio" name="qsmode" value="qs" ${state.qsMode !== 'dsl' ? 'checked' : ''}> query_string</label>
              <label><input type="radio" name="qsmode" value="dsl" ${state.qsMode === 'dsl' ? 'checked' : ''}> DSL（JSON）</label>
              <span class="spacer"></span>
              <button class="btn btn-ghost" id="btn-ai" title="用 AI 根据自然语言生成 DSL 查询">✨ AI 生成</button>
            </div>
            <div class="field" id="qs-qs-wrap" ${state.qsMode === 'dsl' ? 'hidden' : ''}>
              <textarea id="qs-input" rows="6" placeholder="query_string 语法&#10;例：status:paid AND amount>100">${escapeHtml(state.queryString)}</textarea>
            </div>
            <div class="field" id="qs-dsl-wrap" ${state.qsMode === 'dsl' ? '' : 'hidden'}>
              <textarea id="dsl-input" rows="12" wrap="off" placeholder='完整查询请求体 JSON，例：&#10;{ "query": { "bool": { "must": [ { "term": { "status": "paid" } } ] } } }'>${escapeHtml(state.dslText)}</textarea>
            </div>
          </div>
          <div class="query-actions">
            <button class="btn btn-primary" id="btn-run" style="flex:1">查询</button>
            <button class="btn" id="btn-reset">重置</button>
          </div>
          <div class="hint" id="query-hint"></div>
        </div>
      </aside>
      <div class="browse-main">
        <div class="panel">
          <div class="browse-toolbar">
            <span class="idx-name">${escapeHtml(index)}</span>
            <span class="docs" id="docs-info"></span>
            <span class="spacer"></span>
            <select class="page-size" id="page-size">
              ${[10, 25, 50, 100, 200].map(n =>
                `<option value="${n}" ${n === state.pageSize ? 'selected' : ''}>${n} 条/页</option>`).join('')}
            </select>
            <button class="btn" id="btn-newdoc">新增文档</button>
            <button class="btn" id="btn-mapping">字段结构</button>
            <button class="btn" id="btn-cols">列</button>
            <button class="btn btn-primary" id="btn-export">导出 Excel</button>
          </div>
          <div class="table-wrap" id="table-wrap">
            <div class="empty-hint">点击"查询"加载数据</div>
          </div>
          <div class="pagination" id="pager"></div>
        </div>
      </div>
    </div>`;

  const $ = sel => view.querySelector(sel);

  // ---------- 查询面板 ----------
  function drawConditions() {
    const area = $('#cond-area');
    const opts = f => meta.columns.map(c =>
      `<option value="${escapeHtml(c.path)}">${escapeHtml(c.path)} (${c.type})</option>`).join('');
    area.innerHTML = state.conditions.map((c, i) => {
      const opOpts = OPS.map(o =>
        `<option value="${o.v}" ${c.op === o.v ? 'selected' : ''}>${o.label}</option>`).join('');
      const needVal = !NO_VALUE_OPS.has(c.op);
      return `
        <div class="cond-row" data-i="${i}">
          <select class="c-field">${opts()}</select>
          <select class="c-op">${opOpts}</select>
          ${c.op === 'between'
            ? `<input class="c-val" placeholder="起" value="${escapeHtml(c.value || '')}">
               <input class="c-val2" placeholder="止" value="${escapeHtml(c.value2 || '')}">`
            : `<input class="c-val" placeholder="值" value="${escapeHtml(c.value || '')}" ${needVal ? '' : 'style="display:none"'}>`}
          <button class="btn btn-ghost c-del" title="删除">×</button>
        </div>`;
    }).join('') +
      `<button class="btn" id="btn-add-cond" style="width:100%">+ 添加条件</button>`;
    area.querySelectorAll('.cond-row').forEach(row => {
      const i = +row.dataset.i;
      row.querySelector('.c-field').value = state.conditions[i].field ?? '';
      row.querySelector('.c-field').addEventListener('change', e => { state.conditions[i].field = e.target.value; });
      row.querySelector('.c-op').addEventListener('change', e => {
        state.conditions[i].op = e.target.value;
        drawConditions();
      });
      row.querySelector('.c-val').addEventListener('input', e => { state.conditions[i].value = e.target.value; });
      const v2 = row.querySelector('.c-val2');
      if (v2) v2.addEventListener('input', e => { state.conditions[i].value2 = e.target.value; });
      row.querySelector('.c-del').addEventListener('click', () => {
        state.conditions.splice(i, 1);
        drawConditions();
      });
    });
    area.querySelector('#btn-add-cond').addEventListener('click', () => {
      state.conditions.push({ field: meta.columns[0]?.path || '', op: 'eq', value: '', value2: null });
      drawConditions();
    });
  }

  view.querySelectorAll('.query-tab').forEach(t => t.addEventListener('click', () => {
    state.tab = t.dataset.tab;
    view.querySelectorAll('.query-tab').forEach(x => x.classList.toggle('active', x === t));
    $('#cond-area').classList.toggle('hidden', state.tab !== 'simple');
    $('#qs-area').classList.toggle('hidden', state.tab !== 'query');
  }));

  // query_string / DSL 模式切换
  function setQsMode(mode) {
    state.qsMode = mode;
    $('#qs-qs-wrap').hidden = mode === 'dsl';
    $('#qs-dsl-wrap').hidden = mode !== 'dsl';
    view.querySelectorAll('input[name=qsmode]').forEach(r => { r.checked = r.value === mode; });
  }
  view.querySelectorAll('input[name=qsmode]').forEach(r => r.addEventListener('change', () => setQsMode(r.value)));

  function buildQuery() {
    if (state.tab === 'simple') {
      const conds = state.conditions.filter(c =>
        c.field && (NO_VALUE_OPS.has(c.op) || (c.value ?? '') !== ''));
      if (conds.length === 0) return null;
      return { conditions: conds.map(c => ({ field: c.field, op: c.op, value: c.value || null, value2: c.value2 || null })), queryString: null };
    }
    if (state.qsMode === 'dsl') {
      const t = $('#dsl-input').value.trim();
      state.dslText = $('#dsl-input').value;
      if (!t) return null;
      let obj;
      try { obj = JSON.parse(t); } catch (e) {
        throw new Error(`DSL 不是合法 JSON：${e.message}`);
      }
      if (!obj || typeof obj !== 'object' || Array.isArray(obj))
        throw new Error('DSL 需为一个 JSON 对象，如 { "query": { ... } }');
      if (obj.query == null)
        throw new Error('DSL 缺少 query 子句，格式应为 { "query": { ... } }');
      return { conditions: [], queryString: null, dsl: obj };
    }
    const qs = $('#qs-input').value.trim();
    if (!qs) return null;
    return { conditions: [], queryString: qs };
  }

  function resetPages(start = { mode: 'from', from: 0 }) {
    state.history = [start];
  }

  $('#btn-run').addEventListener('click', () => {
    state.queryString = $('#qs-input').value;
    if ($('#dsl-input')) state.dslText = $('#dsl-input').value;
    resetPages();
    load();
  });
  $('#btn-reset').addEventListener('click', () => {
    state.conditions = [];
    $('#qs-input').value = '';
    state.queryString = '';
    if ($('#dsl-input')) { $('#dsl-input').value = ''; state.dslText = ''; }
    state.sort = null;
    drawConditions();
    updateHint();
    resetPages();
    load();
  });

  // AI 生成 DSL：弹窗产出后填入 DSL 模式并切过去
  $('#btn-ai').addEventListener('click', () => {
    state.queryString = $('#qs-input').value;
    if ($('#dsl-input')) state.dslText = $('#dsl-input').value;
    openAiDialog({
      index,
      applyDsl(text) {
        state.tab = 'query';
        view.querySelectorAll('.query-tab').forEach(x => x.classList.toggle('active', x.dataset.tab === 'query'));
        $('#cond-area').classList.add('hidden');
        $('#qs-area').classList.remove('hidden');
        setQsMode('dsl');
        $('#dsl-input').value = text;
        state.dslText = text;
        $('#dsl-input').focus();
        toast('AI 查询已填入，点击「查询」执行');
      }
    });
  });

  function updateHint() {
    const h = $('#query-hint');
    const warn = state.conditions.some(c => c.op === 'contains');
    h.innerHTML = (warn ? '<div class="hint-warn">“包含”用通配符实现，大索引上可能较慢</div>' : '') +
      (state.sampled ? '<div>该索引无 mapping，列由文档采样生成</div>' : '') +
      `<div>共 ${meta.columns.length} 个字段 · max_result_window ${fmtNum(meta.maxResultWindow)}</div>`;
  }

  // ---------- 工具条 ----------
  $('#page-size').addEventListener('change', e => {
    state.pageSize = +e.target.value;
    resetPages();
    load();
  });
  $('#btn-export').addEventListener('click', () => openExportDialog({
    index,
    columns: meta.columns,
    getQuery: buildQuery
  }));
  $('#btn-cols').addEventListener('click', () => openColumnSelector(''));
  $('#btn-newdoc').addEventListener('click', () => openNewDocDialog());
  $('#btn-mapping').addEventListener('click', async () => {
    try {
      const mapping = await get(`/api/indices/${encodeURIComponent(index)}/mapping`);
      showModal(`字段结构 — ${index}`, `<pre>${escapeHtml(JSON.stringify(mapping, null, 2))}</pre>`);
    } catch (e) {
      toast(e.message, true);
    }
  });

  function openColumnSelector(kw) {
    const list = meta.columns.filter(c => !kw || c.path.toLowerCase().includes(kw.toLowerCase()));
    showModal(`列选择（已选 ${state.visible.size}/${meta.columns.length}）`, `
      <input type="text" id="col-search" placeholder="搜索字段名…" value="${escapeHtml(kw || '')}">
      <div class="export-cols" id="col-list">
        ${list.map(c => `
          <label><input type="checkbox" data-path="${escapeHtml(c.path)}"
            ${state.visible.has(c.path) ? 'checked' : ''}>
            ${escapeHtml(c.path)} <span style="color:var(--muted)">(${c.type})</span></label>`).join('') ||
          '<div class="hint">无匹配字段</div>'}
      </div>
      <div class="query-actions">
        <button class="btn" id="col-all">全选（当前列表）</button>
        <button class="btn" id="col-first30">恢复默认 30 列</button>
      </div>`, () => {
      const search = modalRoot.querySelector('#col-search');
      search.addEventListener('input', () => {
        const v = search.value;
        // 防抖重建但保持弹窗
        clearTimeout(search._t);
        search._t = setTimeout(() => rebuildColumnList(v), 200);
      });
      function rebuildColumnList(k) {
        const sub = meta.columns.filter(c => !k || c.path.toLowerCase().includes(k.toLowerCase()));
        const box = modalRoot.querySelector('#col-list');
        box.innerHTML = sub.map(c => `
          <label><input type="checkbox" data-path="${escapeHtml(c.path)}"
            ${state.visible.has(c.path) ? 'checked' : ''}>
            ${escapeHtml(c.path)} <span style="color:var(--muted)">(${c.type})</span></label>`).join('') ||
          '<div class="hint">无匹配字段</div>';
        bindBoxes();
      }
      function bindBoxes() {
        modalRoot.querySelectorAll('#col-list input').forEach(cb => cb.addEventListener('change', () => {
          const p = cb.dataset.path;
          cb.checked ? state.visible.add(p) : state.visible.delete(p);
          modalRoot.querySelector('.modal-head h3').textContent =
            `列选择（已选 ${state.visible.size}/${meta.columns.length}）`;
          if (state.current) drawTable();
        }));
      }
      bindBoxes();
      modalRoot.querySelector('#col-all').addEventListener('click', () => {
        modalRoot.querySelectorAll('#col-list input').forEach(cb => { cb.checked = true; state.visible.add(cb.dataset.path); });
        modalRoot.querySelector('.modal-head h3').textContent =
          `列选择（已选 ${state.visible.size}/${meta.columns.length}）`;
        if (state.current) drawTable();
      });
      modalRoot.querySelector('#col-first30').addEventListener('click', () => {
        state.visible = new Set(meta.columns.slice(0, 30).map(c => c.path));
        if (state.current) drawTable();
        closeAllModals();
        openColumnSelector('');
      });
      search.focus();
    });
  }

  // ---------- 数据加载 ----------
  async function load() {
    const wrap = $('#table-wrap');
    wrap.innerHTML = '<div class="empty-hint">加载中…</div>';
    const start = state.history[state.history.length - 1];
    let body;
    try {
      body = {
        mode: start.mode,
        size: state.pageSize,
        query: buildQuery(),
        sort: state.sort
      };
      if (start.mode === 'from') body.from = start.from;
      else body.after = start.after;
    } catch (e) {
      wrap.innerHTML = `<div class="empty-hint">${escapeHtml(e.message)}</div>`;
      $('#pager').innerHTML = '';
      toast(e.message, true);
      return;
    }
    try {
      const res = await post(`/api/indices/${encodeURIComponent(index)}/documents`, body);
      state.current = res;
      drawTable();
      drawPager();
      $('#docs-info').textContent = res.total != null ? `共 ${fmtNum(res.total)} 条文档` : '';
    } catch (e) {
      wrap.innerHTML = `<div class="empty-hint">加载失败：${escapeHtml(e.message)}</div>`;
      $('#pager').innerHTML = '';
      toast(e.message, true);
    }
  }

  function goNext() {
    const cur = state.current;
    if (!cur?.nextAfter) return;
    state.history.push({ mode: 'after', after: cur.nextAfter });
    load();
  }
  function goPrev() {
    if (state.history.length <= 1) return;
    state.history.pop();
    load();
  }
  function goFrom(from) {
    resetPages({ mode: 'from', from });
    load();
  }

  // ---------- 表格 ----------
  function drawTable() {
    const wrap = $('#table-wrap');
    const res = state.current;
    const visibleCols = meta.columns.filter(c => state.visible.has(c.path));
    if (!res || res.rows.length === 0) {
      wrap.innerHTML = '<div class="empty-hint">无数据</div>';
      return;
    }
    const sorted = state.sort;
    const arrow = f => sorted?.field === f ? (sorted.dir === 'asc' ? ' ▲' : ' ▼') : '';

    wrap.innerHTML = `
      <table class="data">
        <thead><tr>
          <th class="_id-h" style="min-width:120px">_id</th>
          ${visibleCols.map(c => `
            <th class="${c.sortable ? 'sortable' : ''}" data-field="${escapeHtml(c.path)}"
                title="${c.sortable ? '点击排序' : '该类型不支持排序'}">
              ${escapeHtml(c.path)}${arrow(c.path)}
              <span class="col-type">${escapeHtml(c.type)}</span>
            </th>`).join('')}
        </tr></thead>
        <tbody>
          ${res.rows.map(r => `
            <tr data-id="${escapeHtml(r.id)}">
              <td class="_id-cell" title="${escapeHtml(r.id)}">${escapeHtml(r.id)}</td>
              ${visibleCols.map((c, i) => {
                const cell = r.cells[meta.columns.indexOf(c)];
                return `<td>${renderCell(cell, r.id)}</td>`;
              }).join('')}
            </tr>`).join('')}
        </tbody>
      </table>`;

    wrap.querySelectorAll('th.sortable').forEach(th => th.addEventListener('click', () => {
      const f = th.dataset.field;
      if (state.sort?.field === f) {
        state.sort = state.sort.dir === 'asc' ? { field: f, dir: 'desc' } : null;
      } else {
        state.sort = { field: f, dir: 'asc' };
      }
      resetPages();
      load();
    }));
    wrap.querySelectorAll('tbody tr').forEach(tr =>
      tr.addEventListener('click', () => openDocDetail(tr.dataset.id)));
  }

  function renderCell(cell, rowId) {
    if (cell == null || cell.v === null || cell.v === undefined) return '';
    if (cell.kind === 'json') return `<span class="cell-json" data-id="${escapeHtml(rowId)}" title="${escapeHtml(cell.v)}">${escapeHtml(cell.v)}</span>`;
    if (cell.kind === 'longjson') return `<span class="cell-json cell-long" data-id="${escapeHtml(rowId)}" title="点击查看完整值">{…}</span>`;
    if (cell.kind === 'long') return `<span class="cell-long" data-id="${escapeHtml(rowId)}" title="点击查看完整值">${escapeHtml(cell.v)}…</span>`;
    return escapeHtml(cell.v);
  }

  async function openDocDetail(id) {
    let json;
    try {
      json = await get(`/api/indices/${encodeURIComponent(index)}/documents/${encodeURIComponent(id)}`);
    } catch (e) {
      toast(e.message, true);
      return;
    }
    const orig = JSON.stringify(json._source ?? json, null, 2);
    showModal(`文档 ${id}`, `
      <pre id="doc-view">${escapeHtml(JSON.stringify(json, null, 2))}</pre>
      <textarea class="doc-editor hidden" id="doc-editor" spellcheck="false"></textarea>
      <div class="doc-actions">
        <button class="btn btn-danger" id="doc-del">删除</button>
        <span style="flex:1"></span>
        <button class="btn hidden" id="doc-cancel">取消</button>
        <button class="btn btn-primary hidden" id="doc-save">保存</button>
        <button class="btn" id="doc-edit">编辑</button>
      </div>`);
    const view = modalRoot.querySelector('#doc-view');
    const editor = modalRoot.querySelector('#doc-editor');
    const btnEdit = modalRoot.querySelector('#doc-edit');
    const btnSave = modalRoot.querySelector('#doc-save');
    const btnCancel = modalRoot.querySelector('#doc-cancel');
    const setMode = editing => {
      view.classList.toggle('hidden', editing);
      editor.classList.toggle('hidden', !editing);
      btnEdit.classList.toggle('hidden', editing);
      btnSave.classList.toggle('hidden', !editing);
      btnCancel.classList.toggle('hidden', !editing);
      if (editing) { editor.value = orig; editor.focus(); }
    };
    btnEdit.addEventListener('click', () => setMode(true));
    btnCancel.addEventListener('click', () => setMode(false));
    btnSave.addEventListener('click', async () => {
      let doc;
      try { doc = JSON.parse(editor.value); }
      catch { toast('JSON 格式错误，请检查', true); return; }
      if (!doc || typeof doc !== 'object' || Array.isArray(doc)) {
        toast('请输入 JSON 对象', true);
        return;
      }
      btnSave.disabled = true;
      try {
        await put(`/api/indices/${encodeURIComponent(index)}/documents/${encodeURIComponent(id)}`, doc);
        closeAllModals();
        toast('文档已保存');
        window.dispatchEvent(new Event('indices-changed'));
        load();
      } catch (e) {
        toast(e.message, true);
        btnSave.disabled = false;
      }
    });
    modalRoot.querySelector('#doc-del').addEventListener('click', () => {
      showModal('删除文档', `
        <div class="del-confirm">
          <p class="del-warn">将删除文档 <code>${escapeHtml(id)}</code>，该操作不可恢复。</p>
          <div class="del-actions">
            <button class="btn" id="deldoc-cancel">取消</button>
            <button class="btn btn-danger" id="deldoc-ok">确认删除</button>
          </div>
        </div>`);
      modalRoot.querySelector('#deldoc-cancel').addEventListener('click', closeAllModals);
      modalRoot.querySelector('#deldoc-ok').addEventListener('click', async () => {
        try {
          await del(`/api/indices/${encodeURIComponent(index)}/documents/${encodeURIComponent(id)}`);
          closeAllModals();
          toast(`文档 ${id} 已删除`);
          window.dispatchEvent(new Event('indices-changed'));
          load();
        } catch (e) {
          toast(e.message, true);
        }
      });
    });
  }

  function openNewDocDialog() {
    showModal('新增文档', `
      <div class="doc-form">
        <input type="text" class="doc-id-input" id="newdoc-id" placeholder="_id（留空自动生成）" autocomplete="off">
        <textarea class="doc-editor" id="newdoc-json" spellcheck="false"
                  placeholder='{ "field": "value" }' style="min-height:200px"></textarea>
        <div class="del-actions">
          <button class="btn" id="newdoc-cancel">取消</button>
          <button class="btn btn-primary" id="newdoc-ok">保存</button>
        </div>
      </div>`);
    modalRoot.querySelector('#newdoc-cancel').addEventListener('click', closeAllModals);
    modalRoot.querySelector('#newdoc-ok').addEventListener('click', async () => {
      const ok = modalRoot.querySelector('#newdoc-ok');
      let doc;
      try { doc = JSON.parse(modalRoot.querySelector('#newdoc-json').value); }
      catch { toast('JSON 格式错误，请检查', true); return; }
      if (!doc || typeof doc !== 'object' || Array.isArray(doc)) {
        toast('请输入 JSON 对象', true);
        return;
      }
      ok.disabled = true;
      try {
        const res = await post(`/api/indices/${encodeURIComponent(index)}/docs`,
          { id: modalRoot.querySelector('#newdoc-id').value.trim() || null, doc });
        closeAllModals();
        toast(`文档已新增：${res.id}`);
        window.dispatchEvent(new Event('indices-changed'));
        load();
      } catch (e) {
        toast(e.message, true);
        ok.disabled = false;
      }
    });
    modalRoot.querySelector('#newdoc-json').focus();
  }

  // ---------- 分页条 ----------
  function drawPager() {
    const pager = $('#pager');
    const res = state.current;
    if (!res) { pager.innerHTML = ''; return; }
    const cur = state.history[state.history.length - 1];
    const canPrev = state.history.length > 1;
    const canNext = !!res.nextAfter && res.rows.length === state.pageSize;

    if (res.mode === 'from') {
      const page = cur.from / state.pageSize + 1;
      const maxPage = Math.max(1, Math.floor((meta.maxResultWindow - state.pageSize) / state.pageSize) + 1);
      pager.innerHTML = `
        <button class="btn" id="pg-first" ${cur.from === 0 ? 'disabled' : ''}>首页</button>
        <button class="btn" id="pg-prev" ${canPrev ? '' : 'disabled'}>上一页</button>
        <span>第</span>
        <input type="number" id="pg-jump" min="1" value="${page}">
        <span>页</span>
        <button class="btn" id="pg-go">跳转</button>
        <button class="btn" id="pg-next" ${canNext ? '' : 'disabled'}>下一页</button>
        <span class="pos">当前 ${fmtNum(cur.from + 1)} - ${fmtNum(cur.from + res.rows.length)} 条
          ${totalShown(res)}</span>`;
      pager.querySelector('#pg-first').addEventListener('click', () => goFrom(0));
      pager.querySelector('#pg-prev').addEventListener('click', goPrev);
      pager.querySelector('#pg-next').addEventListener('click', goNext);
      pager.querySelector('#pg-go').addEventListener('click', () => {
        const n = Math.max(1, +pager.querySelector('#pg-jump').value || 1);
        const from = (n - 1) * state.pageSize;
        if (from + state.pageSize > meta.maxResultWindow) {
          toast(`第 ${n} 页超出 max_result_window（${fmtNum(meta.maxResultWindow)}），请先用条件过滤或逐页翻到深处`, true);
          return;
        }
        goFrom(from);
      });
    } else {
      pager.innerHTML = `
        <button class="btn" id="pg-prev" ${canPrev ? '' : 'disabled'}>上一页</button>
        <button class="btn" id="pg-next" ${canNext ? '' : 'disabled'}>下一页</button>
        <span class="pos">游标模式 · 已翻 ${state.history.length} 页 · ${totalShown(res)}</span>
        <span class="hint">已超出 max_result_window，仅支持顺序翻页</span>`;
      pager.querySelector('#pg-prev').addEventListener('click', goPrev);
      pager.querySelector('#pg-next').addEventListener('click', goNext);
    }
  }

  function totalShown(res) {
    return res.total != null ? `· 共 ${fmtNum(res.total)} 条` : '';
  }

  drawConditions();
  updateHint();
  load();
}

// ---------- 弹窗工具 ----------
export const modalRoot = document.getElementById('modal-root');

let modalCloseHook = null;   // 当前弹窗被任意方式关闭时触发的回调

export function showModal(title, bodyHtml, onMount, onClose) {
  closeAllModals();
  modalRoot.innerHTML = `
    <div class="modal-mask">
      <div class="modal">
        <div class="modal-head"><h3>${escapeHtml(title)}</h3>
          <button class="modal-close">×</button></div>
        <div class="modal-body">${bodyHtml}</div>
      </div>
    </div>`;
  modalCloseHook = typeof onClose === 'function' ? onClose : null;
  modalRoot.querySelector('.modal-close').addEventListener('click', closeAllModals);
  // 仅当按下与松开都在遮罩上（纯点击遮罩）才关闭，避免弹窗内拖拽选择文本时误关
  const mask = modalRoot.querySelector('.modal-mask');
  let maskDown = false;
  mask.addEventListener('mousedown', e => { maskDown = e.target === mask; });
  mask.addEventListener('click', e => {
    const pureClick = e.target === mask && maskDown;
    maskDown = false;
    if (pureClick) closeAllModals();
  });
  if (onMount) onMount();
}

export function closeAllModals() {
  const hook = modalCloseHook;
  modalCloseHook = null;
  modalRoot.innerHTML = '';
  if (hook) {
    try { hook(); } catch { /* 关闭回调异常不阻塞关闭 */ }
  }
}
