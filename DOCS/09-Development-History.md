# GTAZ — Development History (design log)

What changed and why, round by round. This is **context, not current state** — for what the code is
today see the reference docs (01–05). For things that were tried and failed/removed, see
`10-Archive.md`.

---

## Phase 1 — Foundation ✅
ModCore (the only `Script`), ModuleManager (fixed tick order), ModConfig + ScriptSettings
serialization, Logger, WorldSystem (apocalypse mode: traffic/cop/scenario clearing + time/weather),
LemonUI MenuManager, extensions, ModelCache.

## Phase 2 — Zombie core ✅
PedBudgetManager (120-slot budget) + SpawnRequestQueue (3 grants/pass, 8s TTL), ZombieAI state
machine + Walker/Runner, Perception (sight/hearing), ZombieSystem (ambient + corpse reanimation —
later removed, see Phase 4 round 3), ZombieEvents, night runner modifier. Multiple rounds of
AI/animation/spawn fixes (lessons 1–25).

## Phase 3 — Horde events ✅
Wandering hordes on a timer + a rarer once-per-night siege that hunts the player. Announced
(notification + sound) *before* any entity spawns; materialize 200–400m off-screen through the
shared queue; march to a moving destination; fade via `MarkAsNoLongerNeeded`. While any horde is
live, `RebalanceForHorde(true)` borrows the reserved faction/survivor minimums. Members live in
`HordeSystem`'s own list so ambient cleanup never despawns them.

- **First-pass fix:** the announce played but no horde appeared — ambient suppression gated the only
  `SpawnRequestQueue.ProcessQueue` caller, so horde requests expired on the 8s TTL. `HordeSystem`
  now calls `ProcessQueue()` itself while any horde is live.
- **Second-pass feedback:**
  - Removed `SetAmbientSuppressed` entirely — it stopped ALL ambient spawns for the horde duration
    and left the map empty if you couldn't find the horde. Ambient is throttled in `TryAmbientSpawn`
    (distance + target count), never globally suppressed.
  - Rooftop spawns: `FindSafePosition` rejects candidates >4.5m above the ground surface below
    (lesson 34 refinement).
  - Survivors: green blips, fade past `SurvivorDespawnDistance`, `ConvertNearbyEngineNpcs` re-arms
    engine civilians.
  - Survivor combat relationship flip-flopped here (Neutral → Respect → Companion) — full saga in
    lessons 27/30/32 and `10-Archive.md`.

## Phase 4 — Factions & Territory ✅ (v1 shipped)

Faction/territory roll-out: survivor outposts, hostile faction turf, player territory capture +
recruiting. Data layer (`FactionDef`, `TerritoryZone`, `FactionEvents`), mirrored relationship
matrix, `FactionSystem` (5 factions, 12 zones, weighted ambient spawns, patrols, defender waves,
capture loop, counter-attacks, recruiting), `FactionAI` + `FactionNPC`, module/menu integration.

### Round 1 — Playtest-fix
- Combat attributes 25/68 (`CA_DISABLE_SECONDARY_TARGET`, `CA_DISABLE_REACT_TO_BUDDY_SHOT`) in
  `MakeSurvivorLike`/`MakeFactionLike`.
- Recruits moved to their own `GTAZRecruits` group (Companion to player, Like to survivors, Hate to
  zombies/factions) — fixes 2+ recruits fighting each other.
- Soft-aim recruiting (H reticule → X recruit); recruiting never requires aiming a weapon.
- Defender respawns on a cooldown while the player stays in a hostile zone.
- Counter-attack waves with kill quota + horde-budget borrow; hordes never stage in owned zones.

### Round 2 — Second playtest
- **Radius rings invisible:** `Blip.Scale` on an `ADD_BLIP_FOR_RADIUS` blip sets the *radius in game
  units*, not a display multiplier; short-range blips never render on the pause map (lesson 29).
- **No territory waves:** fights gated on `HordeOverrideActive` silently disabled all counter-attacks
  whenever a wandering horde was live; un-reset `FightKills` re-armed the next siege instantly.
  `RebalanceForHorde` became **ref-counted**; `EndTerritoryFight` resets `FightKills`; a
  `FightTicks` grace stops the fight declaring itself "repelled" before queued raiders spawn.
- **Survivors still hostile:** scripted `HasBeenDamagedBy(player)` aggro windows misfire on stray
  bullets — deleted; attributes 25/68 + Companion group do the work (lesson 30).
- Soft-aim redesigned to a screen-center reticule + green cylinder marker.

### Round 3 — Third playtest
- **Survivors → Respect (1)** — superseded next round (lesson 30).
- **Faction waves now guaranteed** — timer fallback added alongside the kill quota
  (`TerritoryFightCooldownSeconds`); kills accelerate, the timer guarantees (lesson 31).
- **Corpse-infection mechanic removed** (user request) — `ScanForCorpseInfection`/`ReanimateCorpse`,
  infection events, config, menu sliders, and `PlayerData.Infection` deleted. Do not re-add.
- RecruitKey back to X (user request) with the H soft-aim flow.

