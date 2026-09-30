# Waypoint Queue: Tetz version

Tetz95's private version of [PrinceOfPluto/WaypointQueue](https://github.com/PrinceOfPluto/WaypointQueue): upstream Waypoint Queue plus fixes and changes that aren't released upstream (yet). The original project is MIT-licensed; its [LICENSE.txt](../LICENSE.txt) still applies and must stay with the code.

## Repositories and local folders

Work is split between two repositories, each with its own local folder:

| Repository | Visibility | Local folder | Holds |
| --- | --- | --- | --- |
| [Tetz95/WaypointQueue-tetz](https://github.com/Tetz95/WaypointQueue-tetz) | Private | `projects\Railroader\WaypointQueue-Tetz` | `tetz`: the version that gets installed. It's upstream plus every fix and change, plus this `Tetz` folder. |
| [Tetz95/WaypointQueue](https://github.com/Tetz95/WaypointQueue) | Public fork | `projects\Railroader\WaypointQueue` | `main`, an exact copy of upstream, plus one `fix/...` or `feature/...` branch per change meant for upstream. Pull requests to PrinceOfPluto come from here. GitHub doesn't allow a fork of a public repository to be private, which is why this repo is separate. |

Remotes in this folder:

| Remote | Repository | Used for |
| --- | --- | --- |
| `origin` | Tetz95/WaypointQueue-tetz (private) | Pushing `tetz` |
| `upstream` | PrinceOfPluto/WaypointQueue | Merging upstream releases |
| `public` | Tetz95/WaypointQueue (public fork) | Merging `fix/...` branches made for upstream |

Changes that are only for this version can be committed straight to `tetz`. Fixes are now made that way too, without a branch on the public fork, unless they're meant for upstream.

## Building

The upstream project file (`WaypointQueue/WaypointQueue.csproj`) needs Visual Studio's MSBuild. [WaypointQueue.Tetz.csproj](WaypointQueue.Tetz.csproj) builds the same sources with the .NET SDK instead:

1. Copy `Paths.user.example` in the repo root to `Paths.user` and set `RrInstallDir` to the Railroader install folder.
2. Run `dotnet build Tetz/WaypointQueue.Tetz.csproj` from the repo root.

The build installs `WaypointQueue.dll`, its `.pdb` and [Info.json](Info.json) into `Railroader\Mods\WaypointQueue\`. The existing `Settings.xml` there is kept.

To run upstream's unit tests, and any added here, use `dotnet test Tetz/Tests/WaypointQueue.Tetz.Tests.csproj`. Like the build, it needs `Paths.user`.

## Versioning and updates

[Info.json](Info.json) gives this version its own number: upstream's version plus a fourth number, starting at `1.8.2.1`. Raise the fourth number for each new build you keep. When you merge a new upstream release, restart from its version, for example `1.8.3.1`.

It has no `Repository` entry, so Unity Mod Manager won't offer upstream releases. Accepting one would replace this build and remove this version's changes.

## Pulling in upstream changes

In this folder:

```
git fetch upstream
git merge upstream/main
```

Then update the version in `Tetz/Info.json`, rebuild and push to `origin`.

Also keep the public fork's `main` in step, in the `WaypointQueue` folder:

```
git switch main
git pull upstream main
git push origin main
```

## Making a fix for upstream

1. In the `WaypointQueue` folder, start from `main`: `git switch -c fix/<name> main`. Commit the fix and push the branch to `origin` (the public fork).
2. Upstream asks for an issue first, and for the maintainer to agree to the approach before a pull request. Open the pull request from the public fork against `PrinceOfPluto/WaypointQueue`.
3. To use the fix straight away, merge it into `tetz` in this folder:

   ```
   git fetch public
   git merge public/fix/<name>
   ```

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
| Unit tests run again: test locomotives are mocked as `BaseLocomotive` | `tetz` only | |

`fix/send-past-restore-switches` on the public fork is an earlier, Send-past-only version of this fix. It's no longer in this version, but the branch is kept in case upstream prefers the narrower change.
