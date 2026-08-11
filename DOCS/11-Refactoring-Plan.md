# GTAZ — Refactoring Plan (Phase 5 follow-up)

> **Forward-looking plan, not current-state reference.** Unlike `01`–`05` (which describe what the
> code **is**), this doc describes what the code **should become**. Produced from a full read of
> `GTAZ/` against `DOCS/01`–`10` (Aug 2026). Every claim was verified against the source at write
> time; re-verify before acting. The code is the source of truth — if this plan and the code
> disagree, the code wins and this doc should be updated.
>
> **Execution status:** §3.1 (dead code) **done**; §3.2.1–§3.2.4 (misnames/stale comments) **done**;
> §5.1–§5.7 **resolved** (see inline **DONE** marks); §3.3 dedup extractions (**PedLootUtils**,
> **BlipHelper**, **EnqueueZombieSpawn**) **done**; §3.4 all six splits **done** — static data
> (**FactionCatalog**/**TerritoryCatalog**), soft-aim (**UI/SoftAimController**), recruiting
> (**Systems/RecruitManager**), spawning (**Systems/FactionSpawner**), and the territory state
> machine (**Systems/TerritorySystem**); user decisions folded in (§3.1 config props wired vs
> deleted, §3.2.2 slots migrated). All steps 1–10 in §4 built clean; all steps **1–10 playtested good** —
> the FactionSpawner + TerritorySystem splits (step 10) got a dedicated post-split playtest (Aug 2026):
> territory loop, recruiting, and ambient faction spawning all working as expected. **§6 balance
> rebalance DONE** (Phase 5 round 8): all `config-values.txt` annotations applied (slider
> maxes/steps, Families patrol default 15→5, step-0 sliders to 10 incl. the trainer's Set Money/Set
> Ammo, `Max Zombies (Budget)` → `Max Peds (Budget)`); built clean. **The refactoring plan is fully
> executed.** Only §7 (deferred territory fixes) remains, intentionally not part of the refactor —
> see below.

## 1. Goals

1. **Delete dead code** — the single biggest confusion reducer, and zero-risk when done right.
2. **Fix misleading names** — `JsonSerializer` (writes `.ini`), save-slot `.json` names, stale
   comments, stale doc claims.
3. **Remove real duplication** — weapon loot, ped blips, zombie `SpawnRequest` construction.
4. **Reduce `FactionSystem` god-class pressure** in safe, behavior-preserving increments.
5. **Reconcile code vs. docs** (§5) so the reference docs stay truthful.

## 2. Ground rules

- **Build gate only.** `dotnet build .\GTAZ\ZombieMod.csproj` (fails unless GTA V is installed at
  `D:\SteamLibrary\steamapps\common\Grand Theft Auto V\` with `ScriptHookVDotNet3.dll` + LemonUI).
  No tests, no lint, no CI — verification is build + in-game spot checks.
- **Each phase is a buildable, playable checkpoint.** One phase per commit; spot-check the areas
  that phase touched before moving on.
- **Stale-DLL trap:** after any change to `Config.cs`/`MenuManager.cs`, do a clean rebuild
  (`Remove-Item -Recurse -Force .\GTAZ\obj, .\GTAZ\bin`) and `rg "<removed-string>"
  .\GTAZ\bin\ZombieMod.dll` before trusting a playtest (AGENTS.md "Gotchas").
- **Do not re-add archived designs.** `10-Archive.md`: no corpse infection/reanimation, no
  `ZoneCaptureTime`/`ZoneCaptureRate` hold-timer, no `PlayerData` weapon fields.
- **Do not change public API signatures** other systems call, unless a phase says so.
  `ZombieSystem`, `HordeSystem`, `WorldSystem`, `MenuManager` and `ModCore` all reach into
  `FactionSystem` via its singleton + query methods.

## 3. Survey findings

### 3.1 Dead code (zero references outside the declaring file — verified by grep)

| Location | What's dead | Action |
|---|---|---|
| `GTAZ/Data/WorldState.cs` | Whole file: `WorldState`, `FactionData`, `ZoneData` (incl. hold-timer-era `ControlProgress`/`ControlRadius`/`ZoneType`/`ControllingFaction`, `RelationshipGroupHash`, `TimeScale`). Nothing references any of it. | Delete file; remove its row in `03-File-Reference.md` |
| `GTAZ/Utils/ModelCache.cs` | Whole file (`Get`/`Request`/`UnloadAll`). No callers — model handling is done inline (`new Model(hash); model.Request();`). | Delete file |
| `GTAZ/Utils/UIUtils.cs` | Whole file (two unused color constants). | Delete file |
| `GTAZ/Extensions/Vector3Extensions.cs` | Whole file (`VDist`, `Around`, `ToHeading`, `RandomOffset`). No callers. `VDist` is a trivial alias of `DistanceTo`. | Delete file |
| `GTAZ/Utils/MathUtils.cs` | Only `Clamp(int)` is used (`HordeSystem.cs:280`). `Clamp(float)`, `Lerp`, `Map` have no callers (`Lerp` is called only by `Map`). | Trim to `Clamp(int)` |
| `GTAZ/Extensions/WorldExtensions.cs` | `ClearCops` (uses **banned** `CLEAR_AREA_OF_PEDS` — rule 9) and `ClearAreaOfVehicles` have no callers. The five `Set*DensityThisFrame` methods are only used through `SetAllDensitiesZero` (used at `WorldSystem.cs:39`). | Delete `ClearCops` + `ClearAreaOfVehicles`; keep `SetAllDensitiesZero` + the density methods it calls. **Never resurrect `ClearCops`** — cop clearing must iterate peds + `MarkAsNoLongerNeeded`, not `CLEAR_AREA_OF_PEDS` |
| `GTAZ/Data/Serialization/JsonSerializer.cs` | `SetBasePath` — no callers. | **DONE** — `SetBasePath` removed; file renamed to `IniSerializer.cs` (§3.2.1) |
| `GTAZ/Core/Config.cs` (dead props) | `ZombieHealth`, `ZombieDamage`, `ClearPeds`, `TimeScale`, `MaxFactions`, `FactionsFightEachOther`, `ShowHUD`, `UIScale` are written to `ModConfig.ini` but **never read** by any system. (`FactionSpawnChance` **is** read, at `FactionSystem.cs:519`.) | **RESOLVED** — `ZombieHealth`/`ZombieDamage` now wired into `WalkerAI`/`RunnerAI` (+ menu sliders); `TimeScale` wired per-frame in `WorldSystem` (+ menu slider); `ClearPeds`/`MaxFactions`/`FactionsFightEachOther`/`ShowHUD`/`UIScale` **deleted** + doc rows removed |
!!User note: wire up zombie health and damage, and time scale was supposed to toggle game time/real time. clear peds, maxfactions, fighteachother, showHUD and UIscale can go!!

### 3.2 Misleading names & stale comments

1. **`JsonSerializer` writes `.ini`** via SHVDN `ScriptSettings`
   (`Data/Serialization/JsonSerializer.cs`). Already flagged in `03`/`04`. Rename class + file to
   something honest (`IniSerializer`). Touches `Config.cs` + `PersistenceManager.cs` only; the
   compiler finds every call site. The output filename (`ModConfig.ini`) must not change.
   **DONE — renamed to `IniSerializer.cs`; `SetBasePath` (no callers) also removed.**
   !!User note: all values in ModConfig.ini are what i have been testing at. Can set code defaults to match, then output filename can change if nessisary!!
   **Code defaults synced to the tested `ModConfig.ini` (see §5.1); the `ModConfig.ini` filename itself is unchanged.**
2. **Save-slot names embed `.json`** but produce `.ini` files: `PersistenceManager.Save("save.json")`
   → `scripts/GTAZ/PlayerData_save.json.ini`; same for `autosave.json`, `manual_save.json`
   (`PersistenceManager.cs:19,38`, `MenuManager.cs:518,523`). Renaming breaks existing saves —
   **keep the filenames** (save compatibility, lesson 39) and add a comment, or rename only with a
   file migration. This is a decision, not a free change. 
   **DONE — renamed to `*.ini` (save.ini / autosave.ini / manual_save.ini / abort_save.ini); existing saves not migrated (user OK'd).**
   !!User note: I'm not concerned about existing saves at this stage of development.!!
    3. **`SurvivorSystem` header comment** (`SurvivorSystem.cs:13-15`) says "permanent
   survivors/recruiting is Phase 4" — recruiting **shipped** in Phase 4. Fix the comment.
   **DONE.**
4. **`FactionSystem.CountAlive(FactionDef)`** (`FactionSystem.cs:1615-1618`) is a public wrapper
   over private `CountActive` (`:1610-1613`); `MenuManager` calls `CountAlive`. Merge to one name.
   Trivial, behavior-neutral.
   **DONE — merged to public `CountAlive`.**
5. **Soft-aim input is split across two entry points:** `SoftAimKey` (H) is polled via
   `Game.IsKeyPressed` inside `FactionSystem.UpdateSoftAim` (`:1216-1219`), while `RecruitKey` (X)
   is routed by `ModCore.OnKeyUp` → `TryRecruitAtCrosshair` (`ModCore.cs:62-65`). When soft-aim is
   extracted (§3.4) move the key handling next to the feature. 
   !!User note: I'm not sure how soft aim ended up in factions. it will be used for more than recruiting in future versions (recruitController name might not be fitting). opening a menu with options for the ped/item highlighted!!
   **DONE — extracted as `UI/SoftAimController` (not "RecruitController") — see §3.4 table note.**
   The H-key poll moved with it; the X action stays in `ModCore` → `FactionSystem.TryRecruitAtCrosshair`.
6. **Menu slider metadata:** several sliders' max values are far above their config defaults
   (e.g. "Target Active Zombies" max 200 vs default 80; "Max Zombies (Budget)" max 300 vs 120;
   survivor count max 30 vs 4; horde sizes max 100-120 vs 20-40; max zones 10 vs 5). Not a bug, but
   a "ranges vs defaults" consistency pass is worth a Phase 5 look.
   !!User note: i will need a list of all the min-max values and will rebalance around ModConfig.ini!!
   **Delivered in this round — see the slider min/max list in the Phase 5 balance section (§6).**

### 3.3 Duplication

> DONE (this round): `Utils/PedLootUtils.cs`, `Utils/BlipHelper.cs`, and
> `ZombieSystem.EnqueueZombieSpawn` are extracted and wired. HordeSystem now takes a
> `ZombieSystem` in its ctor (ModuleManager wires it) and files horde members via the shared
> enqueue path (still into `horde.Members`, never `_activeZombies`).

| Duplication | Sites | Fix |
|---|---|---|
| `WeaponToPickup` + `DropLoot` | ~~`SurvivorSystem.cs:240-259` and `FactionSystem.cs:1633-1654`~~ (faction copy maps more weapons: PumpShotgun / SawnOffShotgun / CarbineRifle) | **DONE** — `Utils/PedLootUtils.cs` with the union of mappings + `DropLoot(ped, weapon)`; both systems call it. Survivor drops now also map the extra weapons intentionally (was falling through to `WeaponPistol`) |
| Ped minimap blips | ~~`ZombieAI.cs:454-473`, `SurvivorAI.cs:96-115`, `FactionNPC.cs:77-99`~~ | **DONE** — `Utils/BlipHelper.cs` (`Ensure(ref Blip, Ped, sprite, color, scale, show, name?)` + `Delete(ref Blip)`), each caller gated by its own config flag |
| Zombie `SpawnRequest` construction | ~~`ZombieSystem.TryAmbientSpawn` (`:164-191`), `ZombieSystem.SpawnZombieAt` (`:222-246`), `HordeSystem.StartHorde` (`:319-345`)~~ | **DONE** — `ZombieSystem.EnqueueZombieSpawn(pos, runner, origin, onSpawned)`; ambient/debug file into `_activeZombies`, HordeSystem into `horde.Members` + sets `HordeMoveTarget`. `RaiseZombieSpawned` fires after the callback |

### 3.4 `FactionSystem` god-class (1694 lines)

Splits are listed in increasing risk. Do them only after §3.1–§3.3 land.

| Slice | Lines | Contents | Extraction | Risk |
|---|---|---|---|---|
| Static data | ~~85-234, 239-334~~ | 5 `FactionDef`s; `BuildZones` + `GetWaveSpawns` (Territories.txt port) | **DONE** — `Data/FactionCatalog.cs` (`Build()` + the 5 faction-id constants) + `Data/TerritoryCatalog.cs` (`BuildZones`/`GetWaveSpawns`). The FactionSystem ctor calls them; `Zones`/`Definitions` lists stay on FactionSystem; `_survivors`/`_families` are looked up from `Definitions`. External constant references (`PedExtensions`) now use `FactionCatalog.*` | Low — behavior-free move ✅ |
| Soft-aim UI | ~~1200-1324 + fields~~ | ~~`UpdateSoftAim`, `FindSoftTargetUnderCrosshair`, `DrawAimReticule`, `TryGetReticleRing`, `WriteReticleRingPng` (+ `_softAimEnabled`, `_softAimKeyWasDown`, `_softTarget`, `_softAimTimer`, `_reticleRing`, `ReticleRingPath`)~~ | **DONE — named `UI/SoftAimController` (not `RecruitController`)** — user note: soft-aim is a general targeting/highlight system (peds, items, props later); recruiting is just one consumer of the locked target. `SoftAimController` owns the H toggle + reticule + camera-raycast lock + highlight marker and exposes `Ped LockedTarget` + `ClearLock()`. The owner supplies a validity predicate (survivor-only today). `FactionSystem.OnTick` calls `_recruits.UpdateSoftAim()`; the consumer moved with the recruiting split into `Systems/RecruitManager` | Low-Medium — pure UI/input ✅ |
| Weapon loot | 1633-1654 | `WeaponToPickup` + `DropLoot` | Covered by §3.3 `PedLootUtils` | Low |
| Recruiting | ~~1326-1464~~ | ~~`TryRecruitAtCrosshair`, `TryToggleRecruit`, `DismissRecruit`, `DismissAllRecruits`~~ (+ the `_recruits` list, its `UpdatePeds` upkeep loop, the `ClearAllFactions` recruit cleanup, and the soft-aim consumer) | **DONE** — `Systems/RecruitManager.cs` owns `_recruits` + the SoftAimController consumer + the recruit/dismiss actions + the per-tick upkeep (`Update(playerPos)`) + `ClearAll()`. Callbacks into FactionSystem (per the plan's callbacks note): `Func<Ped, FactionNPC> findSurvivorNpc` (wraps `_activePeds.FirstOrDefault(survivor faction)`), `addActivePed`/`removeActivePed` (list moves on join/dismiss), `releaseSlot` (budget). `TryRecruitAtCrosshair`/`DismissAllRecruits` stay public on FactionSystem as thin delegates (ModCore/MenuManager call them); `TryToggleRecruit` became private. `SpawnFactionPed`'s dead `role == Recruit` branch removed (no call site passed Recruit - recruits are adopted peds, never spawned) | Medium — callbacks into the lists accepted ✅ |
| Spawning | ~~511-616, 1111-1198~~ | ~~`TryAmbientSpawn`, `TryBanditPatrol`, `SpawnFactionPed`, `TryMountBike`, `WeightedPick`~~ | **DONE** — `Systems/FactionSpawner.cs` owns the ambient/patrol timers + every faction `SpawnRequest`. Callbacks into FactionSystem (per the plan's callbacks note): `addActivePed` (record spawned peds), `countAlive` (per-faction ambient population), plus the territory queries `isPlayerOwnedZone`/`territoryOwnerAt`/`isInAnyTerritory` (keep ambients out of owned zones, inside hostile turf). `SpawnFactionPed` stayed on the spawner (public) so FactionSystem's defender/wave/guard/raid call sites route through it; `_rng` is shared with FactionSystem so wave picks and model picks use one random stream | Medium — callbacks into the lists accepted ✅ |
| Territory state machine | ~~618-755, 764-796, 798-895, 906-1123~~ | ~~`UpdateZones`, `UpdateHostileDefenseOverride`, defenders/guards, `UpdateTerritoryFights`, `Start/EndTerritoryFight`, `CaptureZone`, `RestoreOwnedTerritories`, `Surrender*`, `RegisterBanditKillNearTerritory`~~ | **DONE** — `Systems/TerritorySystem.cs` owns `Zones` + blips + capture/fight state. FactionSystem keeps peds: TerritorySystem reaches them only via callbacks (`Func<Ped,bool> countLivingZonePeds`, `Action<Ped> cleanupZonePeds`) and spawns defenders/waves/raiders/guards through the `FactionSpawner` (attached via `AttachSpawner` in `FactionSystem.Initialize`). `RebalanceForHorde` budget borrows (hostile defenses + owned raids) moved with the fight state and are ref-counted; released in `ClearAll`. Stable surface kept as FactionSystem delegates: `HostileTerritoryAt`, `IsPlayerOwnedZone`, `IsInAnyTerritory`, `IsPlayerInTerritoryContext`, `HomeFaction`, `RestoreOwnedTerritories`, `SurrenderCurrentZone` | High — done last ✅ |

**Keep stable no matter what** (other systems call these): `HostileTerritoryAt` (`ZombieSystem.cs:132`),
`IsPlayerOwnedZone` (`ZombieSystem.cs:157`), `IsInAnyTerritory` (`HordeSystem`), `IsPlayerInTerritoryContext`
(`FactionSystem.OnTick` → `PedBudgetManager.SetTerritoryContext`), `GetFaction`, `HomeFaction`,
`RestoreOwnedTerritories` (`PersistenceManager.LoadAndApply`), `ClearAllFactions`, `SurrenderCurrentZone`,
`DismissAllRecruits`, `TryRecruitAtCrosshair` (`ModCore`), `GetSummary`/`CountAlive` (`MenuManager`).

## 4. Phased execution order

Each phase is a buildable checkpoint (`dotnet build .\GTAZ\ZombieMod.csproj`) + a targeted in-game
spot-check.

1. **Delete dead code** (§3.1) and its doc rows (`03-File-Reference.md`).
   Spot-check: zombies/hordes/survivors/factions all still behave.
2. **Fix misnames + stale comments** (§3.2, except the save-slot decision).
   Spot-check: config load/save, menu, recruiting still work.
3. **Extract `PedLootUtils`** (§3.3) — unify weapon-drop mapping intentionally. ✅
   Spot-check: kill a survivor + a bandit, both drop their weapon. ✅ playtested
4. **Extract `BlipHelper`** (§3.3) — dedupe the three ped-blip copies. ✅
   Spot-check: each blip toggle (zombie/survivor/faction) applies next tick. ✅ playtested
5. **Extract `ZombieSystem.EnqueueZombieSpawn`** (§3.3) — dedupe the three zombie spawn sites. ✅
   Spot-check: ambient spawn, debug spawn, horde materialization all still work. ✅ playtested
6. **Extract static data** (§3.4) to `Data/FactionCatalog.cs` + `Data/TerritoryCatalog.cs`. ✅
   Spot-check: all 12 territories build with correct home factions + wave points; zone capture works. ✅ playtested
7. **Extract soft-aim UI** (§3.4) to `UI/SoftAimController`; the H-key poll moved with it. ✅
   Spot-check: H toggles reticule, X recruits/dismisses a soft-aim-locked and a crosshair target. ✅ playtested
8. **Docs reconciliation pass** (§5) — update `01`–`04` and `09`/`11` as steps above land. ✅
   All 7 §5 disagreements resolved (verified current API in `01`; `02`/`03`/`04` updated per
   individual rows).
9. **Extract recruiting** (§3.4) to `Systems/RecruitManager.cs`; the soft-aim consumer moved
   with it, FactionSystem keeps `TryRecruitAtCrosshair`/`DismissAllRecruits` as delegates. ✅
   Spot-check: H locks a survivor, X recruits them, X again dismisses, Dismiss All works, recruits
   still follow + drop loot + respect the cap. ✅ built + playtested good
10. **(Optional, later) TerritorySystem split** (§3.4) — step 10's high-risk half, now **done**.
    The **FactionSpawner** half was extracted first (`Systems/FactionSpawner.cs`; FactionSystem
    keeps `TerritoryOwnerAt` for the spawner's callback and routes all defender/wave/raid spawns
    through it). Then the territory state machine moved to `Systems/TerritorySystem.cs` (owns
    `Zones` + blips + capture/fight/raid/surrender state + the ref-counted horde-budget borrows;
    reaches peds only via callbacks, spawns through the FactionSpawner attached in
    `FactionSystem.Initialize`). FactionSystem keeps the stable territory surface as thin
    delegates. **Built clean post-split; dedicated playtest passed (Aug 2026).** ✅

## 5. Code vs. doc disagreements to resolve

1. **`ZombieSystem` field defaults differ from `ModConfig` defaults:** `TargetZombieCount` 70 (code)
   vs 80 (config); `MinSpawnDistance` 50 (code) vs 75 (config). Harmless today
   (`MenuManager.ApplyAllToSystems` overwrites them at startup, `MenuManager.cs:625-641`) but a trap:
   two sources of truth for one number. Decide: sync the field initializers to the config defaults
   (least surprise), or remove the initializers and let `ApplyAllToSystems` own them.
   **DONE — field initializers synced to the config defaults (80/75).**
2. **`03-File-Reference.md` lists `Data/WorldState.cs` as "Persistent world data"** — the file is
   dead (§3.1). Remove the row (keep the file listing, not the purpose claim).
   **DONE — row removed.**
3. **`04-Config-Reference.md` documents 8 dead config props as live** (`ZombieHealth`, `ZombieDamage`,
   `ClearPeds`, `TimeScale`, `MaxFactions`, `FactionsFightEachOther`, `ShowHUD`, `UIScale`).
   Either wire them up as Phase 5 balance (most naturally: `ZombieHealth`/`ZombieDamage` → `ZombieAI`,
   `TimeScale` → a per-frame `SET_TIME_SCALE`, `FactionsFightEachOther` → gate the cross-faction hate
   binds in `PedExtensions.SetupFactionRelationships`) or delete them and update the table.
   **DONE — wired `ZombieHealth`/`ZombieDamage` into `ZombieAI`, wired `TimeScale` per-frame in `WorldSystem`, deleted the other five + removed their table rows.**
4. **Trainer "low gravity" is documented but doesn't exist.** `03-File-Reference.md:56` and
   `04-Config-Reference.md:145` claim `WorldTrainer` has low gravity; no such feature exists
   (`WorldTrainer` = weather + time freeze/set). Remove the claim from both docs.
   **DONE — claim removed from both docs.**
5. **`SoftAimKey` is described as "Hold"** in `04-Config-Reference.md:106` but the code **toggles**
   on the key-press edge (`FactionSystem.cs:1216-1219`). Fix the doc wording.
   **DONE — wording fixed to "Toggle (on key-press edge)".**
6. **Save slots named `*.json` produce `*.json.ini` files** (§3.2.2). Decide: keep for save
   compatibility (recommended) or migrate.
   **DONE — migrated to `*.ini` slot names; existing saves not migrated (user OK'd).**
7. **`01-Architecture.md` still describes the old budget API** (`TryAllocate/Release/Rebalance`, 200
   slots, `config.json`) — it has an inline warning that this is the older design, but the reference
   should state the current API (`TryRequestSlot`/`TryRequestAndSpawn`/`ReleaseSlot`/`PeriodicRescore`/
   ref-counted `RebalanceForHorde`/`SetTerritoryContext`, `GlobalBudget = 120`). Verify current text,
   then update.
    **DONE — `01-Architecture.md` already states the current API** (`TryRequestSlot`/
    `TryRequestAndSpawn`/`AttachPed`/`ReleaseSlot`/`PeriodicRescore`/`RebalanceForHorde`/`SetTerritoryContext`,
    `GlobalBudget = 120`, ref-counted horde override, `PedSlot.IsEssential`); the old-design note at the top
    is historical context, not a reference.

## 6. Phase 5 balance hooks (found while surveying)

- `ZombieAI` stats (`Damage = 15`, `MaxHealth = 100`, speeds, vision) are hardcoded in `ZombieAI.cs`
  (`WalkerAI`/`RunnerAI`); the dead `ZombieHealth`/`ZombieDamage` config props are the natural wires.
  **WIRED — `ZombieHealth`/`ZombieDamage` now drive `WalkerAI`/`RunnerAI` `MaxHealth`/`Damage`.**
- Menu slider maxes vs defaults (§3.2.6) are the balance-tuning surface.
- `HostileTerritoryZombieCap` (40) is read via `ModConfig.Current` inside `ZombieSystem` (`:136`)
  rather than a wired property — fine, just noting it's not surfaced in `ApplyAllToSystems`.

### Slider min/max list (for rebalancing around `ModConfig.ini`)

Current slider metadata as built in `UI/MenuManager.cs` (max values are the lemon `NativeSliderItem`
upper bound; step in parens):

| Menu | Slider | Current default | Max | Step |
|---|---|---|---|---|
| ZOMBIES | Target Active Zombies | 80 | ~~200~~ 100 | 10 |
| ZOMBIES | Max Peds (Budget) | 120 | ~~300~~ 200 | 10 |
| ZOMBIES | Spawn Radius (m) | 200 | 500 | 50 |
| ZOMBIES | Despawn Radius (m) | 300 | ~~1000~~ 500 | ~~100~~ 50 |
| ZOMBIES | Min Spawn Distance (m) | ~~75~~ 80 | 150 | 10 |
| ZOMBIES | Territory Zombie Cap | 30 | 80 | 5 |
| ZOMBIES | Night Runner Chance (%) | 40 | 100 | ~~0~~ 10 |
| ZOMBIES | Zombie Health | 100 | 500 | 10 |
| ZOMBIES | Zombie Damage | 10 | 100 | ~~1~~ 10 |
| WORLD | Time Scale (%) | 100 | 200 | 10 |
| HORDE | Horde Interval (min) | 15 | 120 | ~~1~~ 5 |
| HORDE | Max Horde Size | 40 | 100 | 5 |
| HORDE | Wandering Horde Size | 20 | 100 | 1 |
| HORDE | Horde Night Size | 40 | 120 | 1 |
| HORDE | Horde Night Chance (%) | 50 | 100 | ~~0~~ 10 |
| HORDE | Spawn Distance Min (m) | 100 | ~~400~~ 200 | 50 |
| HORDE | Spawn Distance Max (m) | 300 | ~~800~~ 400 | 50 |
| HORDE | Horde Despawn Distance (m) | 400 | 1000 | 50 |
| HORDE | Horde Runner Chance (%) | 15 | 100 | ~~0~~ 10 |
| SURVIVORS | Survivor Count | 4 | 30 | 1 |
| SURVIVORS | Coward Chance (%) | 25 | 100 | ~~0~~ 10 |
| SURVIVORS | Despawn Distance (m) | 300 | 1000 | 50 |
| FACTIONS | Per-Faction Spawn Target | 10 | 10 | 1 |
| FACTIONS | Outpost Defender Count | 20 | 25 | 1 |
| FACTIONS | Families Encounter Interval (min) | ~~15~~ 5 | 120 | ~~1~~ 5 |
| FACTIONS | Max Recruits | 4 | ~~15~~ 8 | 1 |
| FACTIONS | Recruit Combat Range (m) | 100 | 100 | 5 |
| TERRITORY | Max Player Zones | 5 | 10 | 1 |
| TERRITORY | Territory Attack Interval (min) | 15 | 120 | ~~1~~ 5 |
| TERRITORY | Defense Reinforcement Waves | 3 | 10 | 1 |
| TERRITORY | Defense Kill Quota | 10 | 20 | 1 |

Balance pass (Phase 5, round 8) applied the user's `config-values.txt` annotations (strikethrough =
old value; max changes + step-0 percent sliders to 10 + Families patrol default 15→5 + label
`Max Zombies (Budget)` → `Max Peds (Budget)`). Kept as-is per user: `Per-Faction Spawn Target`
max == default (10). Round 8 follow-up: `TerritoryFightKillQuota` default 4→10 (waves now come in
~10-man groups instead of 4) and surfaced as the new **Defense Kill Quota** slider (max 20) in the
TERRITORY menu. Follow-up in the same round fixed the trainer's step-0 sliders — `Set Money
($10k)` (step 1 = $10k/press, `Multiplier`-pattern) and `Set Ammo` (step 100) — the LemonUI
`Value ± Step` mechanism freezes step-0 sliders at their initial value. All step-0 sliders are now
gone from the menu and trainer.

## 7. Deferred territory fixes (possible future round)

Not yet implemented — flagged from the Aug 2026 Hawick playtest (recruiting extraction round). The
territory loop played fine until one hostile-zone capture ground to a halt: the player cleared the
ring, then killed ambient faction peds for 15 minutes with zero defense-quota logs and no capture.

1. **Hostile-zone capture deadlock (out-of-reach survivor).** Kill credit only counts deaths within
   `TerritoryRadius` (250m) of the zone center (`RegisterBanditKillNearTerritory`), but the capture
   + respawn gate `CountLivingDefenders` counts **every** alive defender regardless of distance. If
   one defender lands at a far mapped point and ends up out of the player's reach — beyond the 300m
   hostile-pursuit range when the player is across the ring, or pathing-stuck in city geometry — then
    `defenders > 0` forever, which holds `RespawnCooldownTicks` full and `ResistTicks = 0`
    (`TerritorySystem.UpdateZones`), so no fresh near-center defenders spawn and capture never fires.
   With the reinforcement cap (`TerritoryFightReinforcementWaves`) spent there is no recovery.
   The "faction enemies" the player then grinds are ambient peds (`HomeZone == null`) that never
   count toward the quota — hence the silent 15 minutes. **Possible fix:** scope `CountLivingDefenders`
   to the `TerritoryRadius` ring (matching the kill ring) and have `CaptureZone` sweep any leftover
   out-of-ring defenders of the old owner so a camped one can't linger.
2. **Wave spawn points are test-quality placements.** The `Territories.txt` mapped points were
   dropped in quickly to verify wave spawning and can be moved to more optimal spots (in-ring,
   on open ground, clear of pathing traps) later. Re-locating them should reduce the out-of-reach
   defender cases from (1) — Hawick's points sit 80-203m from center, up to ~400m in early notes.
3. ~~The Families patrol — misleading name + planned rework~~ **DONE (Phase 5 round 9, user
   design).** `BanditPatrolIntervalMinutes` ("Families Patrol Interval (min)") was a timed
   **open-world encounter** (3-4 Families spawn 100-200m from the player every N minutes, outside
   any territory), NOT a territory attack — the menu label, `04-Config-Reference.md`, and AGENTS.md
   all described it as "patrols/raiding parties" which read like territory action. The rework
   (`TryBanditPatrol` → `TryFamiliesEncounter`, `FactionRole.Crew`) made it a **gang-car chase**:
    1-2 cars (impaler5/superd/cavalcade3, all 4-door — the original faction3/voodoo2 were 2-doors and
    the rear-seat warps failed, lesson 46) full of GSF crew spawn outside any territory, cruise
   until they spot the player (120m + LOS), then chase them in the cars — the driver runs
   `TASK_VEHICLE_CHASE` (aggressive ramming) while passengers drive-by (`TASK_DRIVE_BY`), and the
   player is the crew's only focus (they never stop for ambient zombies). The crew dismounts to
   fight on foot only when the player is on foot and the cars catch up (15m) or the driver dies;
   outrunning them past 300m for ~4s makes them give up and drive off, fading via the normal
   distance-despawn. A notification fires when the patrol rolls in. The menu label is now "Families
   Encounter Interval (min)"; the config key stays `BanditPatrolIntervalMinutes` for save/INI
   compatibility. (Territory attacks still come from each zone's **home faction**, not Families.)

## 8. Suggested verification checklist (in-game)

- Zombies: ambient spawn, night runners, attack/chase/wander, debug spawn/kill/clear.
- Hordes: wandering + night trigger, march + fade, `Clear All Hordes`.
- Survivors: ambient spawn, fight vs flee, despawn on distance, conversion of engine civilians.
- Factions: ambient patrols, The Families patrol, hostile-zone defenders + reinforcement waves,
  owned-zone raids (attack interval), capture + surrender, save/load ownership restore, recruits
  (X / H), Lost riders on bikes.
- World: apocalypse on/off, traffic/cops toggles, wanted level.
- Trainer: god modes (re-assert), vehicle spawn queue, weapons, weather/time.
- Save/load: `DO_AUTO_SAVE` fires, `PlayerData_*.ini` round-trips, owned zones restore.
