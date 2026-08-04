// Password hashing (bcrypt) and stateless session tokens (HMAC-signed), used by both
// the web app (cookie) and the desktop app (Bearer token). A token is
// "<userId>.<expiryMs>.<hmac>"; verifying re-computes the HMAC and checks expiry.
// The signing secret is persisted so tokens survive restarts.

import bcrypt from 'bcryptjs';
import { createHmac, randomBytes, timingSafeEqual } from 'crypto';
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs';
import { resolve, join } from 'path';
import * as store from './store.js';

const DATA_DIR = resolve(process.env.DATA_DIR || './data');
const SECRET_FILE = join(DATA_DIR, 'session-secret');
const TTL_MS = 30 * 24 * 60 * 60 * 1000;   // 30 days

const SECRET = loadOrCreateSecret();
function loadOrCreateSecret() {
  try {
    if (existsSync(SECRET_FILE)) return readFileSync(SECRET_FILE, 'utf8');
    mkdirSync(DATA_DIR, { recursive: true });
    const s = randomBytes(32).toString('hex');
    writeFileSync(SECRET_FILE, s, { mode: 0o600 });
    return s;
  } catch { return randomBytes(32).toString('hex'); }   // ephemeral fallback
}

export async function hashPassword(pw) { return bcrypt.hash(pw, 10); }
export async function verifyPassword(pw, hash) {
  try { return await bcrypt.compare(pw, hash); } catch { return false; }
}

export function issueToken(userId) {
  const exp = Date.now() + TTL_MS;
  const body = `${userId}.${exp}`;
  return `${body}.${sign(body)}`;
}

export function verifyToken(token) {
  if (typeof token !== 'string') return null;
  const i = token.lastIndexOf('.');
  if (i < 0) return null;
  const body = token.slice(0, i), mac = token.slice(i + 1);
  const good = sign(body);
  const a = Buffer.from(mac), b = Buffer.from(good);
  if (a.length !== b.length || !timingSafeEqual(a, b)) return null;
  const [userId, expStr] = body.split('.');
  const exp = Number(expStr);
  if (!userId || !Number.isFinite(exp) || exp < Date.now()) return null;
  // Revocation: the token carries no issue time, but the TTL is fixed, so we can derive
  // it. Anything issued before the user's watermark was revoked (sign-out, admin action)
  // and must be refused even though the signature and expiry are still good.
  if (exp - TTL_MS < store.sessionsValidFrom(userId)) return null;
  return userId;
}

function sign(body) { return createHmac('sha256', SECRET).update(body).digest('hex'); }

// Minimal cookie parse (avoids an extra dependency).
export function parseCookies(req) {
  const out = {};
  for (const part of (req.headers.cookie || '').split(';')) {
    const j = part.indexOf('=');
    if (j > 0) out[part.slice(0, j).trim()] = decodeURIComponent(part.slice(j + 1).trim());
  }
  return out;
}

export const SESSION_COOKIE = 'ftd_session';
