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
recognise. See `PLAN.md` for what is built and what remains.

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
- What REPENTOGON does *not* solve is that there are still only **three slots**.
  Separating saves per person + modpack + build is the actual reason to build a
  save switcher.
- Save state feeds unlock state, which feeds item pool composition, which feeds
  RNG. Mismatched saves between players desync within seconds.

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
- **A self-contained share code cannot be short.** A published file id needs
  ~34 bits, so 40 ids is ~227 base64 chars at optimal packing before a single
  name or hash. Measured on the reference library: 42 mods = **3,679 chars**
  with hashes, **1,309** without (entry names dominate, not ids). Roughly 100
  chars buys 17 ids and nothing else. A Steam collection id is short only
  because Steam stores the list. Do not promise a short self-contained code.
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
- What REPENTOGON does when `rgon_savesyncstatus.json` checksums do not match
  the `.dat` files beside it — reconcile silently, or prompt.
- Whether a non-`1` vanilla slot (`rep+persistentgamedata2/3.dat`) is ever
  created; only slot 1 exists on the reference install.

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
