// ICE resolution: org settings vs server-wide env defaults (STUN_URLS / TURN_URL /
// TURN_USER / TURN_PASS / TURN_SECRET). The store is imported dynamically because it
// reads DATA_DIR at module load. Env is read at call time, so each test sets what it
// needs and restores in finally.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const DATA_DIR = mkdtempSync(join(tmpdir(), 'remotler-ice-test-'));
process.env.DATA_DIR = DATA_DIR;

const ENV_KEYS = ['STUN_URLS', 'TURN_URL', 'TURN_USER', 'TURN_PASS', 'TURN_SECRET'];
function withEnv(vars, fn) {
  const prev = Object.fromEntries(ENV_KEYS.map((k) => [k, process.env[k]]));
  for (const k of ENV_KEYS) delete process.env[k];
  Object.assign(process.env, vars);
  try { return fn(); }
  finally {
    for (const k of ENV_KEYS) {
      if (prev[k] === undefined) delete process.env[k];
      else process.env[k] = prev[k];
    }
  }
}

let store, orgId;

before(async () => {
  store = await import('../lib/store.js');
  orgId = store.createOrgWithOwner({ orgName: 'Ice Test Org', email: 'ice@test', name: 'Ice', passHash: 'x' }).org.id;
});

after(() => rmSync(DATA_DIR, { recursive: true, force: true }));

test('no org config, no env: public STUN, no TURN', () => {
  withEnv({}, () => {
    const ice = store.getIce(orgId);
    assert.deepEqual(ice.stun, ['stun:stun.l.google.com:19302']);
    assert.equal(ice.turnUrl, null);
  });
});

test('env static TURN becomes the default for an unconfigured org', () => {
  withEnv({ STUN_URLS: 'stun:relay.example:3478', TURN_URL: 'turn:relay.example:3478', TURN_USER: 'u', TURN_PASS: 'p' }, () => {
    const ice = store.getIce(orgId);
    assert.deepEqual(ice.stun, ['stun:relay.example:3478']);
    assert.equal(ice.turnUrl, 'turn:relay.example:3478');
    assert.equal(ice.turnUser, 'u');
    assert.equal(ice.turnPass, 'p');
  });
});

test('TURN_SECRET mints verifiable short-lived credentials (TURN REST API)', () => {
  withEnv({ TURN_URL: 'turn:relay.example:3478', TURN_SECRET: 'shh' }, () => {
    const ice = store.getIce(orgId);
    assert.equal(ice.turnUrl, 'turn:relay.example:3478');
    // username = unix expiry in the future; password = base64(HMAC-SHA1(secret, username))
    const expiry = Number(ice.turnUser);
    assert.ok(expiry > Date.now() / 1000, 'expiry must be in the future');
    assert.equal(ice.turnPass, createHmac('sha1', 'shh').update(ice.turnUser).digest('base64'));
  });
});

test('org TURN settings override the env default', () => {
  withEnv({ TURN_URL: 'turn:relay.example:3478', TURN_SECRET: 'shh' }, () => {
    store.setIce(orgId, { stun: 'stun:org.example:3478', turnUrl: 'turn:org.example:3478', turnUser: 'ou', turnPass: 'op' });
    const ice = store.getIce(orgId);
    assert.deepEqual(ice.stun, ['stun:org.example:3478']);
    assert.equal(ice.turnUrl, 'turn:org.example:3478');
    assert.equal(ice.turnUser, 'ou');
    assert.equal(ice.turnPass, 'op');
  });
});

test('an explicitly-empty STUN list (LAN-only) suppresses the env TURN default', () => {
  withEnv({ TURN_URL: 'turn:relay.example:3478', TURN_SECRET: 'shh' }, () => {
    store.setIce(orgId, { stun: '', turnUrl: '', turnUser: '', turnPass: '' });
    const ice = store.getIce(orgId);
    assert.deepEqual(ice.stun, []);
    assert.equal(ice.turnUrl, null);
  });
});

test('org config without its own TURN still inherits the env TURN default', () => {
  withEnv({ TURN_URL: 'turn:relay.example:3478', TURN_USER: 'u', TURN_PASS: 'p' }, () => {
    store.setIce(orgId, { stun: 'stun:org.example:3478', turnUrl: '', turnUser: '', turnPass: '' });
    const ice = store.getIce(orgId);
    assert.deepEqual(ice.stun, ['stun:org.example:3478']);
    assert.equal(ice.turnUrl, 'turn:relay.example:3478');
  });
});

test('getIceStored never exposes minted credentials, and flags the server default', () => {
  withEnv({ TURN_URL: 'turn:relay.example:3478', TURN_SECRET: 'shh' }, () => {
    store.setIce(orgId, { stun: 'stun:stun.l.google.com:19302', turnUrl: '', turnUser: '', turnPass: '' });
    const stored = store.getIceStored(orgId);
    assert.equal(stored.turnUrl, null);
    assert.equal(stored.turnUser, null);
    assert.equal(stored.turnPass, null);
    assert.equal(stored.serverTurn, true);
  });
  withEnv({}, () => {
    assert.equal(store.getIceStored(orgId).serverTurn, false);
  });
});
