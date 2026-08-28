# Isaac Profile Manager — GUI rewrite plan

## Status

| Step | State |
|---|---|
| 1. Services + config store + tests | **Done** |
| 2. Mod profiles tab | **Done** |
| 3. Build variant switcher (was "online-fix toggle") | **Done** |
| 4. Workshop import, shared library, manifest profiles | **Done** |
| 5. Launch button + Steam/direct launch setting | **Done** |
| 6. Launch options tab (StealthMode, HideWindow) | Not started — `LauncherIniService` already supports it |
| 7. Workshop `.acf` lock | Superseded — with nothing subscribed there is nothing to lock |
| 8. Debug tab | **Done** — blueprint below kept as the record of why |
| 9. Save sets | **Done** — capture, backup, restore, gated activation |

| 10. Save set editing, workshop links | **Done** |
| 11. Library hashes, profile export/compare | **Done** |
| 12. Desync table, launch-options check, activate &amp; launch | **Done** |

| 13. In-app first-run setup | **Done** — `Setup.bat` is now optional |
| 14. README revamp + screenshots | **Done** |
| 15. Profile discovery, import, activate-materialises | **Done** |
| 16. Backup retention, log session archiving | **Done** |
| 17. Workshop update check + resubscribe cycle | **Done** |
| 18. Bulk unsubscribe, share codes, collection import | **Done** |
| 19. Steam launch options, unified import, tab refresh | **Done** |

Two conventions worth keeping:

- **Backups are not all copies.** Removing a library mod, replacing a profile
  folder with links and deleting a save set all *move* the original into
  `.backup`, so those folders can be the only remaining instance.
  `BackupService` classifies them and retention never prunes them — only true
  copies, and only when both older than the age floor and beyond the keep count.
- **Machine paths must be injectable.** `SteamCloudService`, `BackupService` and
  `LogArchiveService` all take an override for their LocalAppData folder.
  Without it the tests scanned and deleted real machine state, which hid
  failures and littered 75 files into `%LOCALAPPDATA%`.

A third convention arrived with step 17:

- **The app now ships two executables.** `ipm-steam-helper.exe` is win-x86
  because Isaac's `steam_api.dll` is 32-bit; it loads the game's own copy and
  speaks JSON lines over stdout. It is published by a target in the app's csproj
  and added to `ResolvedFileToPublish` *inside* that target — a static `Content`
  item silently publishes nothing on a clean tree, because the file does not
  exist when items are evaluated. `ExcludeFromSingleFile` keeps it a loose exe;
  a 64-bit bundle cannot run a 32-bit payload.

Two conventions from step 19:

- **One version, in `Directory.Build.props`.** The app and the Steam helper ship
  as a pair and both inherit it; `Package.ps1` refuses to build a release whose
  executables disagree. A stale helper beside a fresh app is otherwise
  undetectable from outside.
- **`Test.ps1` is the pre-release check.** Default runs the build and unit
  tests; `-Live` also exercises Steam's public API and the 32-bit helper against
  the real client, read-only. Those two catch what unit tests cannot — an
  endpoint changing shape, or the helper failing to bind after a game update.

Still not built: live log tail via `FileSystemWatcher`.

Built and verified against a real install: `dotnet test` is green (53 tests), and
the app reads the live junction rather than trusting the config.

The PowerShell script stays as the CLI and first-run wizard. Both tools share
`isaac-profiles.json` at `ConfigVersion 3`.

## Recommended stack

**C# / .NET 8 / WPF**, published as a self-contained single-file exe.

| Option | Verdict |
|---|---|
| **C# + WPF (.NET 8)** | **Recommended.** Native Windows, real data binding for the list/detail UI, one-file distribution, excellent tooling |
| C# + WinForms | Fine and simpler. Choose it if WPF's XAML/binding ceremony slows you down more than it helps |
| PowerShell + WinForms | What we have. Dies at this feature count — no type safety, no real state model, painful layout |
| Python + PySide/tkinter | Users must install Python, or you ship PyInstaller bundles that antivirus flags. Bad fit for a gaming audience |
| Electron / Tauri | Massively oversized for a folder-junction tool |

### Publishing

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

~70 MB, no runtime install, double-click and it runs. Fine for a GitHub release.

Framework-dependent (~200 KB) is the alternative but requires users to install the
.NET runtime — exactly the friction we're trying to avoid.

**Do not require administrator.** Junction creation doesn't need it. Requesting
elevation would make the app look like malware to a modding audience.

---

## Architecture

As built. Core is a separate assembly with no WPF reference, so the tests never
need a UI thread and a ViewModel cannot reach the filesystem by accident.

