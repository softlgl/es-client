// SSE 流读取：POST JSON → 逐事件回调。
// 「AI 生成查询」与「AI 问答」两条链路都要做同样的 fetch / 分帧 / 终止判定，
// 原先各写一份，服务端新增事件类型或调整分帧规则时必须同步改两处。

/**
 * 发起一次 SSE 请求并逐事件回调 onEvent。
 *
 * @param {string} url
 * @param {object} payload   请求体
 * @param {AbortSignal} signal
 * @param {(ev:object)=>Promise<any>|any} onEvent
 *        返回 false 表示业务侧已收尾（收到 done/error），停止读取。
 * @returns {Promise<boolean>}
 *        true  = 流自然结束但未收到终止事件（需按「流被截断」处理）；
 *        false = onEvent 已主动收尾。
 * @throws {Error} HTTP 失败时抛出，message 取自响应体的 error 字段。
 */
export async function streamSse(url, payload, signal, onEvent) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
    signal
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.error || `请求失败（${res.status}）`);
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buf = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) return true;
    buf += decoder.decode(value, { stream: true });
    let idx;
    // SSE 以空行分帧，一个 chunk 可能含多帧，也可能只含半帧（靠 buf 跨包拼接）
    while ((idx = buf.indexOf('\n\n')) >= 0) {
      const raw = buf.slice(0, idx).trim();
      buf = buf.slice(idx + 2);
      if (!raw.startsWith('data:')) continue;
      let ev;
      try { ev = JSON.parse(raw.slice(5)); } catch { continue; }
      if (await onEvent(ev) === false) return false;
    }
  }
}