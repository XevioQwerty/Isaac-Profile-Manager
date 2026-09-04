/**
 * Isaac Profile Manager — save sync lanes.
 *
 * A lane store: `<set>/<device>` holds one pack and one manifest, and only
 * the device that owns the lane ever writes it. The app reconciles; this
 * Worker only keeps objects. Everything is namespaced by a hash of the sync
 * key, which is also the bearer token, so one deployment serves any number of
 * people who never see each other's saves and there are no accounts.
 *
 * Routes (all under /v1, all bearer-authenticated):
 *   GET    /lanes                          every manifest in the namespace
 *   PUT    /lanes/:set/:device/pack        the .ipmsave zip (≤ MAX_PACK_BYTES)
 *   PUT    /lanes/:set/:device/manifest    the manifest JSON, written last
 *   GET    /lanes/:set/:device/pack        the zip back
 *   DELETE /lanes/:set/:device             both objects
 *   GET    /ping                           unauthenticated liveness check
 *
 * Not an open file host: a pack must start with a zip signature, a manifest
 * must parse as a manifest naming the same set and device as its path, and
 * both are capped. Set an R2 lifecycle rule if you want lanes to expire.
 */

export interface Env {
  SAVES: R2Bucket;
  MAX_PACK_BYTES?: string;
}

const NAME = /^[A-Za-z0-9 _.,+()'-]{1,80}$/;
const DEFAULT_MAX_PACK = 8 * 1024 * 1024;
const MAX_MANIFEST = 16 * 1024;

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const parts = url.pathname.split('/').filter(Boolean);

    if (parts.length === 2 && parts[0] === 'v1' && parts[1] === 'ping') {
      return json({ ok: true, service: 'ipm-save-sync', version: 1 });
    }

    const ns = await namespaceOf(request);
    if (!ns) return text(401, 'missing or malformed bearer key');

    if (parts.length < 2 || parts[0] !== 'v1' || parts[1] !== 'lanes') return text(404, 'no such route');

    // GET /v1/lanes
    if (parts.length === 2) {
      if (request.method !== 'GET') return text(405, 'method not allowed');
      return listLanes(env, ns);
    }

    if (parts.length < 4) return text(404, 'no such route');
    const set = decodeURIComponent(parts[2]);
    const device = decodeURIComponent(parts[3]);
    if (!NAME.test(set) || !NAME.test(device)) return text(400, 'set and device names may only use letters, digits, space and _ . , + ( ) \' -');

    const base = `${ns}/${set}/${device}`;

    // DELETE /v1/lanes/:set/:device
    if (parts.length === 4) {
      if (request.method !== 'DELETE') return text(405, 'method not allowed');
      await env.SAVES.delete([`${base}/pack`, `${base}/manifest`]);
      return new Response(null, { status: 204 });
    }

    if (parts.length !== 5) return text(404, 'no such route');
    const kind = parts[4];
    if (kind !== 'pack' && kind !== 'manifest') return text(404, 'no such route');

    if (request.method === 'GET') {
      const object = await env.SAVES.get(`${base}/${kind}`);
      if (!object) return text(404, 'no such lane');
      const headers = new Headers();
      object.writeHttpMetadata(headers);
      headers.set('etag', object.httpEtag);
      headers.set('cache-control', 'no-store');
      return new Response(object.body, { headers });
    }

    if (request.method !== 'PUT') return text(405, 'method not allowed');

    if (kind === 'pack') {
      const max = Number(env.MAX_PACK_BYTES ?? DEFAULT_MAX_PACK);
      const length = Number(request.headers.get('content-length') ?? '0');
      if (!length) return text(411, 'content-length required');
      if (length > max) return text(413, `pack exceeds ${max} bytes`);

      const bytes = new Uint8Array(await request.arrayBuffer());
      if (bytes.length > max) return text(413, `pack exceeds ${max} bytes`);
      if (bytes.length < 4 || bytes[0] !== 0x50 || bytes[1] !== 0x4b) return text(400, 'not a zip');

      await env.SAVES.put(`${base}/pack`, bytes, { httpMetadata: { contentType: 'application/zip' } });
      return new Response(null, { status: 204 });
    }

    // manifest
    const raw = await request.text();
    if (raw.length > MAX_MANIFEST) return text(413, 'manifest too large');
    let manifest: Record<string, unknown>;
    try {
      manifest = JSON.parse(raw);
    } catch {
      return text(400, 'manifest is not JSON');
    }
    if (manifest.SchemaVersion !== 1 || manifest.SetName !== set || manifest.DeviceId !== device || typeof manifest.Clock !== 'object') {
      return text(400, 'manifest must be schema 1 and name the same set and device as its path');
    }
    if (!(await env.SAVES.head(`${base}/pack`))) return text(409, 'push the pack before its manifest');

    await env.SAVES.put(`${base}/manifest`, raw, { httpMetadata: { contentType: 'application/json' } });
    return new Response(null, { status: 204 });
  },
};

async function listLanes(env: Env, ns: string): Promise<Response> {
  const lanes: unknown[] = [];
  let cursor: string | undefined;
  do {
    const page = await env.SAVES.list({ prefix: `${ns}/`, cursor });
    for (const object of page.objects) {
      if (!object.key.endsWith('/manifest')) continue;
      const body = await env.SAVES.get(object.key);
      if (!body) continue;
      try {
        lanes.push(JSON.parse(await body.text()));
      } catch {
        // a manifest that does not parse is not a lane
      }
    }
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
  return json({ lanes });
}

/** The namespace is a hash of the key: the key itself never touches storage. */
async function namespaceOf(request: Request): Promise<string | null> {
  const header = request.headers.get('authorization') ?? '';
  const match = /^Bearer\s+([A-Za-z0-9._~+/=-]{16,200})$/.exec(header);
  if (!match) return null;
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(match[1]));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('').slice(0, 32);
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', 'cache-control': 'no-store' } });
}

function text(status: number, message: string): Response {
  return new Response(message, { status, headers: { 'content-type': 'text/plain' } });
}
