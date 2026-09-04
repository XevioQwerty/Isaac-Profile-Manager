# CLAUDE.md

Project instructions and verified domain knowledge for the Isaac Profile Manager.

Everything under "Verified facts" was established by reading logs and probing a
real install. It is not in any documentation and is easy to get wrong from first
principles. **Read this before changing launch, save, or mod-toggle behaviour.**

---

## What this project is

A Windows tool for switching between named Binding of Isaac mod profiles by
re-pointing the game's `mods\` directory with a directory junction. It also
manages save sets, the REPENTOGON build selection, and a few related toggles.

It exists because Repentance+ online co-op requires every player to have
byte-identical mods, and Isaac gives you no way to manage that.

Current state: C#/WPF app in `src/`, plus the original PowerShell implementation
which is retained for command-line and shortcut switching. Both read and write
the same `isaac-profiles.json` at `ConfigVersion 3` — **do not bump that version
without updating `Assert-Config` in `IsaacProfiles.ps1`**, which refuses to run
below 3. New keys are added additively; both sides preserve keys they do not
recognise. See `PLAN.md` for what is built and what remains, and
`docs/multi-device.md` for the current piece of work: a second machine, save
sets that travel, a save set that insists on the profile it was made with, and
in-app sharing by link.

```
src/IsaacProfileManager.Core/   services, models, config store — all the risky IO
src/IsaacProfileManager/        WPF shell; ViewModels do no IO
tests/IsaacProfileManager.Tests/  filesystem tests, temp dirs only
```

Run the tests before touching anything under `Services/`:
`dotnet test` from the repository root.

---

## Verified facts

### Mod enable/disable

