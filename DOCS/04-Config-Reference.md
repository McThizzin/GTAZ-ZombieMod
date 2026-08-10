# GTAZ — Config & UI Reference (current state)

All defaults from `GTAZ/Core/Config.cs` (`ModConfig`). Persists to `scripts/GTAZ/ModConfig.ini`
via `IniSerializer` (SHVDN `ScriptSettings`; renamed from the misnamed `JsonSerializer` — the class
was always writing `.ini`, never JSON). `ModConfig.Load()` runs in the
`ModCore` constructor **before** hotkeys are read; `Save()` fires on settings-menu close and on
script abort. When you add a config property, it auto-persists — no extra wiring — but you must add
the menu item and update this table.

---

## Zombie Settings

| Property | Default | Meaning |
|---|---|---|
| `TargetZombieCount` | 80 | Ambient zombie target count. |
| `MaxZombies` | 120 | Hard cap (matches `PedBudgetManager.GlobalBudget`). |
| `ZombieSpawnRadius` | 200f | Max distance for ambient spawns. |
| `ZombieDespawnRadius` | 300f | Distance past which ambient zombies are cleaned up. |
| `MinSpawnDistance` | 80f | Never spawn zombies closer than this. |
| `NightRunnersEnabled` | true | Runners only spawn at night when on. |
| `NightRunnerChance` | 0.4f | Runner ratio at night. |
| `ZombieHealth` | 100 | Spawn health, wired into `WalkerAI`/`RunnerAI` (`MaxHealth`). |
| `ZombieDamage` | 10 | Melee damage per attack, wired into `WalkerAI`/`RunnerAI` (`Damage`). |
| `ShowZombieBlips` | true | Red short-range minimap blips (applies on next tick). |

## World Settings

| Property | Default | Meaning |
|---|---|---|
| `IsApocalypseMode` | true | Master switch for world clearing + atmosphere. |
| `ClearTraffic` | true | Clear vehicles around the player. |
| `DisableCops` | true | No cops. |
| `TimeScale` | 1.0f | Game-time scale, applied per-frame via `SET_TIME_SCALE` in `WorldSystem`. 1.0 = normal; lower = slow-motion (clamped to 0.1). |

## Survivor Settings

| Property | Default | Meaning |
|---|---|---|
| `EnableSurvivors` | true | Transient survivor encounters on/off. |
| `SurvivorCount` | 4 | Cap on converted ambient civilians. |
| `SurvivorCowardChance` | 0.25f | Per-spawn chance a survivor flees instead of fights. |
| `SurvivorDespawnDistance` | 300f | Survivors fade past this distance. |
| `ShowSurvivorBlips` | true | Green short-range minimap blips. |

## Horde Settings

| Property | Default | Meaning |
|---|---|---|
| `EnableHordeEvents` | true | Wandering + night hordes on/off. |
| `HordeEventIntervalMinutes` | 15 | Game-minutes between wandering horde events. |
| `MaxHordeSize` | 40 | Global horde cap. |
| `WanderingHordeSize` | 20 | Wandering horde member count. |
| `HordeNightSize` | 40 | Once-per-night siege size. |
| `HordeNightChance` | 0.5f | Chance of a night siege each night window. |
| `HordeSpawnDistanceMin` | 100f | Materialize horde no closer than this. |
| `HordeSpawnDistanceMax` | 300f | Materialize horde no farther than this. |
| `HordeDespawnDistance` | 400f | Horde members fade past this distance. |
| `HordeRunnerChance` | 0.15f | Runner ratio in hordes. |

## Faction Settings (general)

| Property | Default | Meaning |
|---|---|---|
| `FactionSpawnChance` | 0.3f | Roll probability per ambient spawn that a faction ped spawns. |
| `EnableFactions` | true | Factions + territory master switch. |
| `FactionSpawnTarget` | 10 | Per-faction ambient spawn cap near the player. |
| `ShowFactionBlips` | true | Faction ped blips. |
| `BanditPatrolIntervalMinutes` | 5 | Minutes between The Families' gang-car encounters near the player — 1-2 cars of GSF crew cruise the area and chase on sight (the "Families Encounter Interval (min)" slider). Legacy "Bandit" key kept for save/INI compatibility. |
| `OutpostDefenderCount` | 20 | Defenders at hostile zones / guards at owned zones. |
| `MaxRecruits` | 4 | Recruit squad cap. |
| `RecruitCombatRange` | 100f | Distance recruits fight zombies at. |

## Territory Settings

