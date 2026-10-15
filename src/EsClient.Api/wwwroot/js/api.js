export async function api(path, opts = {}) {
  const res = await fetch(path, {
    headers: { 'Content-Type': 'application/json' },
    ...opts
  });
  if (!res.ok) {
    let msg = res.statusText;
    try {
      const j = await res.json();
      msg = j.error || j.title || msg;
    } catch { /* 忽略 */ }
    const e = new Error(msg);
    e.status = res.status;
    throw e;
  }
  return res.json();
}

export const get = p => api(p);
export const post = (p, body) => api(p, { method: 'POST', body: body === undefined ? '{}' : JSON.stringify(body) });
export const put = (p, body) => api(p, { method: 'PUT', body: JSON.stringify(body) });
export const del = p => api(p, { method: 'DELETE' });

export function toast(msg, isError = false) {
  const root = document.getElementById('toast-root');
  const el = document.createElement('div');
  el.className = 'toast' + (isError ? ' error' : '');
  el.textContent = msg;
  root.appendChild(el);
  setTimeout(() => el.remove(), isError ? 6000 : 3000);
}

export function fmtBytes(n) {
  if (n == null) return '-';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0, v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)}${units[i]}`;
}

export function fmtNum(n) {
  if (n == null) return '-';
  return n.toLocaleString('zh-CN');
}