- A disabled mod has an empty file `disable.it` **inside its own folder**
  (`mods\<Mod Name>\disable.it`), not in `mods\`. Presence is the whole signal.
- The REPENTOGON launcher **deletes** `disable.it` when enabling. Same mechanism
  as the in-game menu. Confirmed by observation.
- The launcher config at `Documents\My Games\repentogon_launcher.ini` contains
  **no mod state** — only paths and launch settings.
- Isaac treats **every subfolder of `mods\` as a candidate mod**. Keep sync and
  VCS metadata above the junction target.
- **Isaac loads mods through per-mod junctions**, including two hops: `mods\` is
  a junction to a profile folder, and a subfolder of that profile is itself a
  junction to a shared library. Verified 2026-08-16 with a probe mod — `log.txt`
  showed `LOADED MOD .../mods/ipm-junction-test/content/` and the mod's
  `Isaac.DebugString` fired. This is what makes a shared library with
  junction-built profiles viable, instead of a full copy per profile.
- Replacing a mod folder discards its `disable.it`, silently re-enabling it.

### Builds and launching

- REPENTOGON runs **v1.9.7.12.J273**. Retail is newer (J460 observed 2026-08).
- `<GameDir>\Repentogon\isaac-ng.exe` **refuses to be launched directly** —
  it shows "This exe should only be launched using the REPENTOGONLauncher" and
  exits. Always go through the launcher.
- The launcher is invoked as
  `REPENTOGONLauncher.exe --isaac="<vanilla isaac-ng.exe>"`.
  Note it takes the **vanilla** exe path and resolves the Repentogon build itself.
- `[Shared] LaunchMode` in `repentogon_launcher.ini` selects the build:
  **`1` = REPENTOGON, `0` = vanilla** (vanilla then gets `--repentogonoff`).
  The launcher rewrites this file on exit, so never treat a value written by
  this tool as durable.
- **The launcher must not live inside the game directory.** Official docs warn
  against it, and specifically against a folder named `repentogon` there — that
  name belongs to the downgraded build. (The reference install ignores this and
  keeps the launcher at `<GameDir>\REPENTOGONLauncher\` anyway, which works. Do
  not assume the launcher is outside the game dir when searching for it.)
- `<GameDir>\Repentogon\` — the folder the launcher loads the downgraded build
  from — **can itself be a junction**, and swapping it swaps the whole build.
  Verified on the reference install, where it points into `<GameDir>\~\`, a
  build root holding one complete build per subfolder. This is the same
  indirection the mod profiles use: no file in the game directory is modified,
  and a switch is instant. `BuildVariantService` implements it.
- **Reinstalling REPENTOGON replaces that junction with a real folder.**
  Observed 2026-08-16: after a REPENTOGON reinstall, `Repentogon\` was no longer
  a reparse point but a real 94-file, 1 GB directory — a fresh extract of the
  same build (zero files present there and absent from `~\Vanilla`, which held
  only 3 extra scratch files: `repentogon.log`, `savedatapath.txt`,
  `sig_*.log`). An installer writing a real directory over a link is expected;
  **merely launching the game was not shown to do this** — an earlier note here
  claimed it was, which was a wrong inference from the same evidence.
- Consequence either way: **never cache the build link state.** Re-read it on
  every refresh. `BuildVariantService.GetStatus` reports a real folder as
  `RealFolder` and `Initialize` refuses it, because a baseline variant already
  exists and guessing which build is current would be a desync. A "re-link,
  discarding the redundant copy" path is still worth having, since a reinstall
  is a normal thing to do.
- Verify a launch by reading `Documents\My Games\Binding of Isaac Repentance+\log.txt`:
  the `Command Line:` block and `Game Version:` line say exactly what ran.

### Saves

- Save structures differ between J273 and current retail. Loading a
  REPENTOGON-era save on the newer build **can obliterate all achievements**.
  Treat cross-build save loading as data destruction, not a warning case.
- `savedatapath.txt` in the game root is **informational only** — the file says
  so in its own first line: *"This file is purely informational. Changing it
  will have no effect on saving or loading data."* Verified on a Steam install
  2026-08-14. Repointing it does nothing; **do not build on it.**
- **The live saves are in Steam Cloud userdata, not `Documents\My Games`.**
  Verified 2026-08-17 on the reference install:
  `<Steam>\userdata\<accountid>\250900\remote\` holds
  `rep+persistentgamedata<N>.dat` (vanilla) and
  `rgon_steam_persistentgamedata<N>.dat` (REPENTOGON). **Two earlier notes here
  said the save folder was `Documents\My Games\Binding of Isaac Repentance+\`
  and that junctioning it was the mechanism — both were wrong.** That folder
  holds only `log.txt`, `options.ini`, REPENTOGON's `Repentogon\` settings
  subfolder, and REPENTOGON's own dated `save_backups\`. No live save is in it.
- The two builds are therefore separated **by filename inside one folder**, not
  by folder. A save set is a handful of ~5 KB files, so swapping sets is a file
  copy — **do not junction `remote\`**, Steam Cloud owns it (see below).
- `remote\remotecache.vdf` is Steam Cloud's manifest: per file, a sha1, size and
  `syncstate`. Steam validates the folder against it, so replacing files behind
  Steam's back is what triggers a cloud conflict.
- **Removing files from `remote\` does not trigger a cloud restore.** Probed
  2026-08-17: every save file was moved out, the game launched, and only a fresh
  4,068-byte `rep+persistentgamedata1.dat` appeared — not the 5,268-byte original.
  `remotecache.vdf` was rewritten from local disk, dropping every `rgon_*` entry
  it had listed. Steam treated the local folder as the truth.
  **Caveat: this install is not a normal Steam purchase, so Cloud may simply be
  inert for this app here.** Do not assume the same on a legitimately owned copy
  — keep the "back up before swapping" and "close Steam first" rules regardless,
  since they cost nothing and are the only defence if Cloud is live.
- **A file dropped into `remote\` is not necessarily visible to the game.**
  Found 2026-09-04 syncing a run between two machines: the desktop's live
  folder held `rep+gamestate2.dat` (right bytes, right size) and the game
  logged `SteamCloud could not find or open rep+gamestate2.dat`. The game
  reads saves through Steam's Remote Storage API, which answers from
  `remotecache.vdf`, and that entry carried `"persiststate" "2"` — Steam's
  mark for a file the game had deleted (the desktop's own earlier slot-2 run
  had ended). Steam re-indexed the new bytes (size and sha updated) but kept
  the deleted flag, so the API said the file did not exist. With Steam
  exited, setting `persiststate` to `0` and relaunching made "Continue"
  appear. So: **`persiststate 0` = live, `2` = deleted-by-the-game**, and
  a copy into the folder cannot clear it. The fix that holds is to write
  save files through `ISteamRemoteStorage::FileWrite` from the 32-bit
  helper, which is what the game itself does; a file copy stays as the
  fallback when Steam is not running. **Verified the same day**: the
  helper's `cloud-replace` wrote a probe file and Steam's manifest listed it
  with `persiststate 0` at once, with Steam running and Cloud off for the
  app; `FileDelete` removed files and their entries. `cloud-list` reports
  what the game will see (`persisted` per file — the stray `set.json` showed
  `false`, the saves `true`). Consequence for the old advice: **swap saves
  with Steam running**, not closed, so the write goes through the API. The
  accessor is `SteamAPI_SteamRemoteStorage_v016` on this build.
- **Steam's per-game Cloud toggle is readable**, at
  `<Steam>\userdata\<accountid>\7\remote\sharedconfig.vdf`, path
  `UserRoamingConfigStore\Software\Valve\Steam\apps\<appid>\cloudenabled`.
  Verified 2026-08-17. The key is **absent unless the user has touched the
  toggle**, and the default is on — so treat anything other than an explicit
  `"0"` as enabled. `steam://gameproperties/<appid>` opens the dialog holding it.
