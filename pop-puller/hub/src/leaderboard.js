const encoder = new TextEncoder();
const COOKIE = 'gfw8_leaderboard';
const SESSION_SECONDS = 12 * 60 * 60;

const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, character => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
})[character]);
const base64url = bytes => btoa(String.fromCharCode(...bytes))
  .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const fromBase64url = value => Uint8Array.from(atob(value.replace(/-/g, '+').replace(/_/g, '/')
  .padEnd(Math.ceil(value.length / 4) * 4, '=')), character => character.charCodeAt(0));
const keyFor = secret => crypto.subtle.importKey('raw', encoder.encode(secret),
  { name: 'HMAC', hash: 'SHA-256' }, false, ['sign', 'verify']);

export async function makeSession(secret) {
  const expires = Date.now() + SESSION_SECONDS * 1000;
  const nonce = base64url(crypto.getRandomValues(new Uint8Array(16)));
  const payload = `${expires}.${nonce}`;
  const mac = await crypto.subtle.sign('HMAC', await keyFor(secret), encoder.encode(payload));
  return `${payload}.${base64url(new Uint8Array(mac))}`;
}

export async function validSession(request, secret) {
  if (!secret) return false;
  const value = /(?:^|;\s*)gfw8_leaderboard=([^;]+)/.exec(request.headers.get('Cookie') || '')?.[1];
  if (!value || value.length > 200) return false;
  const match = /^(\d{13})\.([A-Za-z0-9_-]{22})\.([A-Za-z0-9_-]{43})$/.exec(value);
  if (!match) return false;
  const expires = Number(match[1]);
  if (expires <= Date.now() || expires > Date.now() + SESSION_SECONDS * 1000) return false;
  try {
    return crypto.subtle.verify('HMAC', await keyFor(secret), fromBase64url(match[3]),
      encoder.encode(`${match[1]}.${match[2]}`));
  } catch { return false; }
}

export function sessionCookie(value) {
  return `${COOKIE}=${value}; Path=/leaderboard; Max-Age=${SESSION_SECONDS}; HttpOnly; Secure; SameSite=Strict`;
}

export const clearSessionCookie = `${COOKIE}=; Path=/leaderboard; Max-Age=0; HttpOnly; Secure; SameSite=Strict`;

export async function readPassword(request) {
  if (request.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase() !== 'application/x-www-form-urlencoded' ||
      Number(request.headers.get('Content-Length') || 0) > 1024) return '';
  const reader = request.body?.getReader();
  if (!reader) return '';
  const chunks = [];
  let length = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      length += value.byteLength;
      if (length > 1024) { await reader.cancel(); return ''; }
      chunks.push(value);
    }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    const params = new URLSearchParams(new TextDecoder().decode(bytes));
    const passwords = params.getAll('password');
    return passwords.length === 1 && passwords[0].length <= 256 ? passwords[0] : '';
  } finally { reader.releaseLock(); }
}

