// Small in-memory fixed-window rate limiter. No dependency, no shared state — one
// process holds the counters, which is right for this single-node deployment (scale it
// out and this needs Redis).
//
// Used to blunt three things that were previously unbounded: password guessing on
// /api/login, mass org creation on /api/register, and host-ID enumeration over the
// relay WebSocket.

const WINDOW_SWEEP_MS = 60_000;
const MAX_KEYS = 50_000;        // pathological key churn (rotating IPs) must not grow forever

/** @type {Map<string,{count:number,resetAt:number}>} */
const buckets = new Map();
let lastSweep = 0;

function sweep(now) {
  if (now - lastSweep < WINDOW_SWEEP_MS) return;
  lastSweep = now;
  for (const [k, b] of buckets) if (b.resetAt <= now) buckets.delete(k);
  // Still oversized after dropping expired entries → we're being flooded with distinct
  // keys. Dropping everything costs one free window to attackers but bounds memory.
  if (buckets.size > MAX_KEYS) buckets.clear();
}

/**
 * Count one hit against `key`. Returns { ok, retryAfter } — retryAfter in seconds.
 * Callers that only want to *test* a key should not call this (every call counts).
 */
export function hit(key, windowMs, max) {
  const now = Date.now();
  sweep(now);
  let b = buckets.get(key);
  if (!b || b.resetAt <= now) { b = { count: 0, resetAt: now + windowMs }; buckets.set(key, b); }
  b.count++;
  if (b.count > max) return { ok: false, retryAfter: Math.max(1, Math.ceil((b.resetAt - now) / 1000)) };
  return { ok: true, retryAfter: 0 };
}

/** Forget a key — call after a success so a legitimate user isn't punished for typos. */
export function reset(key) { buckets.delete(key); }

/**
 * The client's real IP. Caddy appends the connecting peer to X-Forwarded-For, so the
 * RIGHTMOST entry is the one a client cannot forge — never trust the leftmost, which is
 * whatever the caller sent us.
 */
export function clientIp(req) {
  const strip = (ip) => (ip || '').replace(/^::ffff:/, '').trim();
  const xff = req.headers?.['x-forwarded-for'];
  if (typeof xff === 'string' && xff.trim()) {
    const parts = xff.split(',').map((s) => s.trim()).filter(Boolean);
    if (parts.length) return strip(parts[parts.length - 1]);
  }
  return strip(req.socket?.remoteAddress);
}

/**
 * Express middleware. `keyFn(req)` picks what to limit on (IP, IP+email, …); returning
 * a falsy key skips the check.
 */
export function limit({ windowMs, max, keyFn, message }) {
  return (req, res, next) => {
    const k = keyFn(req);
    if (!k) return next();
    const r = hit(k, windowMs, max);
    if (r.ok) return next();
    res.setHeader('Retry-After', String(r.retryAfter));
    return res.status(429).json({ error: message || `too many attempts — try again in ${r.retryAfter}s` });
  };
}
