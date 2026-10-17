import { get, post, del, toast } from '../api.js?v=13';
import { escapeHtml } from './indices.js?v=13';

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
        <b class="chat-title">✨ AI 问答</b>
        <span class="chat-index" title="当前索引">${escapeHtml(index)}</span>
        <select id="chat-sess" title="切换会话"></select>
        <button class="btn btn-ghost btn-sm" id="chat-new" title="开始一个新会话">＋ 新会话</button>
        <button class="btn btn-ghost btn-sm" id="chat-del" title="删除当前会话">删除</button>
        <span class="spacer"></span>
        <label class="chat-ctrl" title="开启后使用思考模型生成查询">
          <input type="checkbox" id="chat-thinking"> 思考模式
        </label>
        <a href="javascript:void(0)" id="chat-back">返回数据浏览</a>
      </div>
      <div class="chat-msgs" id="chat-msgs"></div>
      <div class="chat-input-row">
        <textarea id="chat-input" rows="2"
          placeholder="针对当前索引提问，例：&#10;各状态的订单数是多少&#10;上个月订单的平均金额&#10;金额最高的 5 笔订单"></textarea>
        <button class="btn btn-primary" id="chat-send">发送</button>
      </div>
      <div class="chat-foot hint">聚合统计与明细均可询问；问答结果由 AI 根据真实查询数据生成，可在回答下方展开 DSL 自行验证</div>
    </div>`;

  const msgs = view.querySelector('#chat-msgs');
  const sessSel = view.querySelector('#chat-sess');
  const input = view.querySelector('#chat-input');
  const sendBtn = view.querySelector('#chat-send');
  const thinkCb = view.querySelector('#chat-thinking');

  thinkCb.checked = localStorage.getItem('ai-thinking') !== '0';
  thinkCb.addEventListener('change', () =>
    localStorage.setItem('ai-thinking', thinkCb.checked ? '1' : '0'));

  view.querySelector('#chat-back').addEventListener('click', () => {
    location.hash = `#/browse/${encodeURIComponent(index)}`;
  });

  function drawSessionSel() {
    sessSel.innerHTML = sessions.map(s =>
      `<option value="${escapeHtml(s.id)}" ${s.id === sessionId ? 'selected' : ''}>${escapeHtml(s.title)}</option>`
    ).join('') || '<option value="">暂无历史会话</option>';
    view.querySelector('#chat-del').disabled = !sessionId;
  }

  // 轻量 JSON 高亮（与 ai-dialog 保持一致，片段先转义再拼接）
  function highlightJson(text) {
    const esc = s => s.replace(/[&<>"']/g, c =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const re = /("(?:\\.|[^"\\])*")(\s*:)?|\b(?:true|false|null)\b|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g;
    let out = '', last = 0, m;
    while ((m = re.exec(text)) !== null) {
      out += esc(text.slice(last, m.index));
      if (m[1] !== undefined) {
        out += m[2] !== undefined
          ? `<span class="j-key">${esc(m[1])}</span>${esc(m[2])}`
          : `<span class="j-str">${esc(m[1])}</span>`;
      } else if (m[0] === 'true' || m[0] === 'false' || m[0] === 'null') {
        out += `<span class="j-kw">${m[0]}</span>`;
      } else {
        out += `<span class="j-num">${esc(m[0])}</span>`;
      }
      last = re.lastIndex;
    }
    out += esc(text.slice(last));
    return out;
  }

  function dataTableHtml(data) {
    if (!data || !Array.isArray(data.rows) || data.rows.length === 0) return '';
    const head = (data.columns || []).map(c => `<th>${escapeHtml(c)}</th>`).join('');
    const rows = data.rows.map(r =>
      `<tr>${r.map(v => `<td>${escapeHtml(String(v))}</td>`).join('')}</tr>`).join('');
    return `<table class="chat-table"><thead><tr>${head}</tr></thead><tbody>${rows}</tbody></table>`;
  }

  function dslBlockHtml(dslObj) {
    if (!dslObj) return '';
    const text = typeof dslObj === 'string' ? dslObj : JSON.stringify(dslObj, null, 2);
    return `
      <details class="chat-dsl">
        <summary>本次查询 DSL</summary>
        <pre>${highlightJson(text)}</pre>
        <div class="chat-dsl-actions">
          <button class="btn btn-sm chat-apply-dsl">填入查询面板</button>
        </div>
      </details>`;
  }

  // 事件委托：历史与新增回答共用
  msgs.addEventListener('click', e => {
    const btn = e.target.closest('.chat-apply-dsl');
    if (!btn) return;
    const box = btn.closest('.chat-dsl');
    const pre = box?.querySelector('pre');
    if (!pre) return;
    try {
      sessionStorage.setItem('ai-apply-dsl', JSON.stringify(JSON.parse(pre.dataset.raw || pre.textContent), null, 2));
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

  sessSel.addEventListener('change', async () => {
    if (streaming) { toast('正在回答中，请稍候', true); drawSessionSel(); return; }
    sessionId = sessSel.value || null;
    await loadTurns(sessionId);
    drawSessionSel();
    drawMsgs();
  });

  view.querySelector('#chat-new').addEventListener('click', () => {
    if (streaming) { toast('正在回答中，请稍候', true); return; }
    sessionId = null;
    turns = [];
    drawSessionSel();
    drawMsgs();
    input.focus();
  });

  view.querySelector('#chat-del').addEventListener('click', async () => {
    if (!sessionId || streaming) return;
    if (!confirm('确定删除当前会话？历史将无法恢复。')) return;
    try {
      await del(`/api/ai/chat/${encodeURIComponent(index)}/${encodeURIComponent(sessionId)}`);
      sessionId = null;
      turns = [];
      await loadSessions();
      if (sessions.length > 0) sessionId = sessions[0].id;
      await loadTurns(sessionId);
      drawSessionSel();
      drawMsgs();
      toast('会话已删除');
    } catch (e) { toast(e.message, true); }
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
      const res = await fetch(`/api/ai/chat/${encodeURIComponent(index)}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sessionId, question, thinking: thinkCb.checked, timeZone }),
        signal: abortCtl.signal
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        fail(err.error || `请求失败（${res.status}）`);
        return;
      }
      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buf = '';
      let finished = false;
      while (!finished) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        let idx;
        while ((idx = buf.indexOf('\n\n')) >= 0) {
          const raw = buf.slice(0, idx).trim();
          buf = buf.slice(idx + 2);
          if (!raw.startsWith('data:')) continue;
          let ev;
          try { ev = JSON.parse(raw.slice(5)); } catch { continue; }

          if (ev.type === 'status') {
            statusEl.innerHTML = `<span class="spinner"></span>${escapeHtml(ev.text || '')}`;
          } else if (ev.type === 'reasoning') {
            if (!reasoningShown) { reasonEl.classList.remove('hidden'); reasoningShown = true; }
            reasonEl.textContent += ev.text || '';
            reasonEl.scrollTop = reasonEl.scrollHeight;
            autoScroll();
          } else if (ev.type === 'content' && !dslText) {
            // 第一阶段：DSL 生成预览
            genEl.classList.remove('hidden');
            genEl.textContent += ev.text || '';
            autoScroll();
          } else if (ev.type === 'dsl') {
            dslText = JSON.stringify(ev.dsl, null, 2);
            genEl.remove();
            statusEl.innerHTML = '<span class="spinner"></span>正在执行查询…';
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
            finished = true;
            const isNew = sessionId !== ev.sessionId;
            sessionId = ev.sessionId;
            turns.push({ role: 'assistant', text: ev.turn.text, dsl: ev.turn.dsl, data: ev.turn.data });
            await loadSessions();
            drawSessionSel();
            live.remove();
            drawMsgs();
            if (isNew) toast('已创建新会话');
            finish();
          } else if (ev.type === 'error') {
            finished = true;
            fail(ev.error || '问答失败');
          }
        }
      }
      if (!finished) {
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
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); ask(); }
  });

  // 初始化：拉会话列表，默认打开最近更新的会话
  await loadSessions();
  if (sessions.length > 0) sessionId = sessions[0].id;
  await loadTurns(sessionId);
  drawSessionSel();
  drawMsgs();
  input.focus();
}
