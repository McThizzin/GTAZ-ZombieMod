# GTAZ — Architecture (current state)

How the code is organized, what each module does, and how they interact. Check this when you need
to understand the structure or figure out where to add new functionality.

> **Currency note:** the code is the source of truth. Config defaults live in
> `GTAZ/Core/Config.cs`; API names live in the code. Older docs in the repo root described an
> older design (a 200-slot budget with `TryAllocate/Release/Rebalance`, a `config.json`, a real
> JSON serializer, ~70-target zombies). None of those exist anymore — this doc reflects the code.

---

## The one-sentence architecture

Only `ModCore` extends `Script` (one SHVDN3 thread). Every other class is a plain object ticked by
`ModuleManager` in a fixed order every frame, and systems talk to each other through an event bus,
never by reaching into each other.

---

## Folder structure

```
GTAZ/
├── ZombieMod.csproj          # .NET Framework 4.8, SHVDN3, LemonUI. Hardcoded OutputPath +
│                             #   HintPaths → D:\SteamLibrary\...\Grand Theft Auto V\ (build
│                             #   drops ZombieMod.dll straight into the GTA V scripts folder)
├── ModCore.cs                # Entry point — the ONLY Script subclass (key input, OnTick/OnAborted)
├── ModuleManager.cs          # Dependency-ordered init, fixed tick order, first-tick actions, abort
│
├── Core/
│   ├── Config.cs             # ModConfig singleton → scripts/GTAZ/ModConfig.ini (ScriptSettings)
│   ├── Logger.cs             # File logger → scripts/GTAZ.log (deleted/recreated each session)
│   ├── PedBudgetManager.cs   # Global ped slot budget (GlobalBudget = 120) + eviction
│   └── SpawnRequestQueue.cs  # Deferred spawn queue: 3 grants/pass, 8s TTL, priority aging
│
├── Systems/
│   ├── WorldSystem.cs        # Apocalypse mode, cop/traffic/scenario clearing, time/weather
│   ├── ZombieSystem.cs       # Ambient spawns, night runners, blips
│   ├── HordeSystem.cs        # Wandering hordes + once-per-night siege (own ped list)
│   ├── SurvivorSystem.cs     # Transient armed civilians (fight/flee), spawned at horde announce
│   ├── FactionSystem.cs      # Faction/territory orchestration (singleton) — delegates to the split-outs
│   ├── FactionSpawner.cs     # Faction spawn construction (ambients, patrol, defenders/waves/raiders) → queue
│   ├── TerritorySystem.cs    # Territory state machine: zones, blips, capture/raid/defense, surrender/restore
│   ├── FactionNPC.cs         # Per-faction-ped wrapper (extends SurvivorNPC)
│   ├── RecruitManager.cs     # Recruiting: recruit list + soft-aim consumer + recruit upkeep
│   ├── GameplaySettings.cs   # Per-tick gameplay tweaks (apocalypse world-state helpers)
│   └── PersistenceManager.cs # Player saves + emergency save on abort (singleton)
│
├── AI/
│   ├── ZombieAI.cs           # Abstract base state machine (idle/wander/chase/attack/flee)
│   ├── Perception.cs         # Static sight/sound helpers
│   ├── SurvivorAI.cs         # Armed-human behavior (fight vs flee, wander)
│   ├── FactionAI.cs          # Faction ped behavior (combat / guard / patrol / follow)
│   └── ZombieTypes/
│       ├── WalkerAI.cs       # Slow melee, 40m sight, 0.8 speed
│       └── RunnerAI.cs       # Fast melee, 60m sight, 1.8 speed, night-only
│
├── UI/
│   ├── MenuManager.cs        # LemonUI MenuPool + menus (MAIN/FACTIONS/TERRITORY/SETTINGS/DEBUG)
│   ├── SoftAimController.cs  # Generic soft-aim targeting (H toggle + reticule + lock); recruiting is one consumer (owned by RecruitManager)
│   └── Trainer/
│       ├── TrainerManager.cs # F5 trainer root + Player/Vehicle/Weapon/World submenus
│       └── Features/         # PlayerTrainer, VehicleTrainer, WeaponTrainer, WorldTrainer
│
├── Events/
│   ├── GameEvents.cs         # Central event bus (pub/sub)
│   ├── ZombieEvents.cs       # Zombie spawn/death events
│   └── FactionEvents.cs      # Zone captured, faction NPC death, recruit joined/left
│
├── Data/
│   ├── FactionDef.cs         # Static per-faction data + FactionArchetype/PlayerRelation enums
│   ├── FactionCatalog.cs     # Builds the 5 FactionDefs + faction-id constants (FactionSystem ctor calls it)
│   ├── TerritoryCatalog.cs   # Builds the 12 territories (centers + home factions + mapped wave spawns)
│   ├── TerritoryZone.cs      # Capturable game-zone + ownership/capture state + wave points
│   ├── PlayerData.cs         # Player save data (position/heading/health/armor/territories only)
│   ├── VehicleCatalog.cs     # Trainer vehicle list (vehicles.ini, fallback built-in list)
│   └── Serialization/
│       └── IniSerializer.cs  # Writes .ini via SHVDN ScriptSettings (was misnamed JsonSerializer)
│
├── Extensions/
│   ├── PedExtensions.cs      # MakeZombieLike, relationship setup + EnsurePlayerFacingRelationships,
│   │                         #   IsHumanoid/IsSurvivor/IsFactionPed, clipset/animation-dict helpers,
│   │                         #   MakeFactionLike, MakeSurvivorLike
│   └── WorldExtensions.cs    # Density helpers
│
└── Utils/
    ├── MathUtils.cs          # Clamp(int)
    ├── SpawnUtils.cs         # Safe spawn finding (GET_SAFE_COORD_FOR_PED + terrain recheck); HorizontalDistance lives here
    ├── PedLootUtils.cs       # Shared weapon-loot drop for killed human peds (DropLoot + WeaponToPickup)
    └── BlipHelper.cs         # Shared ped minimap-blip create-or-reuse/delete pattern
```

