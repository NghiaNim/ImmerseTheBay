// Review saved lesson runs and start a replay of the exact lines (records/ on the server).
const $ = (id) => document.getElementById(id);
const token = new URLSearchParams(location.search).get('portal_token') || '';
const PASS = 60; // matches PASS_SCORE in portal/agent/guardrails.js
let current = null, audio = null;

const withToken = (url) => token ? `${url}${url.includes('?') ? '&' : '?'}portal_token=${encodeURIComponent(token)}` : url;
async function api(path, { method = 'GET', body } = {}) {
  const response = await fetch(path, { method, headers: { ...(token ? { portal_token: token } : {}), ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || `Request failed (${response.status})`);
  return data;
}
function el(tag, props = {}, ...children) {
  const node = Object.assign(document.createElement(tag), props);
  node.append(...children.filter((c) => c !== null && c !== undefined && c !== false));
  return node;
}
const when = (iso) => new Date(iso).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
const modeLabel = (record) => `${record.sourceRecordId ? 'Replay · ' : ''}${record.mode === 'practice' ? 'Practice' : 'Live video'}`;
const statusLabel = (record) => record.status === 'complete' ? 'Completed' : 'Not finished';
const thumbUrl = (record) => record.photoId ? withToken(`/api/photos/${record.photoId}/thumb`)
  : record.sampleImage ? withToken(`/lessons/images/${encodeURIComponent(record.sampleImage)}`) : null;

async function loadSessions(selectId) {
  try {
    const { sessions } = await api('/api/sessions');
    $('sessionCount').textContent = String(sessions.length);
    $('listNotice').textContent = sessions.length ? '' : 'No sessions yet. Start a lesson; every run is saved here automatically.';
    $('listNotice').hidden = sessions.length > 0;
    $('sessions').replaceChildren(...sessions.map((record) => {
      const thumb = thumbUrl(record);
      const button = el('button', { type: 'button', className: 'session-card' },
        thumb ? el('img', { src: thumb, alt: '', loading: 'lazy' }) : el('span', { className: 'thumb-placeholder', lang: 'ja', textContent: '練' }),
        el('span', { className: 'card-text' },
          el('strong', { textContent: record.lessonTitle }),
          el('span', { textContent: `${when(record.startedAt)} · ${modeLabel(record)}` }),
          el('span', { className: 'card-stats', textContent: `${record.stats.passed}/${record.stats.lines} lines passed${record.stats.detours ? ` · ${record.stats.detours} side trip${record.stats.detours > 1 ? 's' : ''}` : ''} · ${statusLabel(record)}` })));
      button.dataset.id = record.id;
      button.onclick = () => void openSession(record.id);
      return el('li', {}, button);
    }));
    if (selectId) void openSession(selectId);
  } catch (error) { $('listNotice').hidden = false; $('listNotice').textContent = `Could not load sessions: ${error.message}`; $('listNotice').classList.add('error'); }
}

function lineItem(entry) {
  const best = entry.attempts.length ? Math.max(...entry.attempts.map((a) => a.score)) : null;
  const state = entry.passed ? 'passed' : entry.attempts.length ? 'missed' : entry.skipped ? 'skipped' : 'unpracticed';
  const label = { passed: `Passed · ${best}`, missed: `Best ${best}`, skipped: 'Skipped', unpracticed: 'Not practiced' }[state];
  const listen = el('button', { type: 'button', className: 'listen', textContent: 'Listen' });
  listen.onclick = () => void speak(entry.line.jp, listen);
  return el('li', { className: `line ${state}` },
    el('div', { className: 'line-head' }, el('span', { className: `state ${state}`, textContent: label }), listen),
    entry.line.partner ? el('p', { className: 'partner', lang: 'ja', textContent: `${current?.record.partnerRole?.ja || '相手'} · ${entry.line.partner.jp}` }) : null,
    entry.line.partner ? el('p', { className: 'translation', textContent: entry.line.partner.en }) : null,
    el('p', { className: 'jp', lang: 'ja', textContent: entry.line.jp }),
    el('p', { className: 'romaji', lang: 'ja-Latn', textContent: entry.line.romaji }),
    el('p', { className: 'kana', lang: 'ja', textContent: entry.line.kana }),
    el('p', { className: 'translation', textContent: entry.line.en }),
    entry.line.alternatives?.length ? el('p', { className: 'alt', lang: 'ja', textContent: `Also fine · ${entry.line.alternatives.map((a) => a.jp).join('　/　')}` }) : null,
    entry.attempts.length ? el('ul', { className: 'attempts' }, ...entry.attempts.map((a) =>
      el('li', { className: a.score >= PASS ? 'ok' : '' }, el('span', { className: 'score', textContent: String(a.score) }), el('span', { lang: 'ja', textContent: a.transcript })))) : null);
}

async function openSession(id) {
  for (const card of document.querySelectorAll('.session-card')) card.setAttribute('aria-current', String(card.dataset.id === id));
  $('detailNotice').textContent = ''; $('detailNotice').classList.remove('error'); hideConfirm();
  try {
    const data = await api(`/api/sessions/${encodeURIComponent(id)}`);
    current = data;
    const { record, lines, stats } = data;
    $('emptyDetail').hidden = true; $('detailBody').hidden = false;
    const photo = record.photoId ? withToken(`/api/photos/${record.photoId}/image`) : record.sampleImage ? withToken(`/lessons/images/${encodeURIComponent(record.sampleImage)}`) : '';
    $('detailPhoto').hidden = !photo; if (photo) $('detailPhoto').src = photo;
    $('detailMeta').textContent = `${when(record.startedAt)} · ${modeLabel(record)} · ${statusLabel(record)}`;
    $('detailTitle').textContent = record.lessonTitle;
    $('detailStats').textContent = `${stats.passed} of ${stats.lines} lines passed · ${stats.attempted} practiced${stats.bestAverage !== null ? ` · best scores average ${stats.bestAverage}` : ''}${stats.detours ? ` · ${stats.detours} side trip${stats.detours > 1 ? 's' : ''}` : ''}`;
    $('replayMissed').disabled = !lines.some((l) => !l.passed); $('replayAll').disabled = !lines.length;
    const groups = [];
    for (const entry of lines) {
      const key = entry.segment.kind === 'detour' ? `detour:${entry.segment.label}` : 'main';
      if (groups.at(-1)?.key !== key) groups.push({ key, segment: entry.segment, entries: [] });
      groups.at(-1).entries.push(entry);
    }
    $('lines').replaceChildren(...groups.map((group) => el('li', { className: `segment ${group.segment.kind}` },
      el('p', { className: 'segment-label', textContent: group.segment.kind === 'detour' ? `Side trip · ${group.segment.label}` : 'Main lesson' }),
      el('ol', {}, ...group.entries.map(lineItem)))));
    if (!lines.length) $('detailNotice').textContent = 'This session ended before any lines were shown.';
    history.replaceState(null, '', withToken(`/sessions.html?session=${encodeURIComponent(id)}`));
  } catch (error) { $('detailNotice').textContent = `Could not open this session: ${error.message}`; $('detailNotice').classList.add('error'); }
}

async function replay(deck) {
  if (!current) return;
  $('replayAll').disabled = $('replayMissed').disabled = true;
  $('detailNotice').classList.remove('error'); $('detailNotice').textContent = 'Preparing your practice…';
  try {
    const { lessonId } = await api(`/api/sessions/${encodeURIComponent(current.record.id)}/replay`, { method: 'POST', body: { deck } });
    location.href = withToken(`/learn.html?lesson=${encodeURIComponent(lessonId)}${$('replayLive').checked ? '' : '&practice=1'}`);
  } catch (error) {
    $('detailNotice').textContent = error.message; $('detailNotice').classList.add('error');
    $('replayAll').disabled = false; $('replayMissed').disabled = !current.lines.some((l) => !l.passed);
  }
}

async function speak(text, button) {
  audio?.pause();
  button.disabled = true; button.textContent = '…';
  try {
    const response = await fetch('/api/agent/speak', { method: 'POST', headers: { ...(token ? { portal_token: token } : {}), 'Content-Type': 'application/json' }, body: JSON.stringify({ text, language: 'ja' }) });
    if (!response.ok) throw new Error(response.status === 503 ? 'Voice is not configured' : 'Voice unavailable');
    const url = URL.createObjectURL(await response.blob());
    audio = new Audio(url); audio.onended = () => URL.revokeObjectURL(url);
    await audio.play();
  } catch (error) { $('detailNotice').textContent = error.message; $('detailNotice').classList.add('error'); }
  finally { button.disabled = false; button.textContent = 'Listen'; }
}

function hideConfirm() { $('confirmDelete').hidden = true; $('deleteSession').hidden = false; }
async function deleteSession() {
  try {
    await api(`/api/sessions/${encodeURIComponent(current.record.id)}`, { method: 'DELETE' });
    current = null; $('detailBody').hidden = true; $('emptyDetail').hidden = false;
    history.replaceState(null, '', withToken('/sessions.html'));
    await loadSessions();
  } catch (error) { $('detailNotice').textContent = `Could not delete: ${error.message}`; $('detailNotice').classList.add('error'); hideConfirm(); }
}

async function loadPhotos() {
  try {
    const { photos } = await api('/api/photos');
    $('photoCount').textContent = String(photos.length);
    $('photoNotice').hidden = photos.length > 0;
    $('photos').replaceChildren(...photos.map((photo) => {
      const remove = el('button', { type: 'button', className: 'photo-delete', textContent: 'Delete' });
      remove.onclick = async () => {
        if (remove.dataset.armed !== 'true') { remove.dataset.armed = 'true'; remove.textContent = 'Confirm delete'; return; }
        try { await api(`/api/photos/${photo.id}`, { method: 'DELETE' }); await loadPhotos(); }
        catch (error) { remove.textContent = 'Delete failed'; }
      };
      return el('li', {}, el('img', { src: withToken(`/api/photos/${photo.id}/thumb`), alt: `Photo from ${when(photo.createdAt)}`, loading: 'lazy' }),
        el('span', { textContent: when(photo.createdAt) }), remove);
    }));
  } catch (error) { $('photoNotice').hidden = false; $('photoNotice').textContent = `Could not load photos: ${error.message}`; }
}

if (token) { $('homeLink').href = withToken('/learn.html'); $('lessonLink').href = withToken('/learn.html'); }
$('replayAll').onclick = () => void replay('all');
$('replayMissed').onclick = () => void replay('missed');
$('deleteSession').onclick = () => { $('deleteSession').hidden = true; $('confirmDelete').hidden = false; };
$('confirmDeleteNo').onclick = hideConfirm;
$('confirmDeleteYes').onclick = () => void deleteSession();
void loadSessions(new URLSearchParams(location.search).get('session'));
void loadPhotos();
