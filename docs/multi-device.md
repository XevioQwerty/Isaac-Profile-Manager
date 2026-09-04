# Multi-device: profiles on a second machine, and saves that follow you

> **Starting from here, cold?** Read `CLAUDE.md` first — all of it. This document
> assumes its verified facts and will lead you into destroying someone's
> achievements if you skip them. Then `PLAN.md` for what is already built.
>
> This file is the spec for steps 20 and 21. **As of 2.0.0, build-order steps
> 1 to 4 are implemented** (history, device and identity, the launch guard and
> Play screen, exit capture) — see `PLAN.md` for the state table — plus one
> thing this document did not know about: a set now carries the per-slot mod
> data and REPENTOGON state the game keeps outside the save folder
> (`CLAUDE.md`, "The save folder is not the whole save"). Steps 5 onward are
> still to build, and **step 0's probes have not been run** — do not build
> step 5 before they are. Run `dotnet test` before touching anything under
> `Services/`.
>
> Two habits this project runs on, both learned the hard way: probe the real
> install instead of reasoning about what it probably does, and record what you
> find in `CLAUDE.md` rather than here. Several confident guesses in this
> project's history turned out to be wrong, and the record of them is why the
> current code is safe.

A design for three things that turn out to be one thing:

1. Getting a profile onto the laptop without rebuilding it by hand.
2. Keeping save sets in step across your own devices.
3. Making a save set *insist* on the mod profile and build it was made with —
   at launch, and again when you close the game.

They are one thing because a save set already records `ModProfile` and `Build`,
and nothing currently reads either of them at the moment it matters. Once a save
set can travel, that record becomes the thing that stops the laptop loading a
REPENTOGON save into retail.

Read `CLAUDE.md` first. Everything below assumes the verified facts there,
especially: the live saves are Steam userdata files separated by *filename
prefix*, not folders; the folder is Steam's and must not be junctioned; and a
cross-build save load is data destruction, not a warning case.

---

## What already exists

| Piece | State |
|---|---|
| `SaveSet.ModProfile` | Recorded at capture. **Never read.** |
| `SaveSet.Build` | Recorded, and enforced on *activation* only |
| `SaveSetService.Check` | Isaac closed · Cloud off · folder found · build matches |
| `SaveSetService.Capture` / `CaptureInto` / `Activate` | Working, backup-first |
| `AppConfig.ActiveProfile` | Mod profile only — there is **no `ActiveSaveSet`** |
| `GameProcessService` | Answers "is it running". No exit event, no watcher |
| `.saves\` | In `.stignore`. Deliberately machine-local |
| `.library\` + `.profiles\*.json` | Sync across machines today, via Syncthing |
| `ShareCodeService` / `ShareImportRunner` | Mod sets only, refetched through Steam |
| `LogReaderService.GameVersion` | Parses `Game Version:` — the J-number |

So the mod half of "set up my laptop" mostly works already. The save half does
not exist, and the pairing between them is recorded but inert.

---

## The four gaps, named

**Gap 1 — nothing knows which save set is live.** `Activate` copies files in and
returns a backup path. It does not record what it did. Ask the app five minutes
later which set is loaded and it cannot answer.

**Gap 2 — the profile pairing is never checked.** You can activate the
`live vanilla solo` set and then launch a heavily modded REPENTOGON profile.
Nothing objects.

**Gap 3 — closing the game strands progress in the live folder.** The set on
disk still holds the bytes from when you captured it. Play an hour, close the
game, sync to the laptop, and you send the *old* save. This is the single most
damaging gap for a two-device setup, and it is invisible until you notice
unlocks missing.

**Gap 4 — a second device has no route in.** The share code fetches mods from
the Workshop through a subscribe/unsubscribe cycle, which is the right design
for a stranger and the wrong one for your own laptop: it re-downloads gigabytes
to reproduce files you already have byte-for-byte on the same LAN.

---

## Two new safety facts this feature creates

Both are new problems that only appear once saves cross a machine boundary.

### The J-number is not the same as the build

`GameBuild` distinguishes vanilla from REPENTOGON. It does **not** distinguish
retail J460 from REPENTOGON's J273 — and it cannot, because "vanilla" on the
desktop and "vanilla" on the laptop can be different retail versions if one has
updated and the other has not. The existing block is necessary and no longer
sufficient.

**Therefore:** capture records `GameVersion` read from `log.txt` at capture
time, and activation blocks when the target machine's last-seen version differs.
Unknown on either side degrades to a warning, not a block — the version is only
readable if the game has run since the log was last truncated.

### We are replacing Steam Cloud, so we own conflicts

The tool requires Cloud off for Isaac. That is correct and stays. But it means
nothing is reconciling two devices except us, and last-writer-wins across two
machines silently destroys an evening's unlocks.

**Therefore:** no automatic merge, ever. A fork is detected, surfaced, and left
for the user to resolve, with both sides kept.

---

## Mechanism

### Device lanes, not a shared folder

The obvious design — put `.saves\` under Syncthing — is wrong. Two devices
writing the same `set.json` produces `.sync-conflict-*` files, and a sync client
has no idea whether Isaac is mid-write.

Instead each device owns a **lane** it alone writes:

```
<SyncRoot>\.savesync\                      [syncs]
    <device-id>\
        device.json                        friendly name, id, last push
        <set name>\
            set.json                       + Clock, Device, GameVersion
            *.dat
            rgon_savesyncstatus.json
