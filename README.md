# Isaac Mod Profiles

Switch between different Binding of Isaac mod sets — and different game builds —
without a second install, and keep those sets identical across several people.

Built for a specific problem. Repentance+ online co-op requires every player to
have **byte-identical mods**, while singleplayer usually wants a much larger set
plus REPENTOGON. Toggling twenty mods by hand before each session doesn't scale,
and one person forgetting one folder desyncs the whole lobby with an error that
tells you nothing useful.

## What it does

Your mod sets live in one folder that you sync however you like. The game's
`mods\` directory is replaced with a **directory junction** pointing at whichever
set is active.

```
F:\IsaacSync\                       <- synced folder / git repo
  online\                           <- mod folders
  singleplayer\                     <- mod folders

...\The Binding of Isaac Repentance+\
  mods\  ==>  junction to F:\IsaacSync\online
```

Switching re-points the junction and launches the right executable. Nothing is
copied, so it's instant, and the synced folder is the only source of truth.

Each profile also remembers which **build** it runs — REPENTOGON or vanilla —
because those are different game versions and mixing them desyncs instantly.

## Install

1. Download this repo (green **Code** button → Download ZIP)
2. Extract somewhere permanent — not your Downloads folder
3. Run **`Setup.bat`**

Everything is a picker or a yes/no. No paths to type.

You get `Play Online.bat` and `Play Singleplayer.bat`, plus optional shortcuts on
your Desktop or Start Menu.

### What setup asks

| Question | Notes |
|---|---|
| Where is `isaac-ng.exe`? | Auto-detected from the REPENTOGON launcher's ini, or standard Steam paths |
| Where should mod sets live? | Folder picker. Keep it **outside** the game directory |
| Profile names | Defaults to `online` and `singleplayer` |
| Migrate existing mods? | Copies your current `mods\` into one or all profiles |
| Which profile is for online play? | That profile gets locked to vanilla — see below |
| Run *(profile)* with REPENTOGON? | Asked for every other profile |
| Clear `disable.it` markers? | Recommended. Forced on for the online profile |
| Shortcuts | Desktop / Start Menu / both / custom / none |
| Syncthing or repo link | Optional, skippable |

Your original `mods\` folder is **renamed**, never deleted — it becomes
`mods.backup-<timestamp>` in the game directory. Delete it yourself once you've
confirmed things work.

## The online profile is special

One profile can be designated as your multiplayer one. It is then:

- **Locked to the vanilla build.** Not a prompt, not a default — you cannot
  select REPENTOGON for it, and the switcher overrides the config at launch even
  if you hand-edit the JSON.
- **Always stripped of `disable.it` markers**, so folder contents are the whole
  mod list with nothing silently switched off.

This is because REPENTOGON targets Repentance+ **v1.9.7.12.J273** while the
current retail build is newer. Two players on different builds desync on the
first frame, and the log gives you no hint that a version mismatch is the cause.

## REPENTOGON notes

**The REPENTOGON build cannot be launched directly.** `Repentogon\isaac-ng.exe`
refuses with a "should only be launched using the REPENTOGONLauncher" dialog. So
profiles using it are started via `REPENTOGONLauncher.exe --isaac="<vanilla exe>"`
— pointing at the *vanilla* executable, which the launcher resolves to the
REPENTOGON build itself. This mirrors the official Steam launch-option setup.

Depending on your launcher settings you may still get its window and have to
click Launch. Look for a "launch immediately" option, or try `StealthMode = 1` in
`Documents\My Games\repentogon_launcher.ini`.

**The launcher lives outside the game folder.** The REPENTOGON docs warn against
extracting it into the install, and specifically against creating a folder named
`repentogon` there — that name is used by the downgraded build. Setup searches
beside your install and at drive roots, then offers a file picker.

**Save files are version-specific.** Loading a REPENTOGON-era save on a newer
build can destroy all achievements. This tool never touches save files, but keep
that in mind if you write anything that does.

## Syncing between people

**Syncthing** — live, automatic, deletions propagate. Add the sync folder, share
it, set your side to **Send Only** and theirs to **Receive Only**. That last part
is what makes removing a mod on your machine remove it on theirs while their
local experiments never travel back.

**Git** — for anyone who won't run a background service. They clone or download
the ZIP.

Setup writes the ignore files that keep the two from fighting:

- `.gitignore` excludes `.stfolder/`, `.stversions/`, `.stignore`
- `.stignore` excludes `.git`, `.gitignore`, `.gitattributes`

Replicating a live `.git` directory across machines causes index and lock
collisions and can corrupt the repo — don't skip that second one.

`.gitattributes` sets `* -text` so git doesn't rewrite line endings in `.lua` and
`.xml` files. Without it a cloned copy differs byte-for-byte from a Syncthing
copy. It almost certainly wouldn't desync anything (Isaac checksums game state,
not source files), but you don't want two distribution paths producing different
bytes while debugging.

**Don't sync the game's `data\` folder.** Mod save state and per-player settings
live there. Syncing it causes exactly the state divergence this tool prevents.

## Safety

The script only ever deletes **junctions**, using `Directory.Delete(path, false)`,
which cannot recurse into the target. If it finds a real folder where it expects
a junction, it refuses and tells you rather than guessing.

Don't point `rmdir /s` or drag-to-Recycle-Bin at a junction yourself — some tools
follow the link and delete the target's contents.

## Requirements

Windows with PowerShell 5.1 (ships with Windows 10/11). Creating junctions does
not require administrator rights.

## Still desyncing?

Identical mod folders are necessary but not sufficient. In rough order of how
often they're the cause:

**Game build.** Compare the `Game Version:` line near the top of each player's
`log.txt`. It must match exactly.

**Save state.** Unlock state feeds item pool composition, which feeds RNG. A
mismatch here desyncs within seconds of a run starting. A fresh save on all
machines is the quick test.

**Loose files in `resources\`.** Anything dropped there overrides everything,
never appears in the mod list, and is invisible to any sync setup.

**Mod versions.** Same folder name, different files inside. Compare file counts
and total sizes, not just the list of names.

### Reading a desync

The host's `log.txt` prints a per-player table when it happens:

```
Checksums:
 - Player0: Checksum (1000fc7b), Global RNG checksum (f4d2dca3)
 - Player1: Checksum (1000fc7b), Global RNG checksum (f4d2dca3)
 - Player2: Checksum (fb9494d0), Global RNG checksum (0dbd257b)
```

Whoever's row differs is the machine to investigate — the others agree.

Then look at what follows:

- **Save checksums differ** in the recovery block → unlock/save state mismatch.
- **Entity types or positions differ** → mod content mismatch. Different files,
  different versions, or different load order.
- **`No Entity Desyncs Detected` but global RNG checksums differ** → something
  consumed a random number on one client and not the others. Usually a mod that
  adds gameplay entities rather than sprites. Bisect by halves.
- **Desync at frame 1** → divergence before anyone pressed a button. Content or
  save state, never gameplay.

## License

MIT. Do whatever you want with it.