---

## Module responsibilities

### ModCore.cs — the only `Script` subclass
- Loads `ModConfig` in the constructor **before** hotkeys are read, then creates `ModuleManager`.
- `OnTick()` → `ModuleManager.Tick()`. `OnAborted()` → `ModuleManager.Abort()` (emergency save,
  faction/horde cleanup).
- `OnKeyUp`: menu, trainer, and `RecruitKey` (X → `FactionSystem.TryRecruitAtCrosshair`, which
  delegates to `RecruitManager`). The
  soft-aim reticule/lock is polled in `FactionSystem.OnTick` (via `RecruitManager.UpdateSoftAim`)
  while `SoftAimKey` (H) is toggled — there is no key-up routing for it.
- **Critical:** never call `Script.Wait()` in a constructor, and never read
  `Game.Player.Character`-anything in a constructor — SHVDN3 can run constructors before the game
  is ready, and natives issued that early can be **silently dropped** (see the relationship trap
  below). Blocking init lives in first-tick actions.

### ModuleManager.cs — orchestrator
- Owns one instance of each system + a fixed ordered list of `_tickActions` / `_abortActions`.
- **Tick order:** first-tick one-shots (`EnsurePlayerFacingRelationships`, then
  `EnsureClipsetsLoaded` / `EnsureAttackAnimLoaded` / `EnsureFacialDictsLoaded`), then
  `GameplaySettings.Tick → World.OnTick → Zombies.OnTick → Horde.OnTick → Survivors.OnTick →
  Factions.OnTick → [startup save-load one-shot] → Menu.Process → Menu.Tick → Trainer.Tick →
  Persistence.TickAutoSave`.
- **First-tick pattern:** blocking/player-dependent init runs as one-shot actions added to
  `_tickActions` (constructor-time native drops are real — see `PedExtensions.EnsurePlayerFacingRelationships`).