<SyncRoot>\.saves\                         [machine-local, stays in .stignore]
```

No two devices ever write the same file, so the transport can never produce a
conflict. Reconciliation happens *inside the app*, on demand, where it can check
that Isaac is closed and report what it is about to do.

This works over Syncthing, OneDrive, Dropbox, a USB stick, or a network share —
the app never opens a socket for this. Same posture as the rest of the project:
nothing hosted, nothing expires.

### Vector clocks for fork detection

`set.json` gains `Clock: { "<device-id>": <counter> }`. Every capture bumps that
device's own counter. Comparing two clocks gives exactly three answers:

- **Theirs dominates** — every counter ≥ ours, at least one greater. Safe to take.
- **Ours dominates** — nothing to do.
- **Neither** — a genuine fork: both machines played from the same starting
  point. Never resolved automatically. Both are kept; the user picks, and the
  loser is filed into that set's history.

This is about fifteen lines of code and it is the difference between "sync"
and "sometimes eats your unlocks".

### Per-set history

A save set is ~24 KB. Every capture files the previous contents into
`.saves\<set>\.history\<utc>-<device>\` before overwriting. Twenty revisions is
half a megabyte. This is the undo for everything else in this document and it
should exist before any of the sync code does.

Retention follows `BackupService`'s existing convention: history entries are
true copies, so they are prunable; anything that was *moved* rather than copied
is not.

---

## The pairing gate

### At launch

Before the Launch button starts anything, resolve three facts:

1. **Which set is live** — by hashing the live files against every set's `Sha1`.
   A remembered `ActiveSaveSet` is a hint; the hashes are the truth, because the
   PowerShell tool or a manual copy can change the live folder behind the app.
   Outcomes: `Exact` · `Drifted` (same set, you have played since) ·
   `Unrecognised` (no set matches).
2. **The active mod profile** — read from the junction on disk, not from config.
3. **What the launcher will start** — `[Shared] LaunchMode`, plus the recorded
   `GameVersion`.

Then apply the severity ladder the project already uses:

| Mismatch | Response | Why |
|---|---|---|
| Build (vanilla ↔ REPENTOGON) | **Block** | Achievement destruction |
| `GameVersion` differs, both known | **Block** | Same failure, across devices |
| `GameVersion` unknown on either side | Warn | Log may simply not have run yet |
| Mod profile differs from `set.ModProfile` | **Recommend**, one-click fix | Costs a desync, not data |
| Live saves unrecognised | Warn | You may be mid-experiment on purpose |

The mod-profile case gets an inline panel above the Launch button:

> These saves were made with **Vanilla+**, but **RPTG** is active.
> [ Switch to Vanilla+ and launch ]  [ Launch anyway ]

That is precisely the "require, or recommend at the least" the feature was asked
for: the dangerous half is required, the annoying half is recommended with the
fix attached.

### Save-set-led launch

The inverse is the button worth having. Pick a save set, press one thing, and it
activates the set, switches the mod profile to `set.ModProfile`, sets
`LaunchMode` to `set.Build`, and launches. Every gate above still runs — the
difference is that they now almost never fire, because the set chose everything.

### On exit

A `GameSessionWatcher` in the shell subscribes to the game process's `Exited`
event (falling back to a poll), waits for the write to settle, re-hashes the live
folder, and:

- Live matches the active set exactly → nothing to do.
- Live has drifted → this is the run you just played. `CaptureInto(set)`, then
  optionally push to the lane.
- Live is unrecognised → offer to capture it as a new set, pre-filled with the
  mod profile that was active and the `GameVersion` from the fresh `log.txt`.

Three settings: **Off · Ask · Automatic**. Default **Ask** until it has soaked;
`Automatic` is the one that makes the two-device story actually work without
thinking about it.

**Honest limitation:** this only fires while the app is running. Launching Isaac
straight from Steam with the manager closed captures nothing. Two mitigations,
in order of cost: keep the app open (it is what you launched from anyway), or a
minimal tray-resident mode that does nothing but watch for the process. The tray
mode is worth building only if the first one turns out to be annoying in
practice — do not build it speculatively.

---

## Getting a profile onto the laptop

Three routes, and they are for different situations. Build them in this order.

### 1. Adopt a synced SyncRoot — the primary route

Syncthing already carries `.library\` and `.profiles\*.json`. What is missing is
that the laptop's first run does not recognise a folder that already has
everything in it, so setup treats it as a blank slate.

Add a second path through the setup screen: **"This machine syncs with another
one"** → point at the SyncRoot → the app detects `.library`, `.profiles` and
`.savesync`, writes a *local* config with this machine's own absolute paths,
materialises the profiles from their manifests, and offers to pull save lanes.

The config stays machine-local and gitignored — that does not change and must
not. What travels is what is already designed to travel: real mod folders and
small manifests.

`ModProfileService` already has `DiscoveredProfile` for "a manifest the config
does not know about", so most of the detection exists.

### 2. A portable pack — no sync client needed

`<name>.ipmpack`: a zip of the library entries a profile references, its
manifest, and the hash export. Import unpacks into the local library and writes
the manifest.

This is strictly better than the share code *for your own devices*, because it
carries the bytes instead of instructions for re-fetching them. The share code
stays the right answer for someone you play with who already owns the mods on
Steam.

Size is honest and should be shown before export: the reference library is
1.8 GB across 39 items. A 26-mod profile is a large file, and saying so up front
beats a progress bar that runs for ten minutes.

### 3. A share link

The share *code* is removed (see below). A link does the same job without the
3,679-character paste.

---

## Sharing, in the app

The share code goes. `CLAUDE.md` already records why it never worked: a
self-contained code cannot be short, because the ids and names *are* the payload.
The note also records the way out, and we skipped it —

> A Steam collection id is short only because Steam stores the list.

So store the list. A link is a code whose payload lives somewhere instead of
inside it, and it is short for exactly the reason a collection id is.

### The reframe that decides the hosting bill

**A mod profile share is almost never a file.** `SharedProfile` is a manifest —
entry names, Workshop ids, hashes. A few KB. The recipient's copy of the tool
refetches the mods from Steam, which is what `ShareImportRunner` already does.
Uploading 1.8 GB of mod bytes to send someone a mod list would be absurd.

The one case that genuinely needs bytes is the one the importer currently gives
up on. `ShareItemAction.Unfetchable` — a mod with no Workshop id — reports
*"NOT on the Workshop — ask them to send this folder"*. That is the hole hosting
fills, and it is usually zero to three folders, not a library.

So a share is:

| Kind | Payload | Typical size |
|---|---|---|
| Mod profile | manifest, always | a few KB |
| Mod profile, with off-Workshop mods | manifest + just those folders | a few MB |
| Save set | the whole set, always | ~24 KB |

Which means the storage question is much smaller than "host my mod library",
and the free tier of anything covers it.

### Route 1 — Cloudflare Worker + R2 · **recommended**

A Worker on `share.<your-domain>`, an R2 bucket, and KV or D1 for the index.

```
POST /new      → { id, uploadUrl }   presigned PUT, if there is a payload
PUT  <r2 url>  → the app uploads direct to R2, not through the Worker
GET  /s/<id>   → the manifest as JSON
GET  /s/<id>/blob → presigned GET, or a public bucket URL
```

Why this one:

- **Zero egress cost.** R2 charges nothing for data transfer out; the free tier
  is 10 GB stored, 1M Class A and 10M Class B operations a month. At a few KB per
  share that is effectively unlimited for this.
- **Nothing to keep alive.** No process, no patching, no instance to be reclaimed.
- **Expiry is a bucket setting.** An R2 lifecycle rule deletes objects after N
  days, so "links expire in 30 days" costs one line of config rather than a cron
  job.
- **It sidesteps the 100 MB wall.** Cloudflare's proxy caps request bodies at
  100 MB on Free and Pro plans. Uploading *through* a Worker inherits that cap.
  Uploading to a presigned R2 URL does not — R2 objects go to 5 TB, with
  multipart parts of 5 MB–5 GB. Since payloads here are small this rarely
  matters, but it is the reason to presign rather than proxy, and it is the thing
  that would quietly break a naive implementation the first time someone shared a
  large off-Workshop mod.

One R2 quirk to write down before implementing multipart: **every part must be
exactly the same size except the last.** S3 and MinIO allow varying parts; R2
does not, and an S3 SDK left on its defaults will produce parts R2 rejects.

### Route 2 — the Oracle VPS

Worth being honest that this is the weaker option *for this job*, despite being
the more capable machine.

In its favour: no storage ceiling, full control, and it can hold payloads far
past R2's free 10 GB if the feature ever grows into hosting whole libraries.

Against it:

- **Idle reclamation.** Oracle deems an Always Free instance idle when, over a
  7-day window, the 95th percentile of CPU, network *and* memory utilisation are
  all under 20%. A share endpoint that serves a few KB a week is the definition
  of idle. Keeping it alive means running a keepalive whose only purpose is to
  lie to the reclaimer — worth checking against your own account before relying
  on it, and not a great foundation either way.
- **The 100 MB proxy cap again.** Behind an orange-clouded hostname, uploads over
  100 MB fail. The workarounds are chunking, or grey-clouding an upload
  subdomain, which exposes the VPS IP directly.
- **You maintain it.** TLS renewal, patching, disk, backups — for an endpoint
  whose whole job is to hand over a 4 KB JSON file.

Where it does earn a place: as the **origin behind a Cloudflare Tunnel** if you
later want something stateful — a group registry, "profiles we play with", an
update feed. `cloudflared` keeps it off the public internet with no open ports.
That is a different feature from a share drop, and it can wait.

### Route 3 — a file, always

`.ipmpack` (mod profile) and `.ipmsave` (save set) stay, and every share is
exportable as one. This is not a fallback in the apologetic sense — it is the
only route that keeps working when the endpoint is down, when someone does not
want to use your infrastructure, or in five years when the domain has lapsed.

**Rule: the link is a convenience over the file, never a replacement for it.**

### Recommendation

Route 1 for the button, Route 3 underneath it, Route 2 held in reserve. The app
gets one Settings key, `ShareEndpoint`, defaulting to the shipped one — so a
group that would rather not route through your account can point at their own
Worker without a rebuild.

### Making it not an open file host

An anonymous upload endpoint is a free file host, and it will be found. The
defences, cheapest first:

- **Validate the shape, not just the size.** The Worker accepts an upload only if
  it parses as a `SharedProfile` or a `SaveSet` plus known-shaped mod folders.
  This is the one that matters: it makes the endpoint useless for storing
  anything that is not an Isaac share, which removes the reason to abuse it.
- **Cap hard.** Per-upload size, uploads per IP per day, total per day.
  Cloudflare rate limiting does this at the edge, before the Worker bills you.
- **Expire by default.** 30 days, as a lifecycle rule. Shares are for "we are
  playing tonight", not archival.
- **Unguessable ids.** 128 bits of randomness, no listing endpoint, no
  enumeration. A share link is a capability.
- **Optional passphrase.** AES-GCM in the app, so the Worker stores ciphertext
  and never holds a readable save. Cheap to add, and the right answer if you ever
  want to share a save with someone you do not fully trust with the link.

### What the recipient sees

Unchanged, and that is the point: the same `SharePlan` preview that exists today
— *"26 mods, 3 to download, 23 already here, 1 not on the Workshop"* — before
anything is written. Only the fetch changes. `ShareImportRunner` is reused whole.

### Sharing a save set specifically

Import runs every gate activation runs, plus one more: an explicit statement that
this **replaces the recipient's unlock state** for the slots it contains, with
their current saves filed into history first.

Worth being clear about why this is a legitimate feature rather than a footgun.
`CLAUDE.md` records that mismatched save state between players desyncs within
seconds, and PLAN.md records that `.saves` must not sync because save state is
personal. Both are right, and they resolve like this: **accidental** sharing is
the hazard; **deliberate** sharing of one agreed save to everyone in a co-op
group is one of the few reliable ways to guarantee the unlock states match. The
design keeps `.saves` unsynced by default and makes the share an explicit,
warned act.

---

## Removing the share code

Not a deletion so much as a transplant: the *manifest* stays and the *envelope*
goes.

**Delete:**

- `src/IsaacProfileManager.Core/Services/ShareCodeService.cs`
- `tests/IsaacProfileManager.Tests/ShareCodeTests.cs`

**Keep:** `SharedProfile` — it lives in `LibraryHashService.cs`, it is the
payload a link resolves to, and the export/import file path already depends on it.

**Rename, do not delete:** `ShareCodeException`. `ShareImportWindow.xaml.cs`
throws it for Steam collection parsing too, so it becomes `ShareException`.

**Rewire:**

- `ShareImportWindow` — the input accepts a share link, a Steam collection id or
  link, or a file. Drop the `IPM1-` branch and rewrite the explainer, which
  currently opens *"Paste a share code (starts with IPM1-)"*.
- `LibraryViewModel` — `CopyShareCode` becomes `ShareProfile`: upload, copy link.
  The two context-menu items and the character-count messaging
  (*"Copied a 3,679 character share code"*) go with it.
- `ModProfilesView`, `SetupView`, `SetupViewModel` — reword "share code" to
  "share link or file" in three places.

**In the docs:** `CLAUDE.md`'s "a self-contained share code cannot be short" note
stays. It is not obsolete — it is the reason this route exists, and deleting the
finding would invite someone to rediscover it the hard way. Reword it to end at
the conclusion it actually supports: store the list, send a link.

---

## Data model changes

All additive. Every schema version stays where it is — the same trick
`ProfileManifest.Disabled` and `SharedProfile.WorkshopIds` already use, and
`ConfigVersion` in particular must not move (`Assert-Config` in
`IsaacProfiles.ps1` refuses below 3).

```csharp
// SaveSet — stays at SchemaVersion 1
string   Device;        // device id that last captured this
Dictionary<string,int> Clock;   // vector clock, per device id
string?  GameVersion;   // J-number from log.txt at capture
string?  ParentRevision;// for history lineage display

