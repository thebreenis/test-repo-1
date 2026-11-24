import assert from 'node:assert/strict';
import WebSocket from 'ws';

const base = process.env.HUB_URL || 'http://127.0.0.1:8800';
const admin = process.env.HUB_TEST_ADMIN || 'synthetic-local-admin-only';
const leaderboardPassword = process.env.HUB_TEST_LEADERBOARD || 'synthetic-local-leaderboard-only';
const socketUrl = base.replace(/^http/, 'ws') + '/connect';
const jwk = { kty: 'RSA', e: 'AQAB', n: 'A'.repeat(512) };
const localExplorer = /^http:\/\/(127\.0\.0\.1|localhost):/.test(base);
const labelA = 'synthetic A ' + Date.now().toString(36);
const labelC = 'synthetic C ' + Date.now().toString(36);

async function ownerInventory(label) {
  if (!localExplorer) return null;
  const root = base + '/cdn-cgi/local/explorer/api/workers/durable_objects/namespaces';
  const namespaces = await (await fetch(root)).json();
  const namespace = namespaces.result.find(row => row.class === 'Hub');
  assert.ok(namespace);
  const query = await fetch(root + '/' + encodeURIComponent(namespace.id) + '/query', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ durable_object_name: 'private-group', queries: [{
      sql: 'SELECT c.label,i.game_ids,i.licenses,i.first_recorded,i.last_changed FROM owner_inventory i JOIN codes c ON c.id=i.code_id WHERE c.label=?',
      params: [label]
    }] })
  });
  assert.equal(query.status, 200);
  const data = await query.json();
  assert.equal(data.success, true);
  assert.equal(data.result[0].rows.length, 1);
  return Object.fromEntries(data.result[0].columns.map((name, index) => [name, data.result[0].rows[0][index]]));
}

async function seedHistoricalContributor(label) {
  if (!localExplorer) return;
  const root = base + '/cdn-cgi/local/explorer/api/workers/durable_objects/namespaces';
  const namespaces = await (await fetch(root)).json();
  const namespace = namespaces.result.find(row => row.class === 'Hub');
  assert.ok(namespace);
  const query = await fetch(root + '/' + encodeURIComponent(namespace.id) + '/query', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ durable_object_name: 'private-group', queries: [{
      sql: "INSERT INTO owner_inventory (code_id,game_ids,licenses,first_recorded,last_changed) SELECT id,'[4]','AC Pirates','2026-11-21T01:00:00.000Z','2026-11-21T01:00:00.000Z' FROM codes WHERE label=?",
      params: [label]
    }] })
  });
  assert.equal(query.status, 200);
  assert.equal((await query.json()).success, true);
}

async function issue(label) {
  const response = await fetch(base + '/admin/issue', {
    method: 'POST', headers: { Authorization: 'Bearer ' + admin, 'Content-Type': 'application/json' },
    body: JSON.stringify({ label })
  });
  assert.equal(response.status, 200);
  const result = await response.json();
  assert.match(result.code, /^GFW8H-[A-Za-z0-9_-]{43}$/);
  return result.code;
}

class Client {
  constructor(code) {
    this.ws = new WebSocket(socketUrl, { headers: { Authorization: 'Bearer ' + code } });
    this.messages = [];
    this.waiters = [];
    this.ws.on('message', raw => {
      const message = JSON.parse(String(raw));
      const index = this.waiters.findIndex(item => item.predicate(message));
      if (index >= 0) this.waiters.splice(index, 1)[0].resolve(message);
      else this.messages.push(message);
    });
  }
  async open() {
    await new Promise((resolve, reject) => { this.ws.once('open', resolve); this.ws.once('error', reject); });
    this.welcome = await this.next(x => x.type === 'welcome');
    return this;
  }
  send(value) { this.ws.send(JSON.stringify(value)); }
  next(predicate) {
    const index = this.messages.findIndex(predicate);
    if (index >= 0) return Promise.resolve(this.messages.splice(index, 1)[0]);
    return new Promise((resolve, reject) => {
      const entry = { predicate, resolve: value => { clearTimeout(timer); resolve(value); } };
      const timer = setTimeout(() => { this.waiters = this.waiters.filter(item => item !== entry); reject(new Error('Timed out waiting for hub message')); }, 8000);
      this.waiters.push(entry);
    });
  }
  async close() { this.ws.close(); await new Promise(resolve => this.ws.once('close', resolve)); }
}

