# GTAZ — Systems Reference (current state)

Deep reference per system. If you're adding a feature to a specific system, start here, then read
the code. Config defaults for every value mentioned are in `04-Config-Reference.md`.

---

## Budget & spawn pipeline (the #1 constraint)

**`PedBudgetManager`** (singleton) owns a hard 120-slot budget (`GlobalBudget`). A slot is reserved
**before** every ped is created, never after:

- `TryRequestSlot(category, priority, position, out PedSlot)` — reserves a slot, evicting a
  lower-priority/distant slot if full.
- `TryRequestAndSpawn(category, priority, position, Func<Ped> spawnFactory, out PedSlot)` — slot +
  spawn + attach in one call; releases the slot if the factory fails or returns a dead/missing ped.
- `ReleaseSlot(slot)` — on death/cleanup.
- `PeriodicRescore(playerPosition)` — **must run every tick** (from `ZombieSystem.OnTick`); frees
  slots whose ped died/despawned and re-scores priority by distance radius bands.
- `RebalanceForHorde(bool)` — ref-counted. Each `true` borrows the reserved FactionNpc/Survivor
  minimums (those categories become eviction-eligible); each holder must pair with exactly one
  `false`, and minimums restore only when refs hit 0. Must run on abort.
- `SetTerritoryContext(bool)` — when the player is near territory ground the FactionNpc 20-slot
  reserve is restored; otherwise it's 0 (nothing to reserve for).
- `PedSlot.IsEssential` — essential slots (recruits) survive eviction and horde rebalancing.

**`SpawnRequestQueue`** (singleton) defers all mass spawning: 3 grants/pass, 8s TTL (expired
requests fire `OnExpired`), priority aging. Enqueue a `SpawnRequest` with a `SpawnFactory` +
`OnGranted` callback. One shared queue for zombies, hordes, survivors, factions.

> Rule: any new spawn path must go through the budget and, for mass spawns, the queue. Never
> `World.CreatePed` spam; never `CLEAR_AREA_OF_PEDS`.

### Safe spawn positions — `SpawnUtils.FindSafePosition`
`GET_SAFE_COORD_FOR_PED` (flags `2 | 4 | 8`) walks the navmesh for a real walkable spot. Never
`World.GetGroundHeight()` (unstreamed collision → mid-air/embedded/T-pose). `FindSafePosition`
additionally probes the terrain Z at each candidate with `GET_GROUND_Z_EXCLUDING_OBJECTS_FOR_3D_COORD`
(fallback `GET_GROUND_Z_FOR_3D_COORD`) and starts the search at `terrainZ + 1` — this rejects
rooftops/inside-roof candidates and survives flat-anchor Zs.

Territory wave spawns **bypass** the search entirely and use player-mapped points
(`GetWaveSpawn` — see Factions below).

---

## Zombies

**`ZombieSystem`** (ambient) + **`HordeSystem`** (events) + **`ZombieAI`/WalkerAI/RunnerAI** (per-ped).

- Ambient: bursts of 2–4, targeting `TargetZombieCount` (80) within `ZombieSpawnRadius` (200m),
  minimum `MinSpawnDistance` (75m) from the player. Night (20:00–05:00) boosts runner chance.
- `TryAmbientSpawn` skips: player-owned zones (safe territory), and — when the player is inside a
  hostile ring — stops once `HostileTerritoryZombieCap` zombies are already in that ring (a bounded
  garrison stream, not a swarm). Ambient is throttled, never globally suppressed, so the map stays
  populated during hordes.
- `ZombieSystem.OnTick` early-returns while the player is dead or `!Game.Player.CanControlCharacter`.
- Horde members live in `HordeSystem`'s own list; `CleanupDistantZombies` only touches
  `_activeZombies`.
- **ZombieAI states:** `Idle → Wandering → Chasing → Attacking → Fleeing`. Wandering uses
  `TASK_WANDER_IN_AREA` (re-issued on a 2s cadence — the engine can clear self-sustaining tasks).
  Dead/missing targets are dropped back to Wandering. Tasks are reissued only when the task slot is
  free (`GET_SCRIPT_TASK_STATUS != 1`).
- **Ladder protection:** config flag 146 (`CPED_CONFIG_FLAG_DisableLadderClimbing`) +
  `SET_PED_PATH_CAN_USE_LADDERS(ped, false)`.
- **Death physics:** reset clipsets to `move_m@generic` and disable ragdoll at `Health <= 30`.

---

## Survivors (transient)

**`SurvivorSystem`** spawns armed civilians through the budget/queue. Per-spawn `SurvivorCowardChance`
decides fight (`TASK_COMBAT_PED`) vs flee (`TASK_SMART_FLEE_PED`); both wander when no zombie is near.
- Relationship group **Companion (0)** to the player in both directions — damageable (loot drop via
  `CREATE_PICKUP`) but never a player combat target, even on direct damage.
