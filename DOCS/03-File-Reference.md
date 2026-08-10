# GTAZ — File Reference

One line per source file. Use this to find where a feature lives. Paths are relative to `GTAZ/`.
Cross-check against `01-Architecture.md` for how the pieces interact.

## Root

| File | Purpose |
|---|---|
| `ZombieMod.csproj` | .NET Framework 4.8, SHVDN3 + LemonUI references. Hardcoded `OutputPath` + `HintPath`s → GTA V folder; build drops `ZombieMod.dll` into `scripts/`. |
| `ModCore.cs` | Entry point — the **only** `Script` subclass. Loads config, creates `ModuleManager`, `OnTick`/`OnAborted`, key-up routing (menu, trainer, recruit). |
| `ModuleManager.cs` | Owns all systems; fixed tick order, first-tick one-shot actions, startup save-load, abort chain. |

## Core/

| File | Purpose |
|---|---|
| `Core/Config.cs` | `ModConfig` singleton (all tunables + keybinds) → `scripts/GTAZ/ModConfig.ini`. Full table in `04-Config-Reference.md`. |
| `Core/Logger.cs` | File logger → `scripts/GTAZ.log`, recreated each session. |
| `Core/PedBudgetManager.cs` | The 120-slot ped budget: `TryRequestSlot`, `TryRequestAndSpawn`, `ReleaseSlot`, `PeriodicRescore`, ref-counted `RebalanceForHorde`, `SetTerritoryContext`. |
| `Core/SpawnRequestQueue.cs` | Deferred spawn queue: 3 grants/pass, 8s TTL, priority aging. |

## Systems/

| File | Purpose |
|---|---|
| `Systems/WorldSystem.cs` | Apocalypse mode: clears traffic/cops/scenarios (zombie-safe), per-frame `SET_TIME_SCALE` (the `TimeScale` world setting). |
| `Systems/ZombieSystem.cs` | Ambient zombie spawns, night runners, per-tick rescore, hostile-ring density cap. |
| `Systems/HordeSystem.cs` | Wandering hordes + once-per-night siege; own ped list, shared queue. |
| `Systems/SurvivorSystem.cs` | Transient armed civilians (fight/flee); `SpawnAt` for horde announce; `ConvertNearbyEngineNpcs`; `DetachAmbientSurvivor` for recruiting. |
| `Systems/FactionSystem.cs` | Faction/territory orchestration (singleton): owns the 5 `FactionDef`s + the active faction-ped list, delegates territory to `TerritorySystem`, spawning to `FactionSpawner`, recruiting to `RecruitManager`. Keeps the stable public surface (`TryRecruitAtCrosshair`, `DismissAllRecruits`, `HostileTerritoryAt`, `IsPlayerOwnedZone`, `IsInAnyTerritory`, `HomeFaction`, `RestoreOwnedTerritories`, `SurrenderCurrentZone`, `ClearAllFactions`) as delegates. See `02-Systems-Reference.md` → Factions. |
| `Systems/FactionSpawner.cs` | Faction spawn construction: ambient members, The Families' timed patrol, territory-bound defenders/reinforcements/raiders/guards (all via `SpawnFactionPed` on the shared SpawnRequestQueue) + Lost biker mounting. |
| `Systems/TerritorySystem.cs` | Territory state machine: the 12 `TerritoryZone`s + blips, hostile-zone takeover (defenders/reinforcement waves/capture), owned-zone timer raids, surrender, save-restore, per-ring queries (`HostileTerritoryAt`/`IsPlayerOwnedZone`/`IsInAnyTerritory`/`IsPlayerInTerritoryContext`). Spawns via the FactionSpawner, reaches peds via callbacks. |
| `Systems/FactionNPC.cs` | Per-faction-ped wrapper (role, homeZone, slot, weapon, essential flag, bike ownership). |
| `Systems/RecruitManager.cs` | Recruiting (DOCS/06-Roadmap.md §4.7): owns the recruit list + soft-aim consumer + recruit/dismiss actions + per-tick recruit upkeep; callbacks into FactionSystem's active list / budget. |
| `Systems/GameplaySettings.cs` | Per-tick gameplay tweaks (disable character switching/phone, HUD). |
| `Systems/PersistenceManager.cs` | Save/load: `PlayerData_*.ini`, autosave every 5 min, abort save, `DO_AUTO_SAVE` on every mod save. |

## AI/