- **Startup load:** on the first tick (player spawned, zones built, after `Factions.OnTick`) it
  resumes the freshest save (autosave first, else manual).
- **Abort order:** `World → Zombies → Horde → Survivors.ClearAllSurvivors →
  Factions.ClearAllFactions → Persistence.Save("abort_save.ini") → GameEvents.RaiseModAborted`.

### Core/Config.cs — `ModConfig.Current`
- All tunable parameters (spawn rates, budgets, AI ranges, keybinds, faction/territory settings).
- Round-trips to `scripts/GTAZ/ModConfig.ini` via `IniSerializer` (SHVDN `ScriptSettings`; renamed
  from the misnamed `JsonSerializer`). `Save()` fires on settings-menu close and on abort. Full table:
  `04-Config-Reference.md`.

### Core/Logger.cs
- File logger → `scripts/GTAZ.log` (deleted/recreated each session). `Logger.Info/Warn/Error`,
  timestamps, stack traces. Used for spawn/AI debugging and verifying runtime state.

### Core/PedBudgetManager.cs — the #1 architectural constraint
Singleton (`PedBudgetManager.Instance`). GTA V caps at ~256 concurrent peds; this enforces a hard
**slot** budget (`GlobalBudget = 120`) across all systems.

- Categories: `Zombie, FactionNpc, Survivor, Ambient`. Priorities: `Distant, Background, Active,
  Critical` (scored by distance radius bands with hysteresis).
- **Category minimums:** Zombie 70, FactionNpc 20, Survivor 8, Ambient 0. The FactionNpc reserve
  **only exists in territory context** (`SetTerritoryContext(true)`, called by `FactionSystem` when
  the player is near a zone) — outside territory ground a 20-slot faction reserve would be dead
  weight on the zombie budget.
- **API:** `TryRequestSlot(category, priority, position, out PedSlot)` /
  `TryRequestAndSpawn(...)` / `AttachPed(slot, ped)` / `ReleaseSlot(slot)` /
  `PeriodicRescore(playerPosition)` / `RebalanceForHorde(bool)` / `SetTerritoryContext(bool)`.
- `PedSlot.IsEssential` — essential slots (recruits) survive eviction and horde rebalancing.
- `RebalanceForHorde` is **ref-counted** (`_hordeOverrideRefs`, `HordeOverrideActive => refs > 0`):
  hordes and territory fights each hold one ref, so they can be live simultaneously without
  stacking; minimums restore only when the last holder releases, and it must run on abort too.
- **Every spawn path must reserve a slot first.** No exceptions.

### Core/SpawnRequestQueue.cs
Singleton (`SpawnRequestQueue.Instance`). Deferred spawn queue preventing frame spikes.
- Systems enqueue a `SpawnRequest` (SpawnFactory + OnGranted + optional OnExpired).
- 3 grants per pass; 8-second TTL (expired requests fire `OnExpired`); priority aging. Budget is
  checked before each grant. One shared queue for zombies, hordes, survivors, factions.

### Systems/WorldSystem.cs
Apocalypse mode: clears traffic, cops, and ambient scenarios around the player, plus time/weather
override.
- **Zombie-safe:** iterates nearby peds/vehicles and calls `MarkAsNoLongerNeeded()` on non-zombies
  **only** (`ClearAmbientPopulation`), skipping survivor and faction peds.
- **Never uses `CLEAR_AREA_OF_PEDS`** — it deletes zombies too.

### Systems/ZombieSystem.cs
Ambient zombie spawning and lifecycle.
- Targets `TargetZombieCount` (80) within `ZombieSpawnRadius` (200m); uses
  `SpawnUtils.FindSafePosition` (navmesh + terrain recheck) — never `World.GetGroundHeight()`.
- Night modifier: more runners 20:00–05:00.
- `TryAmbientSpawn` refuses positions inside player-owned zones; caps density inside hostile
  territory rings at `HostileTerritoryZombieCap`. Hordes are unaffected (own list + shared queue).