```
src/IsaacProfileManager.Core/
  Models/
    AppConfig.cs                   shared with the PowerShell script
    ModProfile.cs
  Services/
    JunctionService.cs             create/remove/inspect reparse points
    NativeMethods.cs               FSCTL_SET_REPARSE_POINT — junctions, not symlinks
    ModProfileService.cs           profile CRUD, disable.it sweeping
    BuildVariantService.cs         swaps what Repentogon\ contains
    LauncherIniService.cs          read/write repentogon_launcher.ini
    GameDetectionService.cs        find exe, launcher, Steam library
    GameProcessService.cs          is Isaac running
    DirectoryCopier.cs             recursive copy that refuses to follow links
  Storage/
    ConfigStore.cs                 JSON, atomic writes, schema refusal
src/IsaacProfileManager/
  App.xaml  MainWindow.xaml        tabbed shell + status bar
  Views/    ModProfilesView  BuildVariantsView  SettingsView
  ViewModels/                      one per view + MainViewModel
tests/IsaacProfileManager.Tests/
```

Still to add: `SaveProfileService.cs`, `WorkshopService.cs`, `LogReaderService.cs`.

Two rules that held up and should keep holding:

- Services hold all the risky filesystem logic and are independently testable.
  ViewModels do no IO.
- Junctions are created through `DeviceIoControl`, not
  `Directory.CreateSymbolicLink` — a symlink needs elevation, a junction does not.

---

## Data model

```csharp
class AppConfig {
    int      SchemaVersion;
    string   IsaacExePath;
    string   GameDir;
    string   LauncherExePath;      // outside the game dir
    string   ProfileRoot;          // synced folder
    string   SaveRoot;             // where save sets live
    string   ActiveModProfile;
    string   ActiveSaveProfile;
}

class ModProfile {
    string   Name;
    string   FolderName;
    string   Description;
    bool     UseRepentogon;
    string[] Players;              // who you play this with
    DateTime LastUsed;
}

class SaveProfile {
    string   Name;
    string   FolderName;
    GameBuild Build;               // Repentogon | Vanilla — REQUIRED
    string   ModProfileName;       // which mod set produced it
    string[] Players;
    string   Notes;
    int      SlotCount;            // how many of 1-3 are populated
    DateTime LastUsed;
}
```

`ModProfile.Description` and `SaveProfile.Notes` are the things you actually
wanted — "what was I running, who was I playing with."

---

## Features

### 1. Mod profiles

Straight port of the working PowerShell logic. List on the left, detail on the
right: name, description, mod count, players, REPENTOGON on/off, Activate button.

Junction rules that must survive the port:

- Delete links with `Directory.Delete(path, recursive: false)` — **never** a
  recursive delete. A recursive delete aimed at a junction can follow the link
  and wipe the target.
