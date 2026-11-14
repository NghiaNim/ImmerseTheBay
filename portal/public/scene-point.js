// Screen-space video grounding. Physical hand depth is not inferred here.
export function captureVisibleFrame(source, rect, point) {
  const sw = source.videoWidth || source.naturalWidth, sh = source.videoHeight || source.naturalHeight;
  if (!sw || !sh || !rect.width || !rect.height) throw new Error('Wait for the scene image to load.');
  const aspect = rect.width / rect.height, cropW = Math.min(sw, sh * aspect), cropH = cropW / aspect;
  const screenPoint = { x: (point.x - rect.left) / rect.width, y: (point.y - rect.top) / rect.height };
  // A local crop preserves small shelf products instead of shrinking them into a few pixels.
  const region = { x: Math.max(0, Math.min(.76, screenPoint.x - .12)), y: Math.max(0, Math.min(.64, screenPoint.y - .18)), width: .24, height: .36 };
  const canvas = document.createElement('canvas'); canvas.width = 640; canvas.height = Math.round(640 * cropH * region.height / (cropW * region.width));
  canvas.getContext('2d').drawImage(source, (sw - cropW) / 2 + region.x * cropW, (sh - cropH) / 2 + region.y * cropH, cropW * region.width, cropH * region.height, 0, 0, canvas.width, canvas.height);
  return { frame: canvas.toDataURL('image/jpeg', .9), point: { x: (screenPoint.x - region.x) / region.width, y: (screenPoint.y - region.y) / region.height }, region, screenPoint };
}
export function installScenePoint({ ready, getRun, getSignal, headers, onIntent, notify }) {
  const screen = document.querySelector('.screen');
  const button = document.createElement('button'); button.id = 'scenePoint'; button.type = 'button'; button.textContent = '指向物体'; button.setAttribute('aria-pressed', 'false');
  document.querySelector('.lesson-controls').append(button);
  const marker = document.createElement('div'); marker.id = 'sceneObject'; marker.hidden = true; marker.setAttribute('aria-live', 'polite'); screen.append(marker);
  let enabled = false, pending = false, down = null, markerTimer;
  const clear = () => { clearTimeout(markerTimer); marker.hidden = true; };
  button.onclick = () => {
    enabled = !enabled; button.setAttribute('aria-pressed', String(enabled)); screen.classList.toggle('pointing', enabled); clear();
    notify(enabled ? '点击中央画面指向物体；按住再松开表示选择。' : '已退出物体指向。');
  };
  screen.addEventListener('pointerdown', event => {
    if (!enabled || pending || event.target.closest('button') || event.button !== 0) return;
    down = { id: event.pointerId, x: event.clientX, y: event.clientY, at: performance.now() };
  });
  screen.addEventListener('pointercancel', () => { down = null; });
  screen.addEventListener('pointerup', async event => {
    const press = down; down = null;
    if (!enabled || pending || !press || press.id !== event.pointerId || event.target.closest('button')) return;
    if (!ready()) { notify('请先开始课程，并等当前操作完成。'); return; }
    if (Math.hypot(event.clientX - press.x, event.clientY - press.y) > 20) return;
    const run = getRun(), rect = screen.getBoundingClientRect();
    const video = document.querySelector('#video');
    const source = video.srcObject && video.videoWidth ? video : document.querySelector('#photoPreview');
    const gesture = performance.now() - press.at >= 500 ? 'pinch' : 'point';
    pending = true; button.disabled = true; clear(); notify('正在识别指向的物体…');
    try {
      const input = captureVisibleFrame(source, rect, { x: event.clientX, y: event.clientY });
      if (Math.hypot((input.screenPoint.x - .5) / .43, (input.screenPoint.y - .47) / .49) > .86) throw new Error('请选择中央可见画面里的物体。');
      const response = await fetch('/api/vision/point', { method: 'POST', headers: { ...headers(), 'content-type': 'application/json' }, signal: getSignal(), body: JSON.stringify({ frame: input.frame, point: input.point, gesture }) });
      const hit = await response.json();
      if (run !== getRun()) return;
      if (!response.ok) throw new Error(hit.error || 'Object recognition unavailable.');
      if (!hit.object || !hit.bounds || hit.intent === 'none') { notify('这里没有足够清晰的物体，请指向瓶子或商品的中间。'); return; }
      const b = hit.bounds, r = input.region; marker.style.left = `${(r.x + b.x * r.width) * 100}%`; marker.style.top = `${(r.y + b.y * r.height) * 100}%`; marker.style.width = `${b.width * r.width * 100}%`; marker.style.height = `${b.height * r.height * 100}%`;
      marker.replaceChildren(Object.assign(document.createElement('span'), { textContent: hit.label })); marker.hidden = false; markerTimer = setTimeout(clear, 3500);
      notify(`识别到 ${hit.label}，正在调整当前互动…`);
      if (ready()) await onIntent(`I would like to ${hit.intent === 'select' ? 'pick up' : 'look at'} the ${hit.object.replaceAll('_', ' ')} I am pointing at.`);
    } catch (error) { if (run === getRun() && error.name !== 'AbortError') notify(error.message, true); }
    finally { pending = false; button.disabled = false; }
  });
  return { clear };
}
