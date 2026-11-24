import { DurableObject } from 'cloudflare:workers';
import catalogue from '../../../backend/games.json';
import { clearSessionCookie, contributorOffersPage, gameOffersPage, gamesPage, htmlResponse, leaderboardPage, loginPage, makeSession, readPassword, sessionCookie, validSession } from './leaderboard.js';

const encoder = new TextEncoder();
const MAX_MESSAGE = 150000;
const OFFER_TTL = 180000;
const REQUEST_TTL = 180000;
const ATTEMPT_TTL = 45000;
const ACK_TTL = 60000;

const reply = (value, status = 200) => Response.json(value, {
  status,
  headers: { 'Cache-Control': 'no-store' }
});
const bearer = request => /^Bearer (\S+)$/.exec(request.headers.get('Authorization') || '')?.[1] || '';
const hex = bytes => [...new Uint8Array(bytes)].map(x => x.toString(16).padStart(2, '0')).join('');
const digest = async value => hex(await crypto.subtle.digest('SHA-256', encoder.encode(value)));
const codeDigest = code => digest('GFW8 Pop Puller code v1|' + code);
const validCode = code => /^GFW8H-[A-Za-z0-9_-]{43}$/.test(code);
const validGame = id => Number.isInteger(id) && id >= 1 && id <= 108;
const gameNames = new Map(catalogue.games.map(game => [game.index, game.name]));
const validJwk = jwk => jwk && jwk.kty === 'RSA' && jwk.e === 'AQAB' &&
  typeof jwk.n === 'string' && /^[A-Za-z0-9_-]{342,684}$/.test(jwk.n) && !jwk.d;