function shell(content, refresh = false) {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">${refresh ? '<meta http-equiv="refresh" content="60">' : ''}<title>GFW8 Pop Puller · Leaderboard</title><style>
*{box-sizing:border-box}body{margin:0;background:#1c0829;color:#f7f0fa;font:16px/1.45 "Segoe UI",Arial,sans-serif}header{background:#2d1238;padding:24px max(24px,4vw);display:flex;justify-content:space-between;align-items:center;gap:16px}h1{font-size:clamp(27px,3vw,42px);font-weight:300;margin:0}.eyebrow{text-transform:uppercase;letter-spacing:.12em;font-size:12px;color:#8af4ed;font-weight:700}main{padding:28px max(24px,4vw) 60px;max-width:1500px;margin:auto}.summary{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px;margin-bottom:12px}.peaks{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;margin-bottom:24px}.tile{background:#008797;padding:20px 24px;min-height:110px}.tile:nth-child(2){background:#682099}.tile:nth-child(3){background:#37203f}.tile strong{display:block;font-size:clamp(23px,3vw,36px);font-weight:300;margin-top:6px}.peaks .tile{min-height:95px;background:#244761}.peaks .tile:nth-child(2){background:#50316c}.peak-date{font-size:13px;color:#e3d5ea}p.note{color:#cbb7d2;margin:0 0 22px}table{width:100%;border-collapse:separate;border-spacing:0 8px;text-align:left}th{color:#b9a3c2;font-size:12px;text-transform:uppercase;letter-spacing:.08em;padding:0 16px}td{background:#36213f;padding:16px}td:first-child{border-left:4px solid #00a9b7}tr.online td:first-child{border-left-color:#71efa2}.rank{color:#a797b0;margin-right:15px}.state{color:#71efa2}.offline{color:#bbb0c0}button{background:#009ca9;border:1px solid #92f1e8;color:white;padding:10px 24px;font:inherit;cursor:pointer}button:hover{background:#007b87}.logout button{background:transparent;border-color:#887396}.login{max-width:440px;margin:8vh auto 0;background:#33203d;padding:28px}.login label{display:block;margin:18px 0 6px}.login input{width:100%;padding:13px;background:#26122f;color:white;border:1px solid #9b7ba5;font:inherit}.login button{margin-top:20px;width:100%}.error{color:#ffb8b8}.empty{padding:30px;background:#36213f}.sub{font-size:13px;color:#cbb7d2}@media(max-width:750px){.summary,.peaks{grid-template-columns:1fr}table,thead,tbody,tr,th,td{display:block}thead{display:none}tr{margin-bottom:12px}td{padding:8px 16px}td:first-child{padding-top:16px}td:last-child{padding-bottom:16px}}\n</style></head><body><header><div><div class="eyebrow">Private hub statistics</div><h1>GFW8 Pop Puller</h1></div>${refresh ? '<form class="logout" method="post" action="/leaderboard/logout"><button type="submit">Sign out</button></form>' : ''}</header><main>${content}</main></body></html>`;
}

export function loginPage(error = false) {
  return shell(`<section class="login"><div class="eyebrow">Owner access</div><h2>Leaderboard</h2><p>Enter your private leaderboard password.</p>${error ? '<p class="error" role="alert">That password was not accepted.</p>' : ''}<form method="post" action="/leaderboard/login"><label for="password">Password</label><input id="password" name="password" type="password" autocomplete="current-password" required autofocus><button type="submit">Open leaderboard</button></form></section>`);
}

const duration = ms => {
  const minutes = Math.floor(Math.max(0, ms) / 60000);
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const rest = minutes % 60;
  return days ? `${days}d ${hours}h` : hours ? `${hours}h ${rest}m` : `${rest}m`;
};

export function leaderboardPage(data) {
  const firstInventory = value => value ? new Intl.DateTimeFormat('en-GB', {
    day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC'
  }).format(new Date(value)) : '—';
  const rows = data.rows.map((row, index) => {
    const best = row.mostServedGame;
    const game = best ? `${escapeHtml(best.name)} <span class="sub">(${best.count} pop${best.count === 1 ? '' : 's'}${best.ties ? `; ${best.ties} other game${best.ties === 1 ? '' : 's'} tied` : ''})</span>` : '—';
    return `<tr class="${row.online ? 'online' : ''}"><td><span class="rank">${index + 1}.</span><strong>${escapeHtml(row.label || 'Unnamed contributor')}</strong></td><td>${row.served.toLocaleString()}</td><td>${game}</td><td>${row.connectedMs === null ? '—' : duration(row.connectedMs)}</td><td>${row.online ? '<span class="state">Connected</span>' : '<span class="offline">Offline</span>'}</td><td>${row.offeredGames}</td><td>${firstInventory(row.firstInventory)}</td></tr>`;
  }).join('');
  const peakTile = (title, peak, unit) => `<div class="tile"><div class="eyebrow">${title}</div><strong>${peak.count.toLocaleString()} ${unit}</strong><div class="peak-date">First reached ${firstInventory(peak.day ? `${peak.day}T00:00:00Z` : '')}</div></div>`;
  const content = `<div class="summary"><div class="tile"><div class="eyebrow">Verified pops served</div><strong>${data.totalServed.toLocaleString()}</strong></div><div class="tile"><div class="eyebrow">Users connected</div><strong>${data.onlineCount}</strong></div><div class="tile"><div class="eyebrow">Contributors recorded</div><strong>${data.rows.length}</strong></div></div><div class="peaks">${peakTile('All-time high · users connected', data.peaks.users, data.peaks.users.count === 1 ? 'user' : 'users')}${peakTile('All-time high · unique games available', data.peaks.games, data.peaks.games.count === 1 ? 'game' : 'games')}</div><p class="note">Historical inventories identify earlier contributors and their first recorded offer. Verified pop, connection-time, and simultaneous peak records begin 22 Nov 2026; earlier totals were not saved. Total time connected adds every recorded session, including the current one. A pop counts only after the requester validates it.</p>${rows ? `<table><thead><tr><th>Contributor</th><th>Pops served</th><th>Most served game</th><th>Total time connected</th><th>Status</th><th>Games offered now</th><th>First offer recorded</th></tr></thead><tbody>${rows}</tbody></table>` : '<div class="empty">No contributor connections or inventories have been recorded.</div>'}<p class="sub">An invite alone does not establish that someone connected. Invite labels and these statistics are shown only on this password-protected page.</p>`;
  return shell(content, true);
}

export function htmlResponse(html, status = 200, extraHeaders = {}) {
  return new Response(html, { status, headers: {
    'Content-Type': 'text/html; charset=utf-8',
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'no-referrer',
    'Content-Security-Policy': "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'",
    ...extraHeaders
  } });
}
