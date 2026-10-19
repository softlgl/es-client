import { get, post, toast, fmtNum } from '../api.js?v=14';
import { escapeHtml, showModal, closeAllModals, modalRoot } from '../ui.js?v=14';
import { openExportDialog } from './export-dialog.js?v=14';
import { openAiDialog } from './ai-dialog.js?v=14';
import { openDocDetail, openNewDocDialog } from './doc-dialog.js?v=14';

const OPS = [
  { v: 'eq', label: '等于' }, { v: 'ne', label: '不等于' },
  { v: 'contains', label: '包含', warn: true }, { v: 'prefix', label: '前缀' },
  { v: 'between', label: '介于' }, { v: 'gt', label: '大于' }, { v: 'gte', label: '≥' },
  { v: 'lt', label: '小于' }, { v: 'lte', label: '≤' },
  { v: 'exists', label: '存在' }, { v: 'missing', label: '不存在' }
];
const RANGE_OPS = new Set(['between', 'gt', 'gte', 'lt', 'lte']);
const NO_VALUE_OPS = new Set(['exists', 'missing']);

// 浏览器 IANA 时区：日期条件发到后端时用它，与 AI 生成路径口径一致
// （否则后端 RangeClause 会退回服务器本地偏移，跨时区机器上结果会差一天）
const BROWSER_TZ = (() => {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch { return ''; }
})();

// 每个索引的浏览状态（条件/翻页/列选择/排序），切换索引后切回可恢复
const stateCache = new Map();
// 每个索引最近一次实际执行的查询（buildQuery 的结果），供 AI 问答读取浏览上下文
const lastQueries = new Map();

// 断开/切换连接时调用：不同集群同名索引结构可能不同
export function clearBrowseCache() { stateCache.clear(); lastQueries.clear(); }

const OP_LABELS = Object.fromEntries(OPS.map(o => [o.v, o.label]));