| File | Purpose |
|---|---|
| `AI/ZombieAI.cs` | Abstract zombie state machine + blips + wander re-issue + horde march mode. |
| `AI/ZombieTypes/WalkerAI.cs` | Slow melee shambler (0.8 speed, 40m sight). |
| `AI/ZombieTypes/RunnerAI.cs` | Fast night-only melee (1.8 speed, 60m sight). |
| `AI/Perception.cs` | Static sight/sound/LOS helpers. |
| `AI/SurvivorAI.cs` | Armed-human behavior (combat/flee/wander). |
| `AI/FactionAI.cs` | Faction behavior: combat/guard/patrol/follow + stuck-scenario fallback. |

## UI/

| File | Purpose |
|---|---|
| `UI/MenuManager.cs` | LemonUI MenuPool: MAIN + FACTIONS/TERRITORY/SETTINGS/DEBUG; config save-on-close. |
| `UI/SoftAimController.cs` | Generic soft-aim targeting: H toggle + center reticule + camera-raycast lock + highlight marker; exposes `Ped LockedTarget` + `ClearLock()`. The owner supplies a validity predicate. |
| `UI/Trainer/TrainerManager.cs` | F5 trainer root + submenus; ticks god-mode re-assert. |
| `UI/Trainer/Features/PlayerTrainer.cs` | Player: god mode (re-asserted), stamina, jump, heal, money, wanted, teleport. |
| `UI/Trainer/Features/VehicleTrainer.cs` | Vehicle: spawn categories, god, fix, clean, delete, seatbelt. |
| `UI/Trainer/Features/WeaponTrainer.cs` | Weapon: give/remove all, select, ammo. |
| `UI/Trainer/Features/WorldTrainer.cs` | Weather, freeze time, set hour/min/sec. |

## Events/

| File | Purpose |
|---|---|
| `Events/GameEvents.cs` | Central bus (mod loaded/aborted). |
| `Events/ZombieEvents.cs` | Zombie spawned/died. |
| `Events/FactionEvents.cs` | Zone captured, faction NPC death, recruit joined/left. |

## Data/

| File | Purpose |
|---|---|
| `Data/FactionDef.cs` | Static per-faction data + `FactionArchetype`/`PlayerRelation` enums. |
| `Data/FactionCatalog.cs` | Builds the 5 `FactionDef`s + faction-id constants (`Build()`); called by `FactionSystem` ctor. |
| `Data/TerritoryCatalog.cs` | Builds the 12 territories (`BuildZones`) from centers + home factions + player-mapped wave spawns. |
| `Data/TerritoryZone.cs` | Zone ownership/capture state + wave spawn points. |
| `Data/PlayerData.cs` | Player save data (position/heading/health/armor/territories). |
| `Data/VehicleCatalog.cs` | Trainer vehicle list (vehicles.ini, fallback built-in). |
| `Data/Serialization/IniSerializer.cs` | Writes `.ini` via SHVDN `ScriptSettings` (was `JsonSerializer` — renamed; the class was always writing `.ini`, never JSON). |

## Extensions/

| File | Purpose |
|---|---|
| `Extensions/PedExtensions.cs` | `MakeZombieLike`, `MakeSurvivorLike`, `MakeFactionLike`, relationship setup + `EnsurePlayerFacingRelationships`, `IsZombie`/`IsSurvivor`/`IsFactionPed`/`IsHumanoid`, clipset/anim helpers, `TryMountBike`. |
| `Extensions/WorldExtensions.cs` | Density helpers. |

## Utils/

| File | Purpose |
|---|---|
| `Utils/MathUtils.cs` | `Clamp(int)`. |
| `Utils/SpawnUtils.cs` | `FindSafePosition` (safe coords + terrain probe), `HorizontalDistance`. |
| `Utils/PedLootUtils.cs` | Shared weapon-loot drop: `DropLoot(ped, weapon)` + `WeaponToPickup` (union of survivor/faction mappings; used by `SurvivorSystem` and `FactionSystem`). |
| `Utils/BlipHelper.cs` | Shared ped minimap-blip pattern: `Ensure(ref Blip, ...)` (create-or-reuse when show, delete when hidden) + `Delete(ref Blip)`. Used by `ZombieAI`, `SurvivorAI`, `FactionNPC`. |

## Support files (not compiled)

| File | Purpose |
|---|---|
| `Territories.txt` (repo root) | Master list of the 12 territories + 4 wave spawn points each. Source of truth for the mapping ported into `FactionSystem.GetWaveSpawns`. Not loaded at runtime. |
| `NATIVES/` | GTA V native reference (markdown). |
| `ModSamples/` | Reference mods (GangMod, SimpleZombies, Trainer) — inspiration only, not part of the build. |