// AppConfig — stays at ConfigVersion 3
string?  ActiveSaveSet;     // hint only; hashes are the truth
string?  DeviceId;          // stable guid for this machine
string?  DeviceName;        // friendly, defaults to Environment.MachineName
string?  LastGameVersion;   // last J-number this machine ran
string?  SaveSyncRoot;      // defaults to <SyncRoot>\.savesync
string?  ExitCapture;       // Off | Ask | Automatic
```

New services, all in Core, all with no WPF reference and testable against temp
directories:

- `DeviceService` — id, name, lane path.
- `SaveIdentityService` — hash the live folder, name the set it is.
- `SaveSyncService` — push, scan lanes, reconcile, detect forks.
- `LaunchGuardService` — the severity ladder above, as data.
- `GameSessionWatcher` — process exit → settle → re-hash → act.
- `PortablePackService` — `.ipmpack` and `.ipmsave` write/read.

---

## Probe before building

Project convention: probe the real install, do not reason about what it probably
does. Three things must be answered first, with a throwaway save.

> **Probe 1 was run 2026-09-04** and is recorded in `CLAUDE.md` ("How that
> sync actually decides"). Short form: no prompt ever; the twin that changed
> since the status file wins, both changed merges. So a lane must carry both
> twins of every slot plus `rgon_savesyncstatus.json`, and reconcile must
> clear the live slot before copying in — which `Activate` already does.

1. **Cross-device restore with REPENTOGON's sync status.**
   `CLAUDE.md` lists as unverified what REPENTOGON does when
   `rgon_savesyncstatus.json` checksums do not match the `.dat` files beside it.
   Restoring a desktop-captured set onto the laptop is *exactly* that case. This
   is the one probe that gates the whole feature — if REPENTOGON reconciles
   destructively, the set must carry something else, or the file must be
   regenerated rather than copied.

2. **A second machine's `userdata` path and account id.** Same Steam account, so
   the account id should match while the root differs. `SaveLocationService`
   already resolves per-machine, but confirm rather than assume — and confirm the
   laptop's Cloud toggle independently, since it is a per-machine file.

3. **What a sync client does to `.savesync` mid-write.** Push a lane while a
   large file is being written and confirm the reader sees either the old
   version or the new one, not a truncated `set.json`. Writing lane files
   atomically (temp + move, as `ConfigStore` already does) probably makes this a
   non-issue, but confirm it rather than assume it.

Record the answers in `CLAUDE.md`, not here.

---

## Build order

Each step is useful on its own, per the project's convention of shipping
features independently.

| # | Step | Ships what |
|---|---|---|
| 0 | The three probes | Answers that may change step 4 |
| 1 | Per-set history on every capture | The undo everything else relies on |
| 2 | `DeviceService`, `SaveIdentityService`, `GameVersion` capture | The app can say which set is live and what ran it |
| 3 | `LaunchGuardService` + the launch panel + save-set-led launch | **The pairing enforcement, on one machine.** Worth having with no sync at all |
| 4 | `GameSessionWatcher` + exit capture (Ask) | Progress stops getting stranded |
| 5 | `.savesync` lanes, push/pull/reconcile, one Sync button | Two devices in step |
| 6 | Adopt-a-synced-SyncRoot setup path | Laptop set up in one screen |
| 7 | `.ipmpack` / `.ipmsave`, and the share code removed | The file route, and the paste gone |
| 8 | Worker + R2 endpoint, Share/Import by link | One button to send a profile or a save |
| 9 | Exit capture `Automatic`, tray watcher | Only if 4 proves it wants to be automatic |

Steps 3 and 4 answer the original request on a single machine and are the
highest value per line of code. Step 5 is where the risk is, and it is behind two
steps of history and identity work by design.

Step 7 comes before step 8 deliberately. The file format is the payload the
endpoint serves, so building it first means the link route is a thin transport
over something already proven — and if the endpoint is ever unavailable, the
feature degrades to a file rather than to nothing.

---

## Testing

Filesystem tests in temp directories, as everything else here does. The ones
that matter:

- [ ] Build mismatch blocks activation *and* launch (the existing gap in PLAN.md's checklist)
- [ ] `GameVersion` mismatch blocks; unknown on either side warns
- [ ] Profile mismatch recommends and never blocks
- [ ] Live-folder identification: exact, drifted, unrecognised
- [ ] Vector clock: dominates / dominated / fork, all three
- [ ] A fork is never auto-resolved and both sides survive
- [ ] Reconcile refuses while Isaac is running
- [ ] History is written before every overwrite, and retention never prunes moves
- [ ] Lane writes are atomic — a killed write leaves the previous lane readable
- [ ] `.ipmsave` import runs the full gate, not a reduced one
- [ ] A share link import produces the same `SharePlan` as the equivalent file
- [ ] An expired or unknown share id reports that, rather than an empty profile
- [ ] The Worker rejects a payload that is not a manifest or a save set
- [ ] Upload failure leaves no half-written share and no dangling link
- [ ] Import still works with the endpoint unreachable, given the file

Plus one manual check that cannot be automated: capture on one machine, restore
on the other, launch, and confirm the unlock screen matches. Do it against a
throwaway save the first time.

---

## Beyond the plan: shape, UI, and features worth arguing about

**Nothing here is committed.** It is what fell out of designing the above, kept
separate so it cannot be mistaken for the spec.

### The shape problem

Seven tabs, and three of them — **Mod profiles**, **Library**, **Workshop** —
are one concept at three stages: get mods, keep mods, choose mods. **Saves** is a
fourth tab holding the other half of a pair that the launch guard is about to
make formal. And the tab strip is where the Launch button and the patch toggles
live, which is a reliable sign that a tab strip is carrying more than a tab strip
should.

The feature in this document changes the model: a mod profile and a save set stop
being independent things you happen to switch, and become a pair with a build
attached. The UI should say so.

#### Proposal A — a Play surface

One screen that answers the only question the app exists to answer: **what
happens when I press Launch?** Profile, save set, build, patches applied, and the
guard's verdict, resolved into a single pre-flight card. Everything else becomes
somewhere you go to *change* one of those five things.

A rail instead of a tab strip:

| Rail item | Holds |
|---|---|
| **Play** | The pre-flight card and the Launch button — absorbs today's launch cluster |
| **Mods** | Profiles, Library and Workshop as segments of one screen |
| **Saves** | Sets, history, sync, sharing |
| **Game** | Build variants and patches |
| **Diagnose** | Log reader plus desync triage |
| **Settings** | Including the share endpoint and exit-capture mode |

Six items, and the first one is new and is the point of the application.

#### Proposal B — sessions

A named triple: profile + save set + build + patch set + who is playing.
*"Co-op Tuesday"*, *"Solo modded"*. One click restores all of it.

This is what you are actually doing when you switch anything, and it is the
natural home for the pairing the launch guard enforces. `SaveSet` already carries
`Players` and `ModProfile`, so a session is largely that record promoted to a
first-class thing.

The risk is real and worth stating: a session can easily become a second, worse
copy of a save set. Keep it a **pointer triple**, never a copy — it names things,
it does not contain them.

### Smaller wins, each cheap

1. **The status bar gains the save set, and a mismatch dot.** It already carries
   profile, build folder, what the launcher will start, and whether Isaac is
   running. The pairing is the missing fourth, and a coloured dot is the entire
   launch guard at a glance.
2. **Blockers become a checklist with the fix attached.**
   `SaveSwapPreconditions.Blockers` is a list of sentences today. Each one either
   has an action — Cloud on → open Steam's properties; Steam running → close it —
   or has none, like Isaac running, where the honest answer is "wait". Rendering
   them as rows with the button next to the reason turns reading into pressing.
3. **One severity language, used everywhere.** The code has a consistent ladder —
   block, recommend, warn — and the UI renders it differently in each place.
   Three chip styles, applied across saves, patches, the launch guard and share
   import, would make the app legible at a glance.
4. **Empty states that answer the documented question.** `CLAUDE.md` records that
   "I subscribed and the tab is empty" has two causes the acf cannot distinguish.
   The empty state should ask the helper for Steam's own count and say which one
   it is, instead of showing nothing and letting the user guess.
5. **Diff what actually loaded, not what should have.** `LogReaderService`
   already parses `LOADED MOD` lines; the manifest already says what should be
   there. Comparing the two catches Steam re-materialising a mod into the active
   profile — a documented failure mode — for almost no new code. This is the best
   value-per-line item on this list.
6. **A post-run summary.** The exit watcher is already re-reading state when the
   game closes. Show what that run was: version, mods loaded, error count,
   whether a desync table appeared. It pairs naturally with exit capture and
   costs one card.
7. **Progress and cancel on the long operations.** The resubscribe cycle and a
   share import run for minutes. `LibraryUpdateRunner` exists; the surface for
   watching it and stopping it does not.
8. **Drag and drop.** A `.ipmpack` or `.ipmsave` dropped on the window imports.
   A mod folder dropped on the library adds it. Cheap, and it matches how people
   already receive these things.
9. **Keyboard.** Ctrl+L to launch, Ctrl+R to refresh, number keys for the rail.

### Features worth considering

- **A desync triage flow.** `CLAUDE.md` has a table mapping log signatures to
  causes, and the app already has every input it needs: versions from the log,
  hashes from the library, the checksum block from the desync table. Walking that
  table interactively — compare versions, compare hashes with your partner,
  compare save checksums — turns a doc into a tool. Highest value of anything
  here, because it is almost entirely assembly of parts that exist.
- **"Ready to play with X".** One button before a co-op session: fetch their
  share, diff the library, run the launch guard, report go or no-go. It is the
  Compare feature plus the guard, in the order you actually need them.
- **Live log tail.** Already in `PLAN.md` and still unbuilt. Read with
  `FileShare.ReadWrite | FileShare.Delete` — the game holds the file open.
- **A Workshop update check on startup**, rather than only when asked.
- **"What changed under me".** Hash history per library entry, so a mod that
  changed without you noticing is visible.
- **Session history.** What you played, when, with whom, which set — nearly free
  once the exit watcher exists, and it answers "what were we running last
  Tuesday", which is the question that starts most desync hunts.

### Not worth building

Said out loud so nobody spends a week on one: a command palette, theming, a
plugin system, or an in-app Workshop browser. This tool is sharp because it does
one job; each of those makes it a worse version of something else.

---

## Deliberately not doing

- **No junction on the live save folder.** Steam owns it; `remotecache.vdf`
  tracks every file's sha1. Settled in `CLAUDE.md`.
- **No hosted *sync* service** — as distinct from the share drop above, which is
  new and deliberate. Sharing is a one-shot handover that can fail harmlessly and
  always has a file underneath it. Sync is continuous, holds the only copy of
  something between two machines, and would be the first thing in this project to
  need uptime. Device lanes over a folder you already sync keep that property.
- **No mod bytes uploaded when a Workshop id would do.** The manifest is the
  share; bytes travel only for mods Steam cannot supply.
- **No automatic conflict merge.** Two divergent unlock states cannot be merged
  correctly, and a wrong merge is indistinguishable from a correct one until you
  notice something missing.
- **No re-enabling Steam Cloud.** It is the thing this whole subsystem exists to
  replace, and it would fight every swap.