async function rejected(code) {
  await new Promise((resolve, reject) => {
    const ws = new WebSocket(socketUrl, { headers: { Authorization: 'Bearer ' + code } });
    ws.on('unexpected-response', (_, response) => { const status = response.statusCode; response.resume(); assert.equal(status, 401); resolve(); });
    ws.on('open', () => { ws.close(); reject(new Error('Unauthorized connection opened')); });
    ws.on('error', () => {});
  });
}

assert.equal((await fetch(base + '/')).status, 404);
assert.equal((await fetch(base + '/admin/issue', { method: 'POST', headers: { Authorization: 'Bearer wrong', 'Content-Type': 'application/json' }, body: '{}' })).status, 404);
assert.equal((await fetch(base + '/admin/status')).status, 404);
assert.equal((await fetch(base + '/admin/codes')).status, 404);
assert.equal((await fetch(base + '/admin/revoke', { method: 'POST', body: '{}' })).status, 404);
const loginPage = await fetch(base + '/leaderboard');
assert.equal(loginPage.status, 200);
assert.match(await loginPage.text(), /Enter your private leaderboard password/);
assert.equal((await fetch(base + '/leaderboard', { headers: { Cookie: 'gfw8_leaderboard=forged' } })).status, 200);
assert.equal((await fetch(base + '/leaderboard/login', {
  method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: 'password=wrong'
})).status, 401);
const login = await fetch(base + '/leaderboard/login', {
  method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  body: new URLSearchParams({ password: leaderboardPassword }), redirect: 'manual'
});
assert.equal(login.status, 303);
const leaderboardCookie = login.headers.get('set-cookie').split(';')[0];
assert.match(login.headers.get('set-cookie'), /HttpOnly; Secure; SameSite=Strict/);
async function leaderboardHtml() {
  const response = await fetch(base + '/leaderboard', { headers: { Cookie: leaderboardCookie } });
  assert.equal(response.status, 200);
  return response.text();
}
assert.match(await leaderboardHtml(), /Verified pops served/);
assert.match(await leaderboardHtml(), /All-time high · unique games available/);
const privateGames = await (await fetch(base + '/leaderboard/games', { headers: { Cookie: leaderboardCookie } })).text();
assert.equal((privateGames.match(/href="\/leaderboard\/games\/\d+"/g) || []).length, 108);
assert.match(privateGames, /AC Pirates/);
assert.match(await (await fetch(base + '/leaderboard/games')).text(), /Enter your private leaderboard password/);
const issuedOnlyLabel = 'synthetic unused ' + Date.now().toString(36);
await issue(issuedOnlyLabel);
assert.equal((await leaderboardHtml()).includes(issuedOnlyLabel), false);
if (localExplorer) {
  await seedHistoricalContributor(issuedOnlyLabel);
  const historical = await leaderboardHtml();
  assert.ok(historical.includes(`<strong>${issuedOnlyLabel}</strong></td><td>0</td><td>—</td>`));
  assert.match(historical, /21 Nov 2026/);
  const oldGame = await (await fetch(base + '/leaderboard/games/4', { headers: { Cookie: leaderboardCookie } })).text();
  assert.match(oldGame, /Last recorded with this game/);
  assert.ok(oldGame.includes(issuedOnlyLabel));
}
await rejected('GFW8H-' + 'A'.repeat(43));