### Round 4 — Fourth playtest
- **Survivors → Companion (0)** — Respect still retaliates on direct damage; Companion never does.
  Final relationship state (lessons 30/32).
- **Owned zones bandit-free between attacks** — `TryAmbientSpawn`/`TryFamiliesEncounter` skip owned
  zones; patrol no longer anchors to a held zone (that was "attacked over and over").
- **Attack frequency is a slider** — `TerritoryFightCooldownSeconds` 120→300 as "Territory Attack
  Interval".
- **Territory events are per-area horde mechanics** (user decision): hostile-zone kills fill a
  defense quota → reinforcement waves; owned-zone raids became **timer-only**; hordes never stage or
  march through any territory.
- **Friend-blip-in-bandit fixed** — `ConvertNearbyEngineNpcs` now skips `IsFactionPed()`.

### Round 5 — Fifth playtest
- **Owned-zone raid loop fixed** — the round-4 rewrite dropped the `FightCooldownTicks` gate with
  the kill-quota condition; restored as its own statement, spawn grace raised to 300 ticks
  (lesson 33).
- **Hostile zones now capturable** — escalation tops defenders back up to `OutpostDefenderCount`
  only (never stacks a full wave on top), capped by `TerritoryFightReinforcementWaves` (lesson 33).
- **No more rooftop/inside-roof spawns** — terrain probe switched to
  `GET_GROUND_Z_EXCLUDING_OBJECTS_FOR_3D_COORD` (lesson 33).
- **Survivor-hostility root-caused** — player-facing relationships were bound in the `ModCore`
  constructor, where SHVDN3 can silently drop natives; `EnsurePlayerFacingRelationships()` re-asserts
  them first-tick (lesson 33).

### Round 6 — Sixth playtest (mapping pass)
- **Real territory centers** — all 12 replaced with player-picked in-game coordinates (no flat
  `Z=40` anchor), keyed by resolved game-zone name (lesson 34).
- **All 12 territories mapped with wave spawns** — 4 player-verified points each in `Territories.txt`
  → `GetWaveSpawns`; waves use mapped points (never a safe-position search), excluding the point
  closest to the player.
- **Capture is instant on a full clear** — `ZoneCaptureTime`/`ZoneCaptureRate` hold-timer removed;
  clear + 1s grace → ownership flips; frozen at `MaxPlayerZones` cap.
- **Hostile zones feel held** — ambient occupants of the holding faction in hostile rings; `FactionAI`
  hostile factions home in on the player (territory peds 300m, ambient 60m).
- **Trainer god modes re-asserted every frame** (lesson 35).
- **Menu alignment** — every `NativeMenu` sets `Alignment = Right` (Center throws).
- **Stale-DLL trap** — capture-time slider persisted after source removal; clean rebuild + binary
  grep became the verification standard (lesson 36).

### Round 7 — v1 polish
- **Ambient zombies standing idle** — dead targets dropped back to Wandering; wander task re-issued
  on a 2s cadence (lesson 37).
- **Survivor guards standing around** — guards patrol `TASK_WANDER_IN_AREA` 100m; idle task issued at
  `_mode == None`; stuck-scenario fallback (`Speed < 0.5f` ~1.5s → `ClearAll()` + re-issue)
  (lesson 38).
- **Autosave** — every mod save fires `DO_AUTO_SAVE`; dead weapon fields removed from `PlayerData`
  (lesson 39).
- **Defenders at the zone center** — initial defenders spawn via
  `FindSafePosition(zone.Center, 5f, 40f)`; territory-bound peds cleaned by zone-center distance
  (lesson 40).
- Faction roll-out completed: The Families (formerly Bandits), Military, Altruists (formerly
  Cultists), and The Lost all live with home-faction zone mapping, Lost ambient riders, and the
  Altruist-zombie truce.

## Post-v1
Documentation revamp (this `DOCS/` directory). Next: Phase 5 (Polish & Balance) + a refactoring
plan for the code — see `06-Roadmap.md`.

### Families car-encounter bugfix round (post-v1, Phase 5 polish start)
The `TryFamiliesEncounter` crew (committed at `fe39ab1`) was playtested and hit five bugs. All
fixed in one round:
- **Crew leaving the car / wasn't in a car:** passengers got no scripted cruise task and the engine's
  ambient AI hijacked them out of the car (SHVDN3 `BlockPermanentEvents` defaults false); the initial
  `TASK_WARP_PED_INTO_VEHICLE` could also fail while the car streamed in. Crew peds now set
  `BlockPermanentEvents` in `SpawnCrewForCar` (not `MakeFactionLike` — shared), and the AI re-warps a
  failed-warp member during cruise (`TryReWarpCrew`, cadence + attempt cap).
