// 通用 UI 原语：HTML 转义 + 弹窗。
// 独立成模块是为了让依赖方向单向化 —— 此前 escapeHtml 住在 indices.js、
// 弹窗住在 browse.js，于是 indices.js ↔ browse.js 互相 import 形成环，
// 仅靠「函数声明会提升」才没出问题（顶层一旦用到对方的导出即 TDZ 报错）。
// 本模块不 import 任何视图模块，可被任意视图安全依赖。

export function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// 轻量 JSON 语法高亮（不依赖外部库），供「AI 生成查询」与「AI 问答」共用。
// 所有片段先转义再拼接 —— 高亮正则匹配的是原文，转义必须发生在拼接之后。
export function highlightJson(text) {
  const esc = escapeHtml;
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