async function sameSecret(left, right) {
  if (!left || !right) return false;
  const [a, b] = await Promise.all([digest(left), digest(right)]);
  let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return difference === 0;
}

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname;
    if (path === '/leaderboard' && request.method === 'GET') {
      if (!env.LEADERBOARD_KEY) return reply({ error: 'Leaderboard unavailable.' }, 503);
      if (!await validSession(request, env.LEADERBOARD_KEY)) return htmlResponse(loginPage());
      return htmlResponse(leaderboardPage(await env.HUB.getByName('private-group').leaderboard()));
    }
    const offersPath = /^\/leaderboard\/offers\/([0-9a-f-]{36})$/.exec(path);
    if (offersPath && request.method === 'GET') {
      if (!env.LEADERBOARD_KEY) return reply({ error: 'Leaderboard unavailable.' }, 503);
      if (!await validSession(request, env.LEADERBOARD_KEY)) return htmlResponse(loginPage());
      const data = await env.HUB.getByName('private-group').leaderboard();
      const contributor = data.rows.find(row => row.id === offersPath[1]);
      return contributor ? htmlResponse(contributorOffersPage(contributor)) : reply({ error: 'Not found.' }, 404);
    }
    if (path === '/leaderboard/games' && request.method === 'GET') {
      if (!env.LEADERBOARD_KEY) return reply({ error: 'Leaderboard unavailable.' }, 503);
      if (!await validSession(request, env.LEADERBOARD_KEY)) return htmlResponse(loginPage());
      return htmlResponse(gamesPage(await env.HUB.getByName('private-group').leaderboard()));
    }
    const gamePath = /^\/leaderboard\/games\/([0-9]{1,3})$/.exec(path);
    if (gamePath && request.method === 'GET') {
      if (!env.LEADERBOARD_KEY) return reply({ error: 'Leaderboard unavailable.' }, 503);
      if (!await validSession(request, env.LEADERBOARD_KEY)) return htmlResponse(loginPage());
      const gameId = Number(gamePath[1]);
      if (!validGame(gameId)) return reply({ error: 'Not found.' }, 404);
      return htmlResponse(gameOffersPage(await env.HUB.getByName('private-group').leaderboard(), gameId));
    }
    if (path === '/leaderboard/login' && request.method === 'POST') {
      if (!env.LEADERBOARD_KEY) return reply({ error: 'Leaderboard unavailable.' }, 503);
      const password = await readPassword(request);
      if (!await sameSecret(password, env.LEADERBOARD_KEY)) return htmlResponse(loginPage(true), 401);
      return new Response(null, { status: 303, headers: {
        Location: '/leaderboard',
        'Set-Cookie': sessionCookie(await makeSession(env.LEADERBOARD_KEY)),
        'Cache-Control': 'no-store'
      } });
    }
    if (path === '/leaderboard/logout' && request.method === 'POST') {
      return new Response(null, { status: 303, headers: {
        Location: '/leaderboard', 'Set-Cookie': clearSessionCookie, 'Cache-Control': 'no-store'
      } });
    }
    if (path === '/admin/status' && request.method === 'GET') {
      if (!await sameSecret(bearer(request), env.ADMIN_KEY)) return reply({ error: 'Not found.' }, 404);
      return reply(await env.HUB.getByName('private-group').status());
    }
    if (path === '/admin/issue' && request.method === 'POST') {
      if (!await sameSecret(bearer(request), env.ADMIN_KEY)) return reply({ error: 'Not found.' }, 404);
      let label = '';
      try {
        if (Number(request.headers.get('content-length') || 0) > 1024) throw new Error();
        const body = await request.json();
        label = typeof body.label === 'string' ? body.label.trim().slice(0, 60) : '';
      } catch { return reply({ error: 'Invalid request.' }, 400); }
      const code = await env.HUB.getByName('private-group').issueCode(label);
      return reply({ code });
    }
    if (path === '/admin/codes' && request.method === 'GET') {
      if (!await sameSecret(bearer(request), env.ADMIN_KEY)) return reply({ error: 'Not found.' }, 404);
      return reply({ codes: await env.HUB.getByName('private-group').listCodes() });
    }
    if (path === '/admin/revoke' && request.method === 'POST') {
      if (!await sameSecret(bearer(request), env.ADMIN_KEY)) return reply({ error: 'Not found.' }, 404);
      let ids;
      try {
        if (Number(request.headers.get('content-length') || 0) > 4096) throw new Error();
        const body = await request.json();
        ids = body.ids;
        if (!Array.isArray(ids) || ids.length < 1 || ids.length > 100 ||
          !ids.every(id => typeof id === 'string' && /^[0-9a-f-]{36}$/.test(id)) ||
          new Set(ids).size !== ids.length) throw new Error();
      } catch { return reply({ error: 'Invalid request.' }, 400); }
      return reply(await env.HUB.getByName('private-group').revokeCodes(ids));
    }
    if (path === '/connect' && request.headers.get('Upgrade')?.toLowerCase() === 'websocket') {
      return env.HUB.getByName('private-group').fetch(request);
    }
    return reply({ error: 'Not found.' }, 404);
  }
};