- **That setting lags while Steam is running.** Observed 2026-08-17: the toggle
  was switched on and then off in Steam's UI, and `sharedconfig.vdf` was left
  reading `"cloudenabled" "1"` — it had recorded the *on* click and not the
  *off*. Steam holds the value in memory and flushes on exit. So a reading taken
  while Steam runs can be a toggle behind the dialog; report when the file was
  last written and offer a re-check rather than trusting it blindly.
- Navigate that path explicitly rather than searching the tree for `apps`:
  `localconfig.vdf` is ~400 KB and contains more than one node by that name, so
  a blind search returns the wrong one. Same file holds Steam's own view of the
  folder at `...\apps\<appid>\cloud\last_sync_state` (observed `changeslocally`
  right after save files were removed — Steam noticed, but did not restore).
- Steam's install root: `HKCU\Software\Valve\Steam\SteamPath` (written
  lowercased with forward slashes) or `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`.
  There may be several `userdata\<accountid>` folders; pick the one that
  actually contains the app.
- `remotecache.vdf` also lists `rep+gamestate1.dat` (~75 KB) — the in-progress
  run, which is transient and absent between runs. A save set should carry it
  when present: restoring unlock data without the matching run state, or vice
  versa, is a mismatch.
- `remote\rgon_savesyncstatus.json` shows REPENTOGON **actively syncs saves
  between the two builds** — observed
  `{"AutoSyncingEnabled":true,"Checksums":{"REPENTOGON.1":...,"Vanilla.1":...}}`.
  So REPENTOGON does *not* isolate the builds' saves the way an earlier note
  claimed; it deliberately copies between them. Any save switcher must carry
  this file with the set or REPENTOGON will reconcile the new save against stale
  checksums. **This is the concrete mechanism behind the achievement-loss risk.**
- **How that sync actually decides — probed 2026-09-04, six launches on
  throwaway slots 2 and 3.** REPENTOGON runs `SaveSyncing` for every slot on
  launch and again on exit, and writes its decisions to
  `<GameDir>\Repentogon\repentogon.log` (truncated per session — read it right
  after the run). Per slot it reads both twins (`rep+…` and `rgon_steam_…`),
  computes a checksum and a save **Counter** for each, and compares the
  checksums with `rgon_savesyncstatus.json`:
  - Neither twin differs from the status → `No synchronization required`.
  - **Exactly one differs → that twin is copied chunk-by-chunk over the
    other**, silently. Observed: an older REPENTOGON file restored beside a
    newer vanilla twin was overwritten from the vanilla twin on the next
    launch. *This is the cross-device restore hazard*: a set that carries one
    build's file onto a machine holding the other build's newer twin is undone
    on launch. `Activate` is safe only because it deletes every live save file
    before copying the set in — keep that. A lane must carry both twins and
    the status file together.
  - **Both differ → `Detected potential external REPENTOGON save file
    modification. Performing a full synchronization...`** and it merges both
    ways. Never a prompt, in any case.
  - No vanilla twin → nothing for that slot. REPENTOGON *creates* REPENTOGON
    twins from existing vanilla files on launch (slots 1 and 3 appeared the
    moment it first ran) but never creates a vanilla twin from a REPENTOGON
    file.
  Two side facts from the same runs: the game **loads and re-saves slot 1 on
  every launch** whatever slot you open (its Counter goes up, so its hash
  changes without any play — drift detection must expect that), and
  `rep+gamestate<N>.dat` carries a checksum bound to its
  `persistentgamedata<N>.dat`; put one back without the other and `log.txt`
  says `[warn] GameState File : Checksum invalid!` and the run is discarded.
  The Saves tab's "carry the run file with the set" rule is therefore load
  bearing, not tidy.