// 把最近一次执行的查询转成一行可读描述，供 AI 问答作为上下文。
// 用可读文本而非项目内部结构，避免让模型额外理解 {conditions, queryString, dsl} 这套私有格式。
export function getBrowseQueryContext(index) {
  const q = lastQueries.get(index);
  if (!q) return null;              // 未查询过 / 无任何条件：等同于 match_all，无需作为上下文
  if (q.conditions?.length) {
    const parts = q.conditions.map(c => {
      const op = OP_LABELS[c.op] || c.op;
      if (NO_VALUE_OPS.has(c.op)) return `${c.field} ${op}`;
      return c.op === 'between' ? `${c.field} ${op} ${c.value} ~ ${c.value2}` : `${c.field} ${op} ${c.value}`;
    });
    return `简单查询：条件全部为「且」关系 —— ${parts.join(' 且 ')}`;
  }
  if (q.queryString) return `query_string 查询表达式：${q.queryString}`;
  if (q.dsl) return `DSL 查询请求体：${JSON.stringify(q.dsl)}`;
  return null;
}

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

  // AI 问答「在数据浏览中打开」：接管本次渲染，切到 DSL 模式、填入查询体并直接执行。
  // 本文件是唯一使用该 sessionStorage 的入口（AI 生成查询弹窗走闭包回调直接改 DOM，不经此处），
  // 所以读取即消费，无需再区分「只填入 / 填入并执行」两种模式。
  const openDsl = sessionStorage.getItem('ai-open-in-browse');
  if (openDsl) {
    sessionStorage.removeItem('ai-open-in-browse');
    state.tab = 'query';
    state.qsMode = 'dsl';
    // 换查询了就得从头看起，否则 load() 会拿 state.history 栈顶当起点（旧偏移），
    // 在新筛选条件下列表可能整页为空，表现为「打开没反应」
    resetPages();
    // 后端仅在 req.Sort 为空时才采用 DSL 内的 sort，残留的表头排序会盖掉 DSL 的排序
    state.sort = null;
    // 原样保留 DSL 的 size：浏览页要复现问答那一次查询，改写会让行数与问答页对不上。
    // 想看更多就在工具条改每页条数，load() 会把该值同步回下拉框与 DSL 的 size。
    state.dslText = openDsl;
  }

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
            <div class="view-switch">
              <button class="active">数据浏览</button>
              <button id="vs-ai" title="针对当前索引向 AI 提问">✨ AI 问答</button>
            </div>
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
    updateHint(); // 切离 DSL 模式后移除「每页条数由 DSL 决定」的提示
  }));

  // query_string / DSL 模式切换
  function setQsMode(mode) {
    state.qsMode = mode;
    $('#qs-qs-wrap').hidden = mode === 'dsl';
    $('#qs-dsl-wrap').hidden = mode !== 'dsl';
    view.querySelectorAll('input[name=qsmode]').forEach(r => { r.checked = r.value === mode; });
    updateHint();
  }
  view.querySelectorAll('input[name=qsmode]').forEach(r => r.addEventListener('change', () => setQsMode(r.value)));

  function buildQuery() {
    if (state.tab === 'simple') {
      const conds = state.conditions.filter(c =>
        c.field && (NO_VALUE_OPS.has(c.op) || (c.value ?? '') !== ''));
      if (conds.length === 0) return null;
      return { conditions: conds.map(c => ({ field: c.field, op: c.op, value: c.value || null, value2: c.value2 || null })), queryString: null, timeZone: BROWSER_TZ || null };
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
    // DSL 模式下每页条数最终来自 DSL 的 size（改工具条会同步改写它），
    // 显式说明来源，否则用户在工具条与 DSL 两处看到的数字不同时会不知该改哪儿。
    const dslSizeNote = state.tab === 'query' && state.qsMode === 'dsl'
      && state.lastQuerySize && state.lastQuerySize !== state.pageSize
      ? `<div>本次每页 ${fmtNum(state.lastQuerySize)} 条由 DSL 中的 size 决定；调整工具条的每页条数会同步改写 DSL</div>`
      : '';
    h.innerHTML = (warn ? '<div class="hint-warn">“包含”用通配符实现，大索引上可能较慢</div>' : '') +
      (state.sampled ? '<div>该索引无 mapping，列由文档采样生成</div>' : '') +
      dslSizeNote +
      `<div>共 ${meta.columns.length} 个字段 · max_result_window ${fmtNum(meta.maxResultWindow)}</div>`;
  }

  // ---------- 工具条 ----------
  $('#page-size').addEventListener('change', e => {
    state.pageSize = +e.target.value;
    // DSL 模式下 size 就写在 DSL 里，改下拉框必须同步改写它，否则表格行数不变、
    // 控件形同失效。此处只改 size，不动用户写的其他查询条件。
    if (state.tab === 'query' && state.qsMode === 'dsl') {
      const t = $('#dsl-input').value.trim();
      if (t) {
        try {
          const o = JSON.parse(t);
          if (o && typeof o === 'object' && !Array.isArray(o)) {
            o.size = state.pageSize;
            state.dslText = JSON.stringify(o, null, 2);
            $('#dsl-input').value = state.dslText;
          }
        } catch { /* DSL 非合法 JSON 时留给 buildQuery 报错，此处不改写 */ }
      }
    }
    resetPages();
    load();
  });
  $('#btn-export').addEventListener('click', () => openExportDialog({
    index,
    columns: meta.columns,
    getQuery: buildQuery
  }));
  $('#btn-cols').addEventListener('click', () => openColumnSelector(''));
  $('#btn-newdoc').addEventListener('click', () => openNewDocDialog(index, refreshAfterWrite));
  $('#vs-ai').addEventListener('click', () => {
    location.hash = `#/ai/${encodeURIComponent(index)}`;
  });
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
  let loading = false; // 请求进行中标志：防止并发查询导致旧响应覆盖新结果
  function setBusy(busy) {
    const run = $('#btn-run');
    if (!run) return;
    run.disabled = busy;
    run.textContent = busy ? '查询中…' : '查询';
    $('#btn-reset').disabled = busy;
    $('#pager').querySelectorAll('button').forEach(b => { b.disabled = busy; });
  }
  async function load() {
    if (loading) return;
    loading = true;
    setBusy(true);
    const wrap = $('#table-wrap');
    wrap.innerHTML = '<div class="empty-hint"><span class="spinner"></span>查询中…</div>';
    const start = state.history[state.history.length - 1];
    let body;
    try {
      const q = buildQuery();
      // DSL 自带 size 时本次执行优先用它；分页控件的默认值（state.pageSize）不受影响
      let size = state.pageSize;
      if (Number.isInteger(+q?.dsl?.size) && +q.dsl.size >= 1)
        size = Math.min(+q.dsl.size, meta.maxResultWindow);
      state.lastQuerySize = size;
      // DSL 自带 size 时本次每页行数与下拉框偏好不同（取值优先级见上）：只同步显示，
      // 不改 state.pageSize —— 那是用户偏好，不该被本次查询的 size 污染。
      // 非预设档位临时补一个 option，避免 select 因找不到对应值而显示空白。
      if (size !== state.pageSize) {
        const sel = $('#page-size');
        if (!Array.from(sel.options).some(o => +o.value === size))
          sel.add(new Option(`${size} 条/页`, size));
        sel.value = String(size);
      }
      updateHint();
      // 记录最近一次实际执行的查询，供 AI 问答作为「用户当前关注范围」带入
      lastQueries.set(index, q);
      body = {
        mode: start.mode,
        size,
        query: q,
        sort: state.sort
      };
      if (start.mode === 'from') body.from = start.from;
      else body.after = start.after;
    } catch (e) {
      wrap.innerHTML = `<div class="empty-hint">${escapeHtml(e.message)}</div>`;
      $('#pager').innerHTML = '';
      toast(e.message, true);
      loading = false;
      setBusy(false);
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
    } finally {
      loading = false;
      setBusy(false);
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
                return `<td>${renderCell(cell, r.id, c.path)}</td>`;
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
      tr.addEventListener('click', () => openDocDetail(index, tr.dataset.id, refreshAfterWrite)));
    // 长值/JSON 单元格点开看完整值。必须 stopPropagation，否则会连带触发上面的
    // 整行点击、把文档详情弹窗顶掉
    wrap.querySelectorAll('td .cell-json, td .cell-long').forEach(el =>
      el.addEventListener('click', e => {
        e.stopPropagation();
        showFullCell(el.dataset.id, el.dataset.col);
      }));
  }

  function renderCell(cell, rowId, path) {
    if (cell == null || cell.v === null || cell.v === undefined) return '';
    const attrs = `data-id="${escapeHtml(rowId)}" data-col="${escapeHtml(path)}"`;
    if (cell.kind === 'json') return `<span class="cell-json" ${attrs} title="${escapeHtml(cell.v)}">${escapeHtml(cell.v)}</span>`;
    if (cell.kind === 'longjson') return `<span class="cell-json cell-long" ${attrs} title="点击查看完整值">{…}</span>`;
    if (cell.kind === 'long') return `<span class="cell-long" ${attrs} title="点击查看完整值">${escapeHtml(cell.v)}…</span>`;
    return escapeHtml(cell.v);
  }

  // 列表接口对超长值只下发截断内容（后端 BrowseTruncateLength），完整值并不在响应里，
  // 只能按 _id 回查单文档再取值 —— 这也是 cell 上带 data-id 的原意。
  async function showFullCell(rowId, path) {
    if (!rowId || !path) return;
    let src;
    try {
      const doc = await get(`/api/indices/${encodeURIComponent(index)}/documents/${encodeURIComponent(rowId)}`);
      src = doc._source ?? doc;
    } catch (e) {
      toast(e.message, true);
      return;
    }
    const v = readPath(src, path);
    const text = v == null ? '（该文档中此字段无值）'
      : typeof v === 'object' ? JSON.stringify(v, null, 2) : String(v);
    showModal(`字段完整值 — ${path}`, `<pre>${escapeHtml(text)}</pre>`);
  }

  // 列路径取值，语义与后端 PagerService.ExtractCell 对齐：逐级下钻，
  // 中途遇到数组即整体取值（对应后端 break 出循环后序列化整个数组）
  function readPath(obj, path) {
    let cur = obj;
    for (const seg of path.split('.')) {
      if (Array.isArray(cur)) return cur;
      if (cur == null || typeof cur !== 'object') return null;
      cur = cur[seg];
    }
    return cur ?? null;
  }

  // 文档增删改成功后：通知侧栏刷新计数，并重载当前页表格
  // （doc-dialog.js 的保存/删除回调，原先靠闭包捕获 load() 实现）
  const refreshAfterWrite = () => {
    window.dispatchEvent(new Event('indices-changed'));
    load();
  };

  // ---------- 分页条 ----------
  function drawPager() {
    const pager = $('#pager');
    const res = state.current;
    if (!res) { pager.innerHTML = ''; return; }
    const cur = state.history[state.history.length - 1];
    const canPrev = state.history.length > 1;
    // DSL 自带 size 时本次每页行数与分页控件不同，用实际请求的值判断是否满页
    const pageSize = state.lastQuerySize || state.pageSize;
    const canNext = !!res.nextAfter && res.rows.length === pageSize;

    if (res.mode === 'from') {
      const page = cur.from / pageSize + 1;
      const maxPage = Math.max(1, Math.floor((meta.maxResultWindow - pageSize) / pageSize) + 1);
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
        const from = (n - 1) * pageSize;
        if (from + pageSize > meta.maxResultWindow) {
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
