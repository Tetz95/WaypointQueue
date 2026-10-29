# Waypoint Queue (Tetz fork)

This branch (`tetz`) is Tetz95's fork of [PrinceOfPluto's Waypoint Queue](https://github.com/PrinceOfPluto/WaypointQueue) for Railroader 2026.1 (0.6), built on his 2.0.0. `main` stays a copy of his `main`. The original is MIT-licensed; its [LICENSE.txt](LICENSE.txt) applies to this fork too.

What the fork adds:
- **Train poses kept through the queue.** The pose chosen in the game's waypoint picker (Space: which end of the train stands on the mark, and on which side) is kept for queued waypoints and sent with them; the Waypoints window shows how the train will stand. *Send past waypoint* goes out as the game's "train past the mark" pose.
- **The train as it will be.** When picking a waypoint for later in the queue, the picker draws and poses the train as it will be by then, after the earlier couplings and cuts, and says so in its prompt.
- **Couplings** go out as the game's coupling order for the near end of the car, and a coupling to a car already in the train is refused instead of cutting from the middle of it.
- **Switches:** switches the auto engineer threw are put back once the train is clear, also after *Send past waypoint*, a cancelled route or a save and load; under CTC a route that turns back beyond an interlocking runs clear of the plant first (setting: *Turn back clear of CTC interlockings*).
- **Cuts:** cutting works with cars turned around in the train, uncouples on the end that faces the next car, and *All except locomotives* sends locomotives in tow with the cars.
- **Non-stop waypoints** are reached along the track (not in a straight line) and hand over to the next waypoint before the engineer brakes for them; no route no longer turns the stop into running at track speed.
- **Refuelling** counts a slot full at the game's own capacity (coal is limited by volume), lines the slot up with a posed waypoint and finds modded loaders built after load.

It loads as its own mod (`tetz95/waypoint-queue`) and is incompatible with the original (`princeofpluto/waypointqueue`): install one or the other.

Building: the projects are SDK-style, so `dotnet build WaypointQueue/WaypointQueue.csproj` builds without Visual Studio. In `Paths.user` set `RrInstallDir` (the game folder) and `RrHomeDir` (the folder whose `Mods` the build installs into); `-p:NoInstall=true` builds into `bin\` instead. The Legacy (2025.1) version of these fixes is on the `tetz-legacy` branch.

---

# Waypoint Queue

## Installation

### Requirements
- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)

### Steps:
1. Download the latest release
2. Open UMM Installer and make sure you have Railroader selected
3. Click the Mods tab
4. Drag and drop the `WaypointQueue_versionNumber.zip` file into UMM
5. Start up the game


### Incompatibilities
- Incompatible with Refuel Waypoint, however Waypoint Queue has built-in support for refueling locomotives.

## Overview
Waypoint Queue significantly expands the Auto Engineer (AE) Waypoint mode feature from vanilla Railroader. For the latest information on mod features, check the [description on the Nexus Mods page](https://www.nexusmods.com/railroader/mods/1029).

All the keybinds are reconfigurable using the UMM Mod Settings menu which you can access by pressing Ctrl+F10.


## Local Development

### Requirements
- Visual Studio (in the installer, make sure you have SDKs for .NET Framework 4.8, C# support, and NuGet Package Manager)

To start developing locally, follow these steps:
1. Clone the repo locally
2. Copy `Paths.user.example` and save it as `Paths.user`.
3. Open this `Paths.user` file and update the path to the game directory containing `Railroader.exe`
4. Open the Solution

### During Development
- Make sure you're using the *Debug* configuration. Every time you build your project, the files will be copied to your Mods folder and you can immediately start the game to test it.
- If you are interested in contributing to Waypoint Queue, please read the [Contributing guide](https://github.com/PrinceOfPluto/WaypointQueue/blob/main/CODE_OF_CONDUCT.md).