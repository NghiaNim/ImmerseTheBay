import { snapshotReplaySource } from './compile.js';
import { LOCAL_USER } from './store.js';
const PHOTO_REF = /^photo:(ph_[a-f0-9]{16})$/;
const SAMPLE_REF = /^sample:([a-zA-Z0-9][a-zA-Z0-9_-]*\.(?:jpg|jpeg|png|webp))$/i;

/** Hook for agent/service.js: persists each committed turn of a lesson run. */
export function createSessionRecorder(store, { userId = LOCAL_USER } = {}) {
  return async function record({ runId, sessionId, lesson, imageReference = '', request, intent, practiced, practicedSegment, practicedTargetId, presented, presentedTargetId, response, plan }) {
    await store.ensureRecord({
      id: runId, userId, lessonId: lesson.id, lessonTitle: lesson.title, sessionId,
      mode: sessionId.startsWith('practice-') ? 'practice' : 'live',
      photoId: PHOTO_REF.exec(imageReference)?.[1] || null,
      sampleImage: SAMPLE_REF.exec(imageReference)?.[1] || null,
      sourceRecordId: lesson.replay?.sourceRecordId || null,
      partnerRole: lesson.partnerRole || null,
    }, () => ({ replaySource: snapshotReplaySource(lesson) }));
    if (plan) await store.appendEvent(runId, { type: 'detour_plan', label: plan.label, returnBeat: plan.returnBeat, nodes: plan.nodes, source: plan.source });
    await store.appendEvent(runId, {
      type: 'turn',
      transcript: request.transcript || '', intent: intent || null, action: request.action || null, gesture: request.context?.gesture || null,
      practiced, practicedSegment, practicedTargetId, score: response.attempt?.score ?? null,
      ...(response.attempt ? { attemptId: response.attempt.id, outcome: response.attempt.outcome } : {}),
      advance: response.advance, route: response.route, presented, presentedTargetId,
      clerk: response.speech?.text || null,
      prompt: response.visko.find((command) => command.name === 'set_prompt')?.data.prompt || null,
    });
    if (response.advance === 'scene_done') await store.updateRecord(runId, { status: 'complete', endedAt: new Date().toISOString() });
  };
}