export class Hub extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    ctx.storage.sql.exec('CREATE TABLE IF NOT EXISTS codes (id TEXT PRIMARY KEY, digest TEXT NOT NULL UNIQUE, label TEXT NOT NULL, created INTEGER NOT NULL)');
    ctx.storage.sql.exec('CREATE TABLE IF NOT EXISTS pending (id TEXT PRIMARY KEY, requester TEXT NOT NULL, provider TEXT NOT NULL, game INTEGER NOT NULL, expires INTEGER NOT NULL)');
    ctx.storage.sql.exec('CREATE TABLE IF NOT EXISTS pending_retry (id TEXT PRIMARY KEY, tried TEXT NOT NULL, deadline INTEGER NOT NULL)');
    ctx.storage.sql.exec('CREATE TABLE IF NOT EXISTS game_seen (game INTEGER PRIMARY KEY, day TEXT NOT NULL)');
    ctx.storage.sql.exec('CREATE TABLE IF NOT EXISTS hub_peaks (kind TEXT PRIMARY KEY, count INTEGER NOT NULL, day TEXT NOT NULL)');
    ctx.storage.sql.exec('CREATE TABLE IF NOT EXISTS owner_inventory (code_id TEXT PRIMARY KEY, game_ids TEXT NOT NULL, licenses TEXT NOT NULL, first_recorded TEXT NOT NULL, last_changed TEXT NOT NULL)');
    ctx.storage.sql.exec('CREATE TABLE IF NOT EXISTS leaderboard_stats (code_id TEXT PRIMARY KEY, served INTEGER NOT NULL DEFAULT 0, connected_ms INTEGER NOT NULL DEFAULT 0)');
    ctx.storage.sql.exec('CREATE TABLE IF NOT EXISTS leaderboard_game_serves (code_id TEXT NOT NULL, game INTEGER NOT NULL, served INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (code_id, game))');
    ctx.storage.sql.exec('CREATE TABLE IF NOT EXISTS leaderboard_sessions (session TEXT PRIMARY KEY, code_id TEXT NOT NULL, started INTEGER NOT NULL, heartbeat INTEGER NOT NULL)');
    if (!ctx.storage.sql.exec('PRAGMA table_info(codes)').toArray().some(row => row.name === 'initial_scan_done')) {
      ctx.storage.sql.exec('ALTER TABLE codes ADD COLUMN initial_scan_done INTEGER NOT NULL DEFAULT 0');
      ctx.storage.sql.exec('UPDATE codes SET initial_scan_done=1 WHERE id IN (SELECT code_id FROM leaderboard_stats UNION SELECT code_id FROM owner_inventory)');
    }
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair('ping', 'pong'));
  }

  async issueCode(label) {
    const bytes = crypto.getRandomValues(new Uint8Array(32));
    const code = 'GFW8H-' + btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    this.ctx.storage.sql.exec('INSERT INTO codes (id,digest,label,created) VALUES (?,?,?,?)',
      crypto.randomUUID(), await codeDigest(code), label, Date.now());
    return code;
  }
  listCodes() {
    const online = new Set(this.sockets().map(ws => this.attachment(ws).codeId));
    return this.ctx.storage.sql.exec('SELECT c.id,c.label,c.created,COALESCE(s.served,0) AS served FROM codes c LEFT JOIN leaderboard_stats s ON s.code_id=c.id ORDER BY c.created,c.label').toArray()
      .map(row => ({ id: row.id, label: row.label, created: row.created, served: row.served, connected: online.has(row.id) }));
  }
  async revokeCodes(ids) {
    const existing = new Set(this.ctx.storage.sql.exec('SELECT id FROM codes').toArray().map(row => row.id));
    if (!ids.every(id => existing.has(id))) return { error: 'One or more invite codes no longer exist.', revoked: 0 };
    const selected = new Set(ids);
    const sockets = this.sockets().filter(ws => selected.has(this.attachment(ws).codeId));
    for (const id of ids) this.ctx.storage.sql.exec('DELETE FROM codes WHERE id=?', id);
    for (const ws of sockets) {
      const session = this.attachment(ws).session;
      try { ws.close(1008, 'Access revoked'); } catch { /* Already closed. */ }
      this.endSession(session);
    }
    for (const ws of sockets) {
      const session = this.attachment(ws).session;
      for (const row of this.ctx.storage.sql.exec('SELECT id,requester,provider FROM pending WHERE requester=? OR provider=?', session, session).toArray()) {
        if (row.requester === session) this.forgetPending(row.id);
        else this.retryPending(row.id, 'Contributor access was revoked.');
      }
    }
    for (const id of ids) {
      this.ctx.storage.sql.exec('DELETE FROM owner_inventory WHERE code_id=?', id);
      this.ctx.storage.sql.exec('DELETE FROM leaderboard_game_serves WHERE code_id=?', id);
      this.ctx.storage.sql.exec('DELETE FROM leaderboard_sessions WHERE code_id=?', id);
      this.ctx.storage.sql.exec('DELETE FROM leaderboard_stats WHERE code_id=?', id);
    }
    this.broadcastAvailability();
    await this.scheduleAlarm();
    return { revoked: ids.length };
  }
  status() {
    const gameIds = this.availability();
    const connected = this.sockets().length;
    this.recordPeaks(gameIds.length, connected);
    return {
      connected,
      gameIds,
      peaks: this.peaks(),
      lastSeen: this.lastSeen(),
      pending: this.ctx.storage.sql.exec('SELECT COUNT(*) AS total FROM pending').toArray()[0].total
    };
  }

  trackSession(session, codeId, now = Date.now()) {
    if (!session || !codeId) return;
    this.ctx.storage.sql.exec('INSERT OR IGNORE INTO leaderboard_stats (code_id) VALUES (?)', codeId);
    this.ctx.storage.sql.exec('INSERT OR IGNORE INTO leaderboard_sessions (session,code_id,started,heartbeat) VALUES (?,?,?,?)',
      session, codeId, now, now);
  }
  endSession(session, now = Date.now(), stale = false) {
    const row = this.ctx.storage.sql.exec('SELECT code_id,started,heartbeat FROM leaderboard_sessions WHERE session=?', session).toArray()[0];
    if (!row) return;
    const end = stale ? Math.min(now, row.heartbeat + OFFER_TTL) : now;
    this.ctx.storage.sql.exec('UPDATE leaderboard_stats SET connected_ms=connected_ms+? WHERE code_id=?',
      Math.max(0, end - row.started), row.code_id);
    this.ctx.storage.sql.exec('DELETE FROM leaderboard_sessions WHERE session=?', session);
  }
  recordServe(providerSession, requesterCodeId, gameId) {
    const provider = this.ctx.storage.sql.exec('SELECT code_id FROM leaderboard_sessions WHERE session=?', providerSession).toArray()[0];
    if (!provider || provider.code_id === requesterCodeId) return;
    this.ctx.storage.sql.exec('UPDATE leaderboard_stats SET served=served+1 WHERE code_id=?', provider.code_id);
    this.ctx.storage.sql.exec('INSERT INTO leaderboard_game_serves (code_id,game,served) VALUES (?,?,1) ON CONFLICT(code_id,game) DO UPDATE SET served=served+1', provider.code_id, gameId);
  }
  leaderboard() {
    const now = Date.now();
    const sockets = this.sockets();
    this.recordPeaks(this.availability().length, sockets.length);
    for (const ws of sockets) {
      const a = this.attachment(ws);
      this.trackSession(a.session, a.codeId, now);
    }
    const active = new Set(sockets.map(ws => this.attachment(ws).session));
    for (const row of this.ctx.storage.sql.exec('SELECT session FROM leaderboard_sessions').toArray()) {
      if (!active.has(row.session)) this.endSession(row.session, now, true);
    }
    const online = new Map(sockets.map(ws => {
      const a = this.attachment(ws);
      return [a.codeId, a];
    }));
    const sessions = new Map(this.ctx.storage.sql.exec('SELECT code_id,started FROM leaderboard_sessions').toArray()
      .map(row => [row.code_id, row.started]));
    const mostServed = new Map();
    for (const game of this.ctx.storage.sql.exec('SELECT code_id,game,served FROM leaderboard_game_serves ORDER BY code_id,served DESC,game').toArray()) {
      const best = mostServed.get(game.code_id);
      if (!best) mostServed.set(game.code_id, { game: game.game, count: game.served, ties: 0 });
      else if (game.served === best.count) best.ties++;
    }
    const namesFor = ids => ids.map(id => gameNames.get(id)).filter(Boolean).sort((a, b) => a.localeCompare(b));
    const lastRecorded = json => {
      try { const ids = JSON.parse(json || '[]'); return Array.isArray(ids) ? [...new Set(ids.filter(validGame))] : []; }
      catch { return []; }
    };
    const rows = this.ctx.storage.sql.exec('SELECT c.id,c.label,s.code_id AS tracked_code,s.served,s.connected_ms,i.first_recorded,i.last_changed,i.game_ids FROM codes c LEFT JOIN leaderboard_stats s ON s.code_id=c.id LEFT JOIN owner_inventory i ON i.code_id=c.id WHERE s.code_id IS NOT NULL OR i.code_id IS NOT NULL').toArray()
      .map(row => {
        const offeredGameIds = online.has(row.id) && online.get(row.id).jwk && now - online.get(row.id).last < OFFER_TTL
          ? online.get(row.id).games.filter(validGame) : [];
        const lastRecordedGameIds = lastRecorded(row.game_ids);
        return {
        id: row.id,
        label: row.label,
        served: row.served || 0,
        mostServedGame: mostServed.has(row.id) ? {
          name: gameNames.get(mostServed.get(row.id).game) || `Game ${mostServed.get(row.id).game}`,
          count: mostServed.get(row.id).count,
          ties: mostServed.get(row.id).ties
        } : null,
        connectedMs: row.tracked_code ? row.connected_ms + (sessions.has(row.id) ? now - sessions.get(row.id) : 0) : null,
        online: online.has(row.id),
        offeredGameIds,
        offeredGameNames: namesFor(offeredGameIds),
        lastRecordedGameIds,
        lastRecordedGameNames: namesFor(lastRecordedGameIds),
        lastInventoryChange: row.last_changed,
        firstInventory: row.first_recorded
      } }).sort((a, b) => b.served - a.served || (b.connectedMs || 0) - (a.connectedMs || 0) || a.label.localeCompare(b.label));
    return { rows, games: catalogue.games.map(game => ({ id: game.index, name: game.name })),
      totalServed: rows.reduce((sum, row) => sum + row.served, 0), onlineCount: sockets.length, peaks: this.peaks() };
  }

  sockets() { return this.ctx.getWebSockets().filter(ws => ws.readyState === 1); }
  attachment(ws) { return ws.deserializeAttachment() || {}; }
  bySession(id) { return this.sockets().find(ws => this.attachment(ws).session === id); }
  activeOffers() { return this.sockets().filter(ws => {
    const a = this.attachment(ws);
    return a.jwk && a.last && Date.now() - a.last < OFFER_TTL && a.games.length;
  }); }
  availability() {
    const games = new Set();
    for (const ws of this.activeOffers()) for (const id of this.attachment(ws).games) games.add(id);
    return [...games].sort((a, b) => a - b);
  }
  providerCounts() {
    const counts = {};
    for (const ws of this.activeOffers()) for (const id of this.attachment(ws).games)
      counts[id] = (counts[id] || 0) + 1;
    return counts;
  }
  lastSeen() {
    return Object.fromEntries(this.ctx.storage.sql.exec('SELECT game, day FROM game_seen').toArray()
      .map(row => [row.game, row.day]));
  }
  recordSeen(ids) {
    if (!ids.length) return;
    const day = new Date().toISOString().slice(0, 10);
    const known = new Map(this.ctx.storage.sql.exec('SELECT game, day FROM game_seen').toArray()
      .map(row => [row.game, row.day]));
    for (const id of ids) if (!known.has(id) || known.get(id) < day)
      this.ctx.storage.sql.exec('INSERT INTO game_seen (game, day) VALUES (?, ?) ON CONFLICT(game) DO UPDATE SET day=excluded.day WHERE excluded.day > game_seen.day', id, day);
  }
  recordPeaks(games, users) {
    const day = new Date().toISOString().slice(0, 10);
    for (const [kind, count] of [['games', games], ['users', users]])
      this.ctx.storage.sql.exec('INSERT INTO hub_peaks (kind,count,day) VALUES (?,?,?) ON CONFLICT(kind) DO UPDATE SET count=excluded.count,day=excluded.day WHERE excluded.count > hub_peaks.count', kind, count, day);
  }
  peaks() {
    const rows = this.ctx.storage.sql.exec('SELECT kind,count,day FROM hub_peaks').toArray();
    const result = { games: { count: 0, day: '' }, users: { count: 0, day: '' } };
    for (const row of rows) if (row.kind === 'games' || row.kind === 'users')
      result[row.kind] = { count: row.count, day: row.day };
    return result;
  }
  recordInventory(codeId, ids) {
    const sorted = [...new Set(ids)].sort((a, b) => a - b);
    const gameIds = JSON.stringify(sorted);
    const previous = this.ctx.storage.sql.exec('SELECT game_ids FROM owner_inventory WHERE code_id=?', codeId).toArray()[0];
    if (previous?.game_ids === gameIds) return;
    const licenses = sorted.map(id => gameNames.get(id)).join('\n') || '(none)';
    const now = new Date().toISOString();
    this.ctx.storage.sql.exec('INSERT INTO owner_inventory (code_id,game_ids,licenses,first_recorded,last_changed) VALUES (?,?,?,?,?) ON CONFLICT(code_id) DO UPDATE SET game_ids=excluded.game_ids, licenses=excluded.licenses, last_changed=excluded.last_changed',
      codeId, gameIds, licenses, now, now);
  }
  send(ws, value) { try { ws.send(JSON.stringify(value)); } catch { /* Closed connection. */ } }
  forgetPending(id) {
    this.ctx.storage.sql.exec('DELETE FROM pending WHERE id=?', id);
    this.ctx.storage.sql.exec('DELETE FROM pending_retry WHERE id=?', id);
  }
  retryPending(id, reason) {
    const row = this.ctx.storage.sql.exec('SELECT p.requester,p.provider,p.game,r.tried,r.deadline FROM pending p JOIN pending_retry r ON r.id=p.id WHERE p.id=?', id).toArray()[0];
    if (!row) {
      const legacy = this.ctx.storage.sql.exec('SELECT requester,game FROM pending WHERE id=?', id).toArray()[0];
      this.forgetPending(id);
      if (legacy) {
        const requester = this.bySession(legacy.requester);
        if (requester) this.send(requester, { type: 'failure', requestId: id, gameId: legacy.game, message: reason });
      }
      return;
    }
    const requester = this.bySession(row.requester);
    if (!requester) { this.forgetPending(id); return; }
    const state = JSON.parse(row.tried);
    const tried = Array.isArray(state) ? state : state.sessions;
    const candidates = Date.now() < row.deadline ? this.activeOffers().filter(ws =>
      this.attachment(ws).games.includes(row.game) && !tried.includes(this.attachment(ws).session)) : [];
    if (!candidates.length) {
      this.forgetPending(id);
      this.send(requester, { type: 'failure', requestId: id, gameId: row.game, message: reason });
      return;
    }
    const provider = candidates[crypto.getRandomValues(new Uint32Array(1))[0] % candidates.length];
    const session = this.attachment(provider).session;
    tried.push(session);
    this.ctx.storage.sql.exec('UPDATE pending SET provider=?,expires=? WHERE id=?', session,
      Math.min(Date.now() + ATTEMPT_TTL, row.deadline), id);
    if (!Array.isArray(state)) { state.awaiting = false; state.sessions = tried; }
    this.ctx.storage.sql.exec('UPDATE pending_retry SET tried=? WHERE id=?', JSON.stringify(state), id);
    this.send(requester, { type: 'retrying', requestId: id, gameId: row.game });
    this.send(provider, { type: 'pull', requestId: id, gameId: row.game, requesterJwk: this.attachment(requester).jwk });
  }
  broadcastAvailability() {
    const gameIds = this.availability();
    const usersActive = this.sockets().length;
    this.recordPeaks(gameIds.length, usersActive);
    const message = { type: 'availability', gameIds, providerCounts: this.providerCounts(), lastSeen: this.lastSeen(), usersActive, peaks: this.peaks(), refreshSeconds: 60 };
    for (const ws of this.sockets()) this.send(ws, message);
  }
  async scheduleAlarm() {
    const next = [];
    for (const ws of this.sockets()) {
      const a = this.attachment(ws);
      if (a.last && a.last + OFFER_TTL > Date.now()) next.push(a.last + OFFER_TTL);
    }
    const pending = this.ctx.storage.sql.exec('SELECT MIN(expires) AS earliest FROM pending').toArray()[0]?.earliest;
    if (pending && pending > Date.now()) next.push(pending);
    if (next.length) await this.ctx.storage.setAlarm(Math.min(...next));
    else await this.ctx.storage.deleteAlarm();
  }

  async fetch(request) {
    const code = bearer(request);
    if (!validCode(code)) return reply({ error: 'Unauthorized.' }, 401);
    const rows = this.ctx.storage.sql.exec('SELECT id,initial_scan_done FROM codes WHERE digest=?', await codeDigest(code)).toArray();
    if (!rows.length) return reply({ error: 'Unauthorized.' }, 401);
    const codeId = rows[0].id;
    for (const ws of this.sockets()) if (this.attachment(ws).codeId === codeId) ws.close(1000, 'Reconnected');
    const pair = new WebSocketPair();
    const client = pair[0], server = pair[1];
    this.ctx.acceptWebSocket(server);
    const session = crypto.randomUUID();
    server.serializeAttachment({ session, codeId, games: [], last: 0, jwk: null });
    this.trackSession(session, codeId);
    this.send(server, { type: 'welcome', refreshSeconds: 60, needsInitialScan: rows[0].initial_scan_done === 0 });
    this.broadcastAvailability();
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, raw) {
    try {
      if (typeof raw !== 'string' || encoder.encode(raw).length > MAX_MESSAGE) throw new Error('Invalid message.');
      const message = JSON.parse(raw);
      const self = this.attachment(ws);
      if (!this.ctx.storage.sql.exec('SELECT id FROM codes WHERE id=?', self.codeId).toArray().length) {
        ws.close(1008, 'Access revoked'); return;
      }
      if (message.type === 'initialScanComplete') {
        this.ctx.storage.sql.exec('UPDATE codes SET initial_scan_done=1 WHERE id=?', self.codeId);
        this.send(ws, { type: 'initialScanAcknowledged' });
      } else if (message.type === 'offer') {
        if (!validJwk(message.jwk) || !Array.isArray(message.gameIds) || message.gameIds.length > 108 ||
          !message.gameIds.every(validGame)) throw new Error('Invalid offer.');
        const games = [...new Set(message.gameIds)];
        this.trackSession(self.session, self.codeId);
        this.ctx.storage.sql.exec('UPDATE leaderboard_sessions SET heartbeat=? WHERE session=?', Date.now(), self.session);
        this.recordInventory(self.codeId, games);
        this.recordSeen(games);
        self.jwk = { kty: 'RSA', e: message.jwk.e, n: message.jwk.n };
        self.games = games;
        self.last = Date.now();
        ws.serializeAttachment(self);
        this.broadcastAvailability();
      } else if (message.type === 'get') {
        if (!validGame(message.gameId) || !self.jwk) throw new Error('Invalid request.');
        const candidates = this.activeOffers().filter(candidate => this.attachment(candidate).games.includes(message.gameId));
        if (!candidates.length) { this.send(ws, { type: 'unavailable', gameId: message.gameId }); return; }
        const provider = candidates[crypto.getRandomValues(new Uint32Array(1))[0] % candidates.length];
        const id = crypto.randomUUID();
        const now = Date.now();
        this.ctx.storage.sql.exec('INSERT INTO pending (id,requester,provider,game,expires) VALUES (?,?,?,?,?)',
          id, self.session, this.attachment(provider).session, message.gameId, now + ATTEMPT_TTL);
        this.ctx.storage.sql.exec('INSERT INTO pending_retry (id,tried,deadline) VALUES (?,?,?)',
          id, JSON.stringify({ sessions: [this.attachment(provider).session], ack: message.validateAck === true, awaiting: false }), now + REQUEST_TTL);
        this.send(provider, { type: 'pull', requestId: id, gameId: message.gameId, requesterJwk: self.jwk });
        this.send(ws, { type: 'queued', requestId: id, gameId: message.gameId });
      } else if (message.type === 'result' || message.type === 'failure') {
        if (typeof message.requestId !== 'string') throw new Error('Invalid result.');
        const row = this.ctx.storage.sql.exec('SELECT requester,provider,game,expires FROM pending WHERE id=?', message.requestId).toArray()[0];
        if (!row || row.provider !== self.session || row.expires < Date.now()) throw new Error('Request unavailable.');
        if (message.type === 'result' && (typeof message.packet !== 'string' || message.packet.length > 130000)) throw new Error('Invalid packet.');
        const retry = this.ctx.storage.sql.exec('SELECT tried,deadline FROM pending_retry WHERE id=?', message.requestId).toArray()[0];
        const state = retry ? JSON.parse(retry.tried) : null;
        if (state?.awaiting) throw new Error('Request is awaiting validation.');
        if (message.type === 'failure') this.retryPending(message.requestId, 'No contributor could complete this receipt.');
        else {
          const requester = this.bySession(row.requester);
          if (!requester) this.forgetPending(message.requestId);
          else {
            if (state?.ack) {
              state.awaiting = true;
              this.ctx.storage.sql.exec('UPDATE pending SET expires=? WHERE id=?', Math.min(Date.now() + ACK_TTL, retry.deadline), message.requestId);
              this.ctx.storage.sql.exec('UPDATE pending_retry SET tried=? WHERE id=?', JSON.stringify(state), message.requestId);
            } else this.forgetPending(message.requestId);
            this.send(requester, { type: 'result', requestId: message.requestId, gameId: row.game, packet: message.packet, providerJwk: self.jwk });
          }
        }
      } else if (message.type === 'accept' || message.type === 'reject') {
        if (typeof message.requestId !== 'string') throw new Error('Invalid validation response.');
        const row = this.ctx.storage.sql.exec('SELECT p.requester,p.provider,p.game,r.tried FROM pending p JOIN pending_retry r ON r.id=p.id WHERE p.id=?', message.requestId).toArray()[0];
        if (!row || row.requester !== self.session) throw new Error('Request unavailable.');
        const state = JSON.parse(row.tried);
        if (!state.ack || !state.awaiting) throw new Error('Request is not awaiting validation.');
        if (message.type === 'accept') {
          this.recordServe(row.provider, self.codeId, row.game);
          this.forgetPending(message.requestId);
        }
        else this.retryPending(message.requestId, 'No contributor returned a valid receipt.');
      } else throw new Error('Unknown message.');
      await this.scheduleAlarm();
    } catch { this.send(ws, { type: 'error', message: 'Request unavailable.' }); }
  }

  async disconnected(ws) {
    const attachment = this.attachment(ws);
    const session = attachment.session;
    this.endSession(session);
    if (attachment.last && Date.now() - attachment.last < OFFER_TTL) this.recordSeen(attachment.games);
    try { ws.close(); } catch { /* Already closed. */ }
    for (const row of this.ctx.storage.sql.exec('SELECT id,requester,provider FROM pending WHERE requester=? OR provider=?', session, session).toArray()) {
      if (row.requester === session) this.forgetPending(row.id);
      else if (row.provider === session) this.retryPending(row.id, 'All contributors disconnected.');
    }
    this.broadcastAvailability();
    await this.scheduleAlarm();
  }
  async webSocketClose(ws) { await this.disconnected(ws); }
  async webSocketError(ws) { await this.disconnected(ws); }
  async alarm() {
    for (const row of this.ctx.storage.sql.exec('SELECT id,requester FROM pending WHERE expires<=?', Date.now()).toArray()) {
      this.retryPending(row.id, 'All contributors timed out.');
    }
    this.broadcastAvailability();
    await this.scheduleAlarm();
  }
}
