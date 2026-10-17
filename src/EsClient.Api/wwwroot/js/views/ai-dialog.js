import { get, post, toast } from '../api.js?v=13';
import { escapeHtml } from './indices.js?v=13';
import { showModal, closeAllModals, modalRoot } from './browse.js?v=13';
import { openAiSettingsDialog } from './ai-settings.js?v=13';

// ---------- AI 生成查询弹窗：自然语言 → DSL → 填入查询语句框（DSL 模式） ----------
export function openAiDialog({ index, applyDsl }) {
  let checking = true;
  showModal('✨ AI 生成查询', '<div class="hint">正在检查 AI 配置…</div>');

  get('/api/ai/settings').then(s => {
    checking = false;
    if (!s.configured) {
      openAiSettingsDialog(() => {
        // 保存成功后回到生成弹窗
        openAiDialog({ index, applyDsl });
      });
      return;
    }
    renderDialog(index, applyDsl, s);
  }).catch(e => {
    checking = false;
    showModal('✨ AI 生成查询', `<div class="hint" style="color:#d9534f">${escapeHtml(e.message)}</div>
      <div class="query-actions"><button class="btn" id="ai-close">关闭</button></div>`);
    modalRoot.querySelector('#ai-close').addEventListener('click', closeAllModals);
  });
}

