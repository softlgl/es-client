import { get, post, del, toast, fmtNum } from '../api.js';
import { escapeHtml, highlightJson } from '../ui.js';
import { streamSse } from '../sse.js';
import { getBrowseQueryContext } from './browse.js';

// 桶值下钻用的 term 查询构造：字段位于 nested 路径下时必须用 nested 包裹，
// 否则 ES 匹配不到嵌套文档里的值
class TermQueryBuilder {
  constructor(field, value, nested) { this.field = field; this.value = value; this.nested = nested || ''; }
  build() {
    const leaf = { term: { [this.field]: this.value } };
    return this.nested ? { nested: { path: this.nested, query: leaf } } : leaf;
  }
}

// ---------- AI 问答面板（#/ai/{index}）：针对当前索引数据的取数型问答 ----------
// 流式展示：生成查询（可展开思考）→ 执行 → 结果表格 + AI 总结。会话历史由服务端持久化。

export async function renderAiChat(view, index) {
  view.innerHTML = '<div class="panel"><span class="spinner"></span>会话加载中…</div>';

  let sessions = [];
  let sessionId = null;   // null = 新会话（发送首条消息时由服务端创建）
  let turns = [];         // 当前会话轮次 [{role,text,dsl,data,time}]
  let streaming = false;
  let abortCtl = null;

  const timeZone = (() => {
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch { return ''; }
  })();

  // 携带数据浏览页最后一次执行的查询作为上下文，避免问答脱离用户已有的筛选口径重新猜
  const browseContext = getBrowseQueryContext(index);
  const browseContextShort = browseContext
    ? (browseContext.length > 160 ? browseContext.slice(0, 160) + '…' : browseContext)
    : null;

  async function loadSessions() {
    try {
      const r = await get(`/api/ai/chat/${encodeURIComponent(index)}`);
      sessions = r.sessions || [];
    } catch { sessions = []; }
  }

  async function loadTurns(id) {
    if (!id) { turns = []; return; }
    try {
      const s = await get(`/api/ai/chat/${encodeURIComponent(index)}/${encodeURIComponent(id)}`);
      turns = s.turns || [];
    } catch { turns = []; sessionId = null; }
  }

  view.innerHTML = `
    <div class="panel ai-chat-panel">
      <div class="chat-head">
        <div class="view-switch">
          <button id="vs-browse">数据浏览</button>
          <button class="active">✨ AI 问答</button>
        </div>
        <span class="chat-index" title="当前索引">${escapeHtml(index)}</span>
        <div class="chat-sess-dd" id="chat-sess-dd">
          <span id="chat-sess-label" title="切换 / 删除会话"></span>
          <div id="chat-sess-menu" class="conn-menu chat-sess-menu hidden"></div>
        </div>
        <button class="btn btn-ghost btn-sm" id="chat-new" title="开始一个新会话">＋ 新会话</button>
        <span class="spacer"></span>
        <label class="chat-ctrl" title="开启后使用思考模型生成查询">
          <input type="checkbox" id="chat-thinking"> 思考模式
        </label>
        <label class="chat-ctrl" title="开启后 AI 可分步调用只读工具（临时查询/计数/采样）探索数据再收敛，适合复杂多层下钻问题">
          <input type="checkbox" id="chat-tools"> 工具探索
        </label>
      </div>
      <div class="chat-msgs" id="chat-msgs"></div>
      <div class="chat-input-row">
        <textarea id="chat-input" rows="2"
          placeholder="针对当前索引提问，例：&#10;各状态的订单数是多少&#10;上个月订单的平均金额&#10;金额最高的 5 笔订单"></textarea>
        <button class="btn btn-primary" id="chat-send">发送</button>
      </div>
      <div class="chat-foot hint"><b>Enter</b> 发送 · <b>Shift+Enter</b> 换行；聚合统计与明细均可询问；问答结果由 AI 根据真实查询数据生成，可在回答下方展开 DSL 自行验证${
        browseContextShort ? `<div class="chat-ctx" title="${escapeHtml(browseContext)}">已带入你在数据浏览中设置的查询：${escapeHtml(browseContextShort)}</div>` : ''}</div>
    </div>`;

  const msgs = view.querySelector('#chat-msgs');
  const sessDd = view.querySelector('#chat-sess-dd');
  const sessLabel = view.querySelector('#chat-sess-label');
  const sessMenu = view.querySelector('#chat-sess-menu');
  const input = view.querySelector('#chat-input');
  const sendBtn = view.querySelector('#chat-send');
  const thinkCb = view.querySelector('#chat-thinking');
  const toolsCb = view.querySelector('#chat-tools');

  thinkCb.checked = localStorage.getItem('ai-thinking') !== '0';
  thinkCb.addEventListener('change', () =>
    localStorage.setItem('ai-thinking', thinkCb.checked ? '1' : '0'));
  toolsCb.checked = localStorage.getItem('ai-tools') === '1';
  toolsCb.addEventListener('change', () =>
    localStorage.setItem('ai-tools', toolsCb.checked ? '1' : '0'));

  view.querySelector('#vs-browse').addEventListener('click', () => {
    location.hash = `#/browse/${encodeURIComponent(index)}`;
  });

  // ---------- 会话下拉（复用连接下拉的菜单风格） ----------
  const SVG_DEL = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>';

  function sessTitle() {
    if (sessionId) return sessions.find(x => x.id === sessionId)?.title || '新会话';
    return '新会话';
  }

  function drawSessLabel() {
    sessLabel.innerHTML = `<b>${escapeHtml(sessTitle())}</b> <span class="dd-arrow">▾</span>`;
  }

  function fmtTime(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return '';
    const now = new Date();
    const hm = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    return d.toDateString() === now.toDateString()
      ? `今天 ${hm}`
      : `${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')} ${hm}`;
  }

  function drawSessionMenu() {
    sessMenu.innerHTML = sessions.map(s => {
      const isCur = s.id === sessionId;
      return `<div class="conn-item${isCur ? ' active' : ''}" data-sid="${escapeHtml(s.id)}">
        <span class="ci-info">
          <b>${escapeHtml(s.title)}</b>
          <span class="ci-url">${s.turns} 轮 · ${fmtTime(s.updatedAt)}</span>
        </span>
        ${isCur ? '<span class="ai-active">当前</span>' : ''}
        <button class="conn-del" data-del="${escapeHtml(s.id)}" title="删除该会话">${SVG_DEL}</button>
      </div>`;
    }).join('') || '<div class="conn-item ai-menu-hint">暂无历史会话</div>';

    sessMenu.querySelectorAll('[data-sid]').forEach(el => el.addEventListener('click', e => {
      if (e.target.closest('.conn-del')) return;
      if (streaming) { toast('正在回答中，请稍候', true); return; }
      switchSession(el.dataset.sid);
    }));
    sessMenu.querySelectorAll('[data-del]').forEach(b => b.addEventListener('click', async e => {
      e.stopPropagation();
      const id = b.dataset.del;
      const t = sessions.find(x => x.id === id)?.title || id.slice(0, 8);
      if (!confirm(`确定删除会话「${t}」？历史将无法恢复。`)) return;
      if (streaming) { toast('正在回答中，请稍候', true); return; }
      try {
        await del(`/api/ai/chat/${encodeURIComponent(index)}/${encodeURIComponent(id)}`);
        await loadSessions();
        if (id === sessionId) {
          sessionId = sessions.length > 0 ? sessions[0].id : null;
          await loadTurns(sessionId);
          drawMsgs();
        }
        drawSessLabel();
        drawSessionMenu();
        toast('会话已删除');
      } catch (err) { toast(err.message, true); }
    }));
  }

  function closeSessMenu() { sessMenu.classList.add('hidden'); }

  async function switchSession(id) {
    sessionId = id;
    await loadTurns(sessionId);
    drawSessLabel();
    drawSessionMenu();
    closeSessMenu();
    drawMsgs();
  }

  sessLabel.addEventListener('click', e => {
    e.stopPropagation();
    sessMenu.classList.toggle('hidden');
    if (!sessMenu.classList.contains('hidden')) drawSessionMenu();
  });
  document.addEventListener('click', e => {
    if (!sessDd.contains(e.target)) closeSessMenu();
  });

  function dataTableHtml(data) {
    if (!data || !Array.isArray(data.rows) || data.rows.length === 0) return '';
    const head = (data.columns || []).map(c => `<th>${escapeHtml(c)}</th>`).join('');
    // 带 aggField 的是聚合分布表：首列桶值做成下钻入口，点击即带 term 条件跳数据浏览。
    // 桶值以 JSON 存入 data-v，保留原始类型（数值字段的 key 是数字而非字符串）
    const drillable = !!data.aggField;
    const rows = data.rows.map(r => `<tr>${r.map((v, i) => {
      if (drillable && i === 0) {
        return `<td><a href="javascript:void(0)" class="chat-drill" data-field="${escapeHtml(data.aggField)}"` +
          `${data.aggNested ? ` data-nested="${escapeHtml(data.aggNested)}"` : ''}` +
          ` data-v="${escapeHtml(JSON.stringify(v ?? null))}"` +
          ` title="只看 ${escapeHtml(String(v))} 的明细">${escapeHtml(String(v))}</a></td>`;
      }
      return `<td>${escapeHtml(String(v))}</td>`;
    }).join('')}</tr>`).join('');
    // 命中总数 + 节选提示：问答表格只展示前若干行，没有这个基准就无法与
    // 数据浏览的结果核对（那边按同一份 DSL 执行，展示的是完整字段）
    const caption = data.note
      ? `<div class="chat-table-cap">${escapeHtml(data.note)}</div>`
      : data.kind === 'table' && data.total != null
        ? `<div class="chat-table-cap">命中 ${fmtNum(data.total)} 条${data.truncated ? `，此处仅展示前 ${data.rows.length} 条` : ''}</div>`
        : '';
    // baseQuery：后端在建聚合表时写入的原始 query（产生该分布的筛选条件）。挂到表格上，
    // 下钻时优先取它——比从 DSL 折叠区读更早可用（流式总结期间表格已渲染、DSL 尚未 finalize）。
    const bq = drillable && data.baseQuery != null
      ? ` data-base-query="${escapeHtml(JSON.stringify(data.baseQuery))}"` : '';
    return `${caption}<table class="chat-table"${bq}><thead><tr>${head}</tr></thead><tbody>${rows}</tbody></table>`;
  }

  // 判据是「浏览页能否执行」——buildQuery 要求 DSL 必须含 query 子句，纯 aggs 的请求会被拒绝。
  // 不能用 size 判：后端 ExtractSearchBody 会无条件补 size（缺省 0），模型省略 size 的
  // 明细查询会被 size>=1 误判掉，按钮不渲染，表现就是「点了没反应 / 压根没有按钮」。
  // 注意：带 aggs 的请求这里会放行，但浏览页只执行 query 子句、丢弃 aggs，
  // 所以按钮文案会改口径说明，聚合结果要看回答上方或点桶值下钻。
  function canOpenInBrowse(dslObj) {
    try {
      const obj = typeof dslObj === 'string' ? JSON.parse(dslObj) : dslObj;
      return !!(obj && obj.query != null);
    } catch { return false; }
  }

  function dslBlockHtml(dslObj) {
    if (!dslObj) return '';
    const text = typeof dslObj === 'string' ? dslObj : JSON.stringify(dslObj, null, 2);
    return `
      <details class="chat-dsl">
        <summary>本次查询 DSL</summary>
        <pre data-raw="${escapeHtml(text)}">${highlightJson(text)}</pre>
      </details>`;
  }

  // 打开浏览是回答后的主操作，放在 DSL 折叠区之外：DSL 是供验证的次要信息，
  // 而 <details> 默认折叠，藏进去等于没有这个入口。
  // 浏览页只执行 DSL 的 query 子句（aggs 会被丢弃），所以带聚合的请求要说清它给的是筛选条件。
  function openBrowseActionHtml(dslObj) {
    if (!canOpenInBrowse(dslObj)) return '';
    let hasAgg = false;
    try {
      const obj = typeof dslObj === 'string' ? JSON.parse(dslObj) : dslObj;
      hasAgg = !!(obj && (obj.aggs || obj.aggregations));
    } catch { /* 解析失败按无聚合处理 */ }
    return `<div class="chat-actions">
      <button class="btn btn-sm btn-primary chat-open-browse">${hasAgg ? '按此筛选条件浏览明细' : '在数据浏览中打开'}</button>
    </div>`;
  }

  // 取桶值下钻需带上的原始 query：优先读表格 data-base-query（后端在建表时写入，流式期间即
  // 可用），回退到该条回答的 DSL 折叠区（每轮 assistant 都持久化了 dsl）。都取不到返回 null
  // → 调用方回退旧行为，不至于更差。
  function readDrillBaseQuery(el) {
    const tbl = el.closest('.chat-table');
    const raw = tbl?.dataset.baseQuery;
    if (raw) {
      try {
        const q = JSON.parse(raw);
        if (q != null) return q;
      } catch { /* 损坏则回退 DSL */ }
    }
    const pre = el.closest('.chat-msg')?.querySelector('.chat-dsl pre');
    if (!pre) return null;
    try {
      const obj = JSON.parse(pre.dataset.raw || pre.textContent);
      return obj && obj.query != null ? obj.query : null;
    } catch { return null; }
  }

  // 事件委托：历史与新增回答共用
  msgs.addEventListener('click', e => {
    // 聚合桶值下钻：按该桶值加 term 条件进数据浏览。浏览页不执行 aggs，只能这样下钻
    const drill = e.target.closest('.chat-drill');
    if (drill) {
      let val;
      try { val = JSON.parse(drill.dataset.v); } catch { return; }
      const field = drill.dataset.field;
      const leaf = new TermQueryBuilder(field, val, drill.dataset.nested);
      // 桶值下钻必须带上产生该聚合的原始 query（userCode/时间区间等），
      // 否则明细变成「全库匹配该值」，与聚合桶数对不上。base 从该条回答的 DSL 里取。
      // 原 query 整体作为一个 must 元素嵌入（无论它是否已是 bool），恒等于「原条件 AND 桶值」。
      const baseQuery = readDrillBaseQuery(drill);
      const query = baseQuery
        ? { bool: { must: [baseQuery, leaf.build()] } }
        : leaf.build();   // 取不到 base（如流式未 finalize / DSL 无 query）→ 回退旧行为，不至于更差
      sessionStorage.setItem('ai-open-in-browse', JSON.stringify({ query, size: 50 }, null, 2));
      location.hash = `#/browse/${encodeURIComponent(index)}`;
      return;
    }

    const btn = e.target.closest('.chat-open-browse');
    if (!btn) return;
    const pre = btn.closest('.chat-msg')?.querySelector('.chat-dsl pre');
    if (!pre) return;
    try {
      const obj = JSON.parse(pre.dataset.raw || pre.textContent);
      if (obj.query == null) { toast('该回答没有可执行的查询条件，无法在数据浏览中打开'); return; }
      // 聚合请求在浏览页只剩筛选条件，提示一下免得以为聚合丢了
      sessionStorage.setItem('ai-open-in-browse', JSON.stringify(obj, null, 2));
      location.hash = `#/browse/${encodeURIComponent(index)}`;
    } catch { toast('DSL 解析失败', true); }
  });

  function msgHtml(t) {
    if (t.role === 'user') {
      return `<div class="chat-msg user"><div class="chat-bubble">${escapeHtml(t.text)}</div></div>`;
    }
    return `
      <div class="chat-msg ai">
        <div class="chat-bubble">
          <div class="chat-answer">${escapeHtml(t.text)}</div>
          ${dataTableHtml(t.data)}
          ${openBrowseActionHtml(t.dsl)}
          ${dslBlockHtml(t.dsl)}
        </div>
      </div>`;
  }

  function drawMsgs() {
    if (!sessionId && turns.length === 0) {
      msgs.innerHTML = `
        <div class="chat-empty">
          <div class="chat-empty-title">问点什么吧</div>
          <div class="hint">统计类：「各状态订单数」「上个月平均金额」「金额 Top 5 的用户」<br>
          明细类：「列出最近 5 条失败记录」</div>
        </div>`;
      return;
    }
    msgs.innerHTML = turns.map(msgHtml).join('');
    msgs.scrollTop = msgs.scrollHeight;
  }

  view.querySelector('#chat-new').addEventListener('click', () => {
    if (streaming) { toast('正在回答中，请稍候', true); return; }
    sessionId = null;
    turns = [];
    drawSessLabel();
    drawMsgs();
    input.focus();
  });

  function setStreaming(on) {
    streaming = on;
    sendBtn.textContent = on ? '停止' : '发送';
    sendBtn.classList.toggle('danger', on);
    input.disabled = on;
  }

  async function ask() {
    const question = input.value.trim();
    if (!question) { toast('请先输入问题', true); return; }
    if (streaming) { abortCtl?.abort(); return; }

    input.value = ''; // 已取到问题，清空输入框（放在 streaming 检查之后，点「停止」不会误清）
    setStreaming(true);
    abortCtl = new AbortController();

    const userMsg = { role: 'user', text: question };
    turns.push(userMsg);
    drawMsgs();

    // 进行中的回答气泡：生成阶段（思考/DSL 预览）→ 结果表格 → 总结流式
    const live = document.createElement('div');
    live.className = 'chat-msg ai';
    live.innerHTML = `
      <div class="chat-bubble">
        <div class="chat-status"><span class="spinner"></span>正在分析问题…</div>
        <div class="chat-reasoning hidden"></div>
        <pre class="chat-gen hidden"></pre>
        <div class="chat-data"></div>
        <div class="chat-answer hidden"></div>
      </div>`;
    msgs.appendChild(live);
    const autoScroll = () => { msgs.scrollTop = msgs.scrollHeight; };
    autoScroll();

    const statusEl = live.querySelector('.chat-status');
    const reasonEl = live.querySelector('.chat-reasoning');
    const genEl = live.querySelector('.chat-gen');
    const dataEl = live.querySelector('.chat-data');
    const answerEl = live.querySelector('.chat-answer');
    let reasoningShown = false;
    let dslText = null;
    let direct = false;   // 免查询直接回答：之后的 content 进回答气泡而非 DSL 预览
    let data = null;
    let answer = '';

    const finish = () => {
      setStreaming(false);
      abortCtl = null;
      input.disabled = false;
      input.focus();
    };
    const fail = msg => {
      live.remove();
      turns.pop(); // 失败不落历史
      drawMsgs();
      toast(msg, true);
      finish();
    };

    try {
      // onEvent 返回 false 表示已收尾（收到 done/error）
      const settled = await streamSse(`/api/ai/chat/${encodeURIComponent(index)}`, {
        sessionId, question, thinking: thinkCb.checked, tools: toolsCb.checked, timeZone,
        currentQuery: browseContext
      }, abortCtl.signal, async ev => {
        if (ev.type === 'status') {
          const t = escapeHtml(ev.text || '');
          statusEl.innerHTML = t.startsWith('🔧')
            ? `<span class="spinner"></span><code>${t}</code>`
            : `<span class="spinner"></span>${t}`;
        } else if (ev.type === 'reasoning') {
          if (!reasoningShown) { reasonEl.classList.remove('hidden'); reasoningShown = true; }
          reasonEl.textContent += ev.text || '';
          reasonEl.scrollTop = reasonEl.scrollHeight;
          autoScroll();
        } else if (ev.type === 'content' && !dslText && !direct) {
          // 第一阶段：DSL 生成预览
          genEl.classList.remove('hidden');
          genEl.textContent += ev.text || '';
          autoScroll();
        } else if (ev.type === 'dsl') {
          dslText = JSON.stringify(ev.dsl, null, 2);
          genEl.remove();
          statusEl.innerHTML = '<span class="spinner"></span>正在执行查询…';
        } else if (ev.type === 'direct') {
          // 问题与索引数据无关：撤掉 DSL 预览，后续 content 转进回答气泡
          direct = true;
          genEl.remove();
          statusEl.innerHTML = '<span class="spinner"></span>正在回答…';
        } else if (ev.type === 'data') {
          data = ev;
          dataEl.innerHTML = dataTableHtml(ev);
          autoScroll();
        } else if (ev.type === 'content') {
          statusEl.remove();
          answerEl.classList.remove('hidden');
          answer += ev.text || '';
          answerEl.textContent = answer;
          autoScroll();
        } else if (ev.type === 'done') {
          const isNew = sessionId !== ev.sessionId;
          sessionId = ev.sessionId;
          turns.push({ role: 'assistant', text: ev.turn.text, dsl: ev.turn.dsl, data: ev.turn.data });
          await loadSessions();
          drawSessLabel();
          live.remove();
          drawMsgs();
          if (isNew) toast('已创建新会话');
          finish();
          return false;
        } else if (ev.type === 'error') {
          fail(ev.error || '问答失败');
          return false;
        }
      });
      // true = 流自然结束却未收到 done/error
      if (settled) {
        if (answer.trim()) {
          // 流被意外截断但已有部分回答：保留为最终回答（服务端可能已落库，此处仅展示）
          turns.push({ role: 'assistant', text: answer, dsl: dslText, data });
          live.remove();
          drawMsgs();
          finish();
        } else {
          fail('AI 服务未返回内容，请重试');
        }
      }
    } catch (e) {
      if (e.name === 'AbortError') { fail('已停止'); return; }
      fail(e.message || '网络错误');
    }
  }

  sendBtn.addEventListener('click', ask);
  input.addEventListener('keydown', e => {
    // Enter 发送；Shift+Enter / Ctrl+Enter 换行（浏览器默认行为，不拦截）
    if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey && !e.metaKey) { e.preventDefault(); ask(); }
  });

  // 初始化：拉会话列表，默认打开最近更新的会话
  await loadSessions();
  if (sessions.length > 0) sessionId = sessions[0].id;
  await loadTurns(sessionId);
  drawSessLabel();
  drawMsgs();
  input.focus();
}
