// Fallback source for app-update artifacts: the project's GitHub Releases.
//
// build.ps1 stages manifest.json + the component exes into UPDATE_DIR, and the release
// workflow attaches those same files to a GitHub Release. A hosted deployment has no
// local UPDATE_DIR (nothing pushes 100 MB installers onto the droplet), so when a file
// is missing locally we resolve the latest release instead:
//
//   * manifest.json is served verbatim from the release — it carries the sha256 hashes
//     the updater verifies, so synthesising one would break integrity checks.
//   * .exe requests 302 to GitHub's CDN, so this server stores and streams nothing.
//
// A local UPDATE_DIR still wins, which keeps air-gapped / self-hosted installs working.

const REPO = process.env.GITHUB_REPO || 'edkeysender/remote-desktop';
const UA = 'remotler-server';
const TTL_MS = 10 * 60 * 1000;   // re-ask GitHub at most every 10 min (rate limit is 60/h)
const FAIL_TTL_MS = 60 * 1000;   // ...but retry sooner after a failure

/** @type {{at:number, manifest:string|null, assets:Map<string,string>}|null} */
let cache = null;
/** @type {Promise<any>|null} */
let inflight = null;

const ghHeaders = () => {
  const h = { Accept: 'application/vnd.github+json', 'User-Agent': UA };
  // Optional — only needed if this server ever trips the anonymous rate limit.
  if (process.env.GITHUB_TOKEN) h.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  return h;
};

// Older releases (cut before build.ps1 staged it) may have no manifest.json asset. The
// Download tab only needs version + the two installer names, so derive that much; the
// updater will simply see no component list and report "no update", which is correct.
function synthesize(tag, assets) {
  const version = String(tag || '').replace(/^v/, '');
  if (!/^\d+\.\d+\.\d+$/.test(version)) return null;
  const names = [...assets.keys()];
  const agentInstaller = names.find((n) => /^RemotlerAgent-Setup-.*\.exe$/i.test(n)) || null;
  const appInstaller = names.find((n) => /-Setup-.*\.exe$/i.test(n) && n !== agentInstaller) || null;
  if (!appInstaller && !agentInstaller) return null;
  return JSON.stringify({ version, notes: '', appInstaller, agentInstaller });
}

async function fetchLatest() {
  const r = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: ghHeaders() });
  if (!r.ok) throw new Error(`GitHub releases/latest -> ${r.status}`);
  const rel = await r.json();

  const assets = new Map();
  for (const a of rel.assets || []) if (a?.name && a?.browser_download_url) assets.set(a.name, a.browser_download_url);

  let manifest = null;
  const murl = assets.get('manifest.json');
  if (murl) {
    const m = await fetch(murl, { headers: { 'User-Agent': UA } });
    if (m.ok) manifest = await m.text();
  }
  return { at: Date.now(), manifest: manifest || synthesize(rel.tag_name, assets), assets };
}

/** Latest release (cached). Never throws — a GitHub outage just means "nothing published". */
async function latest() {
  const ttl = cache?.manifest ? TTL_MS : FAIL_TTL_MS;
  if (cache && Date.now() - cache.at < ttl) return cache;
  if (inflight) return inflight;
  inflight = fetchLatest()
    .then((r) => { cache = r; return r; })
    .catch((e) => {
      console.warn('[update] GitHub release lookup failed:', e.message);
      cache = { at: Date.now(), manifest: null, assets: new Map() };
      return cache;
    })
    .finally(() => { inflight = null; });
  return inflight;
}

/** The latest release's manifest.json as a string, or null if none is published. */
export async function manifestJson() {
  return (await latest()).manifest;
}

/** GitHub CDN URL for a released asset, or null if this release has no such file. */
export async function assetUrl(name) {
  return (await latest()).assets.get(name) || null;
}
