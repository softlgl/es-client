import { get, post, toast, fmtNum } from './api.js?v=14';
import { renderConnect } from './views/connect.js?v=14';
import { openConnectModal } from './views/connect.js?v=14';
import { renderIndices, escapeHtml, confirmDeleteIndex } from './views/indices.js?v=14';
import { renderBrowse, clearBrowseCache } from './views/browse.js?v=14';
import { renderAiChat } from './views/ai-chat.js?v=14';
import { openAiSettingsDialog } from './views/ai-settings.js?v=14';

const view = document.getElementById('view');

// ---------- 顶栏（连接下拉） ----------
function updateHeader(conn) {
  const dd = document.getElementById('conn-dropdown');
  const info = document.getElementById('conn-info');
  const a = conn.active;
  info.classList.add('hidden');
  dd.classList.remove('hidden');
  if (conn.connected && a) {
    document.getElementById('conn-label').innerHTML =
      `<span class="health-dot health-green"></span> <b>${escapeHtml(a.name || a.clusterName)}</b> · v${escapeHtml(a.version)} <span class="ci-url-inline">${escapeHtml(a.url)}</span> <span class="dd-arrow">▾</span>`;
  } else {
    document.getElementById('conn-label').innerHTML =
      `<span class="dd-placeholder">选择连接…</span> <span class="dd-arrow">▾</span>`;
  }
  buildConnMenu(conn);
}

function buildConnMenu(conn) {
  const menu = document.getElementById('conn-menu');
  const SVG_EDIT = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z"/></svg>';
  const SVG_DEL = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 6L6 18"/><path d="M6 6l12 12"/></svg>';
  menu.innerHTML = `
    ${conn.connections.map(c => {
      const isActive = conn.active?.id === c.id && c.connected;
      const displayName = c.name || c.clusterName;
      return `<div class="conn-item${isActive ? ' active' : ''}" data-id="${escapeHtml(c.id)}">
        <span class="health-dot ${c.connected ? 'health-green' : 'health-red'}"
              title="${c.connected ? '已连接' : '已断开（点击自动重连）'}"></span>
        <span class="ci-info">
          <b>${escapeHtml(displayName)}</b>
          <span class="ci-url">${escapeHtml(c.url)} · ${escapeHtml(c.clusterName)} · v${escapeHtml(c.version)}${c.connected ? '' : ' · 已断开'}</span>
        </span>
        ${isActive ? '<span class="badge badge-open">使用中</span>' : `
          <span class="ci-acts">
            <button class="conn-edit" data-edit="${escapeHtml(c.id)}" title="编辑连接">${SVG_EDIT}</button>
            <button class="conn-del" data-del="${escapeHtml(c.id)}" title="删除该连接">${SVG_DEL}</button>
          </span>`}
      </div>`;
    }).join('')}
    <div class="conn-add">＋ 添加连接…</div>
    ${conn.connected ? '<div class="conn-disconnect">断开当前连接</div>' : ''}`;

  menu.querySelectorAll('.conn-item').forEach(el => el.addEventListener('click', e => {
    if (e.target.classList.contains('conn-del') || e.target.classList.contains('conn-edit')) return;
    closeConnMenu();
    if (el.classList.contains('active')) {
      // 已在使用：hash 异常停在引导页时归位到索引列表
      if (location.hash === '' || location.hash === '#/connect') {
        location.hash = '#/indices';
        route();
      }
      return;
    }
    switchConn(el.dataset.id);
  }));
  menu.querySelectorAll('.conn-edit').forEach(b => b.addEventListener('click', e => {
    e.stopPropagation();
    const c = conn.connections.find(x => x.id === b.dataset.edit);
    if (!c) return;
    closeConnMenu();
    openConnectModal(() => { toast('连接已更新'); route(); }, c);
  }));
  menu.querySelectorAll('.conn-del').forEach(b => b.addEventListener('click', async e => {
    e.stopPropagation();
    try {
      const r = await post('/api/connection/remove', { id: b.dataset.del });
      toast('已删除连接');
      if (!r.connected) {
        location.hash = '#/connect';
        window.dispatchEvent(new Event('hashchange'));
      } else route();
    } catch (err) { toast(err.message, true); }
  }));
  if (conn.connected) menu.querySelector('.conn-disconnect').addEventListener('click', async () => {
    closeConnMenu();
    try { await post('/api/disconnect'); } catch { /* 忽略 */ }
    if (location.hash === '#/connect') route();
    else location.hash = '#/connect';
  });
  menu.querySelector('.conn-add').addEventListener('click', () => {
    closeConnMenu();
    openConnectModal(() => {
      toast('连接成功');
      gotoIndices();
    });
  });
}