- Calls `PedBudgetManager.PeriodicRescore()` every tick (budget leaks otherwise).
- Early-returns while the player is dead or `!Game.Player.CanControlCharacter`.

### Systems/HordeSystem.cs
Dynamic horde events: wandering hordes on a timer + once-per-night sieges.
- Announced (notification + distant-gunfire sound cue + cam shake) **before** any entity spawns —
  no survivors dropped at the site (playtest 2 removed the old `SpawnAt` call; horde members are
  player-only, so site survivors just shot the passing horde and ignored `SurvivorCount`).
- Members live in `HordeSystem`'s own list — **not** `ZombieSystem._activeZombies` — so
  `CleanupDistantZombies` never despawns them.
- While any horde is live it borrows reserved minimums via `RebalanceForHorde(true)` (ref-counted);
  ambient spawning is throttled in `ZombieSystem.TryAmbientSpawn` (distance + target count), not
  globally suppressed, so the map stays populated.
- Hordes never stage or march through ANY territory (`FactionSystem.IsInAnyTerritory`).
- Phase 5 (DOCS/12): wandering hordes sweep across the player on a stored `Heading`; the march is a
  cadence-based flow (`FlowPushInterval`, destination ~140m ahead of the Horder/centroid) with
  >25m-straggler pull-back; engaged hordes surge toward the player within `HordeEngageDistance`
  (stopped at the territory edge); a skull map blip (`ShowHordeBlips`) tracks each horde. Each horde
  spawns one `HorderAI` (horde center, immune while any other member lives — see
  `AI/ZombieTypes/HorderAI.cs`).

### Systems/SurvivorSystem.cs
Transient armed human encounters: fighters use `TASK_COMBAT_PED`, cowards `TASK_SMART_FLEE_PED`
(chosen per-spawn by `SurvivorCowardChance`); both wander when no zombie is near.
- Relationship group **Companion (0)** to the player — damageable (killing one drops a weapon
  pickup via `CREATE_PICKUP`, null model → default weapon mesh) but never a combat target, even
  when the player damages them.
- Green minimap blips (`BlipSprite.Friend`, config `ShowSurvivorBlips`).
- Fade once the player passes `SurvivorDespawnDistance` (~518m default) so exploring keeps
  producing fresh encounters.