const a = await new Client(await issue(labelA)).open();
const firstAvailability = await a.next(x => x.type === 'availability');
assert.equal(firstAvailability.usersActive, 1);
assert.equal(firstAvailability.peaks.users.count, 1);
assert.match(firstAvailability.peaks.users.day, /^\d{4}-\d{2}-\d{2}$/);
const b = await new Client(await issue('synthetic B')).open();
const secondAvailability = await b.next(x => x.type === 'availability');
assert.equal(secondAvailability.usersActive, 2);
assert.equal(secondAvailability.peaks.users.count, 2);
assert.equal((await a.next(x => x.type === 'availability' && x.usersActive === 2)).usersActive, 2);
a.send({ type: 'offer', gameIds: [4, 5], jwk });
const availability = await b.next(x => x.type === 'availability' && x.gameIds.includes(4));
assert.deepEqual(availability.gameIds, [4, 5]);
assert.equal(availability.peaks.games.count, 2);
assert.equal(availability.providerCounts['4'], 1);
assert.equal(availability.providerCounts['5'], 1);
assert.match(availability.peaks.games.day, /^\d{4}-\d{2}-\d{2}$/);
assert.match(availability.lastSeen['4'], /^\d{4}-\d{2}-\d{2}$/);
assert.match(availability.lastSeen['5'], /^\d{4}-\d{2}-\d{2}$/);
assert.equal(JSON.stringify(availability).includes('synthetic A'), false);
const codesForOffers = (await (await fetch(base + '/admin/codes', { headers: { Authorization: 'Bearer ' + admin } })).json()).codes;
const contributorId = codesForOffers.find(row => row.label === labelA).id;
const contributorUrl = '/leaderboard/offers/' + contributorId;
const privateContributor = await (await fetch(base + contributorUrl, { headers: { Cookie: leaderboardCookie } })).text();
assert.match(privateContributor, /Games offered now/);
assert.match(privateContributor, /AC Pirates/);
assert.match(privateContributor, /Adera/);
assert.match(await (await fetch(base + contributorUrl)).text(), /Enter your private leaderboard password/);
assert.equal((await (await fetch(base + contributorUrl)).text()).includes(labelA), false);
const privateGame = await (await fetch(base + '/leaderboard/games/4', { headers: { Cookie: leaderboardCookie } })).text();
assert.match(privateGame, /Offering now/);
assert.ok(privateGame.includes(labelA));
assert.equal((await (await fetch(base + '/leaderboard/games/4')).text()).includes(labelA), false);
const initialInventory = await ownerInventory(labelA);
if (initialInventory) {
  assert.equal(initialInventory.game_ids, '[4,5]');
  assert.equal(initialInventory.licenses, 'AC Pirates\nAdera');
  await new Promise(resolve => setTimeout(resolve, 30));
  a.messages = a.messages.filter(message => message.type !== 'availability');
  a.send({ type: 'offer', gameIds: [5, 4, 4], jwk });
  await a.next(x => x.type === 'availability' && x.gameIds.includes(4));
  const unchangedInventory = await ownerInventory(labelA);
  assert.equal(unchangedInventory.first_recorded, initialInventory.first_recorded);
  assert.equal(unchangedInventory.last_changed, initialInventory.last_changed);
}
const adminStatus = await (await fetch(base + '/admin/status', { headers: { Authorization: 'Bearer ' + admin } })).json();
assert.equal(adminStatus.connected, 2);
assert.deepEqual(adminStatus.gameIds, [4, 5]);
assert.equal(adminStatus.lastSeen['4'], availability.lastSeen['4']);
assert.equal(adminStatus.peaks.games.count, 2);
assert.equal(adminStatus.peaks.users.count, 2);
b.send({ type: 'offer', gameIds: [], jwk });
b.send({ type: 'get', gameId: 4 });
const pull = await a.next(x => x.type === 'pull' && x.gameId === 4);
assert.equal(pull.requesterJwk.n, jwk.n);
a.send({ type: 'result', requestId: pull.requestId, packet: 'SYNTHETIC-ENCRYPTED-RECEIPT' });
const result = await b.next(x => x.type === 'result' && x.requestId === pull.requestId);
assert.equal(result.packet, 'SYNTHETIC-ENCRYPTED-RECEIPT');
assert.equal(result.providerJwk.n, jwk.n);
b.send({ type: 'result', requestId: pull.requestId, packet: 'forged' });
assert.equal((await b.next(x => x.type === 'error')).type, 'error');
const c = await new Client(await issue(labelC)).open();
await c.next(x => x.type === 'availability' && x.usersActive === 3);
assert.equal((await (await fetch(base + '/admin/status', { headers: { Authorization: 'Bearer ' + admin } })).json()).peaks.users.count, 3);
c.send({ type: 'offer', gameIds: [5], jwk });
await c.next(x => x.type === 'availability' && x.usersActive === 3 && x.gameIds.includes(5));
function nextRoutedPull(gameId, choices = [[a, 'A'], [c, 'C']]) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => finish(null, new Error('No contributor received the request')), 8000);
    const handlers = choices.map(([client, name]) => {
      const listener = raw => {
        const message = JSON.parse(String(raw));
        if (message.type === 'pull' && message.gameId === gameId) finish({ client, name, message });
      };
      client.ws.on('message', listener);
      return [client, listener];
    });
    function finish(value, error) {
      clearTimeout(timer);
      for (const [client, listener] of handlers) client.ws.off('message', listener);
      if (error) reject(error); else resolve(value);
    }
  });
}
const routed = new Set();
for (let i = 0; i < 24; i++) {
  const provider = nextRoutedPull(5);
  b.send({ type: 'get', gameId: 5 });
  const selected = await provider;
  routed.add(selected.name);
  selected.client.send({ type: 'result', requestId: selected.message.requestId, packet: 'SYNTHETIC-' + i });
  assert.equal((await b.next(x => x.type === 'result' && x.requestId === selected.message.requestId)).packet, 'SYNTHETIC-' + i);
}
assert.deepEqual([...routed].sort(), ['A', 'C']);
const firstAttempt = nextRoutedPull(5);
b.send({ type: 'get', gameId: 5 });
const first = await firstAttempt;
const fallback = nextRoutedPull(5);
first.client.send({ type: 'failure', requestId: first.message.requestId });
assert.equal((await b.next(x => x.type === 'retrying' && x.requestId === first.message.requestId)).gameId, 5);
const second = await fallback;
assert.notEqual(second.name, first.name);
assert.equal(second.message.requestId, first.message.requestId);
second.client.send({ type: 'result', requestId: second.message.requestId, packet: 'FALLBACK-RECEIPT' });
assert.equal((await b.next(x => x.type === 'result' && x.requestId === first.message.requestId)).packet, 'FALLBACK-RECEIPT');
first.client.send({ type: 'result', requestId: first.message.requestId, packet: 'LATE-RESULT' });
assert.equal((await first.client.next(x => x.type === 'error')).type, 'error');
const validationAttempt = nextRoutedPull(5);
b.send({ type: 'get', gameId: 5, validateAck: true });
const validationFirst = await validationAttempt;
validationFirst.client.send({ type: 'result', requestId: validationFirst.message.requestId, packet: 'UNUSABLE-RECEIPT' });
assert.equal((await b.next(x => x.type === 'result' && x.requestId === validationFirst.message.requestId)).packet, 'UNUSABLE-RECEIPT');
const validationFallback = nextRoutedPull(5);
b.send({ type: 'reject', requestId: validationFirst.message.requestId });
await b.next(x => x.type === 'retrying' && x.requestId === validationFirst.message.requestId);
const validationSecond = await validationFallback;
assert.notEqual(validationSecond.name, validationFirst.name);
validationSecond.client.send({ type: 'result', requestId: validationSecond.message.requestId, packet: 'VALID-RECEIPT' });
assert.equal((await b.next(x => x.type === 'result' && x.requestId === validationFirst.message.requestId)).packet, 'VALID-RECEIPT');
b.send({ type: 'accept', requestId: validationFirst.message.requestId });
const countedLabel = validationSecond.name === 'A' ? labelA : labelC;
const rejectedLabel = validationFirst.name === 'A' ? labelA : labelC;
let counted = false;
for (let attempt = 0; attempt < 20; attempt++) {
  const html = await leaderboardHtml();
  if (html.includes(`<strong>${countedLabel}</strong></td><td>1</td><td>Adera <span class="sub">(1 pop)</span>`) &&
      html.includes(`<strong>${rejectedLabel}</strong></td><td>0</td><td>—</td>`)) { counted = true; break; }
  await new Promise(resolve => setTimeout(resolve, 50));
}
assert.equal(counted, true, 'Only the accepted provider should receive a served count and a most served game.');
const privateLogin = await (await fetch(base + '/leaderboard')).text();
assert.equal(privateLogin.includes(labelA), false);
assert.equal(privateLogin.includes(labelC), false);
// A sole eligible contributor still ends the request after failure.
b.send({ type: 'get', gameId: 4 });
const only = await a.next(x => x.type === 'pull' && x.gameId === 4);
a.send({ type: 'failure', requestId: only.requestId });
assert.equal((await b.next(x => x.type === 'failure' && x.requestId === only.requestId)).gameId, 4);
await c.close();
await b.next(x => x.type === 'availability' && x.usersActive === 2 && x.gameIds.includes(5));
if (initialInventory) {
  a.send({ type: 'offer', gameIds: [4], jwk });
  await b.next(x => x.type === 'availability' && x.gameIds.length === 1 && x.gameIds[0] === 4);
  const changedInventory = await ownerInventory(labelA);
  assert.equal(changedInventory.first_recorded, initialInventory.first_recorded);
  assert.ok(changedInventory.last_changed > initialInventory.last_changed);
  assert.equal(changedInventory.game_ids, '[4]');
  assert.equal(changedInventory.licenses, 'AC Pirates');
}
await a.close();
const remaining = await b.next(x => x.type === 'availability' && !x.gameIds.includes(4));
assert.deepEqual(remaining.gameIds, []);
assert.equal(remaining.usersActive, 1);
assert.equal(remaining.peaks.users.count, 3);
assert.equal(remaining.peaks.games.count, 2);
assert.equal(remaining.lastSeen['4'], availability.lastSeen['4']);
await b.close();
const d = await new Client(await issue('synthetic D')).open();
const e = await new Client(await issue('synthetic E')).open();
const f = await new Client(await issue('synthetic F')).open();
d.send({ type: 'offer', gameIds: [6], jwk });
await f.next(x => x.type === 'availability' && x.gameIds.includes(6));
e.send({ type: 'offer', gameIds: [6], jwk });
assert.equal((await f.next(x => x.type === 'availability' && x.providerCounts['6'] === 2)).providerCounts['6'], 2);
f.send({ type: 'offer', gameIds: [], jwk });
const disconnectAttempt = nextRoutedPull(6, [[d, 'D'], [e, 'E']]);
f.send({ type: 'get', gameId: 6 });
const disconnected = await disconnectAttempt;
const disconnectFallback = nextRoutedPull(6, [[d, 'D'], [e, 'E']]);
await disconnected.client.close();
await f.next(x => x.type === 'retrying' && x.requestId === disconnected.message.requestId);
const survivor = await disconnectFallback;
assert.notEqual(survivor.name, disconnected.name);
survivor.client.send({ type: 'result', requestId: survivor.message.requestId, packet: 'AFTER-DISCONNECT' });
assert.equal((await f.next(x => x.type === 'result' && x.requestId === disconnected.message.requestId)).packet, 'AFTER-DISCONNECT');
await survivor.client.close();
await f.close();
const revokeLabel = 'synthetic revoke ' + Date.now().toString(36);
const revokeCode = await issue(revokeLabel);
const revokeClient = await new Client(revokeCode).open();
await revokeClient.next(x => x.type === 'availability');
revokeClient.send({ type: 'offer', gameIds: [7], jwk });
await revokeClient.next(x => x.type === 'availability' && x.providerCounts['7'] === 1);
assert.ok((await leaderboardHtml()).includes(revokeLabel));
const codesResponse = await fetch(base + '/admin/codes', { headers: { Authorization: 'Bearer ' + admin } });
assert.equal(codesResponse.status, 200);
const codes = (await codesResponse.json()).codes;
const target = codes.find(row => row.label === revokeLabel);
assert.equal(target.connected, true);
const beforeRevoke = await (await fetch(base + '/admin/status', { headers: { Authorization: 'Bearer ' + admin } })).json();
const closed = new Promise(resolve => revokeClient.ws.once('close', resolve));
const revoked = await fetch(base + '/admin/revoke', { method: 'POST',
  headers: { Authorization: 'Bearer ' + admin, 'Content-Type': 'application/json' },
  body: JSON.stringify({ ids: [target.id] }) });