// 跳转到索引列表；hash 已相同时 hashchange 不会触发，必须直接调 route()
function gotoIndices() {
  const changed = location.hash !== '#/indices';
  location.hash = '#/indices';
  if (changed) window.dispatchEvent(new Event('hashchange'));
  else route();
}

async function switchConn(id) {
  try {
    await post('/api/connection/switch', { id });
    toast('已切换连接');
    clearBrowseCache();
    // 连接已变：立即作废旧侧栏数据，强制路由重新拉取索引
    sidebarLoaded = false;
    sidebarItems = [];
    gotoIndices();
  } catch (e) { toast(e.message, true); }
}

function closeConnMenu() {
  document.getElementById('conn-menu').classList.add('hidden');
}

const connDd = document.getElementById('conn-dropdown');
connDd.querySelector('#conn-label').addEventListener('click', e => {
  e.stopPropagation();
  document.getElementById('conn-menu').classList.toggle('hidden');
  closeAiMenu();
});
document.addEventListener('click', e => {
  if (!connDd.contains(e.target)) closeConnMenu();
});

// ---------- 顶栏 AI 模型下拉（全局唯一模型切换/配置入口） ----------
const aiDd = document.getElementById('ai-dropdown');
const aiLabel = document.getElementById('ai-label');
const aiMenu = document.getElementById('ai-menu');

function closeAiMenu() { aiMenu.classList.add('hidden'); }

async function refreshAiLabel() {
  try {
    const s = await get('/api/ai/settings');
    const active = (s.models || []).find(m => m.id === s.activeId);
    aiLabel.textContent = active ? `✨ ${active.model}` : '✨ AI 未配置';
  } catch { aiLabel.textContent = '✨ AI 未配置'; }
}

aiLabel.addEventListener('click', async e => {
  e.stopPropagation();
  if (!aiMenu.classList.contains('hidden')) { closeAiMenu(); return; }
  closeConnMenu();
  aiMenu.innerHTML = '<div class="conn-item">加载中…</div>';
  aiMenu.classList.remove('hidden');
  try {
    const s = await get('/api/ai/settings');
    const models = s.models || [];
    aiMenu.innerHTML = (models.map(m => `
      <div class="conn-item" data-ai-id="${escapeHtml(m.id)}">
        <span class="ai-item-name">${escapeHtml(m.model)}</span>
        ${m.id === s.activeId ? '<span class="ai-active">使用中</span>' : ''}
      </div>`).join('') || '<div class="conn-item ai-menu-hint">尚未配置模型</div>')
      + `<div class="ai-menu-divider"></div>
         <div class="conn-item" id="ai-manage">⚙ 管理模型配置…</div>`;
    aiMenu.querySelectorAll('[data-ai-id]').forEach(el => el.addEventListener('click', async () => {
      try {
        await post('/api/ai/settings/switch', { id: el.dataset.aiId });
        toast('已切换模型');
        closeAiMenu();
        refreshAiLabel();
      } catch (err) { toast(err.message, true); }
    }));
    aiMenu.querySelector('#ai-manage').addEventListener('click', () => {
      closeAiMenu();
      openAiSettingsDialog(refreshAiLabel);
    });
  } catch (err) {
    aiMenu.innerHTML = `<div class="conn-item ai-menu-hint">${escapeHtml(err.message)}</div>`;
  }
});
document.addEventListener('click', e => {
  if (!aiDd.contains(e.target)) closeAiMenu();
});
refreshAiLabel();

// ---------- 侧栏宽度（可拖拽） ----------
const sidebarEl = document.getElementById('sidebar');
const resizerEl = document.getElementById('sb-resizer');
const SB_W_MIN = 180, SB_W_MAX = 520, SB_W_DEFAULT = 280;