- **The save file's layout, as REPENTOGON logs it.** Header `ISAACNGSAVE09R`,
  then eleven chunks in order: Achievements (1) @32, Event Counters (2),
  Level Counters (3), Collectibles (4), Minibosses (5), Bosses (6),
  Challenges (7), Cutscenes (8), Settings (9), Special Seeds (10),
  Bestiary (11); each with `sizeWritten` and `numElements`, then a `Counter`
  and a `Checksum`. Enough to build a read-only save viewer against, and
  matches what Zamiell's `isaac-save-viewer` parses.
- What REPENTOGON does *not* solve is that there are still only **three slots**.
  Separating saves per person + modpack + build is the actual reason to build a
  save switcher.
- Save state feeds unlock state, which feeds item pool composition, which feeds
  RNG. Mismatched saves between players desync within seconds.
- **`GameBuild` is not the same thing as the build number.** It separates
  vanilla from REPENTOGON and cannot separate retail J460 from J273 — and two of
  your own machines drift apart the moment one updates and the other does not.
  Once saves cross a machine boundary the recorded `Game Version:` from
  `log.txt` becomes a second, independent check. The existing build block is
  necessary and no longer sufficient.
- **`.saves\` is machine-local and stays in `.stignore`.** What travels between
  your own devices is `.savesync\<device-id>\`, a lane each device alone writes,
  so a sync client can never produce a conflict on a save file. Reconciling
  lanes into `.saves\` is the app's job, not the sync client's — that is where
  "Isaac is closed" can be checked. See `docs/multi-device.md`.
- **The save folder is not the whole save.** Verified 2026-09-03 on the
  reference install, two more per-slot stores exist outside it:
  - **Mod save data** at `<GameDir>\data\<mod folder name>\save<N>.dat` — 11
    mod folders, the largest 33 KB. The folder is named after the mod's folder
    under `mods\`, and on this install those names matched the suffix-free
    library names exactly. Renaming a mod strands its data. This is state
    produced alongside `persistentgamedata<N>.dat` (mod unlocks feed pools,
    which feed RNG), and until 2.0 no save set carried it.
  - **REPENTOGON's modded unlock state** at
    `Documents\My Games\Binding of Isaac Repentance+\Repentogon\achievements<N>.json`
    and `completionmarks<N>.json`, keyed by Workshop id inside. Same folder
    holds `ItemPoolManager\`, `EntitySaveStateManagement\` and
    `VirtualRoomSetManager\` subfolders whose contents are not yet understood.
  `SlotStateCarrier` captures both into the set (`moddata\`, `repentogon\`) and
  restores them on activation — only for a set that captured them, so a 1.x set
  never clears live mod data on activation. `ModDataCaptured` /
  `RepentogonStateCaptured` on the set are the flags that say so.
- **Repentance+ writes its own desync dumps and session snapshots.** Found
  2026-09-03 under `Documents\My Games\Binding of Isaac Repentance+\online_logs\`:
  `desyncs\<MM_dd_yyyy__HH_mm_ss>__<player>\` holds `desync_diff.txt` (player
  ids, the checksum table, then per-entity `Type Mismatch!` / `Missing!` rows),
  `desync_framestate.txt`, a 350 KB `desync_rng_history.txt` with call stacks
  per RNG roll, a screenshot, and `desync_shared_save.dat`;
  `sessions\<stamp>\` holds a copy of `log.txt` plus
  `persistentgamedata<N>_begin.dat`/`_end.dat` and `sharedsave_begin/end.dat`.
  Two consequences: the Diagnose screen should read these rather than only the
  checksum table in `log.txt`, and **online play has a shared save** whose role
  in unlock-state sync is unverified (see below).
- **A stray `set.json` was found in `remote\`** — residue of the bug the
  `RestoreBackup` comment describes. Harmless to the game (it only reads its
  own filenames), but worth a one-time cleanup.

### Steam Workshop

- Workshop content downloads to
  `<Library>\steamapps\workshop\content\250900\<id>\`, the standard location —
  **an earlier note here claimed it went straight to `mods\`; that was wrong.**
  Verified 2026-08-16: 39 item folders, 1.8 GB, matching `SizeOnDisk` in the acf.
- The folders in `mods\` are **materialized copies** of those, named
  `<metadata.xml directory>_<workshopid>`. Verified against four items; the
  odd-looking `golden-items_3338467278_3338495603` is a mod whose own
  `<directory>` already ends in an id. Note `<directory>` is not `<name>` and
  not the folder name in `content\` — read it from the item's `metadata.xml`.
- **This is the overwrite mechanism.** A game update refreshes
  `content\250900\`, the copies are re-laid into `mods\`, and because `mods\` is
  a junction they land in whichever profile is active. Nothing warns you.
- Isaac loads any subfolder of `mods\` regardless of the `_<id>` suffix, so a
  suffix-free copy is a plain local mod that Steam has no claim on. Confirmed by
  a hand-installed External Item Descriptions (`<id>836319872`, absent from the
  acf) loading fine beside the subscribed set.
- Renaming a folder to strip the suffix does **not** by itself detach it: the
  subscription is still live, so the next materialization recreates
  `<directory>_<id>` alongside the renamed copy and the mod loads twice.
  Detaching requires unsubscribing as well, and the local copy must exist first.
- Steam's record lives at
  `<Library>\steamapps\workshop\appworkshop_250900.acf`, in two sections that
  list the same ids: `WorkshopItemsInstalled` and `WorkshopItemDetails`. Count
  ids from one section only — counting raw id matches double-counts.
- **The re-download trigger is a per-profile delta, not the acf.** A subscribed
  item whose folder is absent from the *currently junctioned* profile is
  re-downloaded into that profile. Verified 2026-08-16 on the reference install:
  acf and disk agreed exactly (39 items, no pending updates), yet the vanilla
  profile kept receiving `repentogon_3127536138` because it deliberately
  excluded it. So a profile cannot be used to *subtract* a subscribed mod —
  unsubscribe instead, or keep every profile a superset of the subscriptions.
- **Do not make the acf read-only to fix that.** The endless-retry failure mode
  is caused by precisely this case: Steam downloads a missing folder, cannot
  record it, and retries. Locking is only coherent if you want Steam to stop
  tracking Isaac mods altogether. Unsupported; client updates may reset it.
- Before offering a lock, the check that matters is "subscribed items missing
  from the active profile", and it must be zero. A raw installed-item count does
  not answer that question.
### Keeping detached mods updated

- A library mod is detached from Steam, so it **never receives updates**. The
  cheap way to find out what has moved is the keyless Web API
  `POST https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/`
  with `itemcount=N` and `publishedfileids[i]`. Verified 2026-08-27 against all
  40 Workshop ids in the reference library: it answers for items you are **not**
  subscribed to, needs no API key and no Steam client, and returned 9 stale of
  40. Note `file_size` comes back as a *string* and `time_updated` as a number.
- **`SteamAPI_Init` is not in `steam_api.dll`'s export table.** In the current
  SDK it is a header inline. The exported door for a flat-API consumer is
  `SteamAPI_InitFlat(SteamErrMsg*)` returning `ESteamAPIInitResult` (0 = OK),
  and it fills in Steam's own explanation on failure. Confirmed by parsing the
  PE export directory — grepping the binary for strings finds `SteamAPI_Init`
  and is misleading, because it is a string and not an export.
- Isaac's `steam_api.dll` is **32-bit** (machine `0x14c`), so the x64 app cannot
  load it. Hence `ipm-steam-helper.exe`, a win-x86 process. It loads the game's
  own dll rather than shipping one: that copy is Valve's, byte-identical to the
  `steam_api_o.dll` beside it (both sha1 `e21843f5…`, verified 2026-08-27).
  The flat API is **`__cdecl`** — a stdcall `DllImport` corrupts the x86 stack.
- The accessor is `SteamAPI_SteamUGC_v020` today. It carries the interface
  version, so probe a list of names rather than binding one.
- Never call `SteamAPI_RestartAppIfNecessary` in the helper — that is the call
  that would make Steam launch Isaac.
- **Subscribing does not materialise anything into `mods\`.** Verified
  2026-08-27: a full subscribe + download left the active profile at exactly its
  26 folders. Materialisation happens on game launch, which is why an update run
  refuses while Isaac is running and is otherwise safe.
- **Unsubscribing deletes the content within seconds.** Verified in the same
  run: `content\250900\` went back to empty and `WorkshopItemsInstalled` back to
  `{}`. So the copy into the library must happen *between* the subscribe and the
  unsubscribe — hence two helper invocations, not one.
- `GetItemInstallInfo`'s `punTimeStamp` **equals the Workshop's `time_updated`**
  (both `1787287627` for `3734781489`). That is the revision stamp to record, so
  the next check compares revisions instead of guessing from the import date.
- `steam_api.dll` prints a banner (`Setting breakpad minidump AppID`) on
  **stderr** every run. Do not treat stderr as a failure signal; real failures
  come back as an `error` event on stdout.
- **A copy over the top is not an update.** `DirectoryCopier` merges, so files
  the author deleted upstream would survive and the library's bytes would no
  longer match a partner's fresh install — the exact desync the library exists
  to prevent. `UpdateFromContent` moves the old entry to `.backup` and copies
  fresh instead.
### Workshop metadata

- **Preview images do not come from the mod.** Most Workshop items ship no
  thumbnail; the picture is Steam's *store* image, fetched from the CDN via
  `preview_url` in `GetPublishedFileDetails`. `WorkshopPreviewService` does
  this. Any code path that fills the library must call it, or the library
  arrives with a handful of pictures — the share import shipped without it and
  produced exactly that.
- **Descriptions are BBCode.** Authors paste their whole store page into
  `metadata.xml`, so raw they are a wall of `[h2]`, `[b]` and `[url=…]`.
  `BbCode.Strip` removes them for display. It matches only tags Steam actually
  defines: matching any bracketed word eats prose like `[probably]` and titles
  like `[BETA] …`. `[img]…[/img]` must lose its contents too — they are a URL,
  not a caption — unlike `[url=…]label[/url]`, where the label is the point.
- **The acf lags an unsubscribe.** Steam deletes an item's content folder
  immediately but rewrites `appworkshop_250900.acf` a moment later. In that
  window the acf lists ids whose folders are gone, and with no `metadata.xml`
  to read they render as bare numbers at 0 MB. Filter on
  `WorkshopItem.ContentPresent`; the same check also excludes a subscription
  that has not finished downloading, which cannot be imported either.
- The acf records items Steam has **installed**, not merely subscribed. "I
  subscribed and the tab is empty" therefore has two causes it cannot
  distinguish — ask the helper (`status`) for Steam's own count instead.

### Sharing a set

- `GetCollectionDetails/v1` is **keyless too**, and resolves a collection id to
  its child ids. Verified 2026-08-28 against a live Isaac collection
  (`3775687336`, 29 children). A non-collection id comes back as `result 9` —
  report that rather than returning an empty list, which reads as "empty
  collection" and sends the user the wrong way.
- **A self-contained share code cannot be short — so do not send one.** A
  published file id needs ~34 bits, so 40 ids is ~227 base64 chars at optimal
  packing before a single name or hash. Measured on the reference library:
  42 mods = **3,679 chars** with hashes, **1,309** without (entry names
  dominate, not ids). Roughly 100 chars buys 17 ids and nothing else.
  A Steam collection id is short only because Steam **stores the list** — which
  is the conclusion, not a footnote: store the list and send a link.
  `ShareCodeService` was removed for exactly this reason; see
  `docs/multi-device.md`. The measurement stays recorded so nobody proposes a
  short self-contained code again.
- **A mod profile share is a manifest, not a payload.** `SharedProfile` is entry
  names, Workshop ids and hashes — a few KB — and the recipient refetches the
  mods from Steam. Bytes only ever need to travel for entries with no Workshop
  id, the `ShareItemAction.Unfetchable` case. Anything that proposes uploading
  the library has misread the problem.
- Import must install under **the sender's entry name**, not one derived from
  the downloaded `metadata.xml`. The share's manifest refers to the sender's
  names, and a name that drifts leaves the profile pointing at nothing;
  collision suffixes (`golden-items_3338467278`) are where the two differ.
- `SharedProfile` gained `WorkshopIds` **additively** — schema stayed at 1, so
  exports made before it are still readable, they just cannot be fetched.

- There is **no `steam://subscribe` handler**. Enumerated every `steam://` URL in
  `steamui/` and grepped `steamclient64.dll`, `steam.exe` and `SteamUI.dll`:
  `SubscribeWorkshopItem` exists as an internal client call, but nothing is
  exposed to the protocol handler. Subscribing is reachable only through the
  Steamworks API, the Community web UI, or a collection's "Subscribe to all".