- Fade past `SurvivorDespawnDistance` (~518m). Green `BlipSprite.Friend` blips (`ShowSurvivorBlips`).
- `ConvertNearbyEngineNpcs` re-arms ordinary engine civilians (capped at `SurvivorCount`); skips
  `IsFactionPed()`.
- **Recruiting** converts an ambient survivor into a `FactionNPC` recruit (`FactionRole.Recruit`),
  deletes its transient blip/tracking, follows via `TASK_FOLLOW_TO_OFFSET_OF_ENTITY` (1.2 speed, on
  mode transitions, never per-tick). See "Recruiting" under Factions.

---

## Factions & territory

**`FactionSystem`** (singleton). Owns the 5 `FactionDef`s (built by `Data/FactionCatalog.cs` — the
faction-id constants also live there) and the active faction-ped list. The territory state machine
lives in `Systems/TerritorySystem.cs`, all faction spawn construction in `Systems/FactionSpawner.cs`,
and recruiting in `Systems/RecruitManager.cs` — FactionSystem delegates to all three and keeps the
stable cross-system surface (`HostileTerritoryAt`, `IsPlayerOwnedZone`, `IsInAnyTerritory`,
`HomeFaction`, `RestoreOwnedTerritories`, `SurrenderCurrentZone`, `TryRecruitAtCrosshair`,
`DismissAllRecruits`, `ClearAllFactions`):

| Internal Id | Display | Archetype | Player relation | Accuracy | Spares zombies | Notes |
|---|---|---|---|---|---|---|
| `Survivors` | Survivors | Defensive | Ally (→ Companion) | 60 | no | Shared group with ambient survivors |
| `Bandits` | The Families | Aggressive | Hostile (Hate) | 35 | no | Former "Bandits"; internal Id kept for saves/menu |
| `Military` | Military | Defensive | Hostile | 35 | no | Fort Zancudo + Bolingbroke |
| `Cultists` | Altruists | Defensive | Hostile | 35 | **yes** | Former "Cultists"; one-way zombie truce |
| `TheLost` | The Lost | Aggressive | Hostile | 35 | no | Ambient riders on hexer/daemon |

- **Relationships:** mirrored in both directions (`SET_RELATIONSHIP_BETWEEN_GROUPS`). Factions HATE
  zombies. `PlayerRelation.Ally → Companion(0)`; Hostile → Hate(5). Altruists: faction→zombie
  Neutral, zombie→faction Hate (one-way via `SetRelationshipDirectional`) — cultists don't fight
  zombies but zombies still attack them. All player-facing stances are re-asserted first-tick by
  `EnsurePlayerFacingRelationships()`.
- **Spawn behavior:** all faction spawn construction lives in `Systems/FactionSpawner.cs` (ticked
  from `FactionSystem.OnTick`): weighted ambient spawns (per-faction `FactionSpawnTarget` cap,
  360-tick interval), bandit-family patrols (`BanditPatrolIntervalMinutes`, 5), Lost ambient riders
  (`LostRiderChance`, `TryMountBike`, drive-wander, vehicle-chase then dismount ~20m). **Ambient
  members and patrols only spawn OUTSIDE any territory radius** — territories are event-driven
  ground. Ambient/patrol spawns land 100–200m out (outside the 60m ambient pursuit range).
- **`FactionAI` pursuit:** hostile factions chase the player — territory-bound peds (any `HomeZone`)
  up to 300m, ambient patrols 60m, no LOS gate.

### Territory model
The territory state machine lives in `Systems/TerritorySystem.cs`. 12 zones, built by
`Data/TerritoryCatalog.cs` (`BuildZones` — `GET_ZONE_AT_COORDS` at configured
centers, home faction per zone, 4 player-mapped wave spawn points each; master list:
`05-Data-Reference.md`, source `Territories.txt`).
`TerritoryZone` carries ownership + capture state. "Inside a territory" = the visible
`TerritoryRadius` ring (`SpawnUtils.HorizontalDistance`, X/Y only — never 3D against `zone.Center`,
which carries a flat Z). Owner-colored `GTAOPlayerSafehouse` blips; debug radius ring via
`ADD_BLIP_FOR_RADIUS` (never set `Scale` on a radius blip — it sets the radius in game units).

### Capture loop (hostile zone → player owned)
1. Player enters a hostile ring → `SpawnDefenders` spawns `OutpostDefenderCount` defenders **near
   the zone center** (`FindSafePosition(zone.Center, 5f, 40f)`), falling back to a mapped wave point.