function setSidebarWidth(w) {
  const clamped = Math.max(SB_W_MIN, Math.min(SB_W_MAX, w));
  sidebarEl.style.width = clamped + 'px';
  return clamped;
}
setSidebarWidth(parseInt(localStorage.getItem('sb-width'), 10) || SB_W_DEFAULT);

resizerEl.addEventListener('mousedown', (e) => {
  e.preventDefault();
  const startX = e.clientX;
  const startW = sidebarEl.getBoundingClientRect().width;
  resizerEl.classList.add('dragging');
  document.body.classList.add('sb-resizing');
  const onMove = (ev) => setSidebarWidth(startW + ev.clientX - startX);
  const onUp = () => {
    document.removeEventListener('mousemove', onMove);
    document.removeEventListener('mouseup', onUp);
    resizerEl.classList.remove('dragging');
    document.body.classList.remove('sb-resizing');
    localStorage.setItem('sb-width', parseInt(sidebarEl.style.width, 10));
  };
  document.addEventListener('mousemove', onMove);
  document.addEventListener('mouseup', onUp);
});

// ---------- 侧栏（索引列表） ----------
let sidebarItems = [];
let sidebarKw = '';
let sidebarLoaded = false;
let showSystem = localStorage.getItem('show-system') === '1';

async function refreshSidebar(activeIndex) {
  const sb = document.getElementById('sidebar');
  if (sb && !sb.classList.contains('hidden'))
    sb.innerHTML = '<div class="sb-status"><span class="spinner"></span>索引加载中…</div>';
  try {
    sidebarItems = await get('/api/indices');
    sidebarLoaded = true;
  } catch (e) {
    // 失败不再静默：显示错误与重试入口（远程集群慢/不可达时用户能看到原因）
    sidebarItems = [];
    if (sb && !sb.classList.contains('hidden')) {
      sb.innerHTML = `<div class="sb-status sb-error">索引加载失败：${escapeHtml(e.message)}
        <button class="btn btn-sm" id="sb-retry">重试</button></div>`;
      sb.querySelector('#sb-retry').addEventListener('click', () => refreshSidebar(activeIndex));
    }
    return;
  }
  drawSidebar(activeIndex);
}

function drawSidebar(activeIndex) {
  const sb = document.getElementById('sidebar');
  if (!sb || sb.classList.contains('hidden')) return;
  const kw = sidebarKw.trim().toLowerCase();
  const items = sidebarItems.filter(x =>
    (showSystem || !x.system) && (!kw || x.index.toLowerCase().includes(kw)));
  sb.innerHTML = `
    <div class="sb-head">
      <input type="text" id="sb-filter" placeholder="过滤索引…" value="${escapeHtml(sidebarKw)}">
      <button class="btn btn-ghost btn-sm" id="sb-refresh" title="刷新索引列表">⟳</button>
    </div>
    <label class="sb-sys" title="显示 . 开头的系统索引（Kibana 内部等）">
      <input type="checkbox" id="sb-sys-chk" ${showSystem ? 'checked' : ''}>
      显示系统索引
      <span class="sb-count">${items.length} / ${sidebarItems.length}</span>
    </label>
    <div class="sb-list">
      ${items.map(x => `
        <div class="sb-item${x.index === activeIndex ? ' active' : ''}${x.status === 'close' ? ' closed' : ''}"
             data-idx="${escapeHtml(x.index)}" title="${escapeHtml(x.index)}（${fmtNum(x.docsCount)} 条）">
          <span class="health-dot health-${x.health}"></span>
          <span class="sb-name">${escapeHtml(x.index)}</span>
          ${x.system ? '<span class="sys-badge">sys</span>' : ''}
          <span class="sb-count">${fmtNum(x.docsCount)}</span>
          <button class="sb-ai" data-ai="${escapeHtml(x.index)}" title="AI 问答">✨</button>
          <button class="sb-del" data-del="${escapeHtml(x.index)}" title="删除索引">×</button>
        </div>`).join('') || `<div class="sb-empty">无匹配索引</div>`}
    </div>`;
  const filter = sb.querySelector('#sb-filter');
  filter.addEventListener('input', e => {
    sidebarKw = e.target.value;
    const pos = e.target.selectionStart;
    drawSidebar(activeIndex);
    const f = sb.querySelector('#sb-filter');
    f.focus();
    f.setSelectionRange(pos, pos);
  });
  sb.querySelector('#sb-sys-chk').addEventListener('change', e => {
    showSystem = e.target.checked;
    localStorage.setItem('show-system', showSystem ? '1' : '0');
    drawSidebar(activeIndex);
  });
  sb.querySelector('#sb-refresh').addEventListener('click', () => refreshSidebar(activeIndex));
  sb.querySelectorAll('.sb-ai').forEach(btn => btn.addEventListener('click', e => {
    e.stopPropagation();
    location.hash = `#/ai/${encodeURIComponent(btn.dataset.ai)}`;
  }));
  sb.querySelectorAll('.sb-del').forEach(btn => btn.addEventListener('click', e => {
    e.stopPropagation();
    const name = btn.dataset.del;
    const info = sidebarItems.find(x => x.index === name);
    confirmDeleteIndex(name, info?.docsCount, () => {
      if (activeIndex === name) location.hash = '#/indices';
      else refreshSidebar(activeIndex);
    });
  }));
  sb.querySelectorAll('.sb-item').forEach(el => el.addEventListener('click', () => {
    if (el.classList.contains('closed')) {
      toast('该索引处于关闭状态，无法浏览', true);
      return;
    }
    const target = `#/browse/${encodeURIComponent(el.dataset.idx)}`;
    if (location.hash !== target) location.hash = target;
  }));
}

