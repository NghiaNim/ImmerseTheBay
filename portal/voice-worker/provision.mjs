import { readFile, writeFile, chmod } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { MODEL_ID, TOOLS, agentConfig } from './config.mjs';

const envPath = fileURLToPath(new URL('../.env', import.meta.url));
const apiKey = process.env.CARTESIA_API_KEY;
if (!apiKey) throw new Error('Load portal/.env with node --env-file=portal/.env');
async function api(path, body) {
  const response = await fetch(`https://api.cartesia.ai/v1/agents${path}`, {
    method: body ? 'POST' : 'GET',
    headers: { 'X-API-Key': apiKey, 'Cartesia-Version': '2026-08-14', 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!response.ok) throw new Error(`Cartesia ${path || '/'} failed with HTTP ${response.status}`);
  return response.json();
}
const models = await api('/models?limit=100');
if (!models.data.some(model => model.id === MODEL_ID)) throw new Error(`Approved model ${MODEL_ID} is unavailable; no substitution`);
if (!process.argv.includes('--create')) {
  console.log(JSON.stringify({ model: MODEL_ID, tools: TOOLS, config: agentConfig(process.env.CARTESIA_VOICE_ID, TOOLS.map(t => `<${t.name}_id>`)) }, null, 2));
} else {
  // Save non-secret resource IDs after each mutation so partial provisioning is reviewable.
  const receiptPath = fileURLToPath(new URL('./provisioned.json', import.meta.url));
  let receipt;
  try { receipt = JSON.parse(await readFile(receiptPath, 'utf8')); } catch (error) { if (error.code !== 'ENOENT') throw error; receipt = { tools: {} }; }
  if (receipt.agentId) throw new Error('Dedicated agent already provisioned; inspect provisioned.json before creating another');
  for (const tool of TOOLS) {
    if (!receipt.tools[tool.name]) {
      const created = await api('/tools', tool);
      receipt.tools[tool.name] = created.id;
      await writeFile(receiptPath, JSON.stringify(receipt, null, 2) + '\n');
    }
  }
  const created = await api('', { name: 'ImmerseTheBay Convenience Store', config: agentConfig(process.env.CARTESIA_VOICE_ID, TOOLS.map(t => receipt.tools[t.name])) });
  receipt.agentId = created.id;
  receipt.model = MODEL_ID;
  await writeFile(receiptPath, JSON.stringify(receipt, null, 2) + '\n');
  const old = await readFile(envPath, 'utf8');
  const setting = `CARTESIA_AGENT_ID=${created.id}`;
  await writeFile(envPath, /^CARTESIA_AGENT_ID=.*$/m.test(old) ? old.replace(/^CARTESIA_AGENT_ID=.*$/m, setting) : `${old.trimEnd()}\n${setting}\n`, { mode: 0o600 });
  await chmod(envPath, 0o600);
  console.log(JSON.stringify({ agentId: created.id, model: MODEL_ID, toolCount: TOOLS.length }));
}