- **Car vanished mid-scene:** the car's lifetime was tied to the driver alone — driver death
  `ReleaseVehicle` or driver evict `DeleteVehicle` removed it while the crew was still around it. The
  driver now releases the car on the stand-off dismount (via the AI's `OnCrewStandOff` callback), and
  `DeleteVehicle` is occupant-aware — a car with living crew aboard is released to the game, not
  deleted. Cars behave like The Lost bikes: parked + grab-able after a stand-off, engine-reclaimed
  later, no piles (give-up drive-off cars still clean up).
- **Pulled up + got out immediately:** the dismount gate (`nearOnFoot`, on-foot + ≤15m) tripped the
  instant a car reached an on-foot player. Now a real stand-off is required — player on foot, slower
  than a jog (not fleeing), standing ground within `CrewDismountRange` for ~2s — so an on-foot player
  gets drive-by passes before the crew bails.
- **Crew attacked zombies instead of the player:** after dismount, native `TASK_COMBAT_PED`
  retargeted onto engaging zombies (mutual Hate). On-foot combat is re-pointed at the player on a ~5s
  cadence; drivers also get `CA_DISABLE_TARGET_CHANGES_DURING_VEHICLE_PURSUIT` (59) and
  `BlockPermanentEvents` stops in-car return fire at zombies. The Altruist-style one-way zombie truce
  was **deliberately not** given to The Families (user decision: Altruists untested, one-way bind
  feels off) — zombies still aggro the crew, focus just keeps snapping back to the player.
- Driver-killed → passengers exit (`driverDead` → `TASK_LEAVE_VEHICLE`) was already handled; verified
  as part of this round's playtest checklist.

### Families car-encounter follow-up (post-v1 playtest 2)
Playtest confirmed the chase feels good. Two remaining issues fixed:
- **Warp failures + stranded back-seat crew:** the car pool (faction3/voodoo2/cavalcade3) included two
  **2-door** cars, but the seat list is driver + 3 passengers — warps to seats 1/2 failed every spawn
  (log wall of `failed initial vehicle warp`). Pool is now all 4-doors (impaler5/superd/cavalcade3,
  player-requested), and `SpawnCrewForCar` skips any seat past `Vehicle.PassengerCapacity` as a guard.
  Lesson 46.
- **Idle on the pavement after dismount:** `TASK_LEAVE_VEHICLE` was re-issued every crew pass
  (restarting the exit animation) and combat waited for the ~5s re-point cadence. An
  `_crewExitPending` flag issues the exit once and fires `TASK_COMBAT_PED` the instant the member is
  on foot. Lesson 47.

### Families car-encounter follow-up (post-v1 playtest 3)
Playtest confirmed much better success (sometimes two cars, and the crew engages quickly after
exiting). Two issues fixed + one question answered:
- **Passengers never shot from the car:** the passenger task was `TASK_DRIVE_BY` — a notoriously dead
  native (the project's own `TaskDriveBy.md` says "doesn't seem to do anything"). Passengers now use
  `TASK_VEHICLE_SHOOT_AT_PED` (the reliable lean-out-and-fire primitive), and the drive-by reissue
  cadence was cut to ~2.5s (fire task recovers fast; the driver chase reissue stays at 300 to avoid
  brake-stutter). Combat attribute 59 (`CA_DISABLE_TARGET_CHANGES_DURING_VEHICLE_PURSUIT`) is also
  now driver-only — it was applied to every crew ped including passengers. Lesson 48.
- **Log wall of "failed initial vehicle warp":** spawn order is car-first-then-peds, and the immediate
  warp fires while the car model is still streaming — a guaranteed failure on first attempt. The
  initial warp is now gated on `car.Model.IsLoaded`; otherwise the re-warp net seats the member during
  cruise. The net was doing the real work all along — the warnings were just a false alarm. Lesson 49.

### Families car-encounter follow-up 2 (post-v1 playtest 4)
Playtest showed the crew still never fired from the car while the player was driving, and ambient
zombies went idle during the stand-off. Both fixed:
- **Crew in the car flipped the player off instead of shooting:** two more stacked causes behind the
  drive-by. (a) `TASK_VEHICLE_SHOOT_AT_PED`'s p2 is a **shoot range**, not a flag — passing `0f`
  meant the passenger was never in range. Now passes `CrewDriveByRange`. (b) `MakeFactionLike` arms
  the **on-foot** slot via `Weapons.Select`, but a ped seated in a car only uses its
  **vehicle-weapon slot** when aiming/shooting from the window — with no vehicle weapon the passenger
  is unarmed in the car (the "flip the player off" gesture) and the drive-by has nothing to fire.
  `SpawnCrewForCar` now calls `SET_CURRENT_PED_VEHICLE_WEAPON(ped, weapon)` right after
  `MakeFactionLike`. Lesson 48 (expanded).
- **Ambient zombies standing idle during the stand-off:** the Chasing branch only (re)issued
  `TASK_FOLLOW_TO_OFFSET_OF_ENTITY` on state-entry/target-change. The engine cleared chasing follow
  tasks during the chaotic dismount (physics bumps, blocked events), and with the player as an
  unchanging target the task was never re-issued — the zombie froze in `Chasing`. Chasing now has the
  same stuck-speed fallback as the faction guards (`Speed < 0.5f` ~30 behavior-ticks → re-issue
  follow). Also corrected the wander reissue cadence: it was `120` behavior-ticks (6-12s at
  `_updateInterval` 3-6 frames) where the comment claimed 2s — now `120 / _updateInterval`. Lesson 50.