- `ConvertNearbyEngineNpcs` re-arms ordinary engine-spawned civilians into survivors (capped at
  `SurvivorCount`); skips zombie/survivor/**faction** peds (converting a bandit double-wraps it).

### Systems/FactionSystem.cs
Singleton (`FactionSystem.Instance`). Owns the 5 `FactionDef`s, the active faction-ped list, and the
recruiting/territory/spawning orchestration. See `02-Systems-Reference.md` → "Factions & territory"
for the full capture/wave/raid mechanics. Territory state (zones, capture, raids, surrender,
save-restore) is delegated to `TerritorySystem`; spawning (ambient/patrol/defender/wave/raid
`SpawnRequest`s) to `FactionSpawner`; recruiting (soft-aim + recruit/dismiss + recruit upkeep) to
`RecruitManager`. FactionSystem keeps the stable public surface (`TryRecruitAtCrosshair`,
`DismissAllRecruits`, `HostileTerritoryAt`, `IsPlayerOwnedZone`, `IsInAnyTerritory`,
`HomeFaction`, `RestoreOwnedTerritories`, `SurrenderCurrentZone`, `ClearAllFactions`) as thin delegates.

### Systems/FactionSpawner.cs
Owns every faction ped's `SpawnRequest`: ambient faction members (weighted by
`AmbientSpawnWeight`, forced to the holding faction inside hostile turf), The Families' gang-car
encounter (`TryFamiliesEncounter`), and all territory-bound peds (`SpawnFactionPed` — defenders,
reinforcement waves, raid raiders, outpost guards) plus Lost biker mounting. Territory-bound
spawns enqueue Critical so they grant ahead of hordes/ambient zombies; spawned peds are
essential-marked and recorded into FactionSystem's active list via callbacks.

### Systems/TerritorySystem.cs
Owns the 12 `TerritoryZone`s (built by `TerritoryCatalog`), their blips, and all capture/raid/
defense state: hostile-zone takeover (defenders, kill-scaled reinforcement waves, capture on a
cleared ring), owned-zone timer raids (`TerritoryFightCooldownMinutes`), surrender,
save-restore, and the per-ring queries ZombieSystem/HordeSystem use (`HostileTerritoryAt`,
`IsPlayerOwnedZone`, `IsInAnyTerritory`, `IsPlayerInTerritoryContext`). Reaches the peds only via
callbacks (living-ped counts + zone-ped cleanup) and spawns defenders/raiders/guards through the
`FactionSpawner` (attached via `AttachSpawner` after both exist). Holds the ref-counted
`RebalanceForHorde` borrow for hostile-zone defenses and owned-zone raids, released in `ClearAll`.

### Systems/RecruitManager.cs
Owns the recruit list and everything that touches it: the `SoftAimController` consumer (with the
survivor-only validity predicate), `TryRecruitAtCrosshair`/`TryToggleRecruit`/dismiss actions, and
the per-tick recruit upkeep (missing/dead cleanup + persistent re-assert). Calls back into
FactionSystem for the active-ped list moves and budget-slot release. FactionSystem keeps
`TryRecruitAtCrosshair`/`DismissAllRecruits` as thin public delegates (ModCore/MenuManager call them).

### Systems/FactionNPC.cs
Per-faction-ped wrapper extending `SurvivorNPC`: faction ref, role (`FactionRole`:
Patrol/Guard/Raider/Recruit), homeZone, budget slot, equipped weapon, `MarkEssential`/`ClearEssential`,
`Death`/`Cleanup`. Lost riders own a spawned bike (released to the game on rider death).

### Systems/PersistenceManager.cs
Save/load. Player saves `scripts/GTAZ/PlayerData_*.ini` (position/heading/health/armor/owned
territories — **no weapon fields**); autosave every 5 min → `autosave.ini`; emergency
`abort_save.ini` on abort. **Every mod save also fires the game's `DO_AUTO_SAVE`** so
weapons/money/ammo/stats persist in the GTA save — the mod save is not authoritative for game state.

---

## AI

### AI/ZombieAI.cs — abstract zombie base
State machine: `Idle → Wandering → Chasing → Attacking → Fleeing`. `Update()` every tick;
perception via `Perception` (distance + LOS, doubled hearing for the player).
- Attack = `TASK_PLAY_ANIM` (dict requested/loaded first) + `APPLY_DAMAGE_TO_PED`, cooldown-based.
- Minimap blips (`UpdateBlip`, config `ShowZombieBlips`).
- **v1 idle fixes:** dead targets are dropped back to Wandering (no more standing stuck against a
  corpse), and the self-sustaining `TASK_WANDER_IN_AREA` is re-issued on a 2s cadence
  (`_wanderReissueTimer`) because the engine can clear it.

### AI/ZombieTypes/WalkerAI.cs & RunnerAI.cs & HorderAI.cs
Walker: 0.8 speed, 40m sight, melee. Runner: 1.8 speed, 60m sight, night-only (20:00–03:00).
Horder (Phase 5): g_m_m_zombie_02, 1.3 speed, wide attack range; the horde's center — spawned one
per horde by `HordeSystem`, immune (proofs re-asserted every tick) while any other horde member
lives, killable once the horde is cleared.

### AI/Perception.cs
Static: `CanSee` (distance + LOS), `CanHear` (distance, doubled for player), `HasLineOfSight`.

### AI/SurvivorAI.cs
Armed-human behavior: combat vs flee, wander when clear, mode-gated task reissue.

### AI/FactionAI.cs
Faction behavior. Any ped whose relationship group HATES ours becomes a `TASK_COMBAT_PED` target;
otherwise role tasks — guards patrol `TASK_WANDER_IN_AREA` 100m around their post, recruits follow
via explicit `TASK_FOLLOW_TO_OFFSET_OF_ENTITY` (formation AI alone didn't drive follow), everyone
else wanders their origin. **Hostile factions home in on the player** (no LOS gate): territory-bound
peds (any `HomeZone`) chase up to 300m, ambient patrols 60m. Tasks reissued only on mode/target
change, plus on `_mode == Mode.None` (spawn/role-change) and a stuck-scenario fallback
(`Speed < 0.5f` ~1.5s → `ClearAll()` + re-issue) because survivor peds run with
`BlockPermanentEvents=false` and the engine can freeze them in ambient scenario tasks.

---

## Data flow

### Ambient zombie spawn
1. `ZombieSystem.OnTick` → `PeriodicRescore(player)` (every tick — frees dead slots).
2. `TryAmbientSpawn()` throttled by distance + target count.
3. Skip if position is inside a player-owned territory zone.
4. `SpawnUtils.FindSafePosition(player, min, max)` → `GET_SAFE_COORD_FOR_PED` + terrain recheck.
5. `PedBudgetManager.TryRequestSlot(Zombie, priority, pos)` → `SpawnRequestQueue.Enqueue`.
6. Queue grants 3/pass → `SpawnFactory` → `CREATE_PED` → `MakeZombieLike` → WalkerAI/RunnerAI.
7. `AttachPed(slot, ped)`; `ZombieEvents.RaiseZombieSpawned()`.

### Territory capture (see `02-Systems-Reference.md` for the full loop)

### Recruiting (see `02-Systems-Reference.md`; owned by `Systems/RecruitManager.cs`)

### World clearing
`WorldSystem.OnTick` every ~60 frames while apocalypse mode is on → nearby peds/vehicles →
`MarkAsNoLongerNeeded()` on non-zombies only (survivors/faction peds skipped) → densities zeroed.

---

## Key architectural decisions

1. **Only ModCore extends Script.** SHVDN registers each `Script` subclass as a separate thread;
   multiple scripts = multiple threads = race conditions, perf issues, crashes. One script, one
   thread, systems ticked sequentially in fixed order.
2. **PedBudgetManager is a first-class constraint.** A slot is reserved **before** every spawn,
   never as an optimization pass; `PeriodicRescore()` frees dead/missing slots every tick.
3. **Systems communicate via events.** Systems raise events and subscribe to what they care about,
   holding no cross-references (peers are passed into constructors only where unavoidable, e.g.
   `HordeSystem(Survivors)`).
4. **`GET_SAFE_COORD_FOR_PED` for spawn positions** (+ terrain probe in `SpawnUtils.FindSafePosition`).
   `World.GetGroundHeight()` samples unstreamed collision and silently returns bad Z.
5. **Zombie population comes from ambient + horde spawning only.** The old corpse-infection /
   `CLONE_PED` reanimation was removed at the user's request and must not be re-added.
6. **Zombie-safe world clearing.** `CLEAR_AREA_OF_PEDS` deletes zombies too.
7. **Faction relationships are mirrored in both directions** (`SET_RELATIONSHIP_BETWEEN_GROUPS`
   is directional; every stance is written for (A,B) and (B,A)).
8. **Recruits are essential.** Budget eviction / horde rebalancing would delete a squad member
   mid-fight.
9. **Player-facing relationships are re-asserted on the first tick.**
   `PedExtensions.EnsurePlayerFacingRelationships()` re-applies survivor/recruit/faction stances
   against the live player group — the constructor-time bind can be silently dropped because
   SHVDN3 can run constructors before the game is ready.
10. **Friendly NPCs use Companion (0), never Respect/Neutral.** The native combat AI treats a
    Respect or Neutral ped the player damages as a combat target; Companion never does.