assert.equal(revoked.status, 200);
assert.equal((await revoked.json()).revoked, 1);
await closed;
await rejected(revokeCode);
assert.equal((await leaderboardHtml()).includes(revokeLabel), false);
const afterRevoke = await (await fetch(base + '/admin/status', { headers: { Authorization: 'Bearer ' + admin } })).json();
assert.equal(afterRevoke.gameIds.includes(7), false);
assert.deepEqual(afterRevoke.peaks, beforeRevoke.peaks);
const codesAfter = (await (await fetch(base + '/admin/codes', { headers: { Authorization: 'Bearer ' + admin } })).json()).codes;
assert.equal(codesAfter.some(row => row.id === target.id), false);
const batchA = await issue('synthetic batch A');
const batchB = await issue('synthetic batch B');
const batchClientA = await new Client(batchA).open();
const batchClientB = await new Client(batchB).open();
await batchClientA.next(x => x.type === 'availability');
await batchClientB.next(x => x.type === 'availability');
batchClientA.send({ type: 'offer', gameIds: [8], jwk });
await batchClientB.next(x => x.type === 'availability' && x.providerCounts['8'] === 1);
batchClientB.send({ type: 'offer', gameIds: [8], jwk });
await batchClientA.next(x => x.type === 'availability' && x.providerCounts['8'] === 2);
const batchRows = (await (await fetch(base + '/admin/codes', { headers: { Authorization: 'Bearer ' + admin } })).json()).codes;
const batchIds = batchRows.filter(row => row.label === 'synthetic batch A' || row.label === 'synthetic batch B').map(row => row.id);
assert.equal(batchIds.length, 2);
const batchClosed = Promise.all([batchClientA, batchClientB].map(client => new Promise(resolve => client.ws.once('close', resolve))));
const batchResult = await fetch(base + '/admin/revoke', { method: 'POST',
  headers: { Authorization: 'Bearer ' + admin, 'Content-Type': 'application/json' },
  body: JSON.stringify({ ids: batchIds }) });
assert.equal((await batchResult.json()).revoked, 2);
await batchClosed;
assert.equal((await (await fetch(base + '/admin/status', { headers: { Authorization: 'Bearer ' + admin } })).json()).gameIds.includes(8), false);
await rejected(batchA);
await rejected(batchB);
const onboardingCode = await issue('synthetic onboarding');
const onboarding = await new Client(onboardingCode).open();
assert.equal(onboarding.welcome.needsInitialScan, true);
onboarding.send({ type: 'initialScanComplete' });
assert.equal((await onboarding.next(x => x.type === 'initialScanAcknowledged')).type, 'initialScanAcknowledged');
await onboarding.close();
const returning = await new Client(onboardingCode).open();
assert.equal(returning.welcome.needsInitialScan, false);
await returning.close();
console.log('Hub authentication, availability, provider counts, peaks, failover, revocation, and first-connection scan checks passed.');
