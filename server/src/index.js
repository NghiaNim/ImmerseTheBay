import { createRealtimeServer } from './server.js';

const port = Number(process.env.PORT ?? 8787);
if (!Number.isInteger(port) || port < 0 || port > 65535) {
  throw new Error('PORT must be an integer from 0 to 65535');
}
const server = createRealtimeServer({ port });
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.once(signal, () => server.close());
}