// 索引变化（删除索引/文档增删改）后刷新侧栏计数
window.addEventListener('indices-changed', () => {
  const sb = document.getElementById('sidebar');
  if (sidebarLoaded && sb && !sb.classList.contains('hidden'))
    refreshSidebar(sb.dataset.active || '');
});

// ---------- 路由 ----------
let routing = false;
let pendingRoute = false;
let lastConnUrl = null;
let forceSidebar = false; // 连接变化后强制重拉侧栏（dataset.active 守卫会挡住正常加载路径）
export async function route() {
  if (routing) { pendingRoute = true; return; } // 执行中收到新请求：结束后重放
  routing = true;
  try {
    const conn = await get('/api/connection');
    updateHeader(conn);
    const activeUrl = conn.active?.id || null;
    if (activeUrl !== lastConnUrl) {
      lastConnUrl = activeUrl;
      sidebarLoaded = false;
      sidebarItems = [];
      forceSidebar = true;
    }
    const sb = document.getElementById('sidebar');
    const h = location.hash || '';
    if (!conn.connected) {
      sb.classList.add('hidden');
      resizerEl.classList.add('hidden');
      sb.dataset.active = '';
      sidebarLoaded = false;
      sidebarKw = '';
      clearBrowseCache();
      if (h !== '#/connect') location.hash = '#/connect';
      renderConnect(view);
      return;
    }
    sb.classList.remove('hidden');
    resizerEl.classList.remove('hidden');
    if (h.startsWith('#/browse/') || h.startsWith('#/ai/')) {
      const isAi = h.startsWith('#/ai/');
      const idx = decodeURIComponent(h.slice((isAi ? '#/ai/' : '#/browse/').length));
      if (forceSidebar || sb.dataset.active !== idx) {
        sb.dataset.active = idx;
        forceSidebar = false;
        if (sidebarLoaded) drawSidebar(idx); else await refreshSidebar(idx);
      }
      if (isAi) await renderAiChat(view, idx);
      else renderBrowse(view, idx);
    } else {
      // #/indices、空 hash 或未知 hash：已连接时统一落到索引列表
      if (h !== '#/indices') location.hash = '#/indices';
      if (forceSidebar || sb.dataset.active !== '') {
        sb.dataset.active = '';
        forceSidebar = false;
        if (sidebarLoaded) drawSidebar(''); else await refreshSidebar('');
      }
      renderIndices(view);
    }
  } catch (e) {
    toast(e.message, true);
  } finally {
    routing = false;
    if (pendingRoute) { pendingRoute = false; route(); }
  }
}

window.addEventListener('hashchange', route);

// 新浏览器会话首次打开：不继承服务端遗留的连接，由用户下拉主动选择；
// 同一标签页刷新时 sessionStorage 仍在，保持当前连接
(async () => {
  if (!sessionStorage.getItem('es-client-session')) {
    sessionStorage.setItem('es-client-session', '1');
    try { await post('/api/disconnect'); } catch { /* 忽略 */ }
  }
  route();
})();