- If `mods\` exists and is *not* a reparse point, refuse and tell the user.
  Never silently delete a real folder.
- Sweep `disable.it` on activate — those markers make a mod present but
  silently off, which looks identical to a failed sync.
- Profile folders live one level *below* the sync root so `.git` and `.stfolder`
  sit above the junction target. Isaac treats every subfolder of `mods\` as a
  candidate mod.

### 2. Save profiles — **highest risk feature in this app**

Save structures differ between REPENTOGON (v1.9.7.12.J273) and current retail.
Loading a REPENTOGON save on the newer build **can destroy every achievement**.

Non-negotiable design rules:

1. Every `SaveProfile` is tagged with the build that produced it.
2. Activating a save set whose `Build` doesn't match the currently selected mod
   profile's build is **blocked**, not warned about. A typed confirmation at
   most, never a plain OK button.
3. Timestamped backup of the current save folder before every switch.
4. Never switch while Isaac is running — check for the process first. The game
   caches save state in memory and writes on exit, so a live swap loses data.

**Mechanism decided.** `savedatapath.txt` is out: verified on a Steam install
2026-08-14, the file states in its own text that it is purely informational and
changing it has no effect. The only indirection available is a junction on
`Documents\My Games\Binding of Isaac Repentance+\`, same pattern as mods.

Copying `.dat` files is the fallback and the worst option. Prefer indirection.

REPENTOGON does **not** make this redundant. It keeps its own state in a
`Repentogon\` subfolder, which separates REPENTOGON saves from retail ones — but
there are still only **three slots**. The real requirement is a save set per
person + modpack + build, because remembering which of slots 1–3 a given group
used is exactly what goes wrong.

Open questions to settle before building:

- Steam Cloud syncs that directory. Does a junction there confuse it?
- `log.txt` and `options.ini` live in the same folder. Do they travel with the
  save set or stay put? A junction takes them along whether you want it or not.

### 3. Launch options

Set `[Shared] LaunchMode` in `Documents\My Games\repentogon_launcher.ini`
(`1` = REPENTOGON, `0` = vanilla) so the build follows the active profile.

Preserve unknown keys and section order on write — the launcher owns that file
and rewrites it on exit. Read, modify one key, write back.

Also expose `StealthMode` and `HideWindow` as checkboxes.

### 4. Build variant switcher — **done**

Originally specced as moving individual files in and out of the game root. That
turned out to be the wrong shape: the reference install already swaps the
*whole* build folder by junction, which is strictly safer — no file in the game
directory is ever modified, so there is nothing to hash, back up, or restore.

`<GameDir>\Repentogon\` is a junction into a build root (`<GameDir>\~\` by
default) holding one complete build per subfolder. Switching re-points the link.

First-time setup on an install that has a real `Repentogon\` folder:

1. Move it to `<BuildRoot>\Vanilla` — a move, so nothing is ever deleted
2. Copy that to `<BuildRoot>\OnlineFix`
3. Link `Repentogon\` back at `Vanilla`

Both variants start as identical copies of whatever was installed; what goes in
the second one afterwards is the user's business. Refuses while Isaac is
running, refuses if `Repentogon\` is a real folder at switch time, and refuses
if both a real folder and a baseline variant exist rather than guessing which
build is current.

**Known gap:** reinstalling REPENTOGON writes a real folder over the junction,
after which the refusal above fires and first-time setup has to be redone by
hand. Needs a **re-link** action: when `Repentogon\` is a real folder whose
contents match an existing variant, offer to discard it and restore the link.
Compare by relative file path and size before offering, and move the folder
aside rather than deleting it.

### 5b. Shared library + manifest profiles — services done, UI pending

The architecture the Workshop findings led to. Steam becomes a transient source
for *getting* mods; nothing stays subscribed, so nothing is ever re-materialised
into a profile.

```
<SyncRoot>\.library\<mod>\          one real copy per mod, suffix-free   [syncs]
<SyncRoot>\.library\.meta\<mod>.*   cached name/description/preview      [syncs]
<SyncRoot>\.profiles\<name>.json    manifest: a list of library names    [syncs]
<SyncRoot>\<name>\                  junctions built from the manifest    [DO NOT sync]
```

Why the split: a sync client cannot represent a junction, and following one
would replicate the whole library again. Sharing a profile is therefore sharing
a small text file — the other person's copy of the tool materialises their own
junctions against their own library. This removes the reason to distribute
profiles over git.

Verified before building: Isaac loads mods through per-mod junctions, two hops
deep. See "Verified facts" in `CLAUDE.md`.

Consequences that must stay true:

- `disable.it` is no longer a supported way to exclude a mod — a marker written
  through a junction lands in the *library*, affecting every profile linking it.
  The manifest is the only membership mechanism.
- The activation sweep must not traverse junctions, or it will mutate shared
  library state.
- Metadata caches live beside the library, never inside a mod folder: anything
  added inside changes its bytes, and co-op requires those to match.

Built: `VdfParser`, `WorkshopService`, `WorkshopPreviewService`, `ModLibraryService`,
`ProfileManifest`, plus three screens — **Workshop** (import while subscribed),
**Library** (browse with cover art, build a profile against it), and the contents
editor inside **Mod profiles** (including the adopt-existing-folders migration).

Remaining: rewiring `ModProfileService.Activate` so activating a profile
materialises its manifest first, and the `.stignore` guidance for `.backup` and
the materialised profile folders.

### 5. Workshop tab

**Redesigned** — the original spec had the wrong mechanism. Steam does not
reconcile the acf against `mods\`; it re-downloads any subscribed item whose
folder is missing from the *currently junctioned profile*. So the useful feature
is not a lock, it is a **delta report**:

- Parse subscribed ids from `WorkshopItemsInstalled` in
  `steamapps\workshop\appworkshop_250900.acf` (ids appear again in
  `WorkshopItemDetails` — read one section, or you double-count)
- List, per profile, which subscribed items are absent. Those are exactly the
  mods Steam will push back in when that profile is active
- Offer the two real fixes: unsubscribe, or copy the mod into the profile

A read-only lock can stay as a secondary control, but it must be disabled unless
the active profile's delta is zero, and the UI has to say that locking with a
non-empty delta produces an endless retry loop rather than a fix. Back up the
file on first lock.

### 6. Status bar

Always visible: active mod profile, active save profile, build the launcher will
start, whether Isaac is running. Most support questions answer themselves if
this is on screen.

---

---

## Blueprint: Debug tab

A log reader, so diagnosing a bad modpack does not mean opening a 227 KB text
file in Notepad and scrolling.

### What the log actually is

Measured on the reference install: **227 KB, 3,931 lines**, rewritten from
scratch on every launch. Exactly three severity tags — `[INFO]` ×3851,
`[ASSERT]` ×15, `[ERROR]` ×2 — so a severity filter is three checkboxes, not a
parser.

Lines worth recognising:

| Pattern | Why it matters |
|---|---|
| `Game Version: J460` | The single most common desync cause |
| `Command Line:` + following lines | Says whether `--repentogonoff` was passed |
| `LOADED MOD <path>` | The real mod list, in load order. Count it |
| `Running Lua Script: <path>` | Which mod is about to run |
| `Lua Debug: <text>` | `Isaac.DebugString` output — mod authors' own tracing |
| `Checksums:` block | Per-player desync table |
| Lua stack traces | The actual crash |

### Design

- **Header summary card** parsed from the log: game version, command line, mods
  loaded, error/assert counts, and whether this run matches the currently
  active profile's mod count. That last comparison catches "Steam put a mod
  back" without reading a single line.
- **Severity toggles** + free-text search over a virtualised list. 4k lines now,
  but a crash loop makes it much bigger.
- **Jump buttons**: Errors · Mods loaded · Lua Debug · Checksums. Each is a
  filter preset, not a separate view.
- **Copy selection** — the point is pasting a block to whoever you play with.
- **Live tail** while the game runs, via `FileSystemWatcher`. The game holds the
  file open, so it must be read with
  `FileShare.ReadWrite | FileShare.Delete`; anything stricter throws.
- **Session archive** (optional, later): copy `log.txt` aside on each launch,
  since the game truncates it. Without this you cannot compare a good run to a
  bad one.

Read-only. This tab must never write to the game's log.

---

## Blueprint: Saves tab

### Where the saves actually are

Verified 2026-08-17, and it is **not** where this document previously assumed:

```
<Steam>\userdata\<accountid>\250900\remote\
    rep+persistentgamedata1.dat            vanilla, slot 1
    rgon_steam_persistentgamedata1.dat     REPENTOGON, slot 1
    rgon_steam_persistentgamedata2.dat     REPENTOGON, slot 2
    rgon_steam_persistentgamedata3.dat     REPENTOGON, slot 3
    rgon_savesyncstatus.json               REPENTOGON's cross-build sync state
    ../remotecache.vdf                     Steam Cloud manifest: sha1 per file
