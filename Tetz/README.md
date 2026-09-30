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

Changes that are only for this version can be committed straight to `tetz`.

## Building

The upstream project file (`WaypointQueue/WaypointQueue.csproj`) needs Visual Studio's MSBuild. [WaypointQueue.Tetz.csproj](WaypointQueue.Tetz.csproj) builds the same sources with the .NET SDK instead:

1. Copy `Paths.user.example` in the repo root to `Paths.user` and set `RrInstallDir` to the Railroader install folder.
2. Run `dotnet build Tetz/WaypointQueue.Tetz.csproj` from the repo root.

The build installs `WaypointQueue.dll`, its `.pdb` and [Info.json](Info.json) into `Railroader\Mods\WaypointQueue\`. The existing `Settings.xml` there is kept.

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
| Reset switches the train was on when it reached any waypoint (including Send past waypoint), once it's clear of them | `fix/restore-switches-after-waypoint` | Proposed on [issue #75](https://github.com/PrinceOfPluto/WaypointQueue/issues/75) as the fix |

`fix/send-past-restore-switches` on the public fork is an earlier, Send-past-only version of this fix. It's no longer in this version, but the branch is kept in case upstream prefers the narrower change.
