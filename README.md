# Waypoint Queue: Tetz version

Tetz95's private version of [PrinceOfPluto/WaypointQueue](https://github.com/PrinceOfPluto/WaypointQueue): upstream Waypoint Queue plus fixes and changes that aren't released upstream. The original project is MIT-licensed; its [LICENSE.txt](LICENSE.txt) still applies and must stay with the code.

## Overview

Waypoint Queue significantly expands the Auto Engineer (AE) Waypoint mode feature from vanilla Railroader. For the latest information on mod features, check the [description on the Nexus Mods page](https://www.nexusmods.com/railroader/mods/1029).

All the keybinds are reconfigurable using the UMM Mod Settings menu, which you can open with Ctrl+F10.

It's incompatible with Refuel Waypoint; Waypoint Queue has built-in support for refueling locomotives.

## Repository layout

| Path | Holds |
| --- | --- |
| `WaypointQueue/` | The mod's source and its project, `WaypointQueue.csproj`. |
| `WaypointQueue.Tests/` | Unit tests and their project, `WaypointQueue.Tests.csproj`. |
| `Info.json` | The Unity Mod Manager manifest that gets installed, with this version's number. |
| `Paths.user.example` | Template for `Paths.user`, which says where the game is installed. |

Compared with upstream, the projects are SDK-style so they build with the .NET SDK instead of Visual Studio's MSBuild, and upstream's release and GitHub files (`repository.json`, `UpdateVersion.ps1`, `.github/`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`) are removed.

## Repositories and local folders

Work is split between two repositories, each with its own local folder:

| Repository | Visibility | Local folder | Holds |
| --- | --- | --- | --- |
| [Tetz95/WaypointQueue-tetz](https://github.com/Tetz95/WaypointQueue-tetz) | Private | `projects\Railroader\WaypointQueue-Tetz` | `tetz`: the version that gets installed. |
| [Tetz95/WaypointQueue](https://github.com/Tetz95/WaypointQueue) | Public fork | `projects\Railroader\WaypointQueue` | `main`, an exact copy of upstream, plus the `fix/...` branches made for upstream. GitHub doesn't allow a fork of a public repository to be private, which is why this repo is separate. |

Remotes in this folder:

| Remote | Repository | Used for |
| --- | --- | --- |
| `origin` | Tetz95/WaypointQueue-tetz (private) | Pushing `tetz` |
| `upstream` | PrinceOfPluto/WaypointQueue | Merging upstream releases |
| `public` | Tetz95/WaypointQueue (public fork) | Merging `fix/...` branches made for upstream |

Fixes and changes are committed straight to `tetz`. The public fork is only used for a fix meant for upstream.

## Building

1. Copy `Paths.user.example` to `Paths.user` and set `RrInstallDir` to the Railroader install folder.
2. Run `dotnet build WaypointQueue/WaypointQueue.csproj` from the repo root.

The build installs `WaypointQueue.dll`, its `.pdb` and [Info.json](Info.json) into `Railroader\Mods\WaypointQueue\`. The existing `Settings.xml` there is kept. New source files under `WaypointQueue/` are compiled automatically.

To run the unit tests, use `dotnet test WaypointQueue.Tests/WaypointQueue.Tests.csproj`. Like the build, it needs `Paths.user`.

## Versioning and updates

[Info.json](Info.json) gives this version its own number: upstream's version plus a fourth number, starting at `1.8.2.1`. Raise the fourth number for each new build you keep. When you merge a new upstream release, restart from its version, for example `1.8.3.1`.

It has no `Repository` entry, so Unity Mod Manager won't offer upstream releases. Accepting one would replace this build and remove this version's changes.

## Pulling in upstream changes

In this folder:

```
git fetch upstream
git merge upstream/main
```

[.gitattributes](.gitattributes) keeps this version's `Info.json`, `README.md` and both project files when upstream changes them too. That needs the `ours` merge driver, which is set per clone; run this once in a new clone:

```
git config merge.ours.driver true
```

If upstream changes a file removed here, the merge stops with a "modify/delete" conflict; remove it again with `git rm <file>` and commit. Upstream's version number comes from its release tag, not from a file, so update the version in [Info.json](Info.json) by hand. Then rebuild, run the tests and push to `origin`.

Also keep the public fork's `main` in step, in the `WaypointQueue` folder:

```
git switch main
git pull upstream main
git push origin main
```

## Making a fix for upstream

1. In the `WaypointQueue` folder, start from `main`: `git switch -c fix/<name> main`. Commit the fix and push the branch to `origin` (the public fork).
2. Upstream asks for an issue first, and for the maintainer to agree to the approach before a pull request. Open the pull request from the public fork against `PrinceOfPluto/WaypointQueue`.
3. To use the fix here, merge it into `tetz` in this folder:

   ```
   git fetch public
   git merge public/fix/<name>
   ```

   The public fork still has upstream's layout, so a fix that edits upstream's project file conflicts here; keep this version's project file.


## Fixes in this version

| Change | Branch (public fork) | Upstream |
| --- | --- | --- |
| Reset switches the train was on when it reached any waypoint (including Send past waypoint), once it's clear of them | `fix/restore-switches-after-waypoint` | Proposed on [issue #75](https://github.com/PrinceOfPluto/WaypointQueue/issues/75#issuecomment-5903965984) |
| Don't crash when deleting or replacing a waypoint after the loco left Waypoint mode, inserting before a waypoint that was just resolved, removing the queue of a loco that no longer exists, or toggling Loop after the route was unassigned | `fix/crash-guards` | Not reported yet |
| Save routes captured with "Overwrite/Add from Loco", and don't copy a waypoint's in-progress wait, refuel or coupling state into routes | `fix/route-capture` | Not reported yet |
| Treat a coupling target that no longer exists as missing, so the waypoint is dropped instead of leaving the loco waiting forever | `fix/missing-couple-target` | Not reported yet |
| Queue each loco for periodic reroute only once | `fix/reroute-queue-duplicates` | Not reported yet |
| Import pre-1.6 JSON save data only once per save | `fix/legacy-migration-once` | Not reported yet |
| Store wait times culture-invariant, so saves read back correctly on machines with a comma decimal separator | `fix/invariant-number-storage` | Not reported yet |
| Uncoupling by destination with no matching car no longer tries to cut the whole train (or silently drop every car after coupling); it leaves the train as it is and says so in the console | `tetz` only | Same bug as [#74](https://github.com/PrinceOfPluto/WaypointQueue/issues/74) and [#70](https://github.com/PrinceOfPluto/WaypointQueue/issues/70) |
| Count uncoupled cars from the train end nearest the waypoint, not from the locomotive end nearest it, so a train on curved track isn't read backwards | `tetz` only | [#70](https://github.com/PrinceOfPluto/WaypointQueue/issues/70) |
| Pause a coupling waypoint with an error when its car is already in the train when the waypoint starts (for example, picked up by an earlier waypoint), instead of treating it as coupled and cutting from the middle of the train | `tetz` only | [#72](https://github.com/PrinceOfPluto/WaypointQueue/issues/72) |
| A non-stop waypoint passed at 35 mph or more is only counted as reached when the locomotive is within 25 m of it along the track, not in a straight line, so a train on a parallel track no longer "arrives" (the same for the end-of-track check) | `tetz` only | [#73](https://github.com/PrinceOfPluto/WaypointQueue/issues/73) |
| Unit tests run again: test locomotives are mocked as `BaseLocomotive` | `tetz` only | |

`fix/send-past-restore-switches` on the public fork is an earlier, Send-past-only version of this fix. It's no longer in this version, but the branch is kept in case upstream prefers the narrower change.
