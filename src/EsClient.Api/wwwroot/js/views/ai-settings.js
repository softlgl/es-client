import { get, post, toast } from '../api.js';
import { escapeHtml, showModal, closeAllModals, modalRoot } from '../ui.js';

// ---------- AI 模型配置对话框（OpenAI 兼容接口，全局唯一入口在顶栏 AI 下拉） ----------
export function openAiSettingsDialog(onSaved) {
  get('/api/ai/settings').then(s => {
    const presets = s.presets || {};
    const models = s.models || [];
    let editingId = s.activeId || models[0]?.id || '';   // 当前编辑的配置；'' = 新增

    const presetOpts = Object.entries(presets).map(([k, p]) =>
      `<option value="${escapeHtml(k)}">${escapeHtml(p.label)}</option>`).join('');
    const cfgOpts = '<option value="">＋ 新增配置…</option>' + models.map(m =>
      `<option value="${escapeHtml(m.id)}" ${m.id === editingId ? 'selected' : ''}>${escapeHtml(m.model)}${m.id === s.activeId ? '（使用中）' : ''}</option>`).join('');

    showModal('AI 设置（OpenAI 兼容接口）', `
      <div class="field">
        <label>模型配置</label>
        <div class="ai-cfg-row">
          <select id="ai-cfg-sel">${cfgOpts}</select>
          <button class="btn" id="ai-cfg-del" ${models.length ? '' : 'disabled'}>删除</button>
        </div>
      </div>
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
    const cfgSel = root.querySelector('#ai-cfg-sel');
    const provider = root.querySelector('#ai-provider');
    const url = root.querySelector('#ai-url');
    const key = root.querySelector('#ai-key');
    const model = root.querySelector('#ai-model');
    const tmodel = root.querySelector('#ai-tmodel');
    const jsonmode = root.querySelector('#ai-jsonmode');
    const delBtn = root.querySelector('#ai-cfg-del');

    function fillForm(m) {
      provider.value = m?.provider ?? 'custom';
      url.value = m?.baseUrl ?? '';
      // 后端不回传明文密钥：置空，留空保存则保持原值不变
      key.value = '';
      key.placeholder = m?.hasApiKey ? '已保存，留空则不变' : 'sk-…';
      model.value = m?.model ?? '';
      tmodel.value = m?.thinkingModel ?? '';
      jsonmode.value = m?.jsonMode == null ? '' : (m.jsonMode ? 'on' : 'off');
      delBtn.disabled = !m;
    }
    fillForm(models.find(m => m.id === editingId));

    cfgSel.addEventListener('change', () => {
      editingId = cfgSel.value;
      fillForm(models.find(m => m.id === editingId));
    });

    provider.addEventListener('change', () => {
      const p = presets[provider.value];
      if (p && provider.value !== 'custom') {
        url.value = p.baseUrl;
        model.value = p.model;
        tmodel.value = p.thinkingModel ?? '';
      }
    });

    root.querySelector('#ai-cancel').addEventListener('click', closeAllModals);
    delBtn.addEventListener('click', async () => {
      if (!editingId || !confirm('确定删除该模型配置？')) return;
      try {
        await post('/api/ai/settings/delete', { id: editingId });
        toast('已删除');
        openAiSettingsDialog(onSaved);   // 重新加载列表
      } catch (e) {
        toast(e.message, true);
      }
    });

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
        if (onSaved) onSaved();
      } catch (e) {
        toast(e.message, true);
        btn.disabled = false;
      }
    });

    key.focus();
  }).catch(e => toast(e.message, true));
}
