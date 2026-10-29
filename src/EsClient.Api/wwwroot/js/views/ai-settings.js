import { get, post, toast } from '../api.js';
import { escapeHtml, showModal, closeAllModals, modalRoot } from '../ui.js';

// 配置有任何变化（保存/删除）都广播此事件，让顶栏等持有模型名副本的地方自行刷新
export const AI_SETTINGS_CHANGED = 'ai-settings-changed';

// ---------- AI 模型「新增/编辑」表单弹窗 ----------
// 切换使用中、删除、选「哪一项」都在顶栏下拉完成；这里只编辑单个配置的表单字段。
// existing 为要编辑的配置对象（含 id）；省略或传 null 即新增（密钥需完整填写）。
export function openAiSettingsDialog(onSaved, existing) {
  get('/api/ai/settings').then(s => {
    const presets = s.presets || {};
    const editingId = existing?.id || '';   // '' = 新增
    const cur = existing || {};

    const presetOpts = Object.entries(presets).map(([k, p]) =>
      `<option value="${escapeHtml(k)}">${escapeHtml(p.label)}</option>`).join('');

    showModal(editingId ? `编辑模型配置 — ${cur.model}` : '新增模型配置（OpenAI 兼容接口）', `
      <div class="field">
        <label>服务商</label>
        <select id="ai-provider">${presetOpts}</select>
      </div>
      <div class="field">
        <label>接口地址（Base URL）</label>
        <input type="text" id="ai-url" placeholder="https://api.deepseek.com/v1">
      </div>
      <div class="field">
        <label>API Key</label>
        <input type="password" id="ai-key" placeholder="sk-…" autocomplete="off">
      </div>
      <div class="field">
        <label>模型名称</label>
        <input type="text" id="ai-model" placeholder="deepseek-chat">
      </div>
      <div class="field">
        <label>思考模式模型名（可选）</label>
        <input type="text" id="ai-tmodel" placeholder="开启思考模式时使用，留空则与模型名称相同">
      </div>
      <div class="field">
        <label>JSON 模式（response_format）</label>
        <select id="ai-jsonmode">
          <option value="">自动（推荐）</option>
          <option value="on">强制开启</option>
          <option value="off">关闭</option>
        </select>
      </div>
      <div class="hint">JSON 模式让接口保证输出合法 JSON，减少解析失败；少数端点不支持时「自动」会自行降级。思考模式下不生效。</div>
      <div class="hint">密钥保存在本机 App_Data/ai-settings.json，不会上传；可保存多个配置，在顶栏随时切换</div>
      <div class="query-actions">
        <button class="btn btn-primary" id="ai-save" style="flex:1">保存</button>
        <button class="btn" id="ai-cancel">取消</button>
      </div>`);

    const root = modalRoot;
    const provider = root.querySelector('#ai-provider');
    const url = root.querySelector('#ai-url');
    const key = root.querySelector('#ai-key');
    const model = root.querySelector('#ai-model');
    const tmodel = root.querySelector('#ai-tmodel');
    const jsonmode = root.querySelector('#ai-jsonmode');

    function fillForm(m) {
      provider.value = m?.provider ?? 'custom';
      url.value = m?.baseUrl ?? '';
      // 后端不回传明文密钥：置空，留空保存则保持原值不变
      key.value = '';
      key.placeholder = m?.hasApiKey ? '已保存，留空则不变' : 'sk-…';
      model.value = m?.model ?? '';
      tmodel.value = m?.thinkingModel ?? '';
      jsonmode.value = m?.jsonMode == null ? '' : (m.jsonMode ? 'on' : 'off');
    }
    fillForm(cur);

    provider.addEventListener('change', () => {
      const p = presets[provider.value];
      if (p && provider.value !== 'custom') {
        url.value = p.baseUrl;
        model.value = p.model;
        tmodel.value = p.thinkingModel ?? '';
      }
    });

    root.querySelector('#ai-cancel').addEventListener('click', closeAllModals);

    root.querySelector('#ai-save').addEventListener('click', async () => {
      const btn = root.querySelector('#ai-save');
      btn.disabled = true;
      try {
        const r = await post('/api/ai/settings', {
          id: editingId || null,
          provider: provider.value,
          baseUrl: url.value.trim(),
          apiKey: key.value.trim(),
          model: model.value.trim(),
          thinkingModel: tmodel.value.trim() || null,
          jsonMode: jsonmode.value === '' ? null : (jsonmode.value === 'on')
        });
        // 新增的配置立即切换为使用中
        if (!editingId) await post('/api/ai/settings/switch', { id: r.id }).catch(() => {});
        toast('AI 设置已保存');
        closeAllModals();
        window.dispatchEvent(new Event(AI_SETTINGS_CHANGED));
        if (onSaved) onSaved();
      } catch (e) {
        toast(e.message, true);
        btn.disabled = false;
      }
    });

    if (!editingId) key.focus();
  }).catch(e => toast(e.message, true));
}

// ---------- 删除模型配置确认窗（与删除索引同风格的破坏性操作预警） ----------
// m 为顶栏下拉里点击删除的那一项（含 id / model）；isActive 标识其是否为使用中项；
// onDeleted 在删除成功后回调（用于就地刷新列表）
export function confirmDeleteAiModel(m, isActive, onDeleted) {
  showModal('删除模型配置', `
    <div class="del-confirm">
      <p class="del-warn">将删除模型配置 <code>${escapeHtml(m.model)}</code>，该操作不可恢复。</p>
      ${isActive ? '<p class="del-warn" style="color:#d9534f">这是当前使用中的模型，删除后将自动切换到其余配置。</p>' : ''}
      <div class="del-actions">
        <button class="btn" id="del-cancel">取消</button>
        <button class="btn btn-danger" id="del-ok">确认删除</button>
      </div>
    </div>`);
  const ok = modalRoot.querySelector('#del-ok');
  modalRoot.querySelector('#del-cancel').addEventListener('click', closeAllModals);
  ok.addEventListener('click', async () => {
    ok.disabled = true;
    try {
      await post('/api/ai/settings/delete', { id: m.id });
      closeAllModals();
      toast('已删除');
      window.dispatchEvent(new Event(AI_SETTINGS_CHANGED));
      if (onDeleted) onDeleted();
    } catch (e) {
      toast(e.message, true);
      ok.disabled = false;
    }
  });
}