function renderDialog(index, applyDsl, s) {
  const history = [];   // [{role, content}]：多轮修正时发给后端
  const turns = [];     // UI 轮次：{prompt, dslText, reasoning}

  // 必须在 showModal 注册关闭钩子之前声明（钩子会在任意关闭时机被调用，避免 TDZ 报错）
  let streaming = false;
  let abortCtl = null;
  const timeZone = (() => {
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch { return ''; }
  })();

  showModal(`✨ AI 生成查询 — ${index}`, `
    <div class="hint" style="margin-bottom:8px">
      根据索引 <b>${escapeHtml(index)}</b> 的字段结构（含示例值）生成查询 DSL，
      模型在顶栏 <b>✨ ${escapeHtml((s.models || []).find(m => m.id === s.activeId)?.model) || '未配置'}</b> 切换
    </div>
    <div class="ai-ctrl">
      <label title="开启后使用配置的思考模型生成（可在 AI 设置中配置）">
        <input type="checkbox" id="ai-thinking"> 思考模式
      </label>
    </div>
    <div class="field">
      <textarea id="ai-prompt" rows="3"
        placeholder="用中文描述你要查的数据，例：&#10;状态是 paid 且金额大于 100 的订单，按创建时间倒序&#10;amount 在 50 到 200 之间或状态为 refunded 的记录"></textarea>
    </div>
    <div class="query-actions" style="margin:10px 0 4px">
      <button class="btn btn-primary" id="ai-go" style="flex:1">生成查询</button>
      <button class="btn" id="ai-clear">清空对话</button>
      <button class="btn" id="ai-close">关闭</button>
    </div>
    <div id="ai-status" class="hint"></div>
    <div id="ai-turns" class="ai-turns"></div>`, () => abortCtl?.abort());

  const root = modalRoot;
  const status = root.querySelector('#ai-status');
  const turnsBox = root.querySelector('#ai-turns');
  const input = root.querySelector('#ai-prompt');
  const go = root.querySelector('#ai-go');
  const thinkCb = root.querySelector('#ai-thinking');

  // 思考模式开关记忆（默认开启）
  thinkCb.checked = localStorage.getItem('ai-thinking') !== '0';
  thinkCb.addEventListener('change', () =>
    localStorage.setItem('ai-thinking', thinkCb.checked ? '1' : '0'));

  // 轻量 JSON 高亮（不依赖外部库），所有片段均先转义再拼接
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

  const initPlaceholder = input.placeholder;

  function drawTurns() {
    turnsBox.innerHTML = turns.map((t, i) => {
      if (t.error) {
        return `
        <div class="ai-turn ai-turn-failed">
          <div class="ai-q"><b>需求 ${i + 1}</b>：${escapeHtml(t.prompt)}</div>
          <div class="ai-err">${escapeHtml(t.error)}</div>
          <div class="ai-turn-actions">
            <button class="btn ai-retry" data-i="${i}">重试</button>
          </div>
        </div>`;
      }
      return `
      <div class="ai-turn">
        <div class="ai-q"><b>需求 ${i + 1}</b>：${escapeHtml(t.prompt)}</div>
        ${t.reasoning ? `
        <a href="javascript:void(0)" class="ai-reasoning-toggle" data-i="${i}">思考过程</a>
        <div class="ai-reasoning hidden" data-r="${i}">${escapeHtml(t.reasoning)}</div>` : ''}
        ${t.partial ? '<div class="ai-err">已手动停止，以下内容可能不完整</div>' : ''}
        ${typeof t.hits === 'number' ? `<div class="ai-hits">✓ 试运行命中 ${t.hits.toLocaleString()} 条</div>` : ''}
        ${t.countError ? `<div class="ai-err">试运行失败：${escapeHtml(t.countError)}</div>` : ''}
        <pre class="ai-dsl">${highlightJson(t.dslText)}</pre>
        <div class="ai-turn-actions">
          <button class="btn btn-primary ai-use" data-i="${i}">填入查询</button>
          <button class="btn ai-copy" data-i="${i}">复制</button>
        </div>
      </div>`;
    }).join('') ||
      '<div class="hint" style="text-align:center;padding:8px 0">生成的查询会显示在这里，可继续对话修正</div>';
    turnsBox.querySelectorAll('.ai-reasoning-toggle').forEach(a => a.addEventListener('click', () => {
      const box = turnsBox.querySelector(`.ai-reasoning[data-r="${a.dataset.i}"]`);
      box.classList.toggle('hidden');
      a.textContent = box.classList.contains('hidden') ? '思考过程' : '收起思考过程';
    }));
    turnsBox.querySelectorAll('.ai-use').forEach(b => b.addEventListener('click', () => {
      applyDsl(turns[+b.dataset.i].dslText);
      closeAllModals();
      toast('已填入查询框');
    }));
    turnsBox.querySelectorAll('.ai-copy').forEach(b => b.addEventListener('click', async () => {
      await navigator.clipboard.writeText(turns[+b.dataset.i].dslText).catch(() => {});
      toast('已复制到剪贴板');
    }));
    turnsBox.querySelectorAll('.ai-retry').forEach(b => b.addEventListener('click', () => {
      if (streaming) { toast('正在生成中，请先停止', true); return; }
      const t = turns[+b.dataset.i];
      generate({ prompt: t.prompt, lastError: t.error });
    }));
    turnsBox.scrollTop = turnsBox.scrollHeight;
    // 已有对话时提示可继续修正
    input.placeholder = turns.length
      ? '可继续描述修改，如：把时间范围改成最近 7 天、按金额倒序'
      : initPlaceholder;
  }
  drawTurns();

  async function generate(opts = {}) {
    const prompt = (opts.prompt ?? input.value).trim();
    if (!prompt) { toast('请先描述你要查的数据', true); return; }
    streaming = true;
    abortCtl = new AbortController();
    go.textContent = '停止';
    status.innerHTML = '';

    // 进行中的流式卡片：思考过程 + 生成内容实时追加
    const live = document.createElement('div');
    live.className = 'ai-turn streaming';
    live.innerHTML = `
      <div class="ai-q"><b>需求</b>：${escapeHtml(prompt)}</div>
      <div class="ai-reasoning hidden"></div>
      <pre class="ai-dsl ai-content-stream"></pre>`;
    turnsBox.appendChild(live);
    const reasonBox = live.querySelector('.ai-reasoning');
    const contentBox = live.querySelector('.ai-content-stream');
    const autoScroll = () => { turnsBox.scrollTop = turnsBox.scrollHeight; };
    autoScroll();

    let reasoningShown = false;
    const appendReasoning = t => {
      if (!reasoningShown) { reasonBox.classList.remove('hidden'); reasoningShown = true; }
      reasonBox.textContent += t;
      reasonBox.scrollTop = reasonBox.scrollHeight;
      autoScroll();
    };
    const appendContent = t => {
      contentBox.textContent += t;
      autoScroll();
    };

    let contentStr = '';
    let reasoningStr = '';
    const finish = () => {
      live.remove();
      streaming = false;
      abortCtl = null;
      go.textContent = '生成查询';
      go.disabled = false;
    };
    // 失败轮次保留在列表中，可重试；首次失败自动把错误反馈给模型自愈一次
    const fail = (msg, { heal = true } = {}) => {
      finish();
      turns.push({ prompt, error: msg });
      drawTurns();
      if (heal && !opts.healed) {
        status.innerHTML = '<span style="color:#d97706">生成失败，正在把错误反馈给模型自动重试…</span>';
        setTimeout(() => generate({ prompt, lastError: msg, healed: true }), 300);
      }
    };
    const keepPartial = () => {
      finish();
      turns.push({ prompt, dslText: contentStr, reasoning: reasoningStr, partial: true });
      drawTurns();
    };

    try {
      const res = await fetch('/api/ai/generate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          index, prompt, history, thinking: thinkCb.checked,
          timeZone, lastError: opts.lastError || null
        }),
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
          if (ev.type === 'reasoning') { reasoningStr += ev.text || ''; appendReasoning(ev.text || ''); }
          else if (ev.type === 'content') { contentStr += ev.text || ''; appendContent(ev.text || ''); }
          else if (ev.type === 'done') {
            finished = true;
            input.value = '';
            const dslText = JSON.stringify(ev.dsl, null, 2);
            history.push({ role: 'user', content: prompt });
            history.push({ role: 'assistant', content: JSON.stringify(ev.dsl) });
            const turn = { prompt, dslText, reasoning: reasoningStr };
            turns.push(turn);
            finish();
            drawTurns();
            // 试运行：统计命中数，帮助用户判断生成方向是否正确
            post('/api/ai/count', { index, dsl: ev.dsl })
              .then(r => { turn.hits = r.total; drawTurns(); })
              .catch(e => {
                turn.countError = e.message || '试运行失败';
                drawTurns();
                // ES 的真实报错（字段不存在、类型不匹配、nested path 写错…）是最准确的自愈素材，
                // 回喂给模型重生成一次；healed 置位避免与生成阶段的解析自愈叠加成无限重试
                status.innerHTML = '<span style="color:#d97706">查询执行失败，正在把错误反馈给模型自动重试…</span>';
                setTimeout(() => generate({
                  prompt,
                  lastError: `生成的 DSL 在 Elasticsearch 上执行失败，错误信息：${turn.countError}。请修正后重新输出，仍须遵守全部规则。`,
                  healed: true
                }), 300);
              });
          } else if (ev.type === 'error') {
            finished = true;
            fail(ev.error || '生成失败');
          }
        }
      }
      if (!finished) {
        if (contentStr.trim()) keepPartial();
        else fail('AI 服务未返回内容，请重试');
      }
    } catch (e) {
      if (e.name === 'AbortError') {
        // 手动停止：保留已生成的部分内容，不自动重试
        if (contentStr.trim()) keepPartial();
        else fail('已停止生成', { heal: false });
      } else {
        fail(e.message);
      }
    }
  }

  go.addEventListener('click', () => {
    if (streaming) { abortCtl?.abort(); return; }
    generate();
  });
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); if (!streaming) generate(); }
  });
  root.querySelector('#ai-close').addEventListener('click', () => {
    abortCtl?.abort();
    closeAllModals();
  });
  root.querySelector('#ai-clear').addEventListener('click', () => {
    if (turns.length && !confirm('确定清空全部对话？多轮修正的上下文将一并清除')) return;
    history.length = 0; turns.length = 0; drawTurns();
  });
  input.focus();
}
