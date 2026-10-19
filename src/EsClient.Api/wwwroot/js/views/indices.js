import { del, toast, fmtNum } from '../api.js?v=14';
import { showModal, closeAllModals, modalRoot, escapeHtml } from '../ui.js?v=14';

// 删除索引确认窗（概览页与侧栏复用）；onDeleted 在删除成功后回调
export function confirmDeleteIndex(name, docsCount, onDeleted) {
  showModal('删除索引', `
    <div class="del-confirm">
      <p class="del-warn">该操作不可恢复，将永久删除索引
        <code>${escapeHtml(name)}</code> 及其全部 ${fmtNum(docsCount)} 条文档。</p>
      ${name.startsWith('.') ? '<p class="del-warn" style="color:#d9534f">这是系统索引（Kibana 等组件的内部数据），删除可能导致相关功能异常。</p>' : ''}
      <p class="del-tip">请输入索引名作为确认口令：</p>
      <input type="text" class="del-input" id="del-input" autocomplete="off" spellcheck="false"
             placeholder="${escapeHtml(name)}">
      <div class="del-actions">
        <button class="btn" id="del-cancel">取消</button>
        <button class="btn btn-danger" id="del-ok" disabled>确认删除</button>
      </div>
    </div>`);
  const input = modalRoot.querySelector('#del-input');
  const ok = modalRoot.querySelector('#del-ok');
  input.focus();
  input.addEventListener('input', () => { ok.disabled = input.value !== name; });
  modalRoot.querySelector('#del-cancel').addEventListener('click', closeAllModals);
  ok.addEventListener('click', async () => {
    ok.disabled = true;
    try {
      // 系统索引（以 . 开头）已由弹窗的名称确认 + 红字警告担保，此处带 force 显式越过后端保护闸门
      const qs = name.startsWith('.') ? '?force=true' : '';
      await del(`/api/indices/${encodeURIComponent(name)}${qs}`);
      closeAllModals();
      toast(`索引 ${name} 已删除`);
      window.dispatchEvent(new Event('indices-changed'));
      if (onDeleted) onDeleted();
    } catch (e) {
      toast(e.message, true);
      ok.disabled = false;
    }
  });
}

// 索引列表统一在左侧栏展示（过滤/刷新/删除都在侧栏）：中间区域仅作引导占位
export function renderIndices(view) {
  view.innerHTML = `
    <div class="panel connect-box">
      <h1>选择索引开始浏览</h1>
      <div class="sub">索引列表在左侧栏：点击切换，悬停可删除，顶部支持过滤与刷新。</div>
    </div>`;
}
