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
  `TASK_WANDER_IN_AREA` (re-issued on a ~2s cadence via `120 / _updateInterval` behavior ticks — the
  engine can clear self-sustaining tasks). Chasing uses `TASK_FOLLOW_TO_OFFSET_OF_ENTITY` with the
  same stuck-speed recovery (`Speed < 0.5f` ~30 behavior-ticks → re-issue follow, lesson 50) so a
  task the engine clears mid-stand-off doesn't freeze the zombie.
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
`HomeFaction`, `RestoreOwnedTerritories`, `SurrenderCurrentZone`, `GetActiveLeaveGraces`,
`TryRecruitAtCrosshair`,
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
  360-tick interval), the Families gang-car encounter (`BanditPatrolIntervalMinutes`, 5 — 1-2 cars
  of GSF crew cruise the area and chase the player on sight, see below), Lost ambient riders
  (`LostRiderChance`, `TryMountBike`, drive-wander, vehicle-chase then dismount ~20m). **Ambient
  members and patrols only spawn OUTSIDE any territory radius** — territories are event-driven
  ground. Ambient/patrol spawns land 100–200m out (outside the 60m ambient pursuit range).
- **The Families car chase:** `TryFamiliesEncounter` spawns 1-2 gang cars full of `FactionRole.Crew`
  (4-8 GSF members; impaler5/superd/cavalcade3 — 4-doors only, the old faction3/voodoo2 were 2-doors
  and every rear-seat warp failed, lesson 46). Crew cruises until it spots the player
  (`CrewSpotRange` 120m + LOS), then the driver chases (`TASK_VEHICLE_CHASE`, aggressive ramming)
  and passengers drive-by (`TASK_VEHICLE_SHOOT_AT_PED` — the reliable in-vehicle fire primitive;
  `TASK_DRIVE_BY` is documented as "doesn't seem to do anything" so passengers never shot, lesson 48).
  The drive-by's p2 is a **shoot range** (`CrewDriveByRange`) — `0f` meant never in range — and the
  crew is armed for the car via `SET_CURRENT_PED_VEHICLE_WEAPON` after `MakeFactionLike` (the on-foot
  weapon doesn't carry into the vehicle slot; an unarmed passenger just flips the player off).
  Crew peds run with `BlockPermanentEvents` (set in
  `SpawnCrewForCar`, not `MakeFactionLike` — that's shared) so the engine's ambient AI never hijacks
  a passenger out of the car mid-cruise; a crew member whose initial `TASK_WARP_PED_INTO_VEHICLE`
  failed gets re-warped by the AI net (`TryReWarpCrew`) while the crew is still cruising. The initial
  warp is only attempted once the car model is loaded (otherwise it silently fails on the streaming
  car and the net seats the member during cruise, lesson 49). They pile
  out for a genuine on-foot stand-off only — player on foot, moving slower than a jog, and standing
  their ground within `CrewDismountRange` (15m) for ~2s (otherwise the cars keep harrying/drive-bying
  a moving player) — or when the driver is dead. The exit task is issued once (an exit-pending flag
  stops `TASK_LEAVE_VEHICLE` being re-issued every crew pass, which restarted the exit animation, and
  fires `TASK_COMBAT_PED` the instant the member is on foot — no post-dismount idle, lesson 47). On
  the stand-off the driver's car is **released** to
  the game (parked + grab-able, engine reclaims it later, The Lost bike pattern); a car with living
  crew aboard is also released rather than deleted on cleanup, so it never vanishes mid-scene and no
  car piles build up. Outrunning them past `CrewGiveUpDistance` (300m) for ~4s makes them give up and
  drive off, fading via the normal distance-despawn. The player is the crew's only focus — ambient
  zombies never distract them: the state machine never scans for zombies, and after a stand-off
  `TASK_COMBAT_PED` is re-pointed at the player on a ~5s cadence so native combat retargeting onto a
  swarm can't hold them off the player (zombies still attack the crew — mutual Hate — but focus keeps
  coming back).
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

**Leave grace:** stepping out of the hostile ring doesn't instantly end the attempt. While "in-flight"
takeover progress exists (`FightKills`, pending/respawning defenders, `ResistTicks`, or
`CaptureInProgress`), the attempt is frozen for `TerritoryLeaveGraceSeconds` with a centered on-screen
"Return to {zone} to continue the fight - Ns" countdown (`MenuManager` reads
`FactionSystem.GetActiveLeaveGraces()`). Stepping back in cancels it; on expiry the takeover resets
(FightKills, waves sent, resist progress, respawn timers) so it can't re-arm itself every away tick.
A zone with no progress resets immediately. Living defenders are never cleaned by the reset — they
persist until `FactionDespawnDistance` and the fight resumes on re-entry.

**Surrender (owned zone → home faction):** the menu's surrender item arms `SurrenderPending`; the
actual revert only executes once the player steps OUT of the owned ring (otherwise the newly hostile
defenders would pop on top of them). `SurrenderZone` resets takeover state and grants the full raid
cooldown.

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
- **Sweep-path migration (§3.1):** a wandering horde's destination goes on the *far side of the
  player* (`HordeSpawnDistanceMax + 200m` past them, along a jittered line from spawn through the
  player), so the march sweeps across the player's area instead of a random heading that can walk
  away. The stored `Heading` (direction of travel) drives every later waypoint.
- **Flowing march (§3.3):** every ~5s (`FlowPushInterval` = 300 frames) the destination re-pushes to
  a point ~140m ahead of the horde center (`GetHordeReference`), so the mass flows as one body and
  no member ever "arrives and waits". The cadence is the only task-reissue trigger (lesson 5).
  Stragglers > 25m are reeled back to the center with **hysteresis** (trigger 25m, release 15m,
  `ZombieAI.HordePullingBack`) — without the hysteresis a member hovering on the boundary flipped
  `HordeMoveTarget` every tick and stuttered (lesson 54).
- **Player-only focus (playtest 1):** horde members skip the nearest-ped scan while
  `HordeMoveTarget` is set — the PLAYER is their one target, so random peds can't drag the sweep
  off-course or lead it into gunfire (lesson 53). Ambient zombies keep the full scan.
- **Materialization (lesson 52):** members stream in via the shared queue (3 grants/pass); a horde
  is not depleted while any spawn request is still pending (`ActiveHorde.PendingSpawns`) — counting
  `Members` alone removed a fresh horde on its first tick (ZombieSystem.ProcessQueue runs before
  HordeSystem.OnTick) and orphaned its later-granted members.
- **Engagement cascade (§3.4):** when any member is Chasing/Attacking, the march target re-points at
  the player (within `HordeEngageDistance`) so non-perceiving members converge too — but stops at
  the territory edge if the player stands inside a zone (`ResolveSurgeTarget`).
- **Horder (§3.6):** every horde spawns exactly one `HorderAI` (g_m_m_zombie_02, counts as one of
  the `size` members). While alive it is the horde center — march reference, blip anchor, grouping
  center. It takes no damage while any *other* member lives (proofs applied at construction AND
  re-asserted/cleared per tick, lesson 35), becoming killable once the horde is cleared. The
  distance/territory fade loop skips engaged members so a mid-fight fade can't drop the immunity's
  "other member alive" count (lesson 53).
- **Map blip (§3.2):** `BlipSprite.TeamDeathmatch` (ID 378), short-range, anchored on the
  Horder/centroid, gated by `ShowHordeBlips`, created at announce and deleted on despawn.
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
