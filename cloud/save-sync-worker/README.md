# Save sync Worker

The store behind the app's **Cloud** save sync mode. One Cloudflare Worker in
front of one R2 bucket, holding a lane per set per device. The app does all the
reconciling; this only keeps objects.

It is deliberately not tied to anything: it runs on the free
`<name>.<account>.workers.dev` address with no custom domain, the address is a
setting in the app, and the Folder mode keeps working without it. If the
account or the address ever changes, deploy again and paste the new address on
each machine.

## Deploy once

Needs Node 18+ and a Cloudflare account (the free plan is plenty: R2 gives
10 GB and a pack is about 100 KB).

```bash
cd cloud/save-sync-worker
npm install
npx wrangler login
npm run setup      # creates the R2 bucket "ipm-saves"
npm run deploy     # prints the https://ipm-save-sync.<account>.workers.dev address
```

Then in the app: **Settings → Save sync → Cloud**, paste the address, press
**Generate a sync key** on the first machine, and paste that key on the others.
**Test** should answer with the Worker's name.

## What it accepts

- `GET /v1/ping` — no key needed; says it is alive.
- Everything else needs `Authorization: Bearer <sync key>`. The key is hashed
  to make the namespace, so the key never touches storage and two keys never
  see each other's lanes.
- A pack must be a zip and is capped at 8 MB (`MAX_PACK_BYTES` in
  `wrangler.toml`). A manifest must be schema 1 and name the same set and
  device as its path, and cannot be written before its pack. Names are limited
  to letters, digits and a few punctuation characters.

That is what stops it being a free file host: it will store nothing that is
not a save set lane.

## Expiry

Lanes are overwritten in place, so the bucket holds one pack per set per
device and does not grow with use. If you want abandoned lanes to disappear,
add an R2 lifecycle rule on the bucket (Cloudflare dashboard → R2 → bucket →
Settings → Object lifecycle rules).

## Running it locally

```bash
npm run dev
```

serves it at `http://localhost:8787` with a local R2 emulation. Point the app's
endpoint at that to try the whole loop without deploying.