| Property | Default | Meaning |
|---|---|---|
| `MaxPlayerZones` | 5 | Owned-zone cap (a cleared zone at cap stays contested). |
| `TerritoryRadius` | 250f | Visible ring radius — the "inside a territory" test. |
| `ShowTerritoryBlips` | true | Owner-colored zone blips. |
| `ShowTerritoryRadii` | true | Debug radius rings (map ring, `ADD_BLIP_FOR_RADIUS`). |
| `ShowPlayerCoordinates` | false | Debug HUD: player position/heading/game-zone + live distance to each territory center (mapping aid). |
| `HostileTerritoryZombieCap` | 30 | Ambient zombie cap inside a hostile ring while the player is in it (garrison fights a bounded stream). |
| `DefenderRespawnCooldownSeconds` | 30 | Hostile-zone defender respawn gap. |
| `TerritoryFightKillQuota` | 10 | Defender kills in a hostile ring that trigger a reinforcement wave. Higher = bigger, rarer waves (wave refills to `OutpostDefenderCount`, so each wave is ~quota-sized). |
| `TerritoryFightWaveSize` | 20 | Counter-attack / defense wave size. |
| `TerritoryFightReinforcementWaves` | 3 | Max kill-triggered reinforcement waves per takeover visit ("Defense Reinforcement Waves" slider, reset on leaving). |
| `TerritoryFightCooldownMinutes` | 15 | Minutes between counter-attacks on owned zones ("Territory Attack Interval (min)" slider). Timer-only — kills don't accelerate raids. |

## Gameplay Settings (Phase 5 polish)

| Property | Default | Meaning |
|---|---|---|
| `DisableCharacterSwitching` | true | Locks the character wheel (Alt) and switch keys every frame. Player is NOT forced onto a character — switching is just disabled. |
| `DisablePhone` | true | In-game phone disabled every frame (re-enables when toggled off). |

## UI Settings

| Property | Default | Meaning |
|---|---|---|
| `OpenMenuKey` | F10 | Toggle main menu. |
| `OpenTrainerKey` | F5 | Toggle trainer. |
| `RecruitKey` | X | Recruit the soft-aim-locked (or crosshair) survivor. |
| `SoftAimKey` | H | Toggle (on key-press edge) the recruit reticule + lock. |

---

## Menu layout (LemonUI, `UI/MenuManager.cs`)

`Alignment = GTA.UI.Alignment.Right` on **every** `NativeMenu` (each menu is its own instance;
`Center` throws).

```
MAIN (ZOMBIE SURVIVAL)
├── FACTIONS          enable, per-faction spawn target, outpost defender count,
│                     patrol interval, max recruits, recruit combat range, show blips,
│                     recruiting help, live status line
├── TERRITORY         max player zones, show blips, show radii, attack interval (min),
│                     defense reinforcement waves, show player coordinates (mapping HUD),
│                     live zone-status line
├── SETTINGS
│   ├── ZOMBIES       target count, budget, spawn/despawn/min distances, territory zombie cap,
│   │                 night runners + chance, blips, health, damage
│   ├── WORLD         apocalypse, traffic/cops toggles, time scale
│   ├── HORDE         interval, sizes, night chance, spawn/despawn distances, runner %
│   └── SURVIVORS     enable, count, coward chance, despawn distance, blips
├── DEBUG             horde triggers + status, budget summary, ...
└── TRAINER           (or F5) → see below
```

Config save-on-close (`Closed → ModConfig.Save()`).

## Trainer (auxiliary, `UI/Trainer/`)

F5 toggles. No blocking calls (`Game.GetUserInput`, `Script.Wait`) — instant natives or a pending
vehicle-spawn queue.
- **PLAYER:** god mode (**re-asserted every frame**), infinite stamina, super jump, fast run, heal,
  clean, visibility, set money, wanted level, never wanted, teleport to waypoint.
- **VEHICLE:** per-category spawn menus (non-blocking model load), vehicle god, fix, clean, delete,
  seatbelt.
- **WEAPON:** give/remove all, select weapon, infinite ammo, set ammo, remove current.
- **WORLD:** weather list, freeze time, set hour/min/sec.

**God-mode rule (lesson 35):** the game engine clears invincibility/proof flags on vehicle change,
teleport, streaming, and entity re-creation. Any "god" toggle must re-assert every frame while
enabled (`PlayerTrainer.ApplyGodMode()` from `PlayerTrainer.Tick()`; `VehicleTrainer.ApplyVehicleGodMode()`
from `TrainerManager.Tick()`, armed while out of a vehicle).
