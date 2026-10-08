# RngFix

A ModSharp module for CS2 that fixes movement bugs that depend on where in a tick something happens (slopes, edges, teleports, triggers, stairs, bhop platforms).

The movement code is a standalone port of the `MovementFixModule` from [Source2Surf/Timer](https://github.com/Source2Surf/Timer), taken from commit `063cd772d3e6ac64a5f9261f79c2d51a8ab6973c`. Upstream is licensed under AGPL-3.0, and the ported files keep their original copyright headers.

## Fixes

| Fix | What it does |
|---|---|
| Downhill (slopefix) | Keeps the speed a downhill slope gives when you land on it without colliding with it first. |
| Uphill | Lands you on an uphill slope without the collision that takes away your speed. |
| Edgebug | Lands you on the edge of a block instead of letting you slide off it. |
| Telehop | Gives back speed lost to a collision or landing in the same tick a `trigger_teleport` fires. |
| Trigger jump | Touches teleports, pushes and `trigger_multiple`/`trigger_once` entities in the gap between a landing player and the ground. |
| Stairs | Lets you slide up stairs at speed, both on the ground and when landing on them. |
| mpbhops | Keeps bhop platforms (`func_door`/`func_button`) still and applies their teleport or boost to each player separately. |
| Teleport angles | Optional: keeps view angles and velocity through a `trigger_teleport`. |

## Convars

| Convar | Default | Description |
|---|---|---|
| `rngfix_downhill` | `1` | Downhill slopefix |
| `rngfix_uphill` | `1` | Uphill fix |
| `rngfix_edge` | `1` | Edgebug fix |
| `rngfix_telehop` | `1` | Telehop fix |
| `rngfix_triggerjump` | `1` | Trigger jump fix |
| `rngfix_stairs` | `1` | `0` off, `1` surf maps only, `2` every map |
| `rngfix_mpbhops` | `1` | `0` off, `1` bhop maps only, `2` every map (takes effect on the next map) |
| `rngfix_teleport_keep_angles` | `0` | Keep view angles and velocity through teleports |

The map type comes from the map name: maps starting with `surf` are surf maps and maps starting with `bhop` are bhop maps, in any letter case.

## Install

Copy the contents of the release `module.zip` (or `src/game/` after a build) into the server's `game/` folder:

- `sharp/modules/RngFix/`: `RngFix.dll`, `RngFix.deps.json`, `Iced.dll`
- `sharp/gamedata/rngfix.games.jsonc`

## Build

```
dotnet build -c Release
```
