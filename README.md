# Wait Tracker

A Slay the Spire 2 multiplayer mod that tracks how long each player kept the rest of the party waiting.

## What it does

- Times every stretch where one player is the only one still deciding: combat turns, map picks, treasure/relics, events and Ancients, rewards, rest sites, and act transitions.
- Adds a row per player to the end-of-run screen (e.g. "Ironclad made others wait: 4:12"), worst offender first.
- Hover a row to see that player's time broken down by category.
- Shows Steam display names, falling back to character names when offline.

Solo runs show nothing, since there's nobody to wait on.

## Do my friends need it?

No. Wait Tracker doesn't change gameplay, so the game lets you join lobbies where others don't have it. Only players who have it installed see the leaderboard.

## Installation

Put these files in `Slay the Spire 2/mods/WaitTracker/` (create the folder if needed):

- `WaitTracker.dll`
- `WaitTracker.V111.dll`
- `WaitTracker.V113.dll`
- `WaitTracker.json`
- `clock.png`

All are required. Without the `.json`, the game won't find the mod at all.

To turn it off without deleting files, untick it in the game's Mods screen.

## Game versions

Works on both game v0.111 and v0.113+ (the live and beta branches as of November 2026). `WaitTracker.dll` is a small loader that checks which version is running and loads the matching build.

## Building from source

1. Install the .NET 9 SDK.
2. Copy `sts2.dll` and `0Harmony.dll` from each game version's `data_sts2_windows_x86_64/` into `lib/v111/` and `lib/v113/`. They aren't in this repo because they belong to the game.
3. Run `./build.sh`. The DLLs land in `out/`; copy `clock.png` from `assets/` alongside them.

Source layout: `Loader.cs` (the loader), `src/` (shared code), `src/v111/` and `src/v113/` (the parts that differ by game version).

Development history and implementation notes are in [DEVLOG.md](DEVLOG.md).