2. Defenders respawn on `DefenderRespawnCooldownSeconds` while the player stays — a hostile zone
   always fights back.
3. Killing defenders fills `FightKills`; at `TerritoryFightKillQuota` a reinforcement wave tops
   defenders back up to `OutpostDefenderCount` (never exceeds it), capped per visit by
   `TerritoryFightReinforcementWaves` (reset on leaving) — so a hostile zone always runs dry.
4. Defenders all dead → 1s grace (`ResistTicks >= 60`) → `CaptureInProgress` latches (stops both
   respawns and reinforcement waves) → `CaptureZone()` flips ownership **instantly** (no hold-timer),
   survivor guards spawn, blip flips green, ambient zombie spawns suppressed inside.
5. Frozen at `MaxPlayerZones` cap: a cleared zone at the cap stays contested instead of capturing.

### Owned-zone raids (timer-only)
`UpdateTerritoryFights` fires a `TerritoryFightWaveSize` raid of the zone's **home faction** when the
player is near an owned zone and `TerritoryFightCooldownMinutes` (the "Territory Attack Interval
(min)" slider) elapses — **no kill acceleration**. The `FightCooldownTicks` gate MUST stay in front
of `StartTerritoryFight`. Raids borrow the horde budget (`RebalanceForHorde(true)`, ref-counted) and
the end-check stays open while `PendingRaiderSpawns > 0` so a raid can't "repel" itself before its
queued spawns materialize (3 grants/pass, 8s TTL).

### Recruiting
Owned by `Systems/RecruitManager.cs` (FactionSystem delegates `TryRecruitAtCrosshair`/
`DismissAllRecruits`). Toggle `SoftAimKey` (H) → `SoftAimController` (a field of `RecruitManager`,
ticked from `FactionSystem.OnTick`) draws a
screen-center reticule (green when locked) and re-evaluates a camera-raycast lock every 3 ticks
(green cylinder over the locked head). Press
`RecruitKey` (X) → `TryRecruitAtCrosshair` → `TryToggleRecruit`: an existing `FactionNPC`, or adopt
an ambient survivor via `SurvivorSystem.DetachAmbientSurvivor`. `SET_PED_AS_GROUP_MEMBER(player
group)`, move to the `GTAZRecruits` group (Companion to player, Like to survivors, Hate to
zombies/factions — stops recruits fighting each other), role = Recruit, `MarkEssential`. Cap
`MaxRecruits`. Revert on abandon. Recruits follow via explicit follow task and fight zombies near
the player. Recruiting never requires aiming a weapon (aiming flipped peds hostile pre-recruit).

---

## Hordes

`HordeSystem`: wandering hordes on `HordeEventIntervalMinutes` + once-per-night sieges
(`HordeNightChance`).
- Announce (notification + sound) → materialize 200–400m off-screen via the shared queue
  (`TARGET_SIZE` requests, priority by distance) → march to a moving `Destination` (`HordeMoveTarget`
  + `HordeArriveRadius` on zombies) → engage the player if perceived, else return to marching →
  deplete on death / despawn past `HordeDespawnDistance` / end at daybreak (night).
- While any horde is live: `RebalanceForHorde(true)` (borrow minimums). `HordeSystem` calls
  `ProcessQueue()` itself while a horde is live (a global ambient-suppression gate previously
  starved the queue and horde requests expired on the 8s TTL).
- Hordes never stage or march through ANY territory (`IsInAnyTerritory`).
- On any horde end (incl. abort): release the budget ref.

---

## Persistence

`PersistenceManager` (singleton):
- Saves `scripts/GTAZ/PlayerData_*.ini` (position/heading/health/armor/owned territory hashes).
  **No weapon fields** — the game autosave owns weapons/money/ammo.
- **Every mod save also fires `DO_AUTO_SAVE`** so game state persists with the mod state.
- `TickAutoSave()` every frame (5-min interval → `autosave.ini`); startup loads freshest save;
  abort writes `abort_save.ini`.
- Serialization is SHVDN `ScriptSettings` (`IniSerializer`, renamed from the misnamed
  `JsonSerializer`). Vector3 stored as invariant "X,Y,Z"; collections as comma-separated;
  parse-by-real-property-type in load.

---

## World / trainer

- `WorldSystem`: apocalypse clearing + time/weather. `GameplaySettings.Tick` applies per-tick
  gameplay tweaks (character switching off, phone off, HUD scale).
- `TrainerManager` (F5): PLAYER/VEHICLE/WEAPON/WORLD submenus + per-category vehicle spawn submenus.
  **God modes are re-asserted every frame while enabled** — the game clears invincibility/proof
  flags on vehicle change/teleport/entity re-creation. No blocking calls. Every `NativeMenu` sets
  `Alignment = Right`.
