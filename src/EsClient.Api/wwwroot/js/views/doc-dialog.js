import { get, post, put, del, toast } from '../api.js';
import { escapeHtml, showModal, closeAllModals, modalRoot } from '../ui.js';

// 文档查看/编辑/删除与新增文档弹窗。
// 原先内嵌在 browse.js 的 renderBrowse 闭包里，靠捕获 index 与 load() 运行；
// 抽出后 index 与刷新回调改为显式参数，模块不再依赖浏览视图。

// 文档详情（查看 / 编辑 / 保存 / 删除）
export async function openDocDetail(index, id, onChanged) {
  let json;
  try {
    json = await get(`/api/indices/${encodeURIComponent(index)}/documents/${encodeURIComponent(id)}`);
  } catch (e) {
    toast(e.message, true);
    return;
  }
  const orig = JSON.stringify(json._source ?? json, null, 2);
  const full = JSON.stringify(json, null, 2);
  showModal(`文档 ${id}`, `
    <pre id="doc-view">${escapeHtml(full)}</pre>
    <textarea class="doc-editor hidden" id="doc-editor" spellcheck="false"></textarea>
    <div class="doc-actions">
      <button class="btn btn-danger" id="doc-del">删除</button>
      <span style="flex:1"></span>
      <button class="btn hidden" id="doc-cancel">取消</button>
      <button class="btn btn-primary hidden" id="doc-save">保存</button>
      <button class="btn" id="doc-copy">复制</button>
      <button class="btn" id="doc-edit">编辑</button>
    </div>`);
  modalRoot.querySelector('#doc-copy').addEventListener('click', async () => {
    await navigator.clipboard.writeText(full).catch(() => {});
    toast('已复制到剪贴板');
  });
  const docView = modalRoot.querySelector('#doc-view');
  const editor = modalRoot.querySelector('#doc-editor');
  const btnEdit = modalRoot.querySelector('#doc-edit');
  const btnSave = modalRoot.querySelector('#doc-save');
  const btnCancel = modalRoot.querySelector('#doc-cancel');
  const setMode = editing => {
    docView.classList.toggle('hidden', editing);
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
      onChanged();
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
        onChanged();
      } catch (e) {
        toast(e.message, true);
      }
    });
  });
}

// 新增文档（_id 留空则由 ES 生成）
export function openNewDocDialog(index, onChanged) {
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
      onChanged();
    } catch (e) {
      toast(e.message, true);
      ok.disabled = false;
    }
  });
  modalRoot.querySelector('#newdoc-json').focus();
}