```

`Documents\My Games\Binding of Isaac Repentance+\` holds only `log.txt`,
`options.ini`, REPENTOGON's settings subfolder and its own dated `save_backups\`.
**No live save is in it**, so junctioning it — the plan of record until now —
would have done nothing at all.

### Consequences for the mechanism

1. **Do not junction `remote\`.** Steam Cloud owns that folder and tracks every
   file's sha1 in `remotecache.vdf`. Replacing the folder changes every sha at
   once, which is exactly what provokes a cloud conflict — and Steam may resolve
   it by restoring the *other* save set.
2. **Swap files, not folders.** A whole save set is ~24 KB across five files.
   Copying is fast, atomic enough at this size, and leaves Steam's folder
   structure untouched.
3. **Carry `rgon_savesyncstatus.json` with the set.** It records a checksum per
   build+slot and `AutoSyncingEnabled: true`. Restore `.dat` files without it and
   REPENTOGON reconciles a fresh save against stale checksums — this is the
   concrete route to the achievement loss the docs keep warning about.
4. The two builds share one folder, separated only by filename prefix, so a set
   must record which prefixes it contains.

### Data model

```
<SyncRoot>\.saves\<set name>\
    *.dat                     the slot files this set owns
    rgon_savesyncstatus.json
    set.json