- The workshop item `3127536138` ("REPENTOGON: Isaac Script Extender") is a
  **nag screen, not the build**. Its `main.lua` probes
  `../../../Repentogon/resources-repentogon/gfx/ui/changelog.anm2`; if the real
  build is installed it returns immediately, otherwise it hides the HUD and
  draws an install warning. Safe to unsubscribe when REPENTOGON is installed via
  the launcher.

### Hosting a share drop

**Provenance note:** unlike everything above, these come from vendor
documentation rather than from probing this install. They were checked
2026-09-01 and vendors change limits — re-check before relying on a number.

- **Cloudflare's proxy caps request bodies at 100 MB** on the Free and Pro
  plans (200 MB Business, 500 MB Enterprise). Anything uploaded *through* a
  Worker on a proxied hostname inherits that cap. **Presign and upload straight
  to R2 instead** — R2 objects go to 5 TB. This is the difference between a
  design that works and one that fails the first time someone shares a large
  off-Workshop mod.
- **R2's free tier**: 10 GB stored, 1M Class A and 10M Class B operations a
  month, and **no egress charge at all**. At a few KB per share this is
  effectively unmetered for this use.
- **R2 multipart requires every part to be exactly the same size except the
  last.** S3 and MinIO allow varying part sizes; an S3 SDK left on its defaults
  produces parts R2 rejects.
