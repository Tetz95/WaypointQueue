# Waypoint Queue: Tetz fork

This is Tetz95's fork of [PrinceOfPluto/WaypointQueue](https://github.com/PrinceOfPluto/WaypointQueue): upstream Waypoint Queue plus fixes and changes that aren't released upstream (yet). The original project is MIT-licensed; its [LICENSE.txt](../LICENSE.txt) still applies and must stay with the code.

## Branches

| Branch | Contents |
| --- | --- |
| `main` | An exact copy of upstream `main`. Never commit to it directly; only update it from upstream. |
| `fix/...`, `feature/...` | One branch per change meant for upstream, started from `main`, so its pull request contains only that change. |
| `tetz` | The version that gets installed: `main` plus every fix and change of this fork, plus this `Tetz` folder. |

Changes that are only for this fork can be committed straight to `tetz`.

## Building

The upstream project file (`WaypointQueue/WaypointQueue.csproj`) needs Visual Studio's MSBuild. [WaypointQueue.Tetz.csproj](WaypointQueue.Tetz.csproj) builds the same sources with the .NET SDK instead:

1. Copy `Paths.user.example` in the repo root to `Paths.user` and set `RrInstallDir` to the Railroader install folder.
2. Run `dotnet build Tetz/WaypointQueue.Tetz.csproj` from the repo root.

The build installs `WaypointQueue.dll`, its `.pdb` and [Info.json](Info.json) into `Railroader\Mods\WaypointQueue\`. The existing `Settings.xml` there is kept.

## Versioning and updates

[Info.json](Info.json) gives this fork its own version: upstream's version plus a fourth number, starting at `1.8.2.1`. Raise the fourth number for each new build you keep. When you merge a new upstream release, restart from its version, for example `1.8.3.1`.

It has no `Repository` entry, so Unity Mod Manager won't offer upstream releases. Accepting one would replace this build and remove the fork's changes.

## Pulling in upstream changes

```
git switch main
git pull upstream main
git push origin main
git switch tetz
git merge main
```

Then update the version in `Tetz/Info.json` and rebuild.

## Sending a fix upstream

1. Start from `main`: `git switch -c fix/<name> main`.
2. Commit the fix, push the branch to `origin`, and open a pull request against `PrinceOfPluto/WaypointQueue`. Upstream asks for an issue first, and for the maintainer to agree to the approach before a PR.
3. Merge the branch into `tetz` to use it straight away.

## Fixes in this fork

| Change | Branch | Upstream |
| --- | --- | --- |
| Restore switches left under the train after Send past waypoint | `fix/send-past-restore-switches` | [Issue #75](https://github.com/PrinceOfPluto/WaypointQueue/issues/75); no PR yet, waiting for the maintainer |
| Reset switches the train was on when it reached any waypoint, once it's clear of them | `fix/restore-switches-after-waypoint` | Not proposed upstream yet |
