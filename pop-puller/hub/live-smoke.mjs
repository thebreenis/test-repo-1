// Optional live check. Use only with an owner's issued code and an available owned title.
// Never prints or saves a receipt, token, or access code.
import assert from 'node:assert/strict';
import { createDecipheriv, createHash, createHmac, createPublicKey, generateKeyPairSync, privateDecrypt, verify, constants } from 'node:crypto';
import WebSocket from 'ws';

const code = process.env.HUB_TEST_CODE;
const gameId = Number(process.env.HUB_TEST_GAME || 5);
const url = process.env.HUB_URL || 'wss://gfw8-pop-puller-hub.alec2dabreen.workers.dev/connect';
if (!/^GFW8H-[A-Za-z0-9_-]{43}$/.test(code || '') || !Number.isInteger(gameId)) throw new Error('Private code and game ID required.');
const keys = generateKeyPairSync('rsa', { modulusLength: 3072 });
const jwk = keys.publicKey.export({ format: 'jwk' });
const id = key => createHash('sha256').update(key.n + '.' + key.e).digest('hex');
const hmac = (key, message) => createHmac('sha256', key).update(message).digest();
const ws = new WebSocket(url, { headers: { Authorization: 'Bearer ' + code } });
const messages = [];
const waits = [];
ws.on('message', raw => {
  const message = JSON.parse(String(raw));
  const index = waits.findIndex(item => item.predicate(message));
  if (index >= 0) waits.splice(index, 1)[0].resolve(message);
  else messages.push(message);
});
function next(predicate, timeout = 150000) {
  const index = messages.findIndex(predicate);
  if (index >= 0) return Promise.resolve(messages.splice(index, 1)[0]);
  return new Promise((resolve, reject) => {
    const wait = { predicate, resolve: value => { clearTimeout(timer); resolve(value); } };
    const timer = setTimeout(() => { waits.splice(waits.indexOf(wait), 1); reject(new Error('Hub request timed out.')); }, timeout);
    waits.push(wait);
  });
}
try {
  await new Promise((resolve, reject) => { ws.once('open', resolve); ws.once('error', reject); });
  await next(x => x.type === 'welcome', 8000);
  ws.send(JSON.stringify({ type: 'offer', jwk, gameIds: [] }));
  const status = await next(x => x.type === 'availability' && x.gameIds.includes(gameId), 10000);
  assert(status.gameIds.includes(gameId));
  ws.send(JSON.stringify({ type: 'get', gameId }));
  const outcome = await next(x => ['result', 'failure', 'unavailable'].includes(x.type));
  if (outcome.type !== 'result') throw new Error('Contributor did not return a receipt: ' + outcome.type);
  assert.equal(outcome.gameId, gameId);
  const packet = JSON.parse(outcome.packet);
  assert.equal(packet.pairId, outcome.requestId);
  assert.equal(packet.to, id(jwk));
  assert.equal(packet.from, id(outcome.providerJwk));
  const canon = [packet.from, packet.to, packet.pairId, packet.id, packet.time, packet.key, packet.iv, packet.data, packet.mac].join('|');
  assert(verify('sha256', Buffer.from(canon), { key: createPublicKey({ key: outcome.providerJwk, format: 'jwk' }), padding: constants.RSA_PKCS1_PSS_PADDING, saltLength: 32 }, Buffer.from(packet.signature, 'base64')));
  const secret = privateDecrypt({ key: keys.privateKey, oaepHash: 'sha256' }, Buffer.from(packet.key, 'base64'));
  const auth = hmac(secret, 'GFW8 sharing v1 authentication');
  const expected = hmac(auth, packet.iv + '.' + packet.data);
  assert(expected.equals(Buffer.from(packet.mac, 'base64')));
  const aes = hmac(secret, 'GFW8 sharing v1 encryption');
  const decryptor = createDecipheriv('aes-256-cbc', aes, Buffer.from(packet.iv, 'base64'));
  const clear = Buffer.concat([decryptor.update(Buffer.from(packet.data, 'base64')), decryptor.final()]);
  const body = JSON.parse(clear.toString('utf8'));
  assert.equal(body.gameId, gameId);
  assert(Buffer.from(body.receipt, 'base64').length > 500);
  console.log('Live hub request, contributor encryption, signature, and receipt transfer verified.');
} finally { ws.close(); }