- **Object expiry is a bucket lifecycle rule**, not something to schedule.
- **R2 must be enabled per account in the dashboard before wrangler can
  create a bucket**, and enabling it asks for a payment method even for the
  free tier. Observed 2026-09-04: `wrangler r2 bucket create` fails with
  `code: 10042 Please enable R2 through the Cloudflare Dashboard` until then.
  Workers themselves need nothing of the sort: the save-sync Worker deployed
  to `https://ipm-save-sync.<account>.workers.dev` with no custom domain.
- **Save sync lanes live behind `cloud/save-sync-worker`.** The sync key is
  the bearer token and, hashed, the namespace; the Worker refuses anything
  that is not a zip or a schema-1 manifest naming its own path, and refuses a
  manifest whose pack has not arrived. Verified with curl against the live
  deployment: 401 without a key, 409 for manifest-before-pack, 400 for a
  non-zip, and an empty listing under a different key.
- **Oracle reclaims idle Always Free compute.** An instance counts as idle when,
  across a 7-day window, the 95th percentile of CPU, network *and* memory are
  all below 20%. A share endpoint serving a few KB a week meets that definition,
  so the VPS is the wrong home for one unless something keeps it busy. It is a
  fine home for a *stateful* service behind a Cloudflare Tunnel later.

### Desync diagnosis