```

```csharp
class SaveSet {
    string    Name;
    GameBuild Build;          // Vanilla | Repentogon | Both — REQUIRED
    string    ModProfile;     // which mod set produced it
    string[]  Players;        // the actual reason this feature exists
    string    Notes;
    int[]     Slots;          // which of 1-3 are populated
    string[]  Files;          // filenames captured, so a partial set is visible
    string    CapturedUtc;
    Dictionary<string,string> Sha1;   // per file, to detect drift since capture
}
```

`.saves` is **machine-local and must not sync** — save state is personal, and
sharing it is the unlock-state desync this whole project exists to prevent. It
goes in `.stignore` alongside `.backup` and the materialised profile folders.

### Safety rules, in order of enforcement

1. **Isaac closed.** Already have `GameProcessService`. The game holds save state
   in memory and writes on exit, so a live swap loses data.
2. **Steam closed, or Cloud off for Isaac.** New check. Steam re-syncs `remote\`
   on start and mid-session; swapping underneath it invites a conflict dialog
   the user will resolve wrongly under time pressure.
3. **Timestamped backup of the current files before every swap**, into
   `.backup\<timestamp>\saves\`. Non-negotiable and nearly free at this size.
4. **Build mismatch is blocked, not warned.** If the set's `Build` does not match
   what `[Shared] LaunchMode` will start, refuse. A typed confirmation at most —
   never a plain OK button.
5. **Sha1 drift is surfaced.** If the live files differ from what the active set
   recorded, say so before overwriting: that is unsaved progress.

### Screen

- Left: save sets — name, build badge, mod profile, players, slots populated.
- Right: notes, per-slot detail, captured date, sha1 drift indicator.
- **Capture current saves as a set** — the primary action, and the safest one.
- **Activate set** — runs the five checks above, backs up, copies in.
- **Backups** — restore any timestamped backup, since that is the undo.

### Build order within the tab

Capture and backup/restore first; they are read-mostly and prove the file
handling. Activation last. Ship capture on its own if activation needs more
soak time — a reliable "snapshot my saves before we try this modpack" is
already worth having.

### Cloud behaviour — probed, with a caveat

Tested 2026-08-17: all save files were moved out of `remote\` and the game
launched. Steam did **not** restore them. A fresh 4,068-byte
`rep+persistentgamedata1.dat` was created and `remotecache.vdf` was rewritten
from local disk, dropping the `rgon_*` entries it previously listed. So Steam
treats the local folder as authoritative and file-level swapping is viable.

**This was not measured on a normal Steam purchase**, so Cloud is very likely
inert for this app on this machine. On a licensed copy with Cloud on, Steam
almost certainly *does* restore the files it has in the cloud — which would
silently undo a swap, or worse, merge one set's unlocks into another.

**So Cloud off is a hard precondition, not advice.** `SteamCloudService` reads
the real setting (see `CLAUDE.md` for the path) and returns
`SafeToSwapSaves` only when it is explicitly `"0"`. Absent counts as on, because
that is Steam's default — being wrong that way costs a warning, being wrong the
other way costs achievements.

The Saves tab therefore opens with a gate:

- **Cloud on, or unknown** → activation disabled, with the reason stated and a
  button running `steam://gameproperties/250900`, which opens the dialog holding
  the toggle. Capture and backup stay available; they only read.
- **Cloud off** → full function.

Regardless of state:

- Pre-swap backup stays unconditional. It is ~24 KB.
- After a swap, re-read `remotecache.vdf`. If Steam rewrote it to describe files
  we did not write, Cloud is live despite the setting and the user needs telling.
- Surface `last_sync_state` in the detail pane. It read `changeslocally`
  immediately after files were removed, so it is a real signal that Steam noticed.

### Recovery path worth surfacing

REPENTOGON writes dated copies to
`Documents\My Games\Binding of Isaac Repentance+\save_backups\`
(`<yyyyMMdd>.<original filename>`). That is an independent backup the tool did
not create, and the Saves tab should list it as a restore source — it is what
saves someone who swapped before this feature existed.

---

## Build order

1. ~~Services + config store, no UI. Port the PowerShell logic verbatim, add tests.~~ **Done**
2. ~~Mod profiles tab.~~ **Done** — this alone replaces the current tool
3. ~~Build variant switcher.~~ **Done**
4. Launch options tab + Workshop lock. Small, low risk.
5. Save profiles **last** — most dangerous, and by then the service layer and
   backup conventions are proven.

Shippable now. Don't hold the release for save switching.

---

## Testing

The filesystem operations are the part that can destroy someone's mods or
achievements. Test those against a temp directory, not your real install:

- [x] Junction create / remove / target resolution
- [x] Refusal when `mods\` is a real folder — and the folder survives
- [x] Refusal when the game is running (build switch)
- [x] ini round-trip preserving unknown keys, comments and section order
- [x] Config schema refusal, BOM handling, unknown-key preservation
- [x] `disable.it` sweeping removes the marker and never the mod
- [x] Recursive copy refuses to follow reparse points
- [ ] Save build-mismatch blocking
- [ ] `.acf` lock/unlock and count parsing

`dotnet test` from the repository root. Every filesystem test builds its tree in
a temp directory and tears it down link-first, so a bug in the code under test
cannot reach a real install.

Keep a throwaway Isaac install for manual testing before any release.
