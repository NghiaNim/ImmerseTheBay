import { createVisionClient } from './openai.js';
import { gateDetection, validateRequest, VisionError } from './schema.js';
export { createVisionPrompt } from './schema.js';

export function createVisionService({ client = createVisionClient(), now = Date.now, minIntervalMs = 2000, maxConcurrent = 2 } = {}) {
  let active = 0;
  const recent = new Map(); // Timestamp-only throttle; never retains frames.
  return {
    async point(body, { caller = 'local', sceneHint } = {}) {
      const request = validateRequest(body);
      if (!client) throw new VisionError(503, 'OPENAI_API_KEY is not configured for vision');
      const time = now();
      for (const [key, at] of recent) if (time - at >= minIntervalMs) recent.delete(key);
      if (recent.has(caller) || active >= maxConcurrent || recent.size >= 256) throw new VisionError(429, 'Wait before pointing at another video object');
      recent.set(caller, time);
      active++;
      try { return gateDetection(await client(sceneHint ? { ...request, sceneHint } : request), request); }
      finally { active--; }
    },
  };
}