The host's `log.txt` prints a per-player checksum table. Signatures:

| Log signature | Cause |
|---|---|
| One player's row differs | That machine is the one to investigate |
| Save checksums differ in the recovery block | Unlock/save state mismatch |
| Entity types or positions differ | Mod content mismatch — files, versions, or load order |
| `No Entity Desyncs Detected` but global RNG differs | Hidden RNG roll; usually a mod adding gameplay entities |
| Desync at frame 1 | Divergence before input; content or save state, never gameplay |

---

## Conventions

### Filesystem safety

These rules exist because violating them destroys user data:

- Delete junctions with `Directory.Delete(path, recursive: false)`. **Never**
  a recursive delete on a path that might be a reparse point.
- Before touching `mods\`, check `FileAttributes.ReparsePoint`. If it's a real
  folder, refuse and tell the user — never delete it.
- Rename rather than delete when replacing something the user owns
  (`mods.backup-<timestamp>`).
- Back up any file before making it read-only or rewriting it.
- Check whether Isaac is running before touching saves.

### Config

- Every config carries a `SchemaVersion`. On mismatch, **refuse to act** and
  tell the user to re-run setup. Silently falling back to a default once caused
  the tool to launch the wrong build, which is a desync in an online session.
- Config holds absolute machine-local paths, so it is gitignored.

### PowerShell (existing implementation)

- **Never `2>&1` a native exe in a script.** PowerShell 5.1 wraps each stderr
  line in an ErrorRecord, which `$ErrorActionPreference = 'Stop'` turns into a
  terminating error. `steam_api.dll` prints a breakpad banner to stderr on every
  run, so this made `Test.ps1` report the helper as broken when it was fine.

- Don't name variables `$args`, `$Input`, `$Profile`, `$Host` — automatic vars.
- Use `[Environment]::GetFolderPath('MyDocuments')`, not
  `$env:USERPROFILE\Documents` — OneDrive redirection breaks the latter.
- WinForms dialogs need STA; `pwsh` 7 is MTA by default. Check before using.
- Write files with explicit UTF-8.

---

## Things that are unverified

Flag these rather than assuming:

- The exact format and location of the launcher's own `.txt` mod profiles.
- Whether Steam rewrites `remote\` from the cloud on next start after files are
  swapped underneath it, and whether disabling Steam Cloud for the app stops
  that. **Test with a throwaway save before shipping save switching.**
- ~~What REPENTOGON does when `rgon_savesyncstatus.json` checksums do not match
  the `.dat` files beside it.~~ **Resolved 2026-09-04** — see "How that sync
  actually decides" under Saves. Reconciles silently; the changed twin wins,
  both changed merges. Sync must carry both twins and the status file.
- Whether a non-`1` vanilla slot (`rep+persistentgamedata2/3.dat`) is ever
  created; only slot 1 exists on the reference install.
- ~~Whether a second machine on the same Steam account gets the same
  `userdata\<accountid>` number under a different root.~~ **Confirmed
  2026-09-04** on the laptop: same `351019201`, same default Steam root, Cloud
  explicitly `"0"` there too, one 4,068-byte `rep+persistentgamedata1.dat`
  from 2026-08-17. The laptop runs the same non-Steam copy, REPENTOGON and
  vanilla. `SaveLocationService` resolves per-machine either way.
- Whether the game's `log.txt` `Game Version:` line is reliably present at the
  moment a save is captured. Capture records it, and a missing value has to
  degrade to a warning rather than a block.
- **Where the game saves when Steam is not running.** Found 2026-09-03: a
  `rep+persistentgamedata1.dat` (6,828 bytes, written 2026-08-29 06:17) sitting
  in `Documents\My Games\Binding of Isaac Repentance+\` beside `log.txt`. Its
  hash matches nothing the app ever captured or backed up, and the userdata
  copy was written later (2026-08-31) with Cloud already off — so Cloud-off
  alone does not move the save. Zamiell's FAQ says the Documents folder is the
  non-Steam location. **The game names its save transport on every launch**:
  `Loading PersistentGameData from Steam Cloud: rep+persistentgamedata1.dat.`
  and `Saving PersistentGameData to Steam Cloud: …` — and the archived logs
  for the sessions bracketing 06:17 (launched 06:15:41 and 06:17:41) both say
  Steam Cloud, as does every one of the 40 archived logs. So the game did not
  write that file. The likelier author is **this app**: `SaveLocationService`
  can resolve the live folder to the path in `savedatapath.txt` (which names
  the Documents folder) when `remotecache.vdf` lists no saves — exactly the
  state after Steam rewrote it on 08-17 — and an activation then copies a set
  there. `LogReaderService.SaveTransport` now parses the line, so the app can
  show what the game actually used.
  **Probed 2026-09-03: an offline launch is not reachable on this install.**
  With the Steam client fully exited, double-clicking `isaac-ng.exe` started
  Steam, which then ran the game through its own launch options
  (REPENTOGONLauncher, `--repentogonoff`, J460). Nothing under Documents or
  userdata changed apart from `options.ini`, `savedatapath.txt` and `log.txt`;
  Steam touched `remotecache.vdf` without changing it. So "close Steam" can
  never strand a save in Documents, and the stray file is the app's doing.
  Two smaller facts from the same run: the game rewrites `savedatapath.txt`
  on every start, still naming the Documents folder it does not use; and the
  `PersistentGameData from …` line only appears once a slot is opened — that
  run ended with Alt+F4 at the main menu, which logged the version but no
  save transport, as expected. Whether an *offline* launch is possible at all
  (say with `steam_appid.txt` beside the exe) was not tested and is not
  something this app should encourage.
- **Whether Repentance+ online play distributes one unlock state to the
  lobby** via the `sharedsave_*.dat` seen in `online_logs\sessions\`. If it
  does, the "save state mismatch" desync cause may only apply to local co-op
  and pre-Rep+ builds. Needs a second player to probe.

Resolved since first written — see "Verified facts" above: `savedatapath.txt`
(informational only), the real save location and filenames, REPENTOGON's
cross-build save syncing, per-mod junction loading, and the build folders.

---

## Working style for this project

- Prefer indirection (junctions, path files) over copying or moving user data.
- When a mechanism is undocumented, probe the real install and record the result
  here rather than reasoning about what it probably does. Several confident
  guesses in this project's history turned out to be wrong.
- Ship features independently. The mod profile switcher is useful alone.
