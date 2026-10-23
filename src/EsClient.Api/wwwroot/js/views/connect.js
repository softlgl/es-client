import { post, toast } from '../api.js';
import { showModal, closeAllModals, modalRoot } from '../ui.js';

// 连接表单（连接页与弹窗共用）；existing 传入时为编辑模式
const FORM_HTML = `
  <form class="connect-form">
    <div class="field">
      <label>连接名称 <span style="color:#d9534f">*</span></label>
      <input type="text" class="f-name" placeholder="如：生产集群" required>
    </div>
    <div class="field">
      <label>ES 地址</label>
      <input type="text" class="f-url" placeholder="http://localhost:9200" required>
    </div>
    <div class="field">
      <label>认证方式</label>
      <select class="f-auth">
        <option value="none">无认证</option>
        <option value="basic">用户名 / 密码</option>
        <option value="apikey">API Key</option>
      </select>
    </div>
    <div class="basic-fields hidden">
      <div class="field"><label>用户名</label><input type="text" class="f-user" autocomplete="off"></div>
      <div class="field"><label>密码</label><input type="password" class="f-pass" autocomplete="off"></div>
    </div>
    <div class="apikey-fields hidden">
      <div class="field"><label>API Key（支持原始 id:secret 或已编码值）</label><input type="text" class="f-key" autocomplete="off"></div>
    </div>
    <div class="field">
      <label class="checkbox-row">
        <input type="checkbox" class="f-insecure">
        跳过 HTTPS 证书校验（自签名证书时勾选）
      </label>
    </div>
    <button type="submit" class="btn btn-primary" style="width:100%">连接</button>
  </form>`;

export function renderConnectForm(root, onSuccess, existing = null) {
  root.innerHTML = FORM_HTML;
  const q = sel => root.querySelector(sel);
  const auth = q('.f-auth');
  auth.addEventListener('change', () => {
    q('.basic-fields').classList.toggle('hidden', auth.value !== 'basic');
    q('.apikey-fields').classList.toggle('hidden', auth.value !== 'apikey');
  });

  if (existing) {
    // 编辑模式：默认只展示名称，地址/凭据需点开「修改地址或凭据」才可见
    q('.f-name').value = existing.name || '';
    q('.f-url').value = existing.url || '';
    q('.f-insecure').checked = !!existing.insecure;
    // 后端不再回传明文密钥/密码：按已保存的认证方式回填，凭据框留空表示保持不变
    auth.value = existing.authMode || 'none';
    if (auth.value === 'basic') q('.f-user').value = existing.username || '';
    q('.f-pass').placeholder = existing.hasPassword ? '已保存，留空则不变' : '';
    q('.f-key').placeholder = existing.hasApiKey ? '已保存，留空则不变' : 'API Key（支持原始 id:secret 或已编码值）';
    auth.dispatchEvent(new Event('change'));
    q('button[type="submit"]').textContent = '保存';

    const urlField = q('.f-url').closest('.field');
    const roInfo = document.createElement('div');
    roInfo.className = 'conn-ro-info';
    roInfo.textContent = `${existing.url} · ${existing.clusterName || '未知集群'} · v${existing.version || '?'}`;
    urlField.classList.add('hidden');
    urlField.parentNode.insertBefore(roInfo, urlField);
    const toggle = document.createElement('div');
    toggle.className = 'edit-toggle';
    toggle.textContent = '修改地址或凭据…';
    toggle.addEventListener('click', () => {
      const full = q('.connect-form').classList.toggle('edit-full');
      toggle.textContent = full ? '收起配置修改' : '修改地址或凭据…';
      roInfo.classList.toggle('hidden', full);
      urlField.classList.toggle('hidden', !full);
    });
    q('.connect-form').insertBefore(toggle, q('button[type="submit"]'));
  }

  q('.connect-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const btn = q('button[type="submit"]');
    btn.disabled = true;
    btn.textContent = existing ? '保存中…' : '连接中…';
    try {
      // 编辑且未展开完整配置：仅提交名称（保留原地址与凭据）
      const renameOnly = existing && !q('.connect-form').classList.contains('edit-full');
      if (renameOnly) {
        await post('/api/connection/update', { id: existing.id, name: q('.f-name').value.trim() });
        if (onSuccess) onSuccess(); else { location.hash = '#/indices'; window.dispatchEvent(new Event('hashchange')); }
        return;
      }
      const body = {
        name: q('.f-name').value.trim(),
        url: q('.f-url').value.trim(),
        authMode: auth.value,
        username: null,
        password: null,
        apiKey: null,
        insecure: q('.f-insecure').checked
      };
      if (auth.value === 'basic') {
        body.username = q('.f-user').value;
        body.password = q('.f-pass').value;
      } else if (auth.value === 'apikey') {
        body.apiKey = q('.f-key').value;
      }
      if (existing) await post('/api/connection/update', { id: existing.id, ...body });
      else await post('/api/connect', body);
      if (onSuccess) onSuccess();
      else {
        location.hash = '#/indices';
        window.dispatchEvent(new Event('hashchange'));
      }
    } catch (err) {
      toast(err.message, true);
    } finally {
      btn.disabled = false;
      btn.textContent = existing ? '保存' : '连接';
    }
  });
}

// 顶栏下拉触发的添加/编辑连接弹窗
export function openConnectModal(onSuccess, existing = null) {
  showModal(existing ? '编辑连接' : '添加连接',
    `<div style="min-width:380px"><div class="connect-form-root"></div></div>`);
  renderConnectForm(modalRoot.querySelector('.connect-form-root'), () => {
    closeAllModals();
    if (onSuccess) onSuccess();
  }, existing);
}

export function renderConnect(view) {
  // 连接管理已全部收敛到顶栏下拉：此处仅作未连接时的引导
  view.innerHTML = `
    <div class="panel connect-box">
      <h1>未连接集群</h1>
      <div class="sub">点击右上角「选择连接」重连已保存的集群，或选择「＋ 添加连接…」新建连接。</div>
    </div>`;
}
