# Kill Time Tracker

A CS2 plugin built on SwiftlyS2 that tracks and displays the time a player takes to eliminate an enemy (from first sight to kill).

## Features

- **Kill Time Tracking** — Automatically detects enemies within the player's field of view and records the precise time from visible confirmation to kill
- **Visibility Detection** — Fires 9 TraceRays with different offsets to determine enemy visibility
- **Smart Time Fallback** — Prioritizes line-of-sight time; falls back to first damage time if the enemy was never in sight
- **Global Average Kill Time** — Shows the player's average kill time since connecting (accumulated across rounds)
- **Round Kill Counter** — Displays the player's total kills in the current round (reset automatically each round)
- **Color-Coded Timing** — Displays different colors based on elapsed time:
  - <span color="#5eff3e">Green</span> — Under 500ms (quick reaction)
  - <span color="#ffee00">Yellow</span> — 500ms ~ 1000ms
  - <span color="#fa3b3b">Red</span> — Over 1000ms
- **Spectator Relay** — Spectators can see the kill time info of the player they are watching
- **Multi-language Support** — Built-in Simplified Chinese and English translations
- **Auto Cleanup** — Automatically clears all records when a player disconnects

## Display

After a kill, two lines are shown in the center HTML display:

```
Kill Time  ——  Time taken 352.1ms
Avg Kill Time 284.7ms - 3 Round Kills
```

The first line shows the reaction time for the current kill, the second line shows the player's global average and round kill count.

## Installation

1. Download the latest `KillTimeTracker.zip` from the [Releases](https://github.com/thebrokenmirror/KillTimeTracker/releases) page
2. Extract the folder into your CS2 server's `game/csgo/addons/swiftlys2/plugins/` directory
3. Restart the server or use `sw plugins reload` for hot reload

Expected directory structure:

```
game/csgo/addons/swiftlys2/plugins/
└── KillTimeTracker/
    ├── KillTimeTracker.dll
    ├── KillTimeTracker.deps.json
    └── resources/
        └── translations/
            ├── en.jsonc
            └── zh-CN.jsonc
```

## Build

### Prerequisites

- .NET 10.0 SDK or higher

### Build Steps

```bash
git clone <repo-url>
cd KillTimeTracker
dotnet publish -c Release
```

Build outputs:
- `build/publish/KillTimeTracker/` — Published plugin directory
- `Release/KillTimeTracker-1.0.0.zip` — Ready-to-deploy archive (extract directly into `plugins/`)

## Technical Details

### Workflow

1. **Detection Phase** (every 100ms) — Iterates through all alive players, checks for enemies in the field of view via ray tracing, and records the lock-on start time
2. **Damage Phase** (`player_hurt` event) — Records the time of first damage dealt
3. **Kill Phase** (`player_death` event) — Calculates elapsed time and outputs the result
4. **Round Reset** (`round_start` event) — Clears temporary tracking data and resets round kill counters
5. **Disconnect Cleanup** (client disconnect) — Clears all records for the disconnected player

### Visibility Detection

The plugin uses the following strategy for enemy visibility checks:

- Field of View (FOV) limited to approximately 20 degrees (`cos ≈ 0.939`)
- Fires 9 TraceRays at different offsets toward the target
- If any TraceRay does not hit an obstacle, the target is considered visible
- Detection runs on asynchronous threads to avoid blocking the main game thread

### Data Structures

| Dictionary | Key | Value | Description |
|---|---|---|---|
| `playerTargets` | `int` (Slot) | `TargetState` | Current line-of-sight tracking state |
| `playerDamageTrack` | `int` (Slot) | `ConcurrentDictionary<int, DamageEntry>` | Damage records for time fallback |
| `playerPermanentStats` | `ulong` (SteamID) | `PlayerPermanentStats` | Cross-round persistent stats (avg time, kill count) |

## License

This project is licensed under the GNU GPLv3 License